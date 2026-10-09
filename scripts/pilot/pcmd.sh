#!/usr/bin/env bash
# pcmd.sh <peer|all> <command...> : send one pilot command (h, c1, c2, c3 or all) and print the reply lines.
#   scripts/pilot/pcmd.sh h status
#   scripts/pilot/pcmd.sh all digest t1
. "$(dirname "$0")/env.sh"
w=$1; shift
if [ "$w" = all ]; then
  for n in $(peers); do echo "== $n"; "$0" "$n" "$@"; done
  exit 0
fi
D="$(pdir "$w")"
[ -d "$D" ] || { echo "no pilot dir for $w" >&2; exit 1; }
n=$(wc -l < "$D/out.txt")
echo "$*" >> "$D/cmd.txt"
# Replies arrive within a few frames; long commands (talk, wait) print later lines on their own.
for i in $(seq 1 40); do sleep 0.25; m=$(wc -l < "$D/out.txt"); [ "$m" -gt "$n" ] && sleep 0.5 && break; done
tail -n +$((n + 1)) "$D/out.txt"
