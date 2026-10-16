#!/usr/bin/env python3
"""digest-diff.py <tag> h=<file> c1=<file> ... : diff each client's world digest against the host's.

A digest line is "section key value..." (Sync/TestPilot.Digest.cs). The "self" section is per peer and only shown;
every other line must match the host's. Prints a per-section summary and every differing key; exit 1 on a diff.
"""
import sys
from collections import defaultdict

# Puzzle types whose state is each player's own by design (docs/SYNC.md): shown, never a failure.
PER_PLAYER_TYPES = {"EnemyManagerState"}


def load(path):
    rows = {}
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.rstrip("\n")
                if not line:
                    continue
                parts = line.split(" ", 2)
                section = parts[0]
                key = parts[1] if len(parts) > 1 else ""
                rows[(section, key)] = parts[2] if len(parts) > 2 else ""
    except FileNotFoundError:
        return None
    return rows


def main():
    tag = sys.argv[1]
    peers = [a.split("=", 1) for a in sys.argv[2:]]
    digests = {name: load(path) for name, path in peers}
    host = digests.get("h")
    if host is None:
        print(f"[{tag}] no host digest")
        return 1
    bad = 0
    sections = sorted({s for (s, _) in host if s != "self"})
    for name, rows in digests.items():
        self_rows = {k: v for (s, k), v in (rows or {}).items() if s == "self"}
        print(f"[{tag}] {name}: " + " ".join(f"{k}={v}" for k, v in sorted(self_rows.items())))
    for name, rows in digests.items():
        if name == "h":
            continue
        if rows is None:
            print(f"[{tag}] {name}: NO DIGEST")
            bad += 1
            continue
        diffs = defaultdict(list)
        info = defaultdict(int)
        keys = {k for k in host if k[0] != "self"} | {k for k in rows if k[0] != "self"}
        for k in sorted(keys):
            hv, cv = host.get(k), rows.get(k)
            if k[0] == "pickup":
                # The taker destroyed its prop, the others hide it; a prop a peer never scanned was already gone there.
                hv = "here" if hv == "here" else "taken"
                cv = "here" if cv == "here" else "taken"
            if k[0] == "enemy" and hv is not None and cv is not None:
                # The trailing name is for reading: EnemyManager.delayedInitialization renames a room's enemies
                # ("Enemy N TYPE") once that room's manager started on a peer; the WorldId was pinned before.
                hv, cv = hv.rsplit(" ", 1)[0], cv.rsplit(" ", 1)[0]
            if k[0] == "puzzle" and k[1].startswith("AraNest:") and hv is not None and cv is not None:
                # b=<triggered><activated><dead>: activated is this peer's own player being in the nest's room.
                hv, cv = (" ".join(t[:3] + "-" + t[4:] if t.startswith("b=") else t for t in v.split()) for v in (hv, cv))
            if hv == cv:
                continue
            if k[0] == "puzzle" and k[1].split(":")[0] in PER_PLAYER_TYPES:
                info["per-player " + k[1].split(":")[0]] += 1
                continue
            if hv is not None and cv is not None and (hv.endswith(" asleep") or cv.endswith(" asleep")):
                # Only differs where one peer never woke that room: native Start / the held re-snap settles it on entry.
                info["asleep " + k[0]] += 1
                continue
            if hv is not None and cv is not None and ("=asleep" in hv or "=asleep" in cv):
                # A field only one peer has awake (its room chunk is loaded there): compare the rest.
                hv_, cv_ = hv.split(), cv.split()
                drop = {t.split("=")[0] for t in hv_ + cv_ if t.endswith("=asleep")}
                if [t for t in hv_ if t.split("=")[0] not in drop] == [t for t in cv_ if t.split("=")[0] not in drop]:
                    continue
            if hv != cv:
                diffs[k[0]].append((k[1], hv, cv))
        summary = []
        for s in sorted(set(sections) | set(diffs)):
            n_host = sum(1 for (sec, _) in host if sec == s)
            summary.append(f"{s} {n_host}" + (f" !{len(diffs[s])}" if diffs.get(s) else ""))
        status = "MATCH" if not diffs else "DIFF"
        extra = ("  (not compared: " + ", ".join(f"{k} {v}" for k, v in sorted(info.items())) + ")") if info else ""
        print(f"[{tag}] h vs {name}: {status} | " + ", ".join(summary) + extra)
        for s, rows_ in sorted(diffs.items()):
            for key, hv, cv in rows_[:40]:
                print(f"    {s} {key}: host={hv if hv is not None else '(none)'}  {name}={cv if cv is not None else '(none)'}")
            if len(rows_) > 40:
                print(f"    {s}: ... {len(rows_) - 40} more")
        if diffs:
            bad += 1
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
