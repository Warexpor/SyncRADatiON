#!/usr/bin/env bash
# story-run.sh [chapters...] : after pilot-run.sh, walk the party through the chapters in story order. Each chapter
# load alternates between the host (F7 load, everyone follows) and the first client (F7 = a follow request the host
# runs, AllowClientCheats on). In each chapter: everyone settles and gets out of the opening cutscene, a digest check,
# then every peer takes a pickup in its own room and each client kills an enemy, and a second check.
# PEN_Hole is not in the default list: the wreck -> hole step is each player's own load by design (docs/SYNC.md).
# Results: $PILOT_LOG_DIR/story.txt.
set -u
cd "$(dirname "$0")"
. ./env.sh
P=./pcmd.sh
CHAPTERS=("$@")
[ ${#CHAPTERS[@]} -gt 0 ] || CHAPTERS=(PEN_Wreck LOV_Reeducation DET_Detention MED_Medical RES_Residential RES_School
  ROT_Rotfront EXC_Mines EXC_Gestade LAB_Labyrinth LAB_Emptiness MEM_Memory MEM_Gestade BOS_Adler)
LOG="$PILOT_LOG_DIR/story.txt"; mkdir -p "$PILOT_LOG_DIR"; : > "$LOG"
say() { echo "$*" | tee -a "$LOG"; }
mapfile -t CL < <(peers | tr ' ' '\n' | grep -v '^h$')
fails=0
check() {
  local out; out=$(./check.sh "$1")
  echo "$out" >> "$LOG"
  echo "$out" | grep -E "MATCH|DIFF|NO DIGEST|^    " | head -30
  echo "$out" | grep -q "DIFF\|NO DIGEST" && fails=$((fails + 1))
}

k=0
for ch in "${CHAPTERS[@]}"; do
  k=$((k + 1))
  who=h; [ $((k % 2)) -eq 0 ] && [ ${#CL[@]} -ge 1 ] && who=${CL[0]}
  note "story $k/${#CHAPTERS[@]}: $ch (load by $who)"
  declare -A from=()
  for n in $(peers); do from[$n]=$(lines "$n"); done
  ok=1
  # A follow names a scene, so a host reload of the scene everyone is in is not one (a late duplicate follow must not
  # reload a client that already arrived): the party starts from where it stands.
  if $P h status | tail -1 | grep -q "scene=$ch "; then
    say "  already in $ch: no load"
  else
    $P "$who" scene "$ch" | tail -1
    for n in $(peers); do
      if ! waitfor "$n" "settled $ch" 240 "${from[$n]}"; then say "  $n did not settle in $ch ($(tail -1 "$(pdir "$n")/out.txt"))"; ok=0; fi
    done
  fi
  if [ $ok = 0 ]; then fails=$((fails + 1)); continue; fi
  # Opening cutscenes are per player: skip each peer's own once it runs, then wait for play.
  sleep 2
  $P all skip >/dev/null
  for n in $(peers); do waitplay "$n" 90 skip || { say "  $n not in play in $ch: $($P "$n" status | tail -1 | grep -o 'gs=[a-z]*')"; }; done
  $P all god >/dev/null
  say "  $ch: $($P h status | tail -1 | grep -o 'room=[^ ]*') players=$(st h players) mismatch=$(st c1 mismatch)"
  check "$ch-arrive"

  mapfile -t PK < <($P h pickups | grep -E "item=[A-Za-z]" | grep -v "room=-" | grep -v "self=off" | awk '{print $2, $NF}' | sort -u -k2,2 | awk '{print $1}')
  mapfile -t EN < <($P h enemies | grep -E "state=(sleep|pursuit|attack)" | grep -v "mgr=off" | grep -v "nest=remote" | grep -v "active=False mgr=none" | awk '{print $2}')
  i=0
  for n in $(peers); do
    [ $i -lt ${#PK[@]} ] && { $P "$n" take "${PK[$i]}" >/dev/null; }
    i=$((i + 1))
  done
  sleep 2
  for n in $(peers); do waitplay "$n" 30 >/dev/null; done
  i=0
  for n in "${CL[@]}"; do
    [ $i -lt ${#EN[@]} ] || break
    # From the transcript's length before the command, and only a kill's own "stayed inactive": a take's answer can land
    # up to 15 s later, after this mark (BOS_Adler: the stuck take's line ended a kill wait at once).
    mark=$(lines "$n")
    $P "$n" kill "${EN[$i]}" >/dev/null
    waitfor "$n" "dead after|still [a-z]* after|no live|kill .*stayed inactive" 25 "$mark"
    say "  $n kill ${EN[$i]}: $(tail -n 1 "$(pdir "$n")/out.txt" | cut -c14-) | host $($P h enemies | grep "${EN[$i]}" | grep -o "state=[a-z]*")"
    i=$((i + 1))
  done
  for n in $(peers); do waitplay "$n" 30 >/dev/null; done
  say "  pickups: ${#PK[@]} left before, enemies: ${#EN[@]} alive before"
  check "$ch-play"
done
say "story-run: ${#CHAPTERS[@]} chapter(s), $fails failing step(s)"
[ $fails -eq 0 ]
