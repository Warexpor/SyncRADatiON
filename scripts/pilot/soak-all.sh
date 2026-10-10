#!/usr/bin/env bash
# soak-all.sh [chapters...] : after pilot-run.sh, the full scope in one session: the party walks the chapters in story
# order (each load alternates between the host and the first client, as story-run.sh) and coop-soak.sh runs every
# arrow in each chapter. One boot for the whole run instead of one per chapter. A chapter that ends in a menu scene
# (LAB_Emptiness -> DeadMenu) is loaded and checked but not soaked. Results: $PILOT_LOG_DIR/soak-all.txt plus each
# chapter's checks-<scene>.txt.
set -u
cd "$(dirname "$0")"
. ./env.sh
P=./pcmd.sh
CHAPTERS=("$@")
[ ${#CHAPTERS[@]} -gt 0 ] || CHAPTERS=(PEN_Wreck LOV_Reeducation DET_Detention MED_Medical RES_Residential RES_School
  ROT_Rotfront EXC_Mines EXC_Gestade LAB_Labyrinth LAB_Emptiness MEM_Memory MEM_Gestade BOS_Adler)
LOG="$PILOT_LOG_DIR/soak-all.txt"; mkdir -p "$PILOT_LOG_DIR"; : > "$LOG"
say() { echo "$*" | tee -a "$LOG"; }
mapfile -t CL < <(peers | tr ' ' '\n' | grep -v '^h$')
fails=0
k=0
for ch in "${CHAPTERS[@]}"; do
  k=$((k + 1))
  who=h; [ $((k % 2)) -eq 0 ] && [ ${#CL[@]} -ge 1 ] && who=${CL[0]}
  t0=$SECONDS
  note "soak-all $k/${#CHAPTERS[@]}: $ch (load by $who)"
  declare -A from=()
  for n in $(peers); do from[$n]=$(lines "$n"); done
  ok=1
  if $P h status | tail -1 | grep -q "scene=$ch "; then
    say "  already in $ch: no load"
  else
    $P "$who" scene "$ch" | tail -1
    for n in $(peers); do
      if ! waitfor "$n" "settled $ch" 240 "${from[$n]}"; then say "  $n did not settle in $ch ($(tail -1 "$(pdir "$n")/out.txt"))"; ok=0; fi
    done
  fi
  if [ $ok = 0 ]; then fails=$((fails + 1)); continue; fi
  sleep 2
  $P all skip >/dev/null
  for n in $(peers); do waitplay "$n" 90 skip || say "  $n not in play in $ch: $($P "$n" status | tail -1 | grep -o 'gs=[a-z]*')"; done
  scene=$(st h scene)
  if [ "$scene" != "$ch" ]; then
    say "  $ch ended in $scene: loaded, not soaked"
    continue
  fi
  ./coop-soak.sh | tee -a "$LOG" | grep -E "^====|DIFF|NO DIGEST|^    |failing|not |did not" || true
  r=$(tail -1 "$LOG")
  echo "$r" | grep -q " 0 failing" || fails=$((fails + 1))
  say "  $ch: $((SECONDS - t0)) s"
done
say "soak-all: ${#CHAPTERS[@]} chapter(s), $fails failing chapter(s)"
