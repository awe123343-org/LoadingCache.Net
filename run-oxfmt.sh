#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -eq 0 ]; then
    exit 0
fi

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"
if [ ! -f node_modules/.bin/oxfmt ]; then
    pnpm install --frozen-lockfile --ignore-scripts
fi

# Leave staging to the caller, including when only part of a file was staged.
"$repo_root/node_modules/.bin/oxfmt" --write --config "$repo_root/.oxfmtrc.jsonc" \
    --no-error-on-unmatched-pattern "$@"
