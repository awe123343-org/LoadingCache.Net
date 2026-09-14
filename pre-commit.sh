#!/usr/bin/env bash
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"
make install-tools
status=0
make format-pre-commit || status=1
if ! git diff --quiet HEAD --; then
    git --no-pager diff HEAD --color=always
    status=1
fi
exit "$status"
