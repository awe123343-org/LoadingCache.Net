"""Run reproducible trim/Native AOT consumer publishes on osx-arm64.

The runner uses real project references and never passes
``BuildProjectReferences=false``. It records source state before and after the
matrix, captures resolved project-reference DLL/PDB hashes from an MSBuild
target that runs after ``ResolveReferences``, retains compiler/runtime-pack evidence,
and executes each produced self-contained app. It does not install an SDK,
runtime, or global tool.

Examples::

    uv run --no-project python tools/aot-smoke.py --output artifacts/aot-smoke/run
    uv run --no-project python tools/aot-smoke.py --quick --output artifacts/aot-smoke/quick
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shlex
import subprocess
import sys
import time
from datetime import datetime, timezone
from xml.sax.saxutils import escape as xml_escape


ROOT = Path(__file__).resolve().parent.parent
RID = "osx-arm64"
TFM_RUNTIME_VERSIONS = {"net8.0": "8.0.31", "net10.0": "10.0.12"}
TFM_RUNTIME_HOST_DIRECTORIES = {
    "net8.0": Path("artifacts/runtime8"),
    "net10.0": Path("artifacts/runtime10"),
}
SAMPLES = {
    "core": {
        "project": Path("samples/LoadingCache.AotSmoke/LoadingCache.AotSmoke.csproj"),
        "project_name": "LoadingCache.AotSmoke",
        "binary": "LoadingCache.AotSmoke",
        "project_reference_names": ("LoadingCache",),
    },
    "di": {
        "project": Path("samples/LoadingCache.DiAotSmoke/LoadingCache.DiAotSmoke.csproj"),
        "project_name": "LoadingCache.DiAotSmoke",
        "binary": "LoadingCache.DiAotSmoke",
        "project_reference_names": (
            "LoadingCache",
            "LoadingCache.Extensions.DependencyInjection",
        ),
    },
}


def timestamp() -> str:
    return datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def source_manifest(root: Path) -> dict[str, str]:
    """Hash source/build inputs, matching the repository validation scope."""

    raw_names = subprocess.check_output(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        cwd=root,
    )
    suffixes = {
        ".cs",
        ".csproj",
        ".props",
        ".targets",
        ".slnx",
        ".json",
        ".py",
        ".yml",
    }
    manifest: dict[str, str] = {}
    for raw_name in raw_names.decode().split("\0"):
        if not raw_name:
            continue
        relative = Path(raw_name)
        path = root / relative
        if (
            path.is_file()
            and not path.is_symlink()
            and relative.suffix in suffixes
            and not relative.as_posix().startswith("docs/")
        ):
            manifest[relative.as_posix()] = sha256_file(path)
    return dict(sorted(manifest.items()))


def write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def command_text(command: list[str]) -> str:
    return shlex.join(command)


def run_logged(
    name: str,
    command: list[str],
    output: Path,
    timeout_seconds: int,
    environment_overrides: dict[str, str] | None = None,
) -> dict[str, object]:
    log_path = output / f"{name}.log"
    environment = os.environ.copy()
    if environment_overrides:
        environment.update(environment_overrides)
    started = time.monotonic()
    timed_out = False
    with log_path.open("w", encoding="utf-8") as log:
        log.write(f"Started: {timestamp()}\n")
        log.write(f"WorkingDirectory: {ROOT}\n")
        log.write(f"Command: {command_text(command)}\n")
        if environment_overrides:
            log.write(f"EnvironmentOverrides: {json.dumps(environment_overrides, sort_keys=True)}\n")
        log.write("\n")
        try:
            completed = subprocess.run(
                command,
                cwd=ROOT,
                env=environment,
                stdout=log,
                stderr=subprocess.STDOUT,
                timeout=timeout_seconds,
                check=False,
            )
            exit_code = completed.returncode
        except subprocess.TimeoutExpired:
            exit_code = 124
            timed_out = True
            log.write(f"\nWatchdog expired after {timeout_seconds} seconds.\n")
    return {
        "command": command,
        "commandText": command_text(command),
        "exitCode": exit_code,
        "elapsedSeconds": round(time.monotonic() - started, 3),
        "log": log_path.relative_to(output).as_posix(),
        "timedOut": timed_out,
    }


def write_capture_targets(
    path: Path,
    sample_name: str,
    capture_path: Path,
) -> None:
    target = f"""<Project>
  <Target
    Name="_AotSmokeCapturePublishInputs"
    AfterTargets="ResolveReferences"
    Condition="'$(MSBuildProjectName)' == '{xml_escape(sample_name)}'">
    <ItemGroup>
      <_AotSmokeProjectReference
        Include="@(ReferenceCopyLocalPaths)"
        Condition="
          '%(ReferenceCopyLocalPaths.ReferenceSourceTarget)' == 'ProjectReference'
          and '%(ReferenceCopyLocalPaths.Extension)' == '.dll'" />
      <_AotSmokeProjectReferencePdb
        Include="@(_AotSmokeProjectReference->'%(RootDir)%(Directory)%(Filename).pdb')"
        Condition="Exists('%(_AotSmokeProjectReference.RootDir)%(_AotSmokeProjectReference.Directory)%(_AotSmokeProjectReference.Filename).pdb')" />
    </ItemGroup>
    <WriteLinesToFile
      File="{xml_escape(str(capture_path))}"
      Lines="capture-phase=AfterTargets:ResolveReferences"
      Overwrite="true" />
    <GetFileHash
      Files="@(_AotSmokeProjectReference);@(_AotSmokeProjectReferencePdb)"
      Algorithm="SHA256"
      HashEncoding="hex">
      <Output TaskParameter="Items" ItemName="_AotSmokeInputWithHash" />
    </GetFileHash>
    <WriteLinesToFile
      File="{xml_escape(str(capture_path))}"
      Lines="@(_AotSmokeInputWithHash-&gt;'%(Identity)|%(FileHash)')"
      Overwrite="false"
      />
  </Target>
