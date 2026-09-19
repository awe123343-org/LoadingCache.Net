import copy
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from verify_release_artifact import verify


class ReleaseArtifactTests(unittest.TestCase):
    def test_stable_artifact(self):
        self.test_publication_requires_exact_successful_source_and_archives("0.1.0")

    def test_publication_requires_exact_successful_source_and_archives(
        self, version="0.1.0-alpha.1.2"
    ):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            packages = root / "packages"
            packages.mkdir()
            expected = {
                "version": version,
                "core_id": "LoadingCache.Net",
                "di_id": "LoadingCache.Net.Extensions.DependencyInjection",
                "authors": "Maintainer",
                "repository_url": "https://github.com/awe123343-org/LoadingCache.Net",
                "repository_commit": "a" * 40,
            }
            entries = []
            for role, identity in (("core", "core_id"), ("dependency-injection", "di_id")):
                entry = {"role": role, "id": expected[identity], "version": expected["version"]}
                for kind in ("nupkg", "snupkg"):
                    name = f"{entry['id']}.{entry['version']}.{kind}"
                    content = name.encode()
                    (packages / name).write_bytes(content)
                    entry[kind] = name
                    entry[kind + "_sha256"] = hashlib.sha256(content).hexdigest()
                entries.append(entry)
            report = {
                "result": "passed",
                "mode": "release",
                "local_validation": False,
                "source_unchanged": True,
                "source_manifest_before": {"source.cs": "digest"},
                "source_manifest_after": {"source.cs": "digest"},
                "metadata": expected,
                "package_manifests": entries,
            }

            def save(value):
                (root / "results.json").write_text(json.dumps(value))

            save(report)
            self.assertEqual(set(verify(root, expected)), {"core", "dependency-injection"})
            for key, value in (
                ("result", "failed"),
                ("mode", "local"),
                ("local_validation", True),
                ("source_unchanged", False),
                ("source_manifest_after", {"source.cs": "changed"}),
                ("package_manifests", entries[:1]),
            ):
                with self.subTest(field=key):
                    altered = copy.deepcopy(report)
                    altered[key] = value
                    save(altered)
                    with self.assertRaises(ValueError):
                        verify(root, expected)
            save(report)
            for key, value in (
                ("version", "1.0.0"),
                ("version", "0.1.0-alpha.1.3"),
                ("repository_commit", "b" * 40),
                ("repository_commit", "main"),
                ("authors", "Someone else"),
            ):
                with self.subTest(expected=key, value=value):
                    with self.assertRaises(ValueError):
                        verify(root, dict(expected, **{key: value}))
            for entry in entries:
                for kind in ("nupkg", "snupkg"):
                    archive = packages / entry[kind]
                    original = archive.read_bytes()
                    with self.subTest(tampered=archive.name):
                        archive.write_bytes(b"tampered")
                        with self.assertRaisesRegex(ValueError, "SHA256 mismatch"):
                            verify(root, expected)
                        archive.unlink()
                        with self.assertRaisesRegex(ValueError, "exactly match"):
                            verify(root, expected)
                        archive.write_bytes(original)
            unexpected = packages / "extra.nupkg"
            unexpected.touch()
            with self.assertRaisesRegex(ValueError, "exactly match"):
                verify(root, expected)
            unexpected.unlink()
            altered = copy.deepcopy(report)
            altered["package_manifests"][0]["id"] = "Wrong.Identity"
            save(altered)
            with self.assertRaisesRegex(ValueError, "identity"):
                verify(root, expected)
            save(report)
            self.assertEqual(
                verify(root, expected)["core"], str((packages / entries[0]["nupkg"]).resolve())
            )


if __name__ == "__main__":
    unittest.main()
