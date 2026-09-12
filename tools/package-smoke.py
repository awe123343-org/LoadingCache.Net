"""Pack and execute an isolated local NuGet consumer. Never uploads a package.

uv run --no-project python tools/package-smoke.py --output artifacts/package/<run>
The package uses an explicitly local ID and unconfirmed-author marker. Release
metadata and Source Link with a real repository commit remain release gates.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import time
import xml.etree.ElementTree as ET
import zipfile

from validate import source_manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    output = (root / args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    feed = output / "feed"
    feed.mkdir()
    packages = output / "packages"
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="local-validation", value=str(feed))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    local = ET.SubElement(mapping, "packageSource", key="local-validation")
    ET.SubElement(local, "package", pattern="LoadingCache.LocalValidation")
    official = ET.SubElement(mapping, "packageSource", key="nuget.org")
    ET.SubElement(official, "package", pattern="Microsoft.*")
    config_path = output / "NuGet.Config"
    ET.ElementTree(config).write(config_path, encoding="unicode", xml_declaration=True)
    runtime8 = root / "artifacts/runtime8/dotnet"
    before = source_manifest(root)
    report = {"commands": [], "source_before": before, "purpose": "local validation only"}

    def run(name: str, command: list[str]) -> None:
        print(f"Running {name}", flush=True)
        started = time.monotonic()
        with (output / f"{name}.log").open("w") as log:
            try:
                status = subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT, timeout=600).returncode
            except subprocess.TimeoutExpired:
                status = 124
        report["commands"].append({"name": name, "command": command, "exit_code": status, "seconds": time.monotonic() - started})
        print(f"{name}: exit {status}", flush=True)
        if status:
            print((output / f"{name}.log").read_text()[-12000:], flush=True)
            raise RuntimeError(f"{name} failed with exit {status}")

    try:
        run("pack", ["dotnet", "pack", "src/LoadingCache/LoadingCache.csproj", "-c", "Release", "-o", str(feed),
            "-p:IsPackable=true", "-p:PackageId=LoadingCache.LocalValidation", "-p:PackageVersion=0.1.0-alpha.localvalidation",
            "-p:Authors=LOCAL-VALIDATION-ONLY-AUTHORSHIP-UNCONFIRMED"])
        archive = feed / "LoadingCache.LocalValidation.0.1.0-alpha.localvalidation.nupkg"
        symbols = feed / "LoadingCache.LocalValidation.0.1.0-alpha.localvalidation.snupkg"
        with zipfile.ZipFile(archive) as package:
            expected = {"lib/net8.0/LoadingCache.dll", "lib/net8.0/LoadingCache.xml", "README.md", "LICENSE", "THIRD_PARTY_NOTICES.md"}
            missing = expected.difference(package.namelist())
            if missing:
                raise RuntimeError(f"Missing package assets: {sorted(missing)}")
            manifest = ET.fromstring(package.read("LoadingCache.LocalValidation.nuspec"))
            dependencies = manifest.findall(".//{*}dependency")
            if dependencies:
                raise RuntimeError(f"Core must have no runtime package dependencies: {[item.attrib for item in dependencies]}")
            report["package_files"] = package.namelist()
            report["nuspec"] = package.read("LoadingCache.LocalValidation.nuspec").decode()
        with zipfile.ZipFile(symbols) as package:
            if "lib/net8.0/LoadingCache.pdb" not in package.namelist():
                raise RuntimeError("Missing portable PDB in symbol package")
        report["artifact_sha256"] = {item.name: hashlib.sha256(item.read_bytes()).hexdigest() for item in (archive, symbols)}
        project = "tests/LoadingCache.PackageTests/LoadingCache.PackageTests.csproj"
        run("consumer-restore", ["dotnet", "restore", project, "--configfile", str(config_path), "--packages", str(packages), "--force"])
        assets = json.loads((root / "tests/LoadingCache.PackageTests/obj/project.assets.json").read_text())
        library = assets["libraries"]["LoadingCache.LocalValidation/0.1.0-alpha.localvalidation"]
        if library["type"] != "package":
            raise RuntimeError("Consumer did not resolve the packed asset")
        run("consumer-build", ["dotnet", "build", project, "-c", "Release", "--no-restore"])
        run("consumer-net8", [str(runtime8), str(root / "tests/LoadingCache.PackageTests/bin/Release/net8.0/LoadingCache.PackageTests.dll")])
        run("consumer-net10", ["dotnet", "run", "--project", project, "-c", "Release", "--no-build", "--no-restore", "-f", "net10.0"])
        report["result"] = "passed"
    except (RuntimeError, OSError, KeyError, zipfile.BadZipFile) as error:
        report["result"] = "failed"
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
