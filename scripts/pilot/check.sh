#!/usr/bin/env bash
# check.sh <tag> [delay-seconds] : every peer writes its world digest at the same moment, then each client's is
# diffed against the host's (digest-diff.py). Exit 1 when a shared line differs.
. "$(dirname "$0")/env.sh"
TAG=$1
DELAY="${2:-1}"
at=$(( $(date +%s%3N) + DELAY * 1000 ))
for n in $(peers); do rm -f "$(pdir "$n")/digest-$TAG.txt"; echo "at $at digest $TAG" >> "$(pdir "$n")/cmd.txt"; done
for n in $(peers); do
  for i in $(seq 1 $((DELAY * 10 + 100))); do [ -s "$(pdir "$n")/digest-$TAG.txt" ] && break; sleep 0.1; done
done
args=()
for n in $(peers); do args+=("$n=$(pdir "$n")/digest-$TAG.txt"); done
python3 -I "$(dirname "$0")/digest-diff.py" "$TAG" "${args[@]}"
