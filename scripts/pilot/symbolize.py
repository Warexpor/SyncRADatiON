#!/usr/bin/env python3
"""Name the GameAssembly frames of [Stall] native stacks from the decompile's "// Class$$Method @ RVA 0x..." lines.

usage: symbolize.py <Latest.log> [more logs...]   (DECOMPILE env overrides the Ghidra pseudo-C folder)
Prints each [Stall] line that has native frames, then one frame per line: module+RVA and the function that contains it
(the nearest function start at or below the RVA; other modules are left as they are).
"""
import bisect
import os
import re
import sys

DEFAULT = os.path.expanduser("~/Archive/Windows-Desktop/Dev/SIGNALIS DECOMPILED/07_Ghidra_pseudoC")
HEAD = re.compile(r"^// (.+?) @ RVA 0x([0-9a-fA-F]+)\s*$")
FRAME = re.compile(r"([A-Za-z0-9_.]+)\+0x([0-9a-f]+)")


def load(root):
    starts = {}
    for dirpath, _, files in os.walk(root):
        for f in files:
            if not f.endswith(".c"):
                continue
            with open(os.path.join(dirpath, f), encoding="utf-8", errors="replace") as fh:
                for line in fh:
                    m = HEAD.match(line)
                    if m:
                        starts.setdefault(int(m.group(2), 16), m.group(1))
    rvas = sorted(starts)
    return rvas, [starts[r] for r in rvas]


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    rvas, names = load(os.environ.get("DECOMPILE", DEFAULT))
    for path in sys.argv[1:]:
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if "[Stall]" not in line or " native " not in line:
                    continue
                print(line.split(" native ")[0].strip())
                for mod, off in FRAME.findall(line.split(" native ", 1)[1]):
                    rva = int(off, 16)
                    name = ""
                    if mod.lower() == "gameassembly.dll" and rvas:
                        i = bisect.bisect_right(rvas, rva) - 1
                        if i >= 0:
                            name = "%s +0x%x" % (names[i], rva - rvas[i])
                    print("    %s+0x%x  %s" % (mod, rva, name))
    return 0


if __name__ == "__main__":
    sys.exit(main())
