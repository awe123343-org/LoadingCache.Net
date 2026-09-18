#!/usr/bin/env python3
"""Build the isolated probe and verify its source/config -> core/probe binary link."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
from xml.sax.saxutils import escape

STAGED = Path(__file__).resolve().parents[2]
# Reviewed PrepareFlightExecution: GetTimestamp assignment, then CreateTimer,
# with no intervening provider timestamp read. A core change requires re-review.
TIMEOUT_SOURCE_SHA256 = "51d48b8526bbd4648f7bf9925b73e69b1cbe4258a422e783ec7a366a45311a80"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def ancestor_configs(root, directory):
    require(directory == root or root in directory.parents, "Build project is outside repository")
    names = ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config", "nuget.config", "global.json", ".editorconfig")
    paths = set()
    while True:
        paths.update(directory / name for name in names if (directory / name).is_file())
        if directory == root:
            return paths
        directory = directory.parent


def snapshot(root, project_directory):
    names = subprocess.check_output([
        "git", "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--",
        "src", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
        "global.json", "NuGet.Config", ".editorconfig",
    ], cwd=root).decode().split("\0")
    paths = {root / name for name in names if name and (root / name).is_file()}
    paths.update(ancestor_configs(root, root / "src/LoadingCache"))
    paths.update(ancestor_configs(root, project_directory))
    paths.update(path for path in (STAGED / "tools/LoadingCache.ServiceProbe").iterdir()
                 if path.suffix in (".cs", ".py", ".csproj"))
    paths.update((Path(__file__).resolve(), STAGED / "docs/v1-loading-admission-profile.md"))
    return {str(path): sha(path) for path in sorted(paths)}


def verify_core(directory, root=None):
    record = json.loads((directory / "manifest.json").read_text())
    require(record["schemaVersion"] == 1 and record["kind"] == "core-build", "Wrong core build manifest")
    require(record["status"] == "passed" and record["exitCode"] == 0 and record["sourceUnchanged"], "Core build did not pass")
    require(record["sourceBefore"] == record["sourceAfter"], "Core sources changed during build")
    require(set(record["sourceCopies"]) == set(record["sourceBefore"]), "Core source copies incomplete")
    require(all(sha(directory / record["sourceCopies"][name]) == digest for name, digest in record["sourceBefore"].items()), "Frozen core source differs from build")
    require(all(sha(directory / name) == digest for name, digest in record["outputs"].items()), "Frozen core output differs from build")
    require(sha(directory / "build.log") == record["logSha256"] and sha(directory / "sdk.log") == record["sdkInfoSha256"], "Core build log/SDK evidence changed")
    require(record["sourceBefore"]["src/LoadingCache/EngineTimeout.cs"] == TIMEOUT_SOURCE_SHA256, "Cache timeout origin sequence requires source re-review")
    if root is not None:
        require(root == Path(record["sourceRoot"]), "Core build source root differs")
        config = {"Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "src/Package.props", "global.json", "NuGet.Config", ".editorconfig"}
        names = subprocess.check_output(["git", "ls-files", "-co", "--exclude-standard", "-z"], cwd=root).decode().split("\0")
        inputs = {name for name in names if name and (root / name).is_file() and (
            name.startswith("src/LoadingCache/") and name.endswith((".cs", ".csproj", ".props", ".targets")) or name in config)}
        inputs.update(("src/LoadingCache/obj/project.assets.json", "src/LoadingCache/obj/LoadingCache.csproj.nuget.g.props", "src/LoadingCache/obj/LoadingCache.csproj.nuget.g.targets"))
        inputs.update(str(path.relative_to(root)) for path in ancestor_configs(root, root / "src/LoadingCache"))
        require(inputs == set(record["sourceBefore"]), "Current core source/config inventory differs from build")
        require(all(sha(root / name) == digest for name, digest in record["sourceBefore"].items()), "Current core source/config differs from build")
    return record


def verify(directory, root=None, built=None):
    record = json.loads((directory / "manifest.json").read_text())
    require(record["schemaVersion"] == 1 and record["kind"] == "loading-probe-build", "Wrong probe build manifest")
    require(record["status"] == "passed" and record["exitCode"] == 0, "Probe build did not pass")
    require(record["sourceBefore"] == record["sourceAfter"], "Probe sources/config changed during build")
    require(set(record["sourceCopies"]) == set(record["sourceBefore"]), "Probe source copies incomplete")
    require(all(sha(directory / record["sourceCopies"][name]) == digest for name, digest in record["sourceBefore"].items()), "Frozen probe source differs from build")
    require(all(sha(directory / name) == digest for name, digest in record["buildInputs"].items()), "Probe build inputs changed")
    require(all(sha(directory / name) == digest for name, digest in record["outputs"].items()), "Probe output differs from build")
    require(sha(directory / "build.log") == record["logSha256"] and sha(directory / "sdk.log") == record["sdkInfoSha256"], "Probe build log/SDK evidence changed")
    core = verify_core(directory / "core", root)
    require(sha(directory / "core/manifest.json") == record["coreManifestSha256"], "Core manifest link changed")
    require(record["outputs"]["bin/LoadingCache.dll"] == core["outputs"]["bin/LoadingCache.dll"], "Probe used a different core binary")
    for name, digest in core["sourceBefore"].items():
        source = str(Path(core["sourceRoot"]) / name)
        if source in record["sourceBefore"]:
            require(record["sourceBefore"][source] == digest, "Probe/core source attribution differs")
    if root is not None:
        require(root == Path(record["sourceRoot"]), "Probe build source root differs")
        require(snapshot(root, Path(record["projectDirectory"])) == record["sourceBefore"], "Current source/config differs from probe build")
    if built is not None:
        require({"bin/" + path.name: sha(path) for path in built.iterdir() if path.is_file()} == record["outputs"], "Supplied binaries differ from probe build")
    return record


def prepare(args):
    root, output = args.repository.resolve(), args.output.resolve()
    core = args.core_manifest.resolve().parent
    verify_core(core, root)
    output.mkdir(parents=True, exist_ok=False)
    # Freeze the existing proven core; this build never invokes its project.
    (output / "core").mkdir()
    for name in ("manifest.json", "build.log", "sdk.log"):
        shutil.copy2(core / name, output / "core" / name)
    for name in ("bin", "sources"):
        shutil.copytree(core / name, output / "core" / name)
    before = snapshot(root, output)
    copies = {}
    for index, path in enumerate(before):
        target = output / "sources" / str(index) / Path(path).name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
        copies[path] = str(target.relative_to(output))
    project = output / "LoadingCache.ServiceProbe.csproj"
    project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>{args.framework}</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="LoadingCache"><HintPath>{escape(str(output / "core/bin/LoadingCache.dll"))}</HintPath></Reference>
    <Compile Include="{escape(str(STAGED / "tools/LoadingCache.ServiceProbe/*.cs"))}" />
  </ItemGroup>
</Project>
''', encoding="utf-8")
    command = [str(args.dotnet.resolve()), "build", str(project), "-c", "Release", "-t:Rebuild",
               "--disable-build-servers", "-m:1", "/nodeReuse:false"]
    record = {"schemaVersion": 1, "kind": "loading-probe-build", "sourceRoot": str(root), "projectDirectory": str(output),
              "framework": args.framework, "command": command, "sourceBefore": before,
              "sourceCopies": copies, "coreManifestSha256": sha(output / "core/manifest.json"),
              "buildInputs": {project.name: sha(project)}, "status": "running"}
    write(output / "manifest.json", record)
    sdk = subprocess.run([str(args.dotnet.resolve()), "--info"], cwd=root, capture_output=True, text=True, check=True)
    (output / "sdk.log").write_text(sdk.stdout + sdk.stderr, encoding="utf-8")
    with (output / "build.log").open("w") as log:
        result = subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT)
    record.update(exitCode=result.returncode, sourceAfter=snapshot(root, output), logSha256=sha(output / "build.log"), sdkInfoSha256=sha(output / "sdk.log"))
    if result.returncode == 0 and before == record["sourceAfter"]:
        source_bin = output / "bin/Release" / args.framework
        for path in source_bin.iterdir():
            if path.is_file():
                shutil.copy2(path, output / "bin" / path.name)
        record["outputs"] = {str(path.relative_to(output)): sha(path) for path in (output / "bin").iterdir() if path.is_file()}
        record["status"] = "passed"
    else:
        record["status"] = "failed"
    write(output / "manifest.json", record)
    verify(output, root)
    print(json.dumps({"status": "passed", "manifest": str(output / "manifest.json")}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", type=Path, required=True)
    parser.add_argument("--repository", type=Path, required=True)
    parser.add_argument("--framework", choices=("net8.0", "net10.0"), required=True)
    parser.add_argument("--core-manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    prepare(parser.parse_args())
