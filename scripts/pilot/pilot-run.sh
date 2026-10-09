#!/usr/bin/env bash
# pilot-run.sh [scene] : deploy the built DLL to every pilot instance, start the box, host the scene (default
# DET_Detention) and join CLIENTS clients one after another; returns once everyone is settled in the scene.
#   CLIENTS=1 scripts/pilot/pilot-run.sh MED_Medical      two players
#   PILOT_DLL=... to run another build (default: bin/stage/Debug, scripts/build.sh --debug)
set -uo pipefail
. "$(dirname "$0")/env.sh"
SCENE="${1:-DET_Detention}"

[ -f "$PILOT_DLL" ] || { echo "no DLL at $PILOT_DLL (scripts/build.sh --debug)" >&2; exit 1; }
"$(dirname "$0")/setup.sh" >/dev/null || exit 1
LNL="$(dirname "$PILOT_DLL")/LiteNetLib.dll"
for n in $(peers); do
  cp "$PILOT_DLL" "$PILOT_BASE/$n/Mods/SyncRADation.dll"
  [ -f "$LNL" ] && cp "$LNL" "$PILOT_BASE/$n/Mods/LiteNetLib.dll"
  rm -f "$PILOT_BASE/$n/Mods/SyncRADation.pdb"
done
sums=$(for n in $(peers); do md5sum "$PILOT_BASE/$n/Mods/SyncRADation.dll" | cut -d' ' -f1; done | sort -u | wc -l)
[ "$sums" = 1 ] || { echo "DLL md5 differs between instances" >&2; exit 1; }
echo "dll $(md5sum "$PILOT_DLL" | cut -c1-12) -> $(peers)"

box down >/dev/null 2>&1
systemctl --user stop "$PILOT_SLICE" 2>/dev/null
# An instance holds ~4.6 GB from MelonLoader init on, ~4.8 GB in RES and over 6.7 GB while RES_School loads. The cap
# is 7 GB per instance, or what is available less 2 GB for the desktop when that is less: a spike past it kills a game
# inside the slice (the run reports it), never the desktop. At least 5 GB per instance must be free to start.
np=$(peers | wc -w)
avail=$(awk '/^MemAvailable:/ {print int($2 / 1048576)}' /proc/meminfo)
[ "$avail" -ge $((np * 5 + 2)) ] || { echo "$np instances need ~$((np * 5 + 2)) GB available, have ${avail} GB (CLIENTS=$CLIENTS)" >&2; exit 1; }
cap=$((np * 7)); [ $cap -le $((avail - 2)) ] || cap=$((avail - 2))
PILOT_MEM="${PILOT_MEM:-${cap}G}"
echo "memory cap $PILOT_MEM for $np instances (${avail} GB available)"
# The box and every game (with their tmpfs writes) live under the capped pilot slice, not the caller's cgroup.
fenced omabox -b "$PILOT_BOX" up --xwayland --no-shell --idle 0 --size 1920x1080 --net isolated --overlay "$PILOT_BASE" \
  >/dev/null || exit 1
systemctl --user set-property --runtime "$PILOT_SLICE" MemoryMax="$PILOT_MEM" MemorySwapMax=0 \
  || { echo "could not cap $PILOT_SLICE" >&2; box down >/dev/null 2>&1; exit 1; }
setsid nohup "$(dirname "$0")/mem-guard.sh" >/dev/null 2>&1 &
for n in $(peers); do mkdir -p "$(pdir "$n")"; : > "$(pdir "$n")/cmd.txt"; : > "$(pdir "$n")/out.txt"; done

# start <peer> <pilot mode>
start() {
  local n=$1 mode=$2 g="$PILOT_BASE/$1"
  read -r w h <<< "$PILOT_RES"
  fenced omabox -b "$PILOT_BOX" run -d -q -- env STEAM_COMPAT_DATA_PATH="$PILOT_BASE/pfx-$n" STEAM_COMPAT_CLIENT_INSTALL_PATH="$PILOT_BASE" \
    WINEDLLOVERRIDES="version=n,b" bash -c "cd '$g' && exec '$PILOT_BASE/proton/proton' run '$g/SIGNALIS.exe' \
      -screen-fullscreen 0 -screen-width $w -screen-height $h --melonloader.hideconsole --sync-pilot '$mode' --sync-pilot-dir 'Z:/home/sbx/pilot/$n'" \
    > "$PILOT_HOME/pilot/$n/stdout.log" 2>&1
}

start h "host:$SCENE"
waitfor h "stage InWorld" 240 || { echo "host did not reach the world"; tail -20 "$(pdir h)/out.txt"; exit 1; }
echo "h   in $SCENE"
for n in $(peers); do
  [ "$n" = h ] && continue
  start "$n" join
  waitfor "$n" "stage InWorld" 240 || { echo "$n did not reach the world"; tail -20 "$(pdir "$n")/out.txt"; exit 1; }
  echo "$n  in $SCENE"
done
echo "all in world ($(peers))"
