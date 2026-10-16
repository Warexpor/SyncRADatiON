#!/usr/bin/env bash
# report.sh <log dir> : one block per peer from a collected pilot run (pilot-quit.sh): handshake, scenes, registry
# checksums, warnings by tag, Unity errors, exceptions and the pilot's failed commands.
D="${1:?log dir}"
for f in "$D"/*-Latest.log; do
  [ -f "$f" ] || continue
  n=$(basename "$f" -Latest.log)
  p="$D/$n-pilot.txt"
  echo "=== $n"
  echo "  version   $(grep -a -m1 -o 'SyncRADation v[0-9.]*' "$f")  protocol $(grep -a -m1 -o 'Protocol v[0-9]*' "$f" | cut -c10-)"
  echo "  handshake $(grep -a -c 'Handshake OK' "$f") ok, $(grep -a -i 'rejected\|mismatch' "$f" | grep -a -c -v -e 'mismatch=False' -e '\[Scene\] MISMATCH' -e 'scene mismatch') reject/mismatch line(s)"
  echo "  scenes    $(grep -a -o "\[Scene\] .*loaded '[^']*'" "$f" | sed "s/.*loaded '//; s/'//" | tr '\n' ' ')"
  grep -a -o "\[WorldRegistry\] scene='[^']*'.*checksum=[^ ]*" "$f" | sed 's/^/  registry  /' | sort -u | tail -6
  echo "  warnings  $(grep -a -c '\[W\]\|\[WARNING\]\|Warning' "$f") total; by tag:"
  grep -a -E '\[W\]|WARNING|Warning' "$f" | grep -a -o '\[[A-Za-z]*\]' | grep -v -E '^\[(W|WARNING|SyncRADation)\]$' | sort | uniq -c | sort -rn | head -12 | sed 's/^/            /'
  echo "  guard     $(grep -a -c '\[Guard\]' "$f")   divergence $(grep -a -c 'WorldId divergence' "$f")   exceptions $(grep -a 'Exception' "$f" | grep -a -c -v 'Contacting RemoteAPI Host')"
  if [ -f "$p" ]; then
    echo "  unity     $(grep -a -c '^[0-9:.]* unity ' "$p") error line(s)"
    grep -a -E '^[0-9:.]* unity ' "$p" | cut -c14- | cut -c1-160 | sort | uniq -c | sort -rn | head -8 | sed 's/^/            /'
    echo "  pilot     $(grep -a -c '^[0-9:.]* > ' "$p") command(s), $(grep -a -c 'failed:\|pilot error' "$p") failed"
    grep -a -E 'failed:|pilot error' "$p" | head -5 | sed 's/^/            /'
  fi
done
for f in "$D"/report-*.txt "$D"/checks.txt; do [ -f "$f" ] && { echo "=== $(basename "$f")"; cat "$f"; }; done
if [ -f "$D/mem.txt" ]; then
  echo "=== memory (peak per instance, mem-guard)"
  grep -o ' [a-z0-9]*=[0-9]*M' "$D/mem.txt" | sort -t= -k1,1 -k2,2n | awk -F= '{p[$1]=$2} END {for (k in p) printf " %s peak %s\n", k, p[k]}'
  grep KILL "$D/mem.txt"
fi
