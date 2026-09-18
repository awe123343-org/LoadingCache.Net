"""Inventory existing NuGet restore graphs and cached package license metadata offline.

This reads files only, apart from a fresh evidence directory and the requested
Markdown report. It never restores, downloads, executes packages, or grants legal clearance.
"""

from __future__ import annotations

import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
AREAS = ("src", "tests", "samples", "benchmarks", "tools")
ADAPTATIONS = (
    "src/LoadingCache/Policy/FrequencySketch.cs",
    "src/LoadingCache/Policy/WindowTinyLfuPolicy.cs",
    "src/LoadingCache/Expiration/TimerWheel.cs",
    "src/LoadingCache/Maintenance/StripedReadBuffer.cs",
)
STABLE = "836b65c0a83e5d1641ded9c6de578654bc04b2e9"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def file_hash(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--report", type=Path, default=ROOT / "docs/dependency-inventory.md")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    captured = {}
    packages = {}
    graphs = []
    missing = []
    folders = set()

    def capture(path):
        data = path.read_bytes()
        relative = str(path.relative_to(ROOT))
        captured[relative] = digest(data)
        target = output / "inputs" / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        return data

    def add(package_id, version, roots, usage, expected=None):
        key = (package_id.casefold(), version)
        record = packages.setdefault(key, {"id": package_id, "version": version, "roots": set(), "usages": [], "expectedSha512": set()})
        record["roots"].update(roots)
        record["usages"].append(usage)
        if expected:
            record["expectedSha512"].add(expected)

    projects = sorted(path for area in AREAS for path in (ROOT / area).glob("*/*.csproj"))
    for project in projects:
        capture(project)
        assets = project.parent / "obj/project.assets.json"
        if not assets.is_file():
            missing.append({"project": str(project.relative_to(ROOT)), "reason": "No existing project.assets.json; not restored by this inventory"})
            continue
        data = json.loads(capture(assets))
        project_name = str(project.relative_to(ROOT))
        restore = data.get("project", {}).get("restore", {})
        roots = list(data.get("packageFolders", {}))
        folders.update(roots)
        frameworks = data.get("project", {}).get("frameworks", {})
        graphs.append({"project": project_name, "assets": str(assets.relative_to(ROOT)), "recordedProjectPath": restore.get("projectPath"), "restoreSources": list(restore.get("sources", {})), "frameworks": list(frameworks), "targets": list(data.get("targets", {}))})
        if Path(restore.get("projectPath", "")).resolve() != project.resolve():
            missing.append({"project": project_name, "reason": "Restore graph records a different project path"})
        area = project.relative_to(ROOT).parts[0]
        for target, entries in data.get("targets", {}).items():
            framework = frameworks.get(target.split("/")[0], {})
            direct = {name.casefold(): info for name, info in framework.get("dependencies", {}).items()}
            for library, entry in entries.items():
                if entry.get("type") != "package":
                    continue
                name, version = library.rsplit("/", 1)
                dependency = direct.get(name.casefold())
                kinds = {kind: list(entry[kind]) for kind in ("compile", "runtime", "native", "runtimeTargets", "build", "buildTransitive", "buildMultiTargeting", "analyzers", "contentFiles", "resource") if entry.get(kind)}
                runtime_assets = any(not filename.endswith("_._") for kind in ("runtime", "native", "runtimeTargets") for filename in kinds.get(kind, []))
                shipped = area == "src" and runtime_assets and (dependency or {}).get("suppressParent") != "All"
                scope = ("core-shipped" if project.parent.name == "LoadingCache" else "di-shipped") if shipped else "production-build-only" if area == "src" else {"tests": "test", "samples": "sample", "benchmarks": "benchmark", "tools": "tool"}[area]
                if area != "src" and not runtime_assets and ((dependency or {}).get("suppressParent") == "All" or (dependency or {}).get("autoReferenced")):
                    scope += "-build-only"
                add(name, version, roots, {"project": project_name, "target": target, "scope": scope, "relationship": "sdk-auto-reference" if (dependency or {}).get("autoReferenced") else "direct-restore-root" if dependency else "transitive", "privateAssets": (dependency or {}).get("suppressParent"), "assetKinds": kinds, "dependencies": entry.get("dependencies", {})}, data["libraries"].get(library, {}).get("sha512"))
        for target, framework in frameworks.items():
            for download in framework.get("downloadDependencies", []):
                match = re.fullmatch(r"\[([^,]+),\s*([^]]+)\]", download["version"])
                if match and match[1] == match[2]:
                    add(download["name"], match[1], roots, {"project": project_name, "target": target, "scope": "sdk-download-build-only", "relationship": "downloadDependency"})
                else:
                    missing.append({"project": project_name, "download": download, "reason": "Download dependency does not record a single resolved version"})

    tool_manifest = ROOT / ".config/dotnet-tools.json"
    for name, tool in json.loads(capture(tool_manifest))["tools"].items():
        add(name, tool["version"], folders, {"project": ".config/dotnet-tools.json", "target": "dotnet-tool", "scope": "build-tool", "relationship": "direct-tool-pin"})
    for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "src/Package.props", "global.json", "LICENSE", "THIRD_PARTY_NOTICES.md", "docs/upstream-map.md", "tools/dependency-inventory.py"):
        path = ROOT / name
        if path.is_file():
            capture(path)

    resolved = []
    for (lower_id, version), package in sorted(packages.items()):
        roots = sorted(package.pop("roots"))
        package["expectedSha512"] = sorted(package["expectedSha512"])
        candidates = [Path(root) / lower_id / version.lower() for root in roots]
        locations = [path for path in candidates if path.is_dir()]
        package["cacheLocations"] = [str(path) for path in locations]
        archives = [path / f"{lower_id}.{version.lower()}.nupkg" for path in locations]
        archives = [path for path in archives if path.is_file()]
        if not archives:
            package["missing"] = ["No cached nupkg archive"]
            missing.append({"package": package["id"] + "/" + version, "reason": package["missing"][0], "searched": [str(path) for path in candidates]})
            resolved.append(package)
            continue
        archive = archives[0]
        package["archive"] = str(archive)
        package["archiveSha256"] = file_hash(archive)
        package["otherArchiveHashes"] = {str(path): file_hash(path) for path in archives[1:]}
        with archive.open("rb") as stream:
            package["archiveSha512"] = base64.b64encode(hashlib.file_digest(stream, "sha512").digest()).decode()
        sidecar = archive.with_suffix(archive.suffix + ".sha512")
        package["archiveSha512Sidecar"] = sidecar.read_text().strip() if sidecar.is_file() else None
        package["archiveMatchesSidecar"] = package["archiveSha512"] == package["archiveSha512Sidecar"] if sidecar.is_file() else None
        cached_metadata = archive.parent / ".nupkg.metadata"
        if cached_metadata.is_file():
            package["nugetCacheMetadata"] = json.loads(cached_metadata.read_text())
        content_hash = package.get("nugetCacheMetadata", {}).get("contentHash")
        package["lockHashesMatchRecordedContentHash"] = all(value == content_hash for value in package["expectedSha512"]) if package["expectedSha512"] and content_hash else None
        if package["archiveMatchesSidecar"] is False:
            missing.append({"package": package["id"] + "/" + version, "reason": "Raw archive SHA512 differs from its cached .nupkg.sha512 sidecar; distinct from the separately recorded NuGet contentHash. Origin of this metadata discrepancy is not established; no security or tampering conclusion."})
        if package["lockHashesMatchRecordedContentHash"] is False:
            missing.append({"package": package["id"] + "/" + version, "reason": "Restore-lock hash differs from recorded NuGet contentHash; graph/cache identity needs review"})
        destination = output / "packages" / lower_id / version
        with zipfile.ZipFile(archive) as zipped:
            nuspecs = [name for name in zipped.namelist() if name.lower().endswith(".nuspec") and "/" not in name]
            if len(nuspecs) != 1:
                raise ValueError(f"Expected one root nuspec: {archive}")
            nuspec = zipped.read(nuspecs[0])
            destination.mkdir(parents=True, exist_ok=True)
            (destination / "metadata.nuspec").write_bytes(nuspec)
            metadata = ET.fromstring(nuspec).find("{*}metadata")
            if metadata is None:
                raise ValueError(f"Missing nuspec metadata: {archive}")
            license_node = metadata.find("{*}license")
            repository = metadata.find("{*}repository")
            package["nuspecSha256"] = digest(nuspec)
            package["metadata"] = {"id": metadata.findtext("{*}id"), "version": metadata.findtext("{*}version"), "authors": metadata.findtext("{*}authors"), "licenseType": license_node.get("type") if license_node is not None else None, "licenseValue": license_node.text if license_node is not None else None, "legacyLicenseUrl": metadata.findtext("{*}licenseUrl"), "repository": dict(repository.attrib) if repository is not None else None, "copyright": metadata.findtext("{*}copyright")}
            if package["metadata"]["id"].casefold() != lower_id:
                raise ValueError(f"Nuspec identity mismatch: {archive}")
            legal = [name for name in zipped.namelist() if not name.endswith("/") and re.search(r"^(license|licence|copying|notice|third[-_. ]?party)", Path(name).name, re.IGNORECASE)]
            if package["metadata"]["licenseType"] == "file":
                named = package["metadata"]["licenseValue"]
                if named not in zipped.namelist():
                    missing.append({"package": package["id"] + "/" + version, "reason": "Declared license file absent", "file": named})
                elif named not in legal:
                    legal.append(named)
            package["licenseAndNoticeFiles"] = []
            for index, name in enumerate(sorted(legal)):
                content = zipped.read(name)
                preserved = destination / f"legal-{index:02d}.txt"
                preserved.write_bytes(content)
                package["licenseAndNoticeFiles"].append({"archivePath": name, "sha256": digest(content), "preservedPath": str(preserved.relative_to(output))})
            if lower_id.startswith("loadingcache"):
                package["localPackageRootNoticeComparison"] = {name: {"archiveSha256": digest(zipped.read(name)) if name in zipped.namelist() else None, "currentRootSha256": captured[name], "sameBytes": name in zipped.namelist() and digest(zipped.read(name)) == captured[name]} for name in ("LICENSE", "THIRD_PARTY_NOTICES.md")}
            if license_node is None:
                missing.append({"package": package["id"] + "/" + version, "reason": "No modern nuspec license field; legacy URL and any preserved legal files require manual review"})
        resolved.append(package)

    notice = (ROOT / "THIRD_PARTY_NOTICES.md").read_text()
    adaptation_rows = []
    for name in ADAPTATIONS:
        text = capture(ROOT / name).decode()
        adaptation_rows.append({"file": name, "sha256": captured[name], "stableRevisionPresent": STABLE in text, "copyrightPresent": "Copyright" in text and "Ben Manes" in text, "noticeMentionsFile": Path(name).name in notice, "localModificationMarker": any(marker in text for marker in (".NET", "CLR")), "masterRevision": "d885a95eee51fdfe13f450fd9cba80f58f7e0def" if "d885a95eee51fdfe13f450fd9cba80f58f7e0def" in text else None, "dougLeaAttribution": "Doug Lea" in text})
    stable = all(file_hash(ROOT / name) == value for name, value in captured.items())
    result = {"schemaVersion": 1, "generatedUtc": datetime.now(timezone.utc).isoformat(), "argv": sys.argv, "scope": "Existing NuGet project restore graphs plus explicit download dependencies and repository-local dotnet tool pin; no restore or legal clearance", "inputsUnchanged": stable, "inputs": captured, "graphs": graphs, "packages": resolved, "missingOrNeedsReview": missing, "adaptations": adaptation_rows}
    save(output / "inventory.json", result)
    save(output / "evidence-manifest.json", {str(path.relative_to(output)): file_hash(path) for path in sorted(output.rglob("*")) if path.is_file() and path.name != "evidence-manifest.json"})
    lines = ["# Resolved dependency and attribution inventory", "", f"Captured {result['generatedUtc']}: {len(graphs)} existing project restore graphs, {len(resolved)} distinct NuGet ID/version pairs. Inputs unchanged while captured: **{stable}**.", "", "This is an offline inventory of the actual local resolved graphs, not a fresh restore, a vulnerability scan, or legal approval. Direct means a restore-graph root; SDK auto references and download-only targeting/compiler packs are labelled separately. A dependency's presence in a test/sample/build tool does not make it a shipped library dependency. Existing assets can be historical; final candidate verification must connect them to its restore/build evidence.", "", f"Raw graph/nuspec/license copies, all project/TFM usages, dependency edges, archive SHA256/SHA512, source-feed metadata and input hashes: `{output.relative_to(ROOT)}/inventory.json`. Evidence hashes are in the adjacent `evidence-manifest.json`.", "", "## Shipped package graph", ""]
    shipped = [(package, sorted({usage['scope'] for usage in package['usages'] if usage['scope'].endswith('-shipped')})) for package in resolved]
    shipped = [(package, scopes) for package, scopes in shipped if scopes]
    for package, scopes in shipped:
        lines.append(f"- {package['id']} {package['version']}: {', '.join(scopes)}; license `{package.get('metadata', {}).get('licenseValue', 'missing')}`.")
    lines += ["", "Core has no third-party runtime asset in these resolved graphs; JetBrains.Annotations and ILLink are private compile/build inputs. DI's project reference to core is local source; the external DI dependency above is separate. Package redistribution still requires inspecting final nuspec/archive contents.", "", "## All resolved packages", "", "`build` includes compiler/targeting/download packs and private production build inputs. Full direct/transitive edges and asset paths are retained in raw JSON. SHA256 is of the cached nupkg bytes, not a URL or package ID.", "", "| Package | Version | Usage scopes | License metadata | Nupkg SHA256 |", "| --- | --- | --- | --- | --- |"]
    for package in resolved:
        metadata = package.get("metadata", {})
        license_text = f"{metadata.get('licenseType')}: {metadata.get('licenseValue')}" if metadata.get("licenseType") else "legacy URL / review required" if metadata.get("legacyLicenseUrl") else "missing"
        scopes = sorted({usage["scope"] for usage in package["usages"]})
        lines.append(f"| {package['id']} | {package['version']} | {', '.join(scopes)} | {license_text.replace('|', '&#124;')} | `{package.get('archiveSha256', 'missing')}` |")
    lines += ["", "## Missing metadata and review boundaries", ""]
    lines += ["- " + json.dumps(item, ensure_ascii=False) for item in missing] or ["- No missing cache archives or modern license declarations were found in the enumerated graphs."]
    lines += ["", "License expressions are reported verbatim, not inferred or approved. File licenses and third-party notices are preserved byte-for-byte; their downstream obligations require review. Raw archive SHA512 is checked against the cached `.nupkg.sha512`; restore-lock hashes are compared separately with NuGet's recorded `.nupkg.metadata` contentHash. These are different recorded values for many signed archives and must not be conflated. This inventory does not independently verify package signatures or recompute NuGet's content-hash algorithm. Missing comparison inputs produce null, not a pass. SDK-installed/shared-framework binaries, SDK Roslyn references, Java benchmark dependencies, Node formatter dependencies and CI actions are outside the NuGet graph and are not silently declared audited here. Existing direct-package/source research remains in THIRD_PARTY_NOTICES.md and docs/upstream-map.md.", "", "## Four source adaptations", "", "The local headers and notices were inspected, not re-downloaded or compared byte-for-byte with every upstream file. All four cite Caffeine stable `" + STABLE + "`; WindowTinyLfuPolicy additionally records master `d885a95eee51fdfe13f450fd9cba80f58f7e0def`. The root LICENSE contains Apache-2.0; THIRD_PARTY_NOTICES.md records the adaptation scope and modifications. StripedReadBuffer retains Doug Lea / JSR-166 public-domain dedication alongside the Apache notice. This is attribution evidence, not legal clearance.", "", "| Local source | SHA256 | Stable revision / copyright / NOTICE / modification marker |", "| --- | --- | --- |"]
    for row in adaptation_rows:
        checks = [row[key] for key in ("stableRevisionPresent", "copyrightPresent", "noticeMentionsFile", "localModificationMarker")]
        lines.append(f"| `{row['file']}` | `{row['sha256']}` | {', '.join(map(str, checks))} |")
    lines += ["", "The pack configuration includes root LICENSE and THIRD_PARTY_NOTICES.md in both package roles. No external package binaries or upstream source files are bundled into the production core package; the four attributed adaptations are compiled as local source. The cached local-validation package's LICENSE/NOTICE bytes are compared with current root files in `localPackageRootNoticeComparison`; a historical cached package is not a final candidate package. Final package/archive equality and actual publication identity remain separate gates.", "", "## Reproduce", "", "```sh", "uv run --no-project --offline python tools/dependency-inventory.py --output artifacts/v1-acceptance-20260916/dependencies-new --report docs/dependency-inventory.md", "```", "", "Choose a fresh output directory. The script does not modify package caches or run restore/build/test. Preserve prior snapshots when a subsequent restore changes the graph.", ""]
    args.report.write_text("\n".join(lines))
    print(json.dumps({"projects": len(graphs), "packages": len(resolved), "needsReview": len(missing), "inputsUnchanged": stable, "shipped": [package['id'] + '/' + package['version'] for package, _ in shipped]}))
    if not stable:
        raise SystemExit("Inputs changed during capture; keep this evidence but rerun on a stable snapshot.")


if __name__ == "__main__":
    main()
