#!/usr/bin/env bash
# Build SyncRADation. Does NOT deploy into the game Mods folders unless --deploy is given.
#   scripts/build.sh            Release build into bin/stage/Release (no deploy)
#   scripts/build.sh --debug    Debug build into bin/stage/Debug
#   scripts/build.sh --deploy   Use the csproj CopyToMods target (copies into SignalisDir/Mods + ClientSignalisDir/Mods)
# Env overrides: UNITY_DOTNET_SDK, MELONLOADER_DIR, OUT_DIR
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SDK="${UNITY_DOTNET_SDK:-$HOME/Unity/Hub/Editor/6000.6.0f1/Editor/Data/DotNetSdk}"
MELON_DIR="${MELONLOADER_DIR:-$HOME/Work/MyProjects/SIGNALIS/MelonLoader}"
CONFIG=Release
DEPLOY=0

for arg in "$@"; do
  case "$arg" in
    --debug) CONFIG=Debug ;;
    --release) CONFIG=Release ;;
    --deploy) DEPLOY=1 ;;
    -h|--help) sed -n '2,7p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

if [[ ! -x "$SDK/dotnet" ]]; then
  echo "dotnet not found at $SDK/dotnet (set UNITY_DOTNET_SDK)" >&2
  exit 1
fi
export DOTNET_ROOT="$SDK"
export PATH="$SDK:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

OUT="${OUT_DIR:-$ROOT/bin/stage/$CONFIG}"
VERSION="$(sed -nE 's/.*const[[:space:]]+string[[:space:]]+Version[[:space:]]*=[[:space:]]*"([0-9.]+)".*/\1/p' "$ROOT/Bootstrap/PluginInfo.cs" | head -n1)"
[[ -n "$VERSION" ]] || { echo "Could not read Version from Bootstrap/PluginInfo.cs" >&2; exit 1; }

cd "$ROOT"
ARGS=(build SyncRADation.csproj -c "$CONFIG" -nologo -v q "-p:MelonLoaderDir=$MELON_DIR" -o "$OUT")
if [[ $DEPLOY -eq 1 ]]; then
  echo "Deploying: csproj CopyToMods will copy into SignalisDir/Mods and ClientSignalisDir/Mods."
else
  ARGS+=("-p:NoDeploy=true")
fi
dotnet "${ARGS[@]}"

DLL="$OUT/SyncRADation.dll"
[[ -f "$DLL" ]] || { echo "Build produced no DLL at $DLL" >&2; exit 1; }

# File version check: the assembly version resource is UTF-16 in the PE.
if ! strings -el "$DLL" | grep -qx "$VERSION.0"; then
  echo "WARNING: $DLL does not contain file version $VERSION.0" >&2
fi

echo "DLL:     $DLL"
echo "Version: $VERSION (PluginInfo.Version, single source)"
echo "Config:  $CONFIG  deploy=$([[ $DEPLOY -eq 1 ]] && echo yes || echo no)"
