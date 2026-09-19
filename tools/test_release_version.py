import unittest

from release_version import select


class ReleaseVersionTests(unittest.TestCase):
    def test_first_and_later_official_releases(self):
        self.assertEqual(select("0.1.0", None, "push", "1", "1", []), "0.1.0")
        self.assertEqual(select("0.2.0", "0.1.0", "push", "2", "1", ["v0.1.0"]), "0.2.0")
        self.assertIsNone(select("0.1.0", "0.1.0", "push", "2", "1", ["v0.1.0"]))

    def test_explicit_official_retry(self):
        self.assertEqual(select("0.1.0", None, "official_retry", "12", "2", []), "0.1.0")
        self.assertEqual(select("0.1.0", None, "official_retry", "12", "2", ["v0.1.0"]), "0.1.0")

    def test_manual_uses_next_patch(self):
        self.assertEqual(
            select("0.1.0", None, "workflow_dispatch", "12", "2", []), "0.1.1-alpha.12.2"
        )

    def test_rejects_regression_duplicate_and_invalid_input(self):
        for base, previous, event, run, attempt, tags in [
            ("0.1.0", "0.2.0", "push", "1", "1", []),
            ("0.1.0", None, "push", "1", "1", ["v1.0.0"]),
            ("01.0.0", None, "push", "1", "1", []),
            ("1.0.0-alpha", None, "push", "1", "1", []),
            ("1.0.0", None, "pull_request", "1", "1", []),
            ("1.0.0", None, "workflow_dispatch", "01", "1", []),
        ]:
            with self.subTest(base=base, event=event, tags=tags):
                with self.assertRaises(ValueError):
                    select(base, previous, event, run, attempt, tags)
