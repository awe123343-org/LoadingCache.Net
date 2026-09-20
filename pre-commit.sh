#!/usr/bin/env bash

# Run the formatting workflow used by GitHub Actions.
# Requires GNU Make, Git, the pinned .NET SDK, Node.js/pnpm, Java and uv/uvx.
# Usage: ./pre-commit.sh
# All hooks run once through the manual stage. IDE0005 removes unused usings;
# the final diff against HEAD detects formatter changes in CI and local runs.
# Exit 0 on success, nonzero on tool/hook failure or tracked changes versus HEAD.

set -euo pipefail

ROOT_DIR=$(git rev-parse --show-toplevel)
cd "$ROOT_DIR" || exit 1

git --no-pager diff --color=always

# Tool installation failures stop the script immediately.
make install-tools

COMMAND_FAILURES=0
make format-pre-commit || COMMAND_FAILURES=$((COMMAND_FAILURES + 1))

# Include both staged and unstaged tracked changes.
if [ "$COMMAND_FAILURES" -gt 0 ] || ! git diff --quiet HEAD --; then
    make check-format-changes
    exit 1
fi

make format-success
