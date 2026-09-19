"""Select official VERSION changes or manual next-patch alpha releases."""

import os
import re
import subprocess
from pathlib import Path

PATTERN = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)")


def parse(value):
    if not PATTERN.fullmatch(value):
        raise ValueError("VERSION must contain a canonical major.minor.patch")
    return tuple(map(int, value.split(".")))


def select(base, previous, event, run, attempt, tags):
    numbers = parse(base)
    if event == "workflow_dispatch":
        if not re.fullmatch(r"[1-9][0-9]*", run) or not re.fullmatch(r"[1-9][0-9]*", attempt):
            raise ValueError("invalid run identity")
        major, minor, patch = numbers
        return f"{major}.{minor}.{patch + 1}-alpha.{run}.{attempt}"
    if event not in {"push", "official_retry"}:
        raise ValueError("unsupported release event")
    if previous == base:
        return None
    if previous is not None and numbers <= parse(previous):
        raise ValueError("VERSION must increase")
    official = [
        parse(tag[1:]) for tag in tags if tag.startswith("v") and PATTERN.fullmatch(tag[1:])
    ]
    if official and numbers < max(official):
        raise ValueError("VERSION must not precede existing official tags")
    return base


def git(*args):
    return subprocess.check_output(["git", *args], text=True).strip()


def main():
    if os.environ["GITHUB_REF"] != "refs/heads/main":
        raise ValueError("release requires main")
    base = Path("VERSION").read_text().strip()
    event = os.environ["GITHUB_EVENT_NAME"]
    previous = None
    if event == "push":
        before = os.environ["BEFORE"]
        if not re.fullmatch(r"[0-9a-f]{40}", before) or before == "0" * 40:
            raise ValueError("release requires an existing main before commit")
        # A force-push can leave the previous tip outside the new history.
        subprocess.run(["git", "fetch", "--no-tags", "origin", before], check=True)
        if git("ls-tree", "--name-only", before, "--", "VERSION"):
            previous = git("show", f"{before}:VERSION")
    if event == "workflow_dispatch" and os.environ.get("CHANNEL") == "official":
        event = "official_retry"
    version = select(
        base,
        previous,
        event,
        os.environ["GITHUB_RUN_NUMBER"],
        os.environ["GITHUB_RUN_ATTEMPT"],
        git("tag", "--list").splitlines(),
    )
    with Path(os.environ["GITHUB_OUTPUT"]).open("a") as output:
        output.write(f"publish={str(version is not None).lower()}\n")
        if version:
            output.write(f"version={version}\n")


if __name__ == "__main__":
    main()
