"""Reserve an immutable release tag, or verify it before publication."""

import json
import os
import re
import subprocess
import sys


def api(path, *args):
    return json.loads(subprocess.check_output(["gh", "api", path, *args], text=True))


def ensure_tag(repo, version, sha, create=False):
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError("expected full source SHA")
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-alpha\.[0-9]+\.[0-9]+)?", version):
        raise ValueError("invalid release version")
    prefix = f"repos/{repo}"
    tag = f"v{version}"
    refs = api(f"{prefix}/git/matching-refs/tags/{tag}")
    matches = [r for r in refs if r["ref"] == f"refs/tags/{tag}"]
    if matches:
        obj = matches[0]["object"]
        if obj["type"] != "commit" or obj["sha"] != sha:
            raise ValueError("release tag does not match the exact source commit")
    elif not create:
        raise ValueError("release tag is missing")
    else:
        # Fail before any testing or upload if main has already advanced.
        head = api(f"{prefix}/git/ref/heads/main")["object"]["sha"]
        if head != sha:
            raise ValueError("main advanced before tag reservation; no publication attempted")
        api(
            f"{prefix}/git/refs",
            "--method",
            "POST",
            "-f",
            f"ref=refs/tags/{tag}",
            "-f",
            f"sha={sha}",
        )
        return ensure_tag(repo, version, sha)
    pages = api(f"{prefix}/releases", "--paginate", "--slurp")
    if any(r["tag_name"] == tag for page in pages for r in page):
        raise ValueError("release already exists; inspect partial publication before recovery")


if __name__ == "__main__":
    ensure_tag(
        os.environ["GH_REPO"],
        os.environ["RELEASE_VERSION"],
        os.environ["REPOSITORY_COMMIT"],
        "--create" in sys.argv,
    )
