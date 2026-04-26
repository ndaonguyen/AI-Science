#!/usr/bin/env bash
# Download the sqlite-vec native extension (vec0) for the host OS and arch
# into ~/.dd/, where SqliteVecInvestigationMemory will find it at runtime.
#
# Run this once after cloning. Idempotent — re-running on the same machine
# just overwrites the existing binary with whatever VERSION is set below.
#
# Why download instead of bundle: vec0 ships separate binaries for
# linux-x86_64, linux-arm64, darwin-x86_64, darwin-arm64, and windows-x86_64.
# Committing all five to the repo (~3-4 MB compressed) is overkill for a
# personal tool; a script that grabs the right one for THIS machine is
# simpler. Switch to NuGet bundling once Microsoft ships a stable
# Microsoft.SemanticKernel.Connectors.SqliteVec or sqlite-vec ships an
# official .NET package.
set -euo pipefail

VERSION="v0.1.6"
TARGET_DIR="${HOME}/.dd"
mkdir -p "${TARGET_DIR}"

# Detect OS and arch.
OS_RAW="$(uname -s)"
ARCH_RAW="$(uname -m)"

case "${OS_RAW}" in
  Linux)   OS="linux"  ; EXT="so"    ;;
  Darwin)  OS="macos"  ; EXT="dylib" ;;
  MINGW*|MSYS*|CYGWIN*) OS="windows" ; EXT="dll" ;;
  *) echo "Unsupported OS: ${OS_RAW}" >&2 ; exit 1 ;;
esac

case "${ARCH_RAW}" in
  x86_64|amd64) ARCH="x86_64" ;;
  aarch64|arm64) ARCH="aarch64" ;;
  *) echo "Unsupported architecture: ${ARCH_RAW}" >&2 ; exit 1 ;;
esac

# Releases are at https://github.com/asg017/sqlite-vec/releases/<tag>/.
# Asset name shape: sqlite-vec-<version-without-v>-loadable-<os>-<arch>.tar.gz
VERSION_NO_V="${VERSION#v}"
ASSET="sqlite-vec-${VERSION_NO_V}-loadable-${OS}-${ARCH}.tar.gz"
URL="https://github.com/asg017/sqlite-vec/releases/download/${VERSION}/${ASSET}"

echo "→ fetching ${URL}"
TMP="$(mktemp -d)"
trap "rm -rf ${TMP}" EXIT
curl -fSL --output "${TMP}/${ASSET}" "${URL}"

echo "→ extracting"
tar -xzf "${TMP}/${ASSET}" -C "${TMP}"

# The tarball contains a single shared library file: vec0.so, vec0.dylib, or
# vec0.dll. Find it (the layout has been stable across 0.1.x but tar -tzf'ing
# is the safe move).
LIB_PATH="$(find "${TMP}" -type f -name "vec0.${EXT}" -print -quit)"
if [[ -z "${LIB_PATH}" ]]; then
  echo "Couldn't find vec0.${EXT} in the tarball — sqlite-vec may have changed its asset layout. Inspect ${TMP} manually." >&2
  exit 1
fi

DEST="${TARGET_DIR}/vec0.${EXT}"
cp "${LIB_PATH}" "${DEST}"
chmod +x "${DEST}"

echo "✓ installed ${DEST}"
echo "  set DD_VEC0_PATH=${DEST} (or rely on the default lookup) to use it."
