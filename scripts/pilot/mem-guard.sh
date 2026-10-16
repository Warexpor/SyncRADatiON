#!/usr/bin/env bash
# mem-guard.sh : started by pilot-run.sh, ends with the box. Every 2 s: each SIGNALIS instance's resident memory and
# the pilot slice's total and tmpfs (shmem) use; a line to $PILOT_LOG_DIR/mem.txt every 10 s or when an instance grew by
# 256 MB. An instance past PILOT_PROC_MAX_MB is killed (KILL line), so a leak shows up as a dead peer and a log line
# instead of a frozen desktop. The slice cap (PILOT_MEM, no swap) is the backstop.
. "$(dirname "$0")/env.sh"
LOG="$PILOT_LOG_DIR/mem.txt"; mkdir -p "$PILOT_LOG_DIR"; : > "$LOG"
# One guard per run: a newer one (it rewrites the pid file) ends this one.
echo $$ > "$PILOT_LOG_DIR/mem-guard.pid"
cg=$(systemctl --user show -p ControlGroup --value "$PILOT_SLICE" 2>/dev/null)
cgdir="/sys/fs/cgroup$cg"
declare -A last=()
tick=0
while systemctl --user is-active -q "$PILOT_SLICE" 2>/dev/null && [ "$(cat "$PILOT_LOG_DIR/mem-guard.pid" 2>/dev/null)" = $$ ]; do
  line="" grew=0
  for pid in $(pgrep -f 'SIGNALIS\.exe' 2>/dev/null); do
    [ -r "/proc/$pid/status" ] || continue
    grep -q '^Name:.*SIGNALIS' "/proc/$pid/status" 2>/dev/null || continue
    rss=$(awk '/^VmRSS:/ {print int($2 / 1024)}' "/proc/$pid/status" 2>/dev/null); [ -n "$rss" ] || continue
    peer=$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | grep -o 'pilot/[a-z0-9]*' | head -1 | cut -d/ -f2)
    peer=${peer:-pid$pid}
    line+=" $peer=${rss}M"
    [ $((rss - ${last[$peer]:-0})) -ge 256 ] && grew=1
    last[$peer]=$rss
    if [ "$rss" -gt "$PILOT_PROC_MAX_MB" ]; then
      kill -9 "$pid" 2>/dev/null
      echo "$(date +%T) KILL $peer pid $pid at ${rss} MB (> PILOT_PROC_MAX_MB $PILOT_PROC_MAX_MB)" | tee -a "$LOG" >&2
    fi
  done
  if [ -d "$cgdir" ]; then
    cur=$(( $(cat "$cgdir/memory.current" 2>/dev/null || echo 0) / 1048576 ))
    shm=$(awk '/^shmem / {print int($2 / 1048576)}' "$cgdir/memory.stat" 2>/dev/null)
    line+=" | slice=${cur}M tmpfs=${shm:-?}M"
  fi
  [ $grew = 1 ] || [ $((tick % 5)) = 0 ] && echo "$(date +%T)$line" >> "$LOG"
  tick=$((tick + 1))
  sleep 2
done
echo "$(date +%T) pilot slice gone" >> "$LOG"
