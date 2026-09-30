#!/usr/bin/env bash
# Build a player release zip: dist/SyncRADation-<version>.zip
#   Mods/SyncRADation.dll, Mods/LiteNetLib.dll, INSTALL.md, LICENSE, CHANGELOG.md
# Uses a Release build that does not deploy. Version comes from Bootstrap/PluginInfo.cs.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

"$ROOT/scripts/build.sh" --release
OUT="${OUT_DIR:-$ROOT/bin/stage/Release}"
VERSION="$(sed -nE 's/.*const[[:space:]]+string[[:space:]]+Version[[:space:]]*=[[:space:]]*"([0-9.]+)".*/\1/p' Bootstrap/PluginInfo.cs | head -n1)"

STAGE="$ROOT/dist/_stage"
ZIP="$ROOT/dist/SyncRADation-$VERSION.zip"
rm -rf "$STAGE"
mkdir -p "$STAGE/Mods"
cp "$OUT/SyncRADation.dll" "$STAGE/Mods/"
# LiteNetLib is referenced (Private=true) and copied next to the mod DLL; fall back to lib/.
if [[ -f "$OUT/LiteNetLib.dll" ]]; then cp "$OUT/LiteNetLib.dll" "$STAGE/Mods/"; else cp "$ROOT/lib/LiteNetLib.dll" "$STAGE/Mods/"; fi
cp INSTALL.md LICENSE CHANGELOG.md "$STAGE/"
cp lib/LiteNetLib.LICENSE.txt "$STAGE/LiteNetLib.LICENSE.txt"

rm -f "$ZIP"
(cd "$STAGE" && zip -qr -X "$ZIP" .)
rm -rf "$STAGE"

echo "Package: $ZIP"
unzip -l "$ZIP"
