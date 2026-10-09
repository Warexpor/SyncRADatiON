#!/usr/bin/env bash
# setup.sh [--fresh] : make the pilot instances (h, c1, c2, c3) as reflink clones of the client install, one Proton
# prefix each, and a writable copy of Proton (it locks files in its own folder). Idempotent; --fresh rebuilds all.
set -euo pipefail
. "$(dirname "$0")/env.sh"

[ "${1:-}" = "--fresh" ] && rm -rf "$PILOT_BASE"
mkdir -p "$PILOT_BASE"
[ -f "$PILOT_SRC/SIGNALIS.exe" ] || { echo "no SIGNALIS install at $PILOT_SRC" >&2; exit 1; }
[ -x "$PILOT_PROTON_SRC/proton" ] || { echo "no Proton at $PILOT_PROTON_SRC" >&2; exit 1; }

[ -x "$PILOT_BASE/proton/proton" ] || cp -a --reflink=auto "$PILOT_PROTON_SRC" "$PILOT_BASE/proton"
for n in h c1 c2 c3; do
  if [ ! -f "$PILOT_BASE/$n/SIGNALIS.exe" ]; then
    cp -a --reflink=auto "$PILOT_SRC" "$PILOT_BASE/$n"
    rm -f "$PILOT_BASE/$n"/MelonLoader/Latest.log "$PILOT_BASE/$n"/MelonLoader/Logs/*
  fi
  [ -d "$PILOT_BASE/pfx-$n/pfx" ] || cp -a --reflink=auto "$PILOT_PFX_SRC" "$PILOT_BASE/pfx-$n"
  cfg="$PILOT_BASE/$n/UserData/MelonPreferences.cfg"
  # Pilot prefs: 4-player sessions, clients may cheat (F6/F7/F11 paths the pilot drives), full logging.
  for kv in "MaxPlayers = 4" "AllowClientCheats = true" "VerboseLogging = true" "Diagnostics = true" "FreeCursor = true" \
            'ConnectAddress = "127.0.0.1"' "ConnectPort = 7777"; do
    k="${kv%% =*}"
    if grep -q "^$k = " "$cfg"; then sed -i "s|^$k = .*|$kv|" "$cfg"; else sed -i "/^\[SyncRADation\]/a $kv" "$cfg"; fi
  done
done
du -sh --apparent-size "$PILOT_BASE"/* | sed 's|'"$PILOT_BASE"'/||'
echo "pilot instances ready in $PILOT_BASE"
