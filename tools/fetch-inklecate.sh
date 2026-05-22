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

ls -la "$TOOLS_DIR" | grep -E '\.(exe|dll)$|inklecate$' || true
echo "Done."
