"""Run frozen, prebuilt V1 stability fixtures then endurance, one process at a time.

No builds, restores, installations, or publication. An interrupted run is never
resumed or combined with a later run to claim a continuous duration.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import time
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FILTER = (
    "/*/LoadingCache.StressTests/(LongRunningStabilityTests|FeatureCombinationStabilityTests)/*"
)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def manifest(folder):
    return {
        str(path.relative_to(folder)): sha(path)
        for path in sorted(folder.rglob("*"))
        if path.is_file()
    }


def source_manifest():
    names = (
        subprocess.check_output(
            [
                "git",
                "ls-files",
                "--cached",
                "--others",
                "--exclude-standard",
                "-z",
                "--",
                "src",
                "tests/LoadingCache.StressTests",
                "tools/LoadingCache.EnduranceProbe",
                "Directory.Build.props",
                "Directory.Build.targets",
                "Directory.Packages.props",
                "global.json",
                "NuGet.Config",
                "docs/v1-endurance-profile.md",
            ],
            cwd=ROOT,
        )
        .decode()
        .split("\0")
    )
    return {
        name: sha(ROOT / name) for name in sorted(set(names)) if name and (ROOT / name).is_file()
    }


def records(path):
    with path.open(encoding="utf-8") as stream:
        return [json.loads(line) for line in stream if line.strip()]


def verify_soak(folder, seconds, expected_runtime):
    trx = ET.parse(folder / "results.trx").getroot()
    results = trx.findall(".//{*}UnitTestResult")
    expected = {
        f"{name}({statistics})"
        for name in (
            "ContinuousMixedLifecycleMaintainsBounds",
            "ContinuousAccessOnlyLifecycleMaintainsBounds",
            "BulkWeakReferencesAndListenersRemainConsistent",
        )
        for statistics in ("False", "True")
    }
    require(len(results) == 6, "Expected six executed fixture cases")
    require(
        {result.get("testName", "").rsplit(".", 1)[-1] for result in results} == expected,
        "Unexpected case identities",
    )
    require(
        all(result.get("outcome") == "Passed" for result in results),
        "A stability case did not pass",
    )
    files = sorted((folder / "jsonl").glob("*.jsonl"))
    require(len(files) == 6, "Expected six raw fixture JSONL files")
    identities = set()
    for path in files:
        rows = records(path)
        require(
            rows[0]["Event"] == "start" and rows[-1]["Event"] == "passed",
            f"Incomplete fixture {path}",
        )
        require(rows[0]["seconds"] == seconds, "Fixture duration configuration mismatch")
        require(
            rows[0]["Runtime"] == expected_runtime,
            "Actual fixture runtime did not match selected runtime",
        )
        identity = (rows[0].get("Scenario", "features"), rows[0]["statistics"])
        require(identity not in identities, "Duplicate fixture identity")
        identities.add(identity)
        complete = [row for row in rows if row["Event"] == "workload-complete"]
        require(
            len(complete) == 1 and complete[0]["ElapsedSeconds"] >= seconds, "Missing full duration"
        )
        require(complete[0]["Operations"] > 0, "No workload progress")
        elapsed = [row["ElapsedSeconds"] for row in rows if "ElapsedSeconds" in row]
        require(elapsed == sorted(elapsed), "Fixture monotonic elapsed moved backwards")
    require(
        identities
        == {
            (scenario, statistics)
            for scenario in ("mixed", "access-only", "features")
            for statistics in (False, True)
        },
        "Wrong fixture workload identities",
    )
    return {"cases": 6, "secondsPerCase": seconds, "raw": [path.name for path in files]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--framework", choices=("net8.0", "net10.0"), required=True)
    parser.add_argument("--smoke", action="store_true")
    parser.add_argument("--phase", choices=("all", "soak", "endurance"), default="all")
    args = parser.parse_args()
    runtime = args.runtime.resolve()
    require(runtime.is_file(), "Runtime host does not exist")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    sources = source_manifest()
    inputs = output / "inputs"
    for name in sources:
        target = inputs / "source" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / name, target)
    for name, source in (
        ("stress", ROOT / "tests/LoadingCache.StressTests/bin/Release" / args.framework),
        ("endurance", ROOT / "tools/LoadingCache.EnduranceProbe/bin/Release" / args.framework),
    ):
        if (name == "stress" and args.phase == "endurance") or (
            name == "endurance" and args.phase == "soak"
        ):
            continue
        require(source.is_dir(), f"Prebuilt output missing: {source}")
        shutil.copytree(source, inputs / name)
    binaries = manifest(inputs)
    if args.phase == "all":
        require(
            sha(inputs / "stress/LoadingCache.dll") == sha(inputs / "endurance/LoadingCache.dll"),
            "Stress and endurance must use identical core assembly bytes",
        )
    save(output / "inputs-manifest.json", binaries)
    save(output / "source-manifest.json", sources)
    runtime_files = manifest(runtime.parent)
    save(output / "runtime-manifest.json", runtime_files)
    environment = os.environ.copy()
    runtime_environment = {
        key: value
        for key, value in environment.items()
        if key.startswith(("DOTNET_", "COMPlus_", "CORECLR_", "COR_"))
    }
    require(
        not any("tier" in key.lower() or "gcstress" in key.lower() for key in runtime_environment),
        "Runtime tuning overrides must be removed before acceptance",
    )
    summary = {
        "status": "running",
        "formal": not args.smoke,
        "phase": args.phase,
        "framework": args.framework,
        "runtime": str(runtime),
        "runtimeHostSha256": sha(runtime),
        "gitHead": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True
        ).strip(),
        "startedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "environment": runtime_environment,
        "commands": [],
    }
    save(output / "summary.json", summary)
    heartbeat = (output / "heartbeat.jsonl").open("w", encoding="utf-8", buffering=1)

    def run(name, command, timeout, overrides=None):
        entry = {
            "name": name,
            "argv": command,
            "timeoutSeconds": timeout,
            "environmentOverrides": overrides or {},
        }
        summary["commands"].append(entry)
        save(output / "summary.json", summary)
        started = time.monotonic()
        with (output / f"{name}.log").open("w", encoding="utf-8") as log:
            process = subprocess.Popen(
                command,
                cwd=ROOT,
                env=environment | (overrides or {}),
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            entry["pid"] = process.pid
            save(output / "summary.json", summary)
            try:
                prior_size = 0
                last_output = started
                while process.poll() is None:
                    passed = time.monotonic() - started
                    heartbeat.write(
                        json.dumps(
                            {
                                "step": name,
                                "pid": process.pid,
                                "elapsedSeconds": passed,
                                "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                            }
                        )
                        + "\n"
                    )
                    require(passed <= timeout, f"{name} watchdog exceeded {timeout}s")
                    # A living process without progress is not a successful endurance run.
                    if name == "endurance":
                        size = (output / f"{name}.log").stat().st_size
                        if size > prior_size:
                            prior_size = size
                            last_output = time.monotonic()
                        require(
                            time.monotonic() - last_output <= 120,
                            "Endurance produced no progress for 120 seconds",
                        )
                    try:
                        process.wait(timeout=min(30, max(1, timeout - passed)))
                    except subprocess.TimeoutExpired:
                        pass
            except BaseException:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                raise
            finally:
                entry.update(exitCode=process.poll(), elapsedSeconds=time.monotonic() - started)
                save(output / "summary.json", summary)
        require(
            entry["exitCode"] == 0, f"{name} exited {entry['exitCode']}; inspect its preserved log"
        )

    try:
        run("runtime-info", [str(runtime), "--info"], 30)
        run("runtime-list", [str(runtime), "--list-runtimes"], 30)
        versions = re.findall(
            r"^Microsoft\.NETCore\.App (\S+) ",
            (output / "runtime-list.log").read_text(),
            re.MULTILINE,
        )
        selected = [
            version
            for version in versions
            if version.startswith(args.framework.removeprefix("net") + ".")
        ]
        require(
            len(selected) == 1,
            "Use a runtime host with exactly one installed patch in the selected framework",
        )
        expected_runtime = ".NET " + selected[0]
        summary["expectedRuntime"] = expected_runtime
        if args.phase != "endurance":
            seconds = 5 if args.smoke else 1800
            folder = output / "soak"
            (folder / "jsonl").mkdir(parents=True)
            run(
                "soak",
                [
                    str(runtime),
                    "exec",
                    "--fx-version",
                    selected[0],
                    str(inputs / "stress/LoadingCache.StressTests.dll"),
                    "--treenode-filter",
                    FILTER,
                    "--report-trx",
                    "--report-trx-filename",
                    "results.trx",
                    "--results-directory",
                    str(folder),
                    "--maximum-parallel-tests",
                    "6",
                ],
                seconds + 300,
                {
                    "LOADINGCACHE_SOAK_SECONDS": str(seconds),
                    "LOADINGCACHE_FEATURE_SOAK_SECONDS": str(seconds),
                    "LOADINGCACHE_SOAK_OUTPUT": str(folder / "jsonl"),
                },
            )
            summary["soakVerification"] = verify_soak(folder, seconds, expected_runtime)
        if args.phase != "soak":
            endurance_seconds = 30 if args.smoke else 28_800
            command = [
                str(runtime),
                str(inputs / "endurance/LoadingCache.EnduranceProbe.dll"),
                "--seconds",
                str(endurance_seconds),
            ]
            if args.smoke:
                command.extend(["--cycle-seconds", "1", "--warmup-cycles", "2"])
            run("endurance", command, endurance_seconds + 300)
            rows = records(output / "endurance.log")
            terminal = rows[-1]
            require(
                rows[0]["Event"] == "start" and terminal["Event"] == "passed",
                "Missing endurance terminal result",
            )
            require(
                terminal["ElapsedSeconds"] >= endurance_seconds and terminal["Operations"] > 0,
                "Incomplete endurance duration or progress",
            )
            require(
                terminal["formal"] is (not args.smoke), "Endurance formal/smoke identity mismatch"
            )
            require(
                rows[0]["Runtime"] == expected_runtime,
                "Actual endurance runtime did not match selected runtime",
            )
            require(
                rows[0]["CacheAssemblySha256"].lower()
                == sha(inputs / "endurance/LoadingCache.dll"),
                "Executing core assembly bytes differ from frozen inputs",
            )
            require(
                rows[0]["ProbeAssemblySha256"].lower()
                == sha(inputs / "endurance/LoadingCache.EnduranceProbe.dll"),
                "Executing probe assembly bytes differ from frozen inputs",
            )
            summary["enduranceVerification"] = terminal
        require(manifest(inputs) == binaries, "Frozen input bytes changed during acceptance")
        require(source_manifest() == sources, "Candidate source changed during acceptance")
        require(
            manifest(runtime.parent) == runtime_files, "Runtime files changed during acceptance"
        )
        summary.update(status="passed")
    except BaseException as error:
        summary.update(status="failed", error=repr(error))
        raise
    finally:
        heartbeat.close()
        summary["finishedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        save(output / "summary.json", summary)
        save(
            output / "evidence-manifest.json",
            {
                name: digest
                for name, digest in manifest(output).items()
                if name != "evidence-manifest.json"
            },
        )


if __name__ == "__main__":
    main()
