"""Validate the same immutable package artifact before each publication destination."""

import hashlib
import json
import os
import re
from pathlib import Path


def verify(root: Path, expected: dict[str, str]) -> dict[str, str]:
    root = root.resolve()
    if not re.fullmatch(r"0\.1\.0-alpha\.[1-9][0-9]*\.[1-9][0-9]*", expected["version"]):
        raise ValueError("expected an automatically versioned prerelease")
    if not re.fullmatch(r"[0-9a-f]{40}", expected["repository_commit"]):
        raise ValueError("expected a full source commit")
    report = json.loads((root / "results.json").read_text(encoding="utf-8"))
    if (
        report.get("result") != "passed"
        or report.get("mode") != "release"
        or report.get("local_validation") is not False
    ):
        raise ValueError("release manifest is not an independently passed release artifact")
    if (
        report.get("source_unchanged") is not True
        or not report.get("source_manifest_before")
        or not report.get("source_manifest_after")
    ):
        raise ValueError("release manifest does not prove source identity was unchanged")
    if report["source_manifest_before"] != report["source_manifest_after"]:
        raise ValueError("release source manifests differ")
    metadata = report.get("metadata", {})
    for key, value in expected.items():
        if metadata.get(key) != value:
            raise ValueError(f"release metadata mismatch for {key}")
    entries = report.get("package_manifests")
    if (
        not isinstance(entries, list)
        or len(entries) != 2
        or {entry.get("role") for entry in entries} != {"core", "dependency-injection"}
    ):
        raise ValueError(
            "release manifest must contain exactly core and dependency-injection entries"
        )
    package_dir = (root / "packages").resolve()
    actual = {
        item.name
        for item in package_dir.iterdir()
        if item.is_file() and item.suffix in {".nupkg", ".snupkg"}
    }
    expected_files = {entry[key] for entry in entries for key in ("nupkg", "snupkg")}
    if actual != expected_files or len(actual) != 4:
        raise ValueError("package directory does not exactly match manifest archives")
    by_role = {entry["role"]: entry for entry in entries}
    outputs = {}
    for role in ("core", "dependency-injection"):
        entry = by_role[role]
        if entry.get("version") != expected["version"] or entry.get("id") != (
            expected["core_id"] if role == "core" else expected["di_id"]
        ):
            raise ValueError(f"unexpected {role} package identity")
        for kind, digest_key in (("nupkg", "nupkg_sha256"), ("snupkg", "snupkg_sha256")):
            name = entry.get(kind)
            if (
                not isinstance(name, str)
                or Path(name).name != name
                or Path(name).suffix != f".{kind}"
            ):
                raise ValueError(f"unsafe {role} archive path")
            if name != f"{entry['id']}.{entry['version']}.{kind}":
                raise ValueError(f"unexpected {role} archive name")
            archive = package_dir / name
            if archive.is_symlink() or archive.resolve().parent != package_dir:
                raise ValueError(f"unsafe archive location for {name}")
            digest = hashlib.sha256(archive.read_bytes()).hexdigest()
            if digest != entry.get(digest_key):
                raise ValueError(f"SHA256 mismatch for {name}")
        outputs[role] = str(package_dir / entry["nupkg"])
    return outputs


def main() -> None:
    expected = {
        "version": os.environ["RELEASE_VERSION"],
        "core_id": os.environ["NUGET_CORE_PACKAGE_ID"],
        "di_id": os.environ["NUGET_DI_PACKAGE_ID"],
        "authors": os.environ["NUGET_AUTHORS"],
        "repository_url": os.environ["REPOSITORY_URL"],
        "repository_commit": os.environ["REPOSITORY_COMMIT"],
    }
    outputs = verify(Path(os.environ["ARTIFACT_ROOT"]), expected)
    with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
        output.write(f"core_package={outputs['core']}\n")
        output.write(f"di_package={outputs['dependency-injection']}\n")


if __name__ == "__main__":
    main()
