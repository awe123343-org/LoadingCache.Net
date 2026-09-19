import subprocess
import unittest
from unittest.mock import patch

from release_tag import ensure_tag


class ReleaseTagTests(unittest.TestCase):
    sha = "a" * 40

    def ref(self, sha=None, kind="commit"):
        return [{"ref": "refs/tags/v0.1.0", "object": {"type": kind, "sha": sha or self.sha}}]

    @patch("release_tag.api")
    def test_reserve_then_verify(self, api):
        api.side_effect = [[], {"object": {"sha": self.sha}}, {}, self.ref(), [[]]]
        ensure_tag("owner/repo", "0.1.0", self.sha, create=True)
        self.assertIn("POST", api.call_args_list[2].args)
        self.assertEqual(api.call_count, 5)

    @patch("release_tag.api")
    def test_same_source_retry_and_later_main_change(self, api):
        api.side_effect = [self.ref(), [[]]]
        ensure_tag("owner/repo", "0.1.0", self.sha, create=True)
        self.assertEqual(api.call_count, 2)

    @patch("release_tag.api")
    def test_missing_conflicting_annotated_or_existing_release_fail(self, api):
        for responses in (
            [[]],
            [self.ref("b" * 40)],
            [self.ref(kind="tag")],
            [self.ref(), [[], [{"tag_name": "v0.1.0", "draft": True}]]],
        ):
            with self.subTest(responses=responses):
                api.side_effect = responses
                with self.assertRaises(ValueError):
                    ensure_tag("owner/repo", "0.1.0", self.sha)

    @patch("release_tag.api")
    def test_main_advanced_or_api_failure_does_not_publish(self, api):
        api.side_effect = [[], {"object": {"sha": "b" * 40}}]
        with self.assertRaises(ValueError):
            ensure_tag("owner/repo", "0.1.0", self.sha, create=True)
        self.assertEqual(api.call_count, 2)
        api.side_effect = subprocess.CalledProcessError(1, "gh")
        with self.assertRaises(subprocess.CalledProcessError):
            ensure_tag("owner/repo", "0.1.0", self.sha, create=True)
