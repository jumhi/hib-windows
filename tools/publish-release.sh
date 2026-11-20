#!/usr/bin/env bash
# H.I.B for Windows — builds both release assets into dist/ (nothing is uploaded or pushed):
#   dist/HIB-Windows.zip    portable copy: H.I.B\HIB.exe + H.I.B\tools\ (adb, scrcpy) + H.I.B\README.txt
#   dist/H.I.B-Setup.exe    the same app with the tools embedded; run it to install per user
#                           (%LOCALAPPDATA%\Programs\H.I.B, Start menu, Settings › Apps uninstall)
#   dist/SHA256SUMS.txt
#
# Needs the official scrcpy win64 release (it includes adb.exe) unzipped into tools/win-x64,
# or HIB_TOOLS_DIR=/path/to/it. Usage: tools/publish-release.sh [--test]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="${DOTNET:-/opt/homebrew/bin/dotnet}"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
TOOLS="${HIB_TOOLS_DIR:-$ROOT/tools/win-x64}"
OUT="$ROOT/out"
DIST="${HIB_DIST_DIR:-$ROOT/dist}"
PUBLISH=(publish "$ROOT/src/HIB/HIB.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
         "-p:HibToolsDir=$TOOLS" --nologo -v:q)

if [[ ! -f "$TOOLS/adb.exe" || ! -f "$TOOLS/scrcpy.exe" ]]; then
  echo "error: adb.exe/scrcpy.exe not found in $TOOLS (unzip scrcpy-win64-v5.0.1.zip there or set HIB_TOOLS_DIR)" >&2
  exit 1
fi

if [[ "${1:-}" == "--test" ]]; then
  echo "==> dotnet test"
  "$DOTNET" test "$ROOT/HIB.sln" --nologo -v:q
fi

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/HIB/HIB.csproj" | head -1)"
echo "==> H.I.B for Windows $VERSION"
rm -rf "$OUT/win-x64" "$OUT/setup" "$DIST"
mkdir -p "$DIST"

echo "==> portable (HIB.exe + tools/)"
"$DOTNET" "${PUBLISH[@]}" -o "$OUT/win-x64"
STAGE="$OUT/zip"
rm -rf "$STAGE" && mkdir -p "$STAGE/H.I.B"
cp "$OUT/win-x64/HIB.exe" "$STAGE/H.I.B/"
cp -R "$OUT/win-x64/tools" "$STAGE/H.I.B/tools"
sed 's/$/\r/' "$ROOT/docs/release/README.txt" > "$STAGE/H.I.B/README.txt"   # CRLF for Notepad
(cd "$STAGE" && zip -qr -X "$DIST/HIB-Windows.zip" H.I.B)

echo "==> setup (tools embedded, -p:HibSetup=true)"
"$DOTNET" "${PUBLISH[@]}" -p:HibSetup=true -o "$OUT/setup"
cp "$OUT/setup/HIB.exe" "$DIST/H.I.B-Setup.exe"

# The portable exe must not carry the embedded tools zip (the setup build has its own obj/bin).
if grep -q "HIB.Tools.zip" "$OUT/win-x64/HIB.exe"; then
  echo "error: the portable HIB.exe contains the setup's tools zip" >&2
  exit 1
fi
grep -q "HIB.Tools.zip" "$DIST/H.I.B-Setup.exe" || { echo "error: H.I.B-Setup.exe has no tools zip" >&2; exit 1; }

(cd "$DIST" && shasum -a 256 HIB-Windows.zip H.I.B-Setup.exe > SHA256SUMS.txt)
echo "==> done"
ls -l "$DIST"
cat "$DIST/SHA256SUMS.txt"
