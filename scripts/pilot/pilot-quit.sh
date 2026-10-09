#!/usr/bin/env bash
# pilot-quit.sh [tag] : collect every peer's MelonLoader log, pilot transcript and digests into
# $PILOT_LOG_DIR/<tag>/, print the run report, then quit the games and take the box down.
#   KEEP=1 collects without stopping anything.
. "$(dirname "$0")/env.sh"
TAG="${1:-last}"
OUT="$PILOT_LOG_DIR/$TAG"
rm -rf "$OUT"; mkdir -p "$OUT"
for n in h c1 c2 c3; do
  [ -d "$(pdir "$n")" ] || continue
  boxcat "$(latest_log "$n")" > "$OUT/$n-Latest.log"
  [ -s "$OUT/$n-Latest.log" ] || rm -f "$OUT/$n-Latest.log"
  cp "$(pdir "$n")/out.txt" "$OUT/$n-pilot.txt" 2>/dev/null
  for d in "$(pdir "$n")"/digest-*.txt "$(pdir "$n")"/*.png; do [ -f "$d" ] && cp "$d" "$OUT/$n-$(basename "$d")"; done
done
cp "$PILOT_LOG_DIR/mem.txt" "$OUT/mem.txt" 2>/dev/null
"$(dirname "$0")/report.sh" "$OUT" | tee "$OUT/report.txt"
if [ "${KEEP:-0}" != 1 ]; then
  for n in c3 c2 c1 h; do [ -d "$(pdir "$n")" ] && echo quit >> "$(pdir "$n")/cmd.txt"; done
  sleep 4
  box down >/dev/null 2>&1
  systemctl --user stop "$PILOT_SLICE" 2>/dev/null
fi
echo "logs: $OUT"
