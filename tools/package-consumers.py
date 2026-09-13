"""Restore and execute core and DI consumers using exact local NuGet archives.

No ProjectReference, shared package cache, credentials, or package upload is used.
The DI consumer references only the DI package, exercising its core dependency.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import time
import xml.etree.ElementTree as ET

from validate import source_manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("feed", "output", "core-id", "di-id", "version"):
        parser.add_argument(f"--{name}", required=True)
    parser.add_argument("--runtime8-host", default="dotnet")
    parser.add_argument("--runtime10-host", default="dotnet")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    output = (root / args.output).resolve()
    feed = (root / args.feed).resolve()
    output.mkdir(parents=True, exist_ok=False)

    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="local", value=str(feed))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    local = ET.SubElement(mapping, "packageSource", key="local")
    for name in (args.core_id, args.di_id):
        ET.SubElement(local, "package", pattern=name)
    official = ET.SubElement(mapping, "packageSource", key="nuget.org")
    ET.SubElement(official, "package", pattern="Microsoft.*")
    config_path = output / "NuGet.Config"
    ET.ElementTree(config).write(config_path, encoding="unicode", xml_declaration=True)
    before = source_manifest(root)
    report = {"result": "failed", "commands": [], "archives": {}, "source_before": before}

    def run(name: str, command: list[str]) -> None:
        started = time.monotonic()
        with (output / f"{name}.log").open("w") as log:
            try:
                status = subprocess.run(
                    command, cwd=root, stdout=log, stderr=subprocess.STDOUT, timeout=600
                ).returncode
            except subprocess.TimeoutExpired:
                status = 124
        report["commands"].append({
            "name": name, "command": command, "exit_code": status,
            "seconds": time.monotonic() - started,
        })
        print(f"{name}: exit {status}", flush=True)
        if status:
            print((output / f"{name}.log").read_text()[-12000:], flush=True)
            raise RuntimeError(f"{name} failed")

    try:
        for role, linked_sources in {
            "Core": [
                "tests/LoadingCache.PackageTests/Program.cs",
                "tests/LoadingCache.ConsumerSmoke/LoaderContractSmoke.cs",
                "tests/LoadingCache.ConsumerSmoke/NativePolicySmoke.cs",
            ],
            "DI": ["samples/LoadingCache.DiAotSmoke/Program.cs"],
        }.items():
            directory = output / role
            directory.mkdir()
            project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
            properties = ET.SubElement(project, "PropertyGroup")
            for key, value in {
                "OutputType": "Exe", "TargetFramework": "", "TargetFrameworks": "net8.0;net10.0",
                "ManagePackageVersionsCentrally": "false", "IsPackable": "false",
                "GenerateDocumentationFile": "true",
            }.items():
                ET.SubElement(properties, key).text = value
            items = ET.SubElement(project, "ItemGroup")
            for source in linked_sources:
                ET.SubElement(items, "Compile", Include=str(root / source), Link=Path(source).name)
            package_id = args.core_id if role == "Core" else args.di_id
            ET.SubElement(items, "PackageReference", Include=package_id, Version=args.version)
            if role == "DI":
                ET.SubElement(
                    items, "PackageReference", Include="Microsoft.Extensions.DependencyInjection",
                    Version="10.0.12",
                )
            path = directory / f"{role}Consumer.csproj"
            ET.ElementTree(project).write(path, encoding="unicode", xml_declaration=True)
            run(role + "-restore", [
                "dotnet", "restore", str(path), "--configfile", str(config_path),
                "--packages", str(output / "packages"), "--force",
            ])
            assets = json.loads((directory / "obj/project.assets.json").read_text())
            for dependency in [args.core_id] + ([args.di_id] if role == "DI" else []):
                library = assets["libraries"][dependency + "/" + args.version]
                if library["type"] != "package":
                    raise RuntimeError(f"{role} resolved {dependency} without a package")
                archive_name = f"{dependency.lower()}.{args.version.lower()}.nupkg"
                cached = output / "packages" / library["path"] / archive_name
                archive = feed / f"{dependency}.{args.version}.nupkg"
                digest = hashlib.sha256(archive.read_bytes()).hexdigest()
                if hashlib.sha256(cached.read_bytes()).hexdigest() != digest:
                    raise RuntimeError(f"{role} restored a different archive for {dependency}")
                report["archives"][archive.name] = digest
            run(role + "-build", ["dotnet", "build", str(path), "-c", "Release", "--no-restore"])
            for framework, host in [
                ("net8.0", args.runtime8_host), ("net10.0", args.runtime10_host),
            ]:
                binary = directory / "bin/Release" / framework / f"{role}Consumer.dll"
                run(role + "-" + framework, [host, str(binary)])
        report["result"] = "passed"
    except (RuntimeError, OSError, KeyError, ValueError) as error:
        report["error"] = str(error)
        print(error, flush=True)
    after = source_manifest(root)
    report["source_unchanged"] = before == after
    if before != after:
        report["source_after"] = after
        report["result"] = "source changed during validation"
    (output / "results.json").write_text(json.dumps(report, indent=2) + "\n")
    return 0 if report["result"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
