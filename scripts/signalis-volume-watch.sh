#!/usr/bin/env bash
# Dual-box audio: every new SIGNALIS audio stream gets a fixed per-app volume once.
#   host   (Steam prefix compatdata/1262350)          -> HOST_VOLUME   (default 60%)
#   client (prefix compatdata/syncradation-client)    -> muted
# The prefix is read from the stream owner's /proc/<pid>/environ (both games are SIGNALIS.exe).
# Each stream is set once, so a manual change in the mixer afterwards sticks.
# Runs as the user service signalis-volume.service; stop with:
#   systemctl --user disable --now signalis-volume
set -uo pipefail

HOST_VOLUME="${HOST_VOLUME:-60%}"
declare -A done_idx=()

prefix_of() {
  local pid=$1 env
  for _ in 1 2 3 4; do
    [[ -r /proc/$pid/environ ]] || return 1
    env=$(tr '\0' '\n' < "/proc/$pid/environ" 2>/dev/null | grep -E '^(STEAM_COMPAT_DATA_PATH|WINEPREFIX)=' | head -1)
    if [[ -n $env ]]; then echo "${env#*=}"; return 0; fi
    pid=$(awk '/^PPid:/{print $2}' "/proc/$pid/status" 2>/dev/null) || return 1
    [[ -n $pid && $pid != 0 && $pid != 1 ]] || return 1
  done
  return 1
}

apply_all() {
  local rows idx pid pfx
  rows=$(pactl -f json list sink-inputs 2>/dev/null \
    | jq -r '.[] | [.index, (.properties."application.process.id" // "")] | @tsv') || return
  while IFS=$'\t' read -r idx pid; do
    [[ -n $idx && -n $pid ]] || continue
    [[ -n ${done_idx[$idx]:-} ]] && continue
    pfx=$(prefix_of "$pid") || continue
    case "$pfx" in
      *compatdata/syncradation-client*)
        pactl set-sink-input-mute "$idx" 1 && echo "client stream $idx (pid $pid) muted"
        done_idx[$idx]=1 ;;
      *compatdata/1262350*)
        pactl set-sink-input-mute "$idx" 0
        pactl set-sink-input-volume "$idx" "$HOST_VOLUME" && echo "host stream $idx (pid $pid) -> $HOST_VOLUME"
        done_idx[$idx]=1 ;;
    esac
  done <<< "$rows"
}

apply_all
pactl subscribe 2>/dev/null | while read -r line; do
  [[ $line == *"'new' on sink-input"* ]] || continue
  sleep 0.2
  apply_all
done
