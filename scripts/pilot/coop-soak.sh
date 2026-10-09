#!/usr/bin/env bash
# coop-soak.sh : after pilot-run.sh, one pass over the shared world with every peer acting, a digest check
# (check.sh) after each step. Steps: each peer takes a different pickup; all peers grab the same one at the same
# moment; each peer kills an enemy (host native, clients through the client damage path); a client opens a door;
# a client goes down and is revived; the last client leaves, the host changes the world, the client rejoins late.
# Every step's result lands in $PILOT_LOG_DIR/checks-<scene>.txt as well.
set -u
cd "$(dirname "$0")"
. ./env.sh
P=./pcmd.sh
SCENE=$(st h scene)
LOG="$PILOT_LOG_DIR/checks-$SCENE.txt"; mkdir -p "$PILOT_LOG_DIR"; : > "$LOG"
fails=0
check() { if ./check.sh "$1" | tee -a "$LOG" | grep -E "MATCH|DIFF|NO DIGEST|^    "; then :; fi
  grep -q "^\[$1\] .*\(DIFF\|NO DIGEST\)" "$LOG" && fails=$((fails + 1)); }
mapfile -t CL < <(peers | tr ' ' '\n' | grep -v '^h$')

note "coop-soak $SCENE: $(peers)"
$P all god >/dev/null
$P all autodlg 1 0 >/dev/null
for n in $(peers); do waitplay "$n" 90 skip || echo "  $n not in play"; done
check s0-start

# --- pickups: one each, in different rooms
mapfile -t PK < <($P h pickups | grep -E "item=[A-Za-z]" | grep -v "room=-" | grep -v "self=off" | awk '{print $2, $NF}' | sort -u -k2,2 | awk '{print $1}')
note "pickups: ${#PK[@]} candidates"
i=0
for n in $(peers); do
  [ $i -lt ${#PK[@]} ] || break
  $P "$n" take "${PK[$i]}" | grep -E "take|no pickup|inactive" ; i=$((i + 1))
done
sleep 6
for n in $(peers); do waitplay "$n" 30 >/dev/null; done
$P all inv | grep bag=
check s1-pickups

# --- race: every peer takes the same pickup at the same moment
if [ $i -lt ${#PK[@]} ]; then
  RACE=${PK[$i]}
  note "race on $RACE"
  at=$(( $(date +%s%3N) + 3000 ))
  for n in $(peers); do echo "at $at take $RACE" >> "$(pdir "$n")/cmd.txt"; done
  sleep 10
  for n in $(peers); do waitplay "$n" 30 >/dev/null; done
  $P all inv | grep bag=
  check s2-race
fi

# --- combat: each peer kills a live enemy (clients go through the client hit path)
mapfile -t EN < <($P h enemies | grep -E "state=(sleep|pursuit|attack)" | grep -v "mgr=off" | grep -v "nest=remote" | grep -v "active=False mgr=none" | awk '{print $2}')
note "combat: ${#EN[@]} live enemies"
i=0
for n in $(peers); do
  [ $i -lt ${#EN[@]} ] || break
  mark=$(lines "$n")
  $P "$n" kill "${EN[$i]}" >/dev/null; i=$((i + 1))
  waitfor "$n" "dead after|still [a-z]* after|no live|inactive" 25 "$mark" && tail -n 2 "$(pdir "$n")/out.txt" | grep -E "dead after|still|no live|inactive"
done
sleep 4
for k in $(seq 0 $((i - 1))); do echo "  ${EN[$k]}: host $($P h enemies | grep "${EN[$k]}" | grep -o "state=[a-z]*")"; done
check s3-combat

# --- door: the first client opens a double door the host has never been near
# A door in the client's own (awake) room, unlocked there.
DOOR=$($P "${CL[0]}" doors 60 | grep " double " | grep "open=False locked=False" | head -1 | awk '{print $3}')
if [ -n "$DOOR" ] && [ ${#CL[@]} -ge 1 ]; then
  note "door $DOOR by ${CL[0]}"
  $P "${CL[0]}" door "$DOOR" open >/dev/null
  sleep 1.2
  for n in $(peers); do echo "  $n: $($P "$n" doors | grep "$DOOR" | grep -o "open=[A-Za-z]* locked=[A-Za-z]*")"; done
  # A walk-through opens and closes the door again within half a second (native traverse), so the host's state is
  # checked in its log: it applied the client's open after this step's marker.
  boxcat "$(latest_log h)" | LC_ALL=C awk -v m="==== door $DOOR by" 'index($0, m) { on = 1 } on && /\[Door\] H apply open/ { found = 1 } END { exit !found }' \
    || { echo "  door did not open on the host"; fails=$((fails + 1)); }
  check s4-door
fi

# --- death: the last client goes down, waits for the revive
if [ ${#CL[@]} -ge 1 ]; then
  V=${CL[${#CL[@]} - 1]}
  note "death of $V"
  $P "$V" god 0 >/dev/null
  $P "$V" die | tail -1
  seen=no
  for t in $(seq 1 10); do sleep 1; $P h status | grep -q "| p[0-9]*@[^|]* dead" && { seen=yes; break; }; done
  echo "  host sees the downed proxy: $seen"
  [ $seen = yes ] || fails=$((fails + 1))
  if waitplay "$V" 60; then echo "  $V revived: $(st "$V" room) hp=$(st "$V" hp)"; else echo "  $V NOT revived"; fails=$((fails + 1)); fi
  $P "$V" god >/dev/null
  check s5-revive
fi

# --- late join: the last client leaves, the host takes a pickup, the client comes back
if [ ${#CL[@]} -ge 1 ]; then
  V=${CL[${#CL[@]} - 1]}
  note "late join of $V"
  $P "$V" leave | tail -1
  sleep 3
  LJ=$($P h pickups | grep -E "item=[A-Za-z]" | grep -v "room=-" | grep -v "self=off" | tail -1 | awk '{print $2}')
  [ -n "$LJ" ] && $P h take "$LJ" | grep take
  sleep 5
  from=$(lines "$V")
  $P "$V" connect | tail -1
  waitfor "$V" "party \[p0.*role=Client" 60 "$from" || echo "  $V did not rejoin the party"
  sleep 8
  waitplay "$V" 60 >/dev/null
  $P "$V" god >/dev/null
  check s6-latejoin
fi

echo "coop-soak $SCENE: $fails failing check(s)" | tee -a "$LOG"
[ $fails -eq 0 ]
