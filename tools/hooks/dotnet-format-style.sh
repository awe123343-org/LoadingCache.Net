#!/usr/bin/env bash
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"
args=(style LoadingCache.slnx --diagnostics IDE0005 --severity info)
# Hook calls pass the selected files. A direct call with no paths fixes the solution.
if [ "$#" -gt 0 ]; then
    args+=(--include "$@")
fi
dotnet format "${args[@]}"
