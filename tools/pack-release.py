"""Build and validate the two NuGet packages used by the release workflow.

This helper only creates local package artifacts. It never authenticates or pushes
to a feed. Pipeline metadata is required explicitly; ``--local-validation`` is a
separate mode with deliberately non-publishable IDs and authors.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import time
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path
from urllib.parse import urlparse

try:
    from validate import source_manifest
except ImportError:
    from tools.validate import source_manifest


PACKAGE_ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
SEMVER_PATTERN = re.compile(
    r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)
SHA_PATTERN = re.compile(r"^[0-9a-fA-F]{40}$")
CORE_ASSEMBLY = "LoadingCache"
DI_ASSEMBLY = "LoadingCache.Extensions.DependencyInjection"


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output", required=True, help="Fresh output directory for logs and packages"
    )
    parser.add_argument("--version", help="SemVer 2.0 version")
    parser.add_argument("--core-id", help="NuGet ID for the core package")
    parser.add_argument("--di-id", help="NuGet ID for the DI integration package")
    parser.add_argument("--authors", help="NuGet Authors metadata")
    parser.add_argument("--repository-url", help="Canonical source repository URL")
    parser.add_argument("--repository-commit", help="40-character source revision")
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable")
    parser.add_argument(
        "--local-validation",
        action="store_true",
        help="Use non-publishable local IDs and metadata instead of release identity",
    )
    return parser.parse_args()


def fail(message: str) -> ValueError:
    return ValueError(message)


def validate_package_id(value: str, name: str) -> str:
    if len(value) > 100:
        raise fail(f"{name} must be at most 100 characters")
    if not value or not PACKAGE_ID_PATTERN.fullmatch(value):
        raise fail(
            f"{name} must be a NuGet package ID containing only letters, digits, '.', '-' or '_'"
        )
    return value


def validate_version(value: str) -> str:
    if not SEMVER_PATTERN.fullmatch(value):
        raise fail("version must be canonical SemVer 2.0")
    prerelease = value.partition("-")[2]
    for identifier in prerelease.split("."):
        if identifier.isdigit() and len(identifier) > 1 and identifier.startswith("0"):
            raise fail("numeric prerelease identifiers must not contain leading zeroes")
    return value


def validate_repository_url(value: str) -> str:
    parsed = urlparse(value)
    if (
        parsed.scheme != "https"
        or not parsed.netloc
        or not parsed.path.strip("/")
        or parsed.query
        or parsed.fragment
    ):
        raise fail("repository-url must be a canonical HTTPS repository URL")
    return value


def escape_msbuild_literal(value: str) -> str:
    """Escape expansion sigils while preserving semicolon author separators."""
    return value.replace("%", "%25").replace("$", "%24").replace("@", "%40")


def resolve_metadata(args: argparse.Namespace) -> dict[str, str | None]:
    supplied = {
        "core_id": args.core_id,
        "di_id": args.di_id,
        "authors": args.authors,
        "repository_url": args.repository_url,
        "repository_commit": args.repository_commit,
    }
    if args.local_validation:
        if any(
            supplied[name] is not None
            for name in ("core_id", "di_id", "repository_url", "repository_commit")
        ):
            raise fail(
                "--local-validation cannot be combined with package or repository identity arguments"
            )
        metadata = {
            "core_id": "LoadingCache.LocalValidation",
            "di_id": "LoadingCache.Extensions.DependencyInjection.LocalValidation",
            "authors": args.authors or "LOCAL-VALIDATION-ONLY-AUTHORSHIP-UNCONFIRMED",
            "repository_url": None,
            "repository_commit": None,
            "version": args.version or "0.1.0-alpha.localvalidation",
        }
    else:
        required = {name: value for name, value in supplied.items() if value is None}
        if required or args.version is None:
            missing = [*required, *(["version"] if args.version is None else [])]
            raise fail(f"release metadata is required: {', '.join(missing)}")
        metadata = {**supplied, "version": args.version}

    validate_package_id(str(metadata["core_id"]), "core-id")
    validate_package_id(str(metadata["di_id"]), "di-id")
    if str(metadata["core_id"]).casefold() == str(metadata["di_id"]).casefold():
        raise fail("core-id and di-id must be distinct case-insensitively")
    validate_version(str(metadata["version"]))

    authors = str(metadata["authors"])
    if not authors.strip() or any(character in authors for character in "\r\n\x00"):
        raise fail("authors must be non-empty and contain no control characters")
    if not args.local_validation and "UNCONFIRMED" in authors.upper():
        raise fail("release authors must be confirmed; use --local-validation for test metadata")

    if not args.local_validation:
        repository_url = str(metadata["repository_url"])
        validate_repository_url(repository_url)
        if not SHA_PATTERN.fullmatch(str(metadata["repository_commit"])):
            raise fail("repository-commit must be a 40-character hexadecimal revision")

    return metadata


def write_props(path: Path, metadata: dict[str, str | None]) -> None:
    project = ET.Element("Project")
    properties = ET.SubElement(project, "PropertyGroup")
    values = {
        "LoadingCacheCorePackageId": metadata["core_id"],
        "LoadingCacheDependencyInjectionPackageId": metadata["di_id"],
        "PackageVersion": metadata["version"],
        "Authors": metadata["authors"],
    }
    if metadata["repository_url"] is not None:
        values["RepositoryUrl"] = metadata["repository_url"]
        values["RepositoryCommit"] = metadata["repository_commit"]
    for name, value in values.items():
        ET.SubElement(properties, name).text = escape_msbuild_literal(str(value))
    ET.ElementTree(project).write(path, encoding="utf-8", xml_declaration=True)


def write_nuget_config(path: Path, feed: Path, core_package_id: str) -> None:
    configuration = ET.Element("configuration")
    sources = ET.SubElement(configuration, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="local-release", value=str(feed))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(configuration, "packageSourceMapping")
    local = ET.SubElement(mapping, "packageSource", key="local-release")
    ET.SubElement(local, "package", pattern=core_package_id)
    official = ET.SubElement(mapping, "packageSource", key="nuget.org")
    ET.SubElement(official, "package", pattern="Microsoft.*")
    ET.SubElement(official, "package", pattern="JetBrains.Annotations")
    ET.ElementTree(configuration).write(path, encoding="utf-8", xml_declaration=True)


def verify_source_identity(
    root: Path, metadata: dict[str, str | None], local_validation: bool
) -> None:
    if local_validation:
        return
    result = subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=root, capture_output=True, text=True, check=False
    )
    actual = result.stdout.strip().lower()
    expected = str(metadata["repository_commit"]).lower()
    if result.returncode != 0 or actual != expected:
        raise fail(
            f"checked-out git HEAD {actual or '<unavailable>'} does not match repository-commit {expected}"
        )


def run_command(
    root: Path,
    output: Path,
    name: str,
    command: list[str],
    report: dict[str, object],
) -> None:
    started = time.monotonic()
    log_path = output / f"{name}.log"
    with log_path.open("w", encoding="utf-8") as log:
        try:
            result = subprocess.run(
                command, cwd=root, stdout=log, stderr=subprocess.STDOUT, timeout=900
            )
            exit_code = result.returncode
        except subprocess.TimeoutExpired:
            exit_code = 124
    report.setdefault("commands", []).append(
        {
            "name": name,
            "command": command,
            "exit_code": exit_code,
            "seconds": time.monotonic() - started,
        }
    )
    if exit_code:
        tail = log_path.read_text(encoding="utf-8", errors="replace")[-12000:]
        raise RuntimeError(f"{name} failed with exit {exit_code}\n{tail}")


def package_paths(packages: Path, package_id: str, version: str) -> tuple[Path, Path]:
    archive = packages / f"{package_id}.{version}.nupkg"
    symbols = packages / f"{package_id}.{version}.snupkg"
    if not archive.is_file() or not symbols.is_file():
        raise fail(f"missing expected package artifacts for {package_id} {version}")
    return archive, symbols


def inspect_package(
    archive: Path,
    symbols: Path,
    package_id: str,
    version: str,
    assembly: str,
    metadata: dict[str, str | None],
    role: str,
    require_core_dependency: str | None = None,
) -> dict[str, object]:
    with zipfile.ZipFile(archive) as package:
        files = set(package.namelist())
        expected = {
            f"lib/net8.0/{assembly}.dll",
            f"lib/net8.0/{assembly}.xml",
            "README.md",
            "LICENSE",
            "THIRD_PARTY_NOTICES.md",
        }
        missing = expected - files
        if missing:
            raise fail(f"{archive.name} is missing package assets: {sorted(missing)}")
        nuspec_files = [name for name in files if name.endswith(".nuspec")]
        if len(nuspec_files) != 1:
            raise fail(f"{archive.name} must contain exactly one nuspec")
        nuspec = ET.fromstring(package.read(nuspec_files[0]))
        metadata_node = nuspec.find("{*}metadata")
        if metadata_node is None:
            raise fail(f"{archive.name} has no nuspec metadata")

        def read(name: str) -> str | None:
            return metadata_node.findtext(f"{{*}}{name}")

        if read("id") != package_id or read("version") != version:
            raise fail(f"{archive.name} has unexpected nuspec identity")
        # NuGet serializes the MSBuild semicolon list as a comma-separated
        # nuspec value. Preserve every author character otherwise, including
        # apostrophes and ampersands.
        expected_authors = re.sub(r";\s*", ",", str(metadata["authors"]))
        if read("authors") != expected_authors:
            raise fail(f"{archive.name} has unexpected authors metadata")
        dependencies = [
            (item.attrib.get("id"), item.attrib.get("version"))
            for item in nuspec.findall(".//{*}dependency")
        ]
        if require_core_dependency is None and dependencies:
            raise fail(f"core package has runtime dependencies: {dependencies}")
        if require_core_dependency is not None:
            matching = [item for item in dependencies if item[0] == require_core_dependency]
            if matching != [(require_core_dependency, version)]:
                raise fail(
                    f"DI package must depend on {require_core_dependency} {version}: {dependencies}"
                )
        repository = metadata_node.find("{*}repository")
        if metadata["repository_url"] is not None:
            if repository is None or repository.attrib.get("url") != metadata["repository_url"]:
                raise fail(f"{archive.name} has unexpected repository URL")
            if repository.attrib.get("commit") != metadata["repository_commit"]:
                raise fail(f"{archive.name} has unexpected repository commit")
    with zipfile.ZipFile(symbols) as symbol_package:
        symbol_name = f"lib/net8.0/{assembly}.pdb"
        if symbol_name not in symbol_package.namelist():
            raise fail(f"{symbols.name} is missing {symbol_name}")
    return {
        "role": role,
        "id": package_id,
        "version": version,
        "nupkg": archive.name,
        "snupkg": symbols.name,
        "nupkg_sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
        "snupkg_sha256": hashlib.sha256(symbols.read_bytes()).hexdigest(),
    }


def main() -> int:
    args = parse_arguments()
    root = Path(__file__).resolve().parent.parent
    output = (root / args.output).resolve()
    try:
        metadata = resolve_metadata(args)
    except ValueError as error:
        print(error)
        return 1
    local_validation = args.local_validation
    try:
        verify_source_identity(root, metadata, local_validation)
    except ValueError as error:
        print(error)
        return 1
    output.mkdir(parents=True, exist_ok=False)
    packages = output / "packages"
    packages.mkdir()
    restore_cache = output / "restore-cache"
    restore_cache.mkdir()
    props = output / "pack-metadata.props"
    config = output / "NuGet.Config"
    write_props(props, metadata)
    write_nuget_config(config, packages, str(metadata["core_id"]))
    source_before = source_manifest(root)
    report: dict[str, object] = {
        "schema_version": 1,
        "result": "failed",
        "mode": "local-validation" if local_validation else "release",
        "local_validation": local_validation,
        "metadata": metadata,
        "output": str(output),
        "packages": str(packages),
        "source_manifest_before": source_before,
    }
    common = [
        f"-p:CustomBeforeMicrosoftCommonProps={props}",
        f"-p:ContinuousIntegrationBuild={'false' if local_validation else 'true'}",
        "-p:IsPackable=true",
    ]
    try:
        core = "src/LoadingCache/LoadingCache.csproj"
        di = "src/LoadingCache.Extensions.DependencyInjection/LoadingCache.Extensions.DependencyInjection.csproj"
        run_command(
            root,
            output,
            "core-restore",
            [
                args.dotnet,
                "restore",
                core,
                "--configfile",
                str(config),
                "--packages",
                str(restore_cache),
                "--force-evaluate",
                *common,
            ],
            report,
        )
        run_command(
            root,
            output,
            "core-pack",
            [
                args.dotnet,
                "pack",
                core,
                "-c",
                args.configuration,
                "-o",
                str(packages),
                "--no-restore",
                *common,
            ],
            report,
        )
        run_command(
            root,
            output,
            "di-restore",
            [
                args.dotnet,
                "restore",
                di,
                "--configfile",
                str(config),
                "--packages",
                str(restore_cache),
                "--force-evaluate",
                *common,
            ],
            report,
        )
        run_command(
            root,
            output,
            "di-pack",
            [
                args.dotnet,
                "pack",
                di,
                "-c",
                args.configuration,
                "-o",
                str(packages),
                "--no-restore",
                *common,
            ],
            report,
        )
        expected_archives = {
            f"{metadata['core_id']}.{metadata['version']}.nupkg",
            f"{metadata['core_id']}.{metadata['version']}.snupkg",
            f"{metadata['di_id']}.{metadata['version']}.nupkg",
            f"{metadata['di_id']}.{metadata['version']}.snupkg",
        }
        actual_archives = {
            path.name
            for path in packages.iterdir()
            if path.is_file() and path.suffix in {".nupkg", ".snupkg"}
        }
        if actual_archives != expected_archives:
            raise fail(
                f"package directory archives do not match expected set: {sorted(actual_archives)}"
            )
        core_archive, core_symbols = package_paths(
            packages, str(metadata["core_id"]), str(metadata["version"])
        )
        di_archive, di_symbols = package_paths(
            packages, str(metadata["di_id"]), str(metadata["version"])
        )
        report["package_manifests"] = [
            inspect_package(
                core_archive,
                core_symbols,
                str(metadata["core_id"]),
                str(metadata["version"]),
                CORE_ASSEMBLY,
                metadata,
                role="core",
            ),
            inspect_package(
                di_archive,
                di_symbols,
                str(metadata["di_id"]),
                str(metadata["version"]),
                DI_ASSEMBLY,
                metadata,
                role="dependency-injection",
                require_core_dependency=str(metadata["core_id"]),
            ),
        ]
        report["result"] = "passed"
    except (RuntimeError, OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        report["error"] = str(error)
        print(error)
    source_after = source_manifest(root)
    report["source_manifest_after"] = source_after
    report["source_unchanged"] = source_before == source_after
    if not report["source_unchanged"]:
        report["result"] = "failed"
        report["error"] = "source changed during package validation"
        print(report["error"])
    (output / "results.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return 0 if report["result"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
