#!/usr/bin/env bash
# Launch the second-box SIGNALIS copy under Proton (client). Steam = host.
set -euo pipefail

STEAM_ROOT="${STEAM_ROOT:-$HOME/.local/share/Steam}"
GAME_DIR="${SIGNALIS_CLIENT_DIR:-$HOME/Work/MyProjects/SIGNALIS}"
PROTON_DIR="${PROTON_DIR:-$STEAM_ROOT/steamapps/common/Proton - Experimental}"
COMPAT_DATA="${STEAM_COMPAT_DATA_PATH:-$STEAM_ROOT/steamapps/compatdata/syncradation-client}"

if [[ ! -x "$PROTON_DIR/proton" ]]; then
  echo "Proton not found: $PROTON_DIR/proton" >&2
  exit 1
fi
if [[ ! -f "$GAME_DIR/SIGNALIS.exe" ]]; then
  echo "Client install missing SIGNALIS.exe: $GAME_DIR" >&2
  exit 1
fi
if [[ ! -f "$GAME_DIR/version.dll" ]]; then
  echo "MelonLoader proxy missing (version.dll) in $GAME_DIR" >&2
  exit 1
fi

mkdir -p "$COMPAT_DATA"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM_ROOT"
export STEAM_COMPAT_DATA_PATH="$COMPAT_DATA"
# Proton/Wine ships its own version.dll; force MelonLoader's proxy from the game dir.
export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:-version=n,b}"

# Keep dual-instance friendly; match Steam host Proton.
# Half-height windowed so Hyprland can stack host (top) + client (bottom).
cd "$GAME_DIR"
exec "$PROTON_DIR/proton" run "$GAME_DIR/SIGNALIS.exe" \
  -screen-fullscreen 0 -screen-width 2560 -screen-height 720 "$@"
