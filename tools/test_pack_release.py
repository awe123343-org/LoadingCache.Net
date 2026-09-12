import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile


MODULE_PATH = Path(__file__).with_name("pack-release.py")
SPEC = importlib.util.spec_from_file_location("pack_release", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
PACK_RELEASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PACK_RELEASE)
inspect_package = PACK_RELEASE.inspect_package
validate_package_id = PACK_RELEASE.validate_package_id
validate_version = PACK_RELEASE.validate_version
write_props = PACK_RELEASE.write_props


class PackReleaseTests(unittest.TestCase):
    def test_rejects_stable_and_leading_zero_versions(self) -> None:
        with self.assertRaises(ValueError):
            validate_version("1.0.0")
        with self.assertRaises(ValueError):
            validate_version("0.1.0-alpha.01")
        with self.assertRaises(ValueError):
            validate_version("0.1.0-alpha+build")

    def test_rejects_oversized_package_id(self) -> None:
        with self.assertRaises(ValueError):
            validate_package_id("A" * 101, "core-id")

    def test_write_props_escapes_expansion_literals_and_preserves_separator(self) -> None:
        metadata = {
            "core_id": "LoadingCache.LocalValidation",
            "di_id": "LoadingCache.Extensions.DependencyInjection.LocalValidation",
            "authors": "O'Brien; $(DoNotExpand); @(Items); 100%",
            "version": "0.1.0-alpha.localvalidation",
            "repository_url": None,
            "repository_commit": None,
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "pack-metadata.props"
            write_props(path, metadata)
            text = path.read_text(encoding="utf-8")
            self.assertIn("O'Brien; %24(DoNotExpand); %40(Items); 100%25", text)
            self.assertNotIn("$(DoNotExpand)", text)
            self.assertNotIn("@(Items)", text)
            self.assertEqual(
                ET.parse(path).findtext(".//Authors"),
                "O'Brien; %24(DoNotExpand); %40(Items); 100%25",
            )

    def test_inspect_package_validates_archive_manifest(self) -> None:
        package_id = "LoadingCache.LocalValidation"
        version = "0.1.0-alpha.localvalidation"
        metadata = {
            "core_id": package_id,
            "di_id": "LoadingCache.Extensions.DependencyInjection.LocalValidation",
            "authors": "Maintainer",
            "version": version,
            "repository_url": None,
            "repository_commit": None,
        }
        nuspec = f"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{package_id}</id>
    <version>{version}</version>
    <authors>Maintainer</authors>
    <dependencies />
  </metadata>
</package>"""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive = root / f"{package_id}.{version}.nupkg"
            symbols = root / f"{package_id}.{version}.snupkg"
            with zipfile.ZipFile(archive, "w") as package:
                for name in (
                    "lib/net8.0/LoadingCache.dll",
                    "lib/net8.0/LoadingCache.xml",
                    "README.md",
                    "LICENSE",
                    "THIRD_PARTY_NOTICES.md",
                ):
                    package.writestr(name, b"artifact")
                package.writestr(f"{package_id}.nuspec", nuspec)
            with zipfile.ZipFile(symbols, "w") as symbol_package:
                symbol_package.writestr("lib/net8.0/LoadingCache.pdb", b"symbols")
            manifest = inspect_package(
                archive,
                symbols,
                package_id,
                version,
                "LoadingCache",
                metadata,
                role="core",
            )
            self.assertEqual(manifest["role"], "core")
            self.assertEqual(manifest["id"], package_id)


if __name__ == "__main__":
    unittest.main()
