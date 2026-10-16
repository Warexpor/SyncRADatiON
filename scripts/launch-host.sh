#!/usr/bin/env bash
# Launch the Steam SIGNALIS install (host). The copy = client (launch-client.sh).
set -euo pipefail

STEAM_ROOT="${STEAM_ROOT:-$HOME/.local/share/Steam}"
GAME_DIR="${SIGNALIS_HOST_DIR:-$STEAM_ROOT/steamapps/common/SIGNALIS}"
APP_ID=1262350

if ! command -v steam >/dev/null; then
  echo "steam not found in PATH" >&2
  exit 1
fi
if [[ ! -f "$GAME_DIR/SIGNALIS.exe" ]]; then
  echo "Host install missing SIGNALIS.exe: $GAME_DIR" >&2
  exit 1
fi
if [[ ! -f "$GAME_DIR/version.dll" ]]; then
  echo "MelonLoader proxy missing (version.dll) in $GAME_DIR" >&2
  exit 1
fi

# WINEDLLOVERRIDES and the 1280x720 window args live in the Steam launch options (docs/DEV_MACHINE.md).
# Extra args go to the game, e.g. --sync-host --sync-scene DET_Detention.
exec steam -applaunch "$APP_ID" "$@"
