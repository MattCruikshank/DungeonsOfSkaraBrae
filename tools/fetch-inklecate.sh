#!/usr/bin/env bash
# Downloads the inklecate v1.2.1 release for the current OS into this folder.
# The csproj references tools/ink-engine-runtime.dll directly, so this must run
# before `dotnet build` on a fresh clone.

set -euo pipefail

VERSION="v1.2.1"
case "$(uname -s)" in
  Linux*)  ASSET="inklecate_linux.zip" ;;
  Darwin*) ASSET="inklecate_mac.zip" ;;
  MINGW*|MSYS*|CYGWIN*) ASSET="inklecate_windows.zip" ;;
  *) echo "Unsupported OS: $(uname -s)"; exit 1 ;;
esac

URL="https://github.com/inkle/ink/releases/download/$VERSION/$ASSET"
TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ZIP="$TOOLS_DIR/$ASSET"

echo "Downloading $URL"
curl -fsSL -o "$ZIP" "$URL"

echo "Extracting"
unzip -oq "$ZIP" -d "$TOOLS_DIR"
rm "$ZIP"

if [ -f "$TOOLS_DIR/inklecate" ]; then
  chmod +x "$TOOLS_DIR/inklecate"
fi

# ── esbuild (for transpiling Game/*.ts combat scripts) ──────────────────────
ESBUILD_VERSION="0.28.0"
case "$(uname -s)" in
  Linux*)  EPKG="linux-x64";  EBIN_IN="package/bin/esbuild";   EBIN_OUT="esbuild" ;;
  Darwin*) if [ "$(uname -m)" = "arm64" ]; then EPKG="darwin-arm64"; else EPKG="darwin-x64"; fi
           EBIN_IN="package/bin/esbuild"; EBIN_OUT="esbuild" ;;
  *)       EPKG="win32-x64"; EBIN_IN="package/esbuild.exe";    EBIN_OUT="esbuild.exe" ;;
esac
ETGZ="$TOOLS_DIR/esbuild-$EPKG.tgz"
echo "Downloading esbuild $ESBUILD_VERSION ($EPKG)"
curl -fsSL -o "$ETGZ" "https://registry.npmjs.org/@esbuild/$EPKG/-/$EPKG-$ESBUILD_VERSION.tgz"
tar -xzf "$ETGZ" -C "$TOOLS_DIR" "$EBIN_IN"
mv -f "$TOOLS_DIR/$EBIN_IN" "$TOOLS_DIR/$EBIN_OUT"
rm -rf "$TOOLS_DIR/package" "$ETGZ"
[ -f "$TOOLS_DIR/esbuild" ] && chmod +x "$TOOLS_DIR/esbuild"

ls -la "$TOOLS_DIR" | grep -E '\.(exe|dll)$|inklecate$|esbuild$' || true
echo "Done."
