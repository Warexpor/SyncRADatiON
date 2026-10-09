# Shared settings for the test pilot scripts (sourced). Override any of these in the environment.
#
# The pilot runs every SIGNALIS instance inside one headless omabox box (its own Hyprland screen, its own loopback
# network), never on the desktop. Instances are reflink clones of the client install, each with its own Proton prefix,
# under $PILOT_BASE; the box mounts that tree as a discarded overlay, so every run starts from the same files (no
# saves, no logs carried over) and the clones are never written to. Commands and results go through
# $PILOT_HOME/<peer>/{cmd,out}.txt, inside the box's HOME (visible from both sides).

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PILOT_SRC="${PILOT_SRC:-$HOME/Work/MyProjects/SIGNALIS}"                 # install the clones are made from
PILOT_PFX_SRC="${PILOT_PFX_SRC:-$HOME/.local/share/Steam/steamapps/compatdata/syncradation-client}"
PILOT_PROTON_SRC="${PILOT_PROTON_SRC:-$HOME/.local/share/Steam/steamapps/common/Proton - Experimental}"
PILOT_BASE="${PILOT_BASE:-$HOME/.local/share/syncradation-pilot}"
PILOT_BOX="${PILOT_BOX:-syncpilot}"
PILOT_HOME="${PILOT_HOME:-/run/user/$(id -u)/omabox/$PILOT_BOX/home}"
PILOT_LOG_DIR="${PILOT_LOG_DIR:-$ROOT/artifacts/pilot}"
PILOT_DLL="${PILOT_DLL:-$ROOT/bin/stage/Debug/SyncRADation.dll}"
# Peers: h = host, c1..c3 = clients. CLIENTS picks how many clients a run starts (2 = three players).
CLIENTS="${CLIENTS:-2}"
PILOT_RES="${PILOT_RES:-640 360}"
# Memory fence. The box and every game start in their own scopes under one slice (PILOT_SLICE) capped at PILOT_MEM
# with no swap (`box run` starts a command from the caller's cgroup, so each game needs its own scope there). The box's
# overlay and HOME are tmpfs, so what the games write is RAM too; a runaway instance must hit the cap, not the desktop
# (an instance idles at ~4.6 GB resident and peaks near 6). mem-guard.sh samples every instance into $PILOT_LOG_DIR/mem.txt and kills
# one past PILOT_PROC_MAX_MB.
PILOT_MEM="${PILOT_MEM:-}"   # empty: 7 GB per instance, at most available - 2 GB (pilot-run.sh)
PILOT_PROC_MAX_MB="${PILOT_PROC_MAX_MB:-10240}"   # a chapter load spikes an instance past 8 GB for a moment
PILOT_SLICE="${PILOT_SLICE:-syncpilot.slice}"
# fenced <command...>: run it in a new scope under the pilot slice.
fenced() { systemd-run --user --scope --quiet --collect --slice="$PILOT_SLICE" "$@"; }

peers() { local p=(h); for ((i = 1; i <= CLIENTS; i++)); do p+=("c$i"); done; echo "${p[@]}"; }
pdir() { echo "$PILOT_HOME/pilot/$1"; }
box() { omabox -b "$PILOT_BOX" "$@"; }
# Read a file inside the box (the game's own writes live in the box's overlay, not on disk).
boxcat() { box run -- cat "$1" 2>/dev/null; }
latest_log() { echo "$PILOT_BASE/$1/MelonLoader/Latest.log"; }

# waitfor <peer> <regex> <seconds> [from-line]: wait until out.txt has a line matching the regex (after from-line).
waitfor() {
  local f; f="$(pdir "$1")/out.txt"
  local end=$((SECONDS + $3)) from="${4:-0}"
  while [ $SECONDS -lt $end ]; do
    tail -n +"$((from + 1))" "$f" 2>/dev/null | grep -qE -- "$2" && return 0
    sleep 1
  done
  return 1
}
lines() { wc -l < "$(pdir "$1")/out.txt" 2>/dev/null || echo 0; }

# st <peer> <field>: one field of the peer's status line (gs, dead, room, scene, hp, ...).
st() { "$(dirname "${BASH_SOURCE[0]}")/pcmd.sh" "$1" status | grep -o " $2=[^ ]*" | tail -1 | cut -d= -f2; }
# waitplay <peer> <seconds> [skip]: until the peer is in play (no cutscene / dialogue / load) and not downed. With
# "skip", a cutscene still running is skipped again: a chapter can open with several in a row (LAB_Emptiness: the
# crash site cutscenes play one after another, the host was still in the second when the digests ran). An event
# screen held for 10 s counts: it is an interactive zoom-in (keypad, photo) that waits for the player's click, not a
# sync stall (RES_School opens in the elevator's event screen and the pilot waited 270 s for it).
waitplay() {
  local end=$((SECONDS + $2)) es=0
  while [ $SECONDS -lt $end ]; do
    local s; s=$("$(dirname "${BASH_SOURCE[0]}")/pcmd.sh" "$1" status | tail -1)
    echo "$s" | grep -q "gs=play" && echo "$s" | grep -q "dead=False" && return 0
    # A chapter that ends in a menu scene (LAB_Emptiness -> DeadMenu): nothing left to play.
    echo "$s" | grep -q "gs=menu" && return 0
    if [ "${3:-}" = skip ] && echo "$s" | grep -q "gs=cutscene"; then
      "$(dirname "${BASH_SOURCE[0]}")/pcmd.sh" "$1" skip >/dev/null
    fi
    if echo "$s" | grep -q "gs=eventScreen" && echo "$s" | grep -q "cutscene=False"; then
      es=$((es + 1)); [ $es -ge 5 ] && return 0
    else es=0; fi
    sleep 2
  done
  return 1
}
# note <text>: a marker line in every peer's transcript and log (find a step in the logs by it).
note() { for n in $(peers); do echo "say ==== $*" >> "$(pdir "$n")/cmd.txt"; done; echo "==== $*"; }
