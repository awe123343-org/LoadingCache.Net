#!/usr/bin/env bash

set -euo pipefail

ROOT="${ROOT:-$(git rev-parse --show-toplevel)}"

if [[ $# -eq 0 ]]; then
  exit 0
fi

VERSIONS_TOML="${ROOT}/gradle/libs.versions.toml"
KTFMT_VERSION=$(sed -n 's/^ktfmt = "\(.*\)"/\1/p' "${VERSIONS_TOML}")
KTLINT_VERSION=$(sed -n 's/^ktlint = "\(.*\)"/\1/p' "${VERSIONS_TOML}")
BIN_DIR="${ROOT}/bin"

KTFMT_JAR=${BIN_DIR}/ktfmt-${KTFMT_VERSION}.jar
KTFMT_URL="https://github.com/Kotlin/ktfmt/releases/download/v${KTFMT_VERSION}/ktfmt-${KTFMT_VERSION}-with-dependencies.jar"

KTLINT_JAR=${BIN_DIR}/ktlint-$KTLINT_VERSION.jar
KTLINT_URL="https://github.com/ktlint/ktlint/releases/download/${KTLINT_VERSION}/ktlint"

mkdir -p "${BIN_DIR}"

if [ ! -f "${KTFMT_JAR}" ]; then
  >&2 echo "Downloading ktfmt v${KTFMT_VERSION}..."
  rm -rf "${BIN_DIR}/ktfmt-*.jar"
  curl -fL "${KTFMT_URL}" -o "${KTFMT_JAR}.tmp"
  mv "${KTFMT_JAR}.tmp" "${KTFMT_JAR}"
  echo "ktfmt downloaded to ${KTFMT_JAR}"
fi

if [ ! -f "${KTLINT_JAR}" ]; then
  >&2 echo "Downloading ktlint v${KTLINT_VERSION}..."
  rm -rf "${BIN_DIR}/ktlint-*.jar"
  curl -fsSL "${KTLINT_URL}" -o "${KTLINT_JAR}.tmp"
  mv "${KTLINT_JAR}.tmp" "${KTLINT_JAR}"
  chmod +x "${KTLINT_JAR}"
  echo "ktlint downloaded to ${KTLINT_JAR}"
fi

# Use four-space indentation and preserve existing trailing commas via .editorconfig.
java -jar "$KTFMT_JAR" --kotlinlang-style --enable-editorconfig "$@"
java -Djava.awt.headless=true --add-opens java.base/java.lang=ALL-UNNAMED -jar "$KTLINT_JAR" -F "$@"
git add "$@"