</Project>
"""
    path.write_text(target, encoding="utf-8")


def parse_capture_manifest(
    path: Path,
    expected_reference_names: tuple[str, ...],
) -> dict[str, object]:
    files: list[dict[str, object]] = []
    malformed: list[str] = []
    phase: str | None = None
    if path.is_file():
        for line in path.read_text(encoding="utf-8").splitlines():
            if not line:
                continue
            if line.startswith("capture-phase="):
                phase = line.removeprefix("capture-phase=")
                continue
            if "|" not in line:
                malformed.append(line)
                continue
            file_name, file_hash = line.rsplit("|", 1)
            if not re.fullmatch(r"[0-9a-fA-F]{64}", file_hash):
                malformed.append(line)
                continue
            file_path = Path(file_name)
            if not file_path.is_absolute():
                file_path = (ROOT / file_path).resolve()
            files.append(
                {
                    "path": file_path.relative_to(ROOT).as_posix()
                    if file_path.is_relative_to(ROOT)
                    else str(file_path),
                    "sha256Captured": file_hash,
                    "existsAtInspection": file_path.is_file(),
                }
            )
    captured_paths = {
        Path(item["path"]).resolve()
        for item in files
        if isinstance(item.get("path"), str)
    }
    reference_names = sorted(set(expected_reference_names))
    complete_pairs = {
        name
        for name in reference_names
        if any(
            path.name == f"{name}.dll"
            and path.with_suffix(".pdb") in captured_paths
            for path in captured_paths
        )
    }
    complete = bool(
        phase == "AfterTargets:ResolveReferences"
        and set(reference_names) == complete_pairs
        and not malformed
    )
    return {
        "path": path.relative_to(ROOT).as_posix() if path.is_relative_to(ROOT) else str(path),
        "captureFilePresent": path.is_file(),
        "capturePhase": phase,
        "files": files,
        "expectedProjectReferences": [
            {
                "assemblyName": name,
                "dllCaptured": any(
                    captured_path.name == f"{name}.dll"
                    for captured_path in captured_paths
                ),
                "pdbCaptured": any(
                    captured_path.name == f"{name}.pdb"
                    for captured_path in captured_paths
                ),
            }
            for name in reference_names
        ],
        "malformedLines": malformed,
        "complete": complete,
        "authority": "captured-by-msbuild-after-resolve-references",
    }


def collect_pack_evidence(
    project: Path,
    tfm: str,
    runtime_version: str,
    native: bool,
    started_epoch: float,
) -> dict[str, object]:
    def matching_target(target_name: str) -> bool:
        parts = target_name.split("/")
        return parts[0] == tfm and (len(parts) == 1 or RID in parts[1:])

    def matching_framework(framework_name: str, framework: object) -> bool:
        parts = framework_name.split("/")
        if parts[0] != tfm:
            return False
        if len(parts) > 1:
            return RID in parts[1:]
        return not isinstance(framework, dict) or framework.get("runtimeIdentifier") in (None, RID)

    def add_package(
        name: object,
        version: object,
        assets_path: Path,
        source: str,
    ) -> None:
        if not isinstance(name, str) or not isinstance(version, str) or not version:
            return
        lowered = name.lower()
        is_runtime = (
            RID in lowered
            and (
                "microsoft.netcore.app.runtime" in lowered
                or ("runtime." in lowered and "microsoft.netcore.app" in lowered)
            )
        )
        is_compiler = "ilcompiler" in lowered or "illink" in lowered
        if is_runtime or is_compiler:
            package_key = (name, version)
            packages.setdefault(
                package_key,
                {
                    "name": name,
                    "version": version,
                    "resolvedVersion": normalise_exact_version(version),
                    "assetsFile": assets_path.relative_to(ROOT).as_posix(),
                    "source": source,
                },
            )

    def normalise_exact_version(version: str) -> str:
        value = version.strip()
        if value.startswith("[") and value.endswith("]"):
            bounds = [part.strip() for part in value[1:-1].split(",")]
            if len(bounds) == 1:
                return bounds[0]
            if len(bounds) == 2 and bounds[0] == bounds[1]:
                return bounds[0]
        return value

    assets_files: list[dict[str, object]] = []
    packages: dict[tuple[str, str], dict[str, object]] = {}
    for assets_path in sorted(project.parent.rglob("project.assets.json")):
        if "obj" not in assets_path.parts:
            continue
        try:
            stat = assets_path.stat()
            assets = json.loads(assets_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        fresh = stat.st_mtime >= started_epoch - 2
        assets_files.append(
            {
                "path": assets_path.relative_to(ROOT).as_posix(),
                "sha256": sha256_file(assets_path),
                "freshAtPublishStart": fresh,
                "modifiedAt": datetime.fromtimestamp(stat.st_mtime, timezone.utc).isoformat(),
            }
        )
        targets = assets.get("targets", {})
        target_libraries: set[str] = set()
        for target_name, target in targets.items():
            if not matching_target(target_name):
                continue
            if isinstance(target, dict):
                target_libraries.update(target)
        libraries = assets.get("libraries", {})
        for library in target_libraries:
            if "/" not in library or not isinstance(libraries, dict):
                continue
            name, version = library.rsplit("/", 1)
            if library in libraries:
                add_package(name, version, assets_path, "targets")

        project_frameworks = assets.get("project", {}).get("frameworks", {})
        if isinstance(project_frameworks, dict):
            for framework_name, framework in project_frameworks.items():
                if not matching_framework(framework_name, framework) or not isinstance(framework, dict):
                    continue
                for dependency in framework.get("downloadDependencies", []):
                    if isinstance(dependency, dict):
                        add_package(
                            dependency.get("name"),
                            dependency.get("version") or dependency.get("versionRange"),
                            assets_path,
                            "project.frameworks.downloadDependencies",
                        )
    package_list = sorted(packages.values(), key=lambda item: (item["name"], item["version"]))
    runtime_matches = [
        item
        for item in package_list
        if (
            RID in item["name"].lower()
            and (
                "microsoft.netcore.app.runtime" in item["name"].lower()
                or (
                    "runtime." in item["name"].lower()
                    and "microsoft.netcore.app" in item["name"].lower()
                )
            )
        )
        and item["resolvedVersion"] == runtime_version
    ]
    compiler_markers = ("ilcompiler",) if native else ("microsoft.net.illink", "illink")
    compiler_matches = [
        item
        for item in package_list
        if any(marker in item["name"].lower() for marker in compiler_markers)
    ]
    complete = bool(runtime_matches and compiler_matches and assets_files)
    return {
        "assetsFiles": assets_files,
        "packages": package_list,
        "expectedRuntimeFrameworkVersion": runtime_version,
        "runtimePackMatchesExpected": bool(runtime_matches),
        "compilerOrLinkerPacks": compiler_matches,
        "requiredCompiler": "ILCompiler" if native else "ILLink",
        "targetFramework": tfm,
        "rid": RID,
        "complete": complete,
        "note": "Package versions are observed from project.assets.json after the publish command; no version is inferred from FrameworkDescription.",
    }


def output_manifest(output_dir: Path, manifest_path: Path) -> dict[str, object]:
    files: list[dict[str, object]] = []
    canonical: list[str] = []
    for path in sorted(item for item in output_dir.rglob("*") if item.is_file()):
        if path == manifest_path:
            continue
        relative = path.relative_to(output_dir).as_posix()
        digest = sha256_file(path)
        size = path.stat().st_size
        files.append({"path": relative, "bytes": size, "sha256": digest})
        canonical.append(f"{relative}\0{digest}\n")
    tree_hash = hashlib.sha256("".join(canonical).encode()).hexdigest()
    result = {"files": files, "fileCount": len(files), "treeSha256": tree_hash}
    write_json(manifest_path, result)
    return result


def verify_runtime_output(
    text: str,
    expected_runtime: str,
    native: bool,
) -> dict[str, object]:
    runtime_match = re.search(r"\.NET\s+(\d+\.\d+\.\d+)", text)
    dynamic_match = re.search(r"(?:dynamic code|DynamicCodeSupported):\s*(True|False)", text)
    architecture_match = re.search(
        r"(?:ProcessArchitecture:\s*|;\s*)(Arm64|X64|X86|Arm)\b", text
    )
    observed_runtime = runtime_match.group(1) if runtime_match else None
    dynamic = dynamic_match.group(1) == "True" if dynamic_match else None
    architecture = architecture_match.group(1) if architecture_match else None
    return {
        "observedRuntimeVersion": observed_runtime,
        "expectedRuntimeVersion": expected_runtime,
        "runtimeMatchesExpected": observed_runtime == expected_runtime,
        "dynamicCodeSupported": dynamic,
        "expectedDynamicCodeSupported": not native,
        "dynamicCodeMatchesExpected": dynamic is (not native),
        "processArchitecture": architecture,
        "architectureMatchesExpected": architecture == "Arm64",
        "nativeExpectationArgument": "--expect-native" if native else None,
        "complete": (
            observed_runtime == expected_runtime
            and dynamic is (not native)
            and architecture == "Arm64"
        ),
    }


def select_jobs(sample_selection: str, quick: bool) -> list[tuple[str, str, bool]]:
    sample_keys = ["core", "di"] if sample_selection == "both" else [sample_selection]
    jobs = [
        (sample, tfm, native)
        for sample in sample_keys
        for tfm in ("net8.0", "net10.0")
        for native in (False, True)
    ]
    if quick:
        return [(sample_keys[0], "net10.0", False)]
    return jobs


def run_job(
    sample_key: str,
    tfm: str,
    native: bool,
    output: Path,
    dotnet: str,
    timeout_seconds: int,
) -> dict[str, object]:
    sample = SAMPLES[sample_key]
    runtime_version = TFM_RUNTIME_VERSIONS[tfm]
    runtime_host_directory = (ROOT / TFM_RUNTIME_HOST_DIRECTORIES[tfm]).resolve()
    mode = "native" if native else "trimmed"
    job_name = f"{sample_key}-{tfm}-{mode}"
    publish_dir = output / "publish" / job_name
    publish_dir.mkdir(parents=True)
    capture_path = output / f"{job_name}-input-capture.txt"
    target_path = output / f"{job_name}-capture.targets"
    expected_reference_names = sample["project_reference_names"]
    write_capture_targets(target_path, sample["project_name"], capture_path)
    command = [
        dotnet,
        "publish",
        str(ROOT / sample["project"]),
        "-c",
        "Release",
        "-f",
        tfm,
        "-r",
        RID,
        "--self-contained",
        "true",
        "-p:PublishAot=" + ("true" if native else "false"),
        "-p:PublishTrimmed=true",
        "-p:TrimMode=full",
        "-p:RuntimeFrameworkVersion=" + runtime_version,
        "-p:_DotNetHostDirectory=" + str(runtime_host_directory),
        "-p:CustomAfterMicrosoftCommonTargets=" + str(target_path),
        "-o",
        str(publish_dir),
    ]
    if any("BuildProjectReferences=false" in item for item in command):
        raise RuntimeError("AOT runner must never disable project-reference builds")
    started_epoch = time.time()
    publish_result = run_logged(
        f"{job_name}-publish",
        command,
        output,
        timeout_seconds,
    )
    capture = parse_capture_manifest(capture_path, expected_reference_names)
    pack_evidence = collect_pack_evidence(
        ROOT / sample["project"], tfm, runtime_version, native, started_epoch
    )
    result: dict[str, object] = {
        "name": job_name,
        "sample": sample_key,
        "project": sample["project"].as_posix(),
        "targetFramework": tfm,
        "runtimeFrameworkVersion": runtime_version,
        "runtimeHostDirectory": runtime_host_directory.relative_to(ROOT).as_posix(),
        "runtimeHostExists": (runtime_host_directory / "dotnet").is_file(),
        "rid": RID,
        "mode": mode,
        "native": native,
        "publish": publish_result,
        "inputCapture": capture,
        "packEvidence": pack_evidence,
        "run": None,
        "output": None,
        "verification": None,
        "status": "publish-failed" if publish_result["exitCode"] else "incomplete",
    }
    if publish_result["exitCode"]:
        return result

    binary = publish_dir / sample["binary"]
    output_manifest_path = output / f"{job_name}-output-files.json"
    output_info = output_manifest(publish_dir, output_manifest_path)
    result["output"] = {
        "directory": publish_dir.relative_to(output).as_posix(),
        "primaryBinary": binary.relative_to(output).as_posix(),
        "primaryBinaryExists": binary.is_file(),
        "primaryBinarySha256": sha256_file(binary) if binary.is_file() else None,
        "manifest": output_manifest_path.relative_to(output).as_posix(),
        "treeSha256": output_info["treeSha256"],
        "fileCount": output_info["fileCount"],
    }
    if not binary.is_file():
        result["status"] = "missing-primary-output"
        return result

    run_command = [str(binary)]
    if native:
        run_command.append("--expect-native")
    run_result = run_logged(
        f"{job_name}-run",
        run_command,
        output,
        min(timeout_seconds, 300),
    )
    result["run"] = run_result
    run_text = (output / run_result["log"]).read_text(encoding="utf-8")
    verification = verify_runtime_output(run_text, runtime_version, native)
    result["verification"] = verification
    result["status"] = (
        "passed"
        if run_result["exitCode"] == 0
        and capture["complete"]
        and pack_evidence["complete"]
        and verification["complete"]
        else "incomplete"
    )
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, help="Fresh evidence directory to create")
    parser.add_argument(
        "--sample",
        choices=("core", "di", "both"),
        default="both",
        help="Sample family to publish (default: both)",
    )
    parser.add_argument(
        "--quick",
        action="store_true",
        help="Run one managed-trimmed net10 job for the selected sample family",
    )
    parser.add_argument("--dotnet", default="dotnet", help="SDK-bearing dotnet command")
    parser.add_argument(
        "--timeout-seconds",
        type=int,
        default=1800,
        help="Per-publish watchdog in seconds (default: 1800)",
    )
    args = parser.parse_args()
    if args.timeout_seconds <= 0:
        parser.error("--timeout-seconds must be positive")
    output = (ROOT / args.output).resolve()
    if output.exists():
        parser.error(f"Evidence directory already exists; choose a fresh path: {output}")
    output.mkdir(parents=True)

    before = source_manifest(ROOT)
    write_json(output / "source-manifest.json", before)
    jobs = select_jobs(args.sample, args.quick)
    results: dict[str, object] = {
        "schemaVersion": 1,
        "startedAt": timestamp(),
        "repository": str(ROOT),
        "platform": platform.platform(),
        "architecture": platform.machine(),
        "rid": RID,
        "dotnetCommand": args.dotnet,
        "matrix": [
            {"sample": sample, "targetFramework": tfm, "native": native}
            for sample, tfm, native in jobs
        ],
        "sourceManifest": "source-manifest.json",
        "sourceUnchanged": None,
        "commands": [],
        "jobs": [],
    }

    environment_result = run_logged(
        "environment",
        [args.dotnet, "--info"],
        output,
        min(args.timeout_seconds, 120),
    )
    results["commands"].append({"name": "environment", **environment_result})
    for tfm in ("net8.0", "net10.0"):
        runtime_host = (ROOT / TFM_RUNTIME_HOST_DIRECTORIES[tfm]).resolve() / "dotnet"
        runtime_info = run_logged(
            f"runtime-{tfm}-info",
            [str(runtime_host), "--info"],
            output,
            min(args.timeout_seconds, 120),
        )
        results["commands"].append(
            {
                "name": f"runtime-{tfm}-info",
                "runtimeHost": runtime_host.relative_to(ROOT).as_posix(),
                **runtime_info,
            }
        )
    if environment_result["exitCode"]:
        results["status"] = "environment-failed"
    else:
        for sample_key, tfm, native in jobs:
            print(f"Running {sample_key}/{tfm}/{'native' if native else 'trimmed'}", flush=True)
            job = run_job(sample_key, tfm, native, output, args.dotnet, args.timeout_seconds)
            results["jobs"].append(job)
            print(f"{job['name']}: {job['status']}", flush=True)

        statuses = [job["status"] for job in results["jobs"]]
        results["status"] = "passed" if statuses and all(status == "passed" for status in statuses) else "failed"

    after = source_manifest(ROOT)
    results["finishedAt"] = timestamp()
    results["sourceUnchanged"] = before == after
    if before != after:
        write_json(output / "source-manifest-after.json", after)
        results["status"] = "source-changed-during-validation"
    write_json(output / "results.json", results)
    if not results["sourceUnchanged"]:
        return 2
    if results["status"] == "passed":
        return 0
    return 1


if __name__ == "__main__":
    sys.exit(main())
