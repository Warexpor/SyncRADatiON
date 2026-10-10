# Logging

Each install writes `MelonLoader/Latest.log` (paths: `docs/DEV_MACHINE.md`). A line reads `[HH:MM:SS.mmm] [SyncRADation] [Tag] H|C text`: `H` is the host's log, `C` a client's (no letter outside a session). Compare two logs by timestamp; on one machine they share a clock.

## Reading order

1. **`[Log] summary`** (at session end and at quit): how many lines, and every tag that warned or failed, loudest first. `no warnings` means a clean session.
2. **`[Desync] DESYNC`**: the world differed between host and a client. Each line names the object and both values.
3. **`[State]`**: the nearest one before the moment you care about says where everyone was and what the world was doing.
4. The domain tags around that timestamp.

```bash
grep -a -E '\[Log\] summary|DESYNC|\[State\]' Latest.log
```

## What every line should be

- **An edge, not a level.** A line is written when something changes, with the values that explain it (who, which object, before -> after, which room). Nothing is written every frame.
- **Named.** Objects carry their WorldId and a human name: `id=<16 hex> '<object>' in <room>`.
- **Throttled out loud.** A line that can repeat fast goes through `PlaytestLog.Rate`: one per key per interval, and the next one that goes out ends in `(+N suppressed)`. An identical line repeated back to back is written once and followed by `(xN in a row: <line>)`.
- **ASCII.** `Latest.log` mangles anything else.

## API (`Sync/PlaytestLog.cs`, `Sync/LogSink.cs`)

| Call | When |
|------|------|
| `PlaytestLog.Event(tag, msg)` | A state edge. Always on. |
| `PlaytestLog.Rate(tag, key, seconds, msg)` / `RateOk(key, seconds, out suppressed)` | An edge that can come in bursts (shots, enemy state flips). `RateOk` first when the message is costly to build. |
| `PlaytestLog.Verbose(tag, msg)` | Extra detail from code that runs anyway (`VerboseLogging` pref). |
| `PlaytestLog.Warn(tag, msg)` | The mod did not expect this. Counted in the summary. |
| `Guard.Swallow(e)` | A caught exception: first hit at once, then one line per 30 s with the count. |
| `ModRuntime.Log.Msg / Warning / Error` | Same sink (role prefix added, warnings counted); prefer `PlaytestLog`. |

Costly instrumentation (per-frame sampling) belongs behind `ModConfig.DiagnosticsOn` in a trace class (`Sync/FlickerTrace.cs`, `MoveTrace`, `BagTrace`, `EventCamTrace`, `HitchTrace`), change-only.

## Prefs

| Pref | Default | Effect |
|------|---------|--------|
| `VerboseLogging` | false | `Verbose` lines: FMOD Play/Stop, proxy clone / FX internals, puzzle diffs |
| `Diagnostics` | false | The traces: `[Room]`, `[Proxy]`, `[Move]`, `[Bag]`, `[EventCam]`, `[Music]`, `[Hitch] phase=`, full Harmony audit (restart to change) |
| `DesyncCheck` | true | Host: send the world digest for the live desync check |
| `DesyncCheckSeconds` | 20 | Seconds between checks (5-300) |

For a bug report: `VerboseLogging` and `Diagnostics` on, on every install, quit cleanly, send every player's `Latest.log`.

## Session picture

| Line | Meaning |
|------|---------|
| `[State] <scene> \| me pN '<room>' gs= hp= weapon= at= \| pM '<room>' d= ping= \| enemies A/T alive, W awake, H hunting, inCombat= presence= \| cutscene <names> \| fps=` | Written when any of scene, room, loading, a player down, a peer's room or scene, alive enemies, hunting, combat statics or the running cutscene changes (at most one every 2 s; a cutscene flipping cutscene/dialogue and a door transit do not count), and every 30 s as `(heartbeat)`. |
| `[Net] 30s tx N msg X KB top: <Type> n/KB ... \| rx ... \| pN ping= lost=` | Traffic by message type, top five by bytes each way, and the link. |
| `[Log] summary (session end\|last player left\|quit): N lines, warnings W (Tag n, ...), errors E (...)` | See above. |
| `[Room] pN 'old' -> 'new'` | A remote player's room as this peer knows it. `[Room] enter` / `chunk ON\|OFF` are this peer's own (Diagnostics). |

## Desync check (`Domains/Session/DesyncCheck.cs`)

Every `DesyncCheckSeconds` the host sends its digest of the shared world; each client builds its own at that moment and compares. The digest is the pilot's (`docs/PILOT.md`, Digest): scene, shared story flags, key ring, storage box, enemies (alive + HP), doors, pickups, puzzles, cutscenes. A key that differs in two checks in a row is a desync:

```
[Desync] C DESYNC door2 0941EAC23D2F387C: host=open=False locked=False client=open=True locked=False
[Desync] C resolved door2 0941EAC23D2F387C
[Desync] C check #15: 0 desync open, 0 differing now, 284 lines compared, 31 not compared (asleep / per player), 4 ms
[Desync] H p1 DESYNC door2 0941EAC23D2F387C: host=... client=...
```

The host's log carries each client's lines as `[Desync] H pN ...`. Two in a row because the digests are taken a network hop apart (a door mid-swing, a hit in flight). Not compared, by design: a room one peer never woke (`asleep`), per-player puzzle state (`EnemyManagerState`), the AraNest activated bit, enemy display names, how a taken pickup is gone (`claimed` / `hidden` / `gone`). The rules are `Domains/Session/DesyncCompare.cs`, unit tested, the same as `scripts/pilot/digest-diff.py`. No check runs while loading, in a menu scene, with a scene mismatch or during a join dump.

## Domain lines worth knowing

| Line | Meaning |
|------|---------|
| `[Enemy] H state <name> id= a->b stagger= target=host\|pN\|- hp= room=` | Host: an enemy woke, died, staggered or switched player (`->` marks what changed). Pursuit / attack flips are one line per enemy per 5 s with the count. A sleeping enemy is not logged. |
| `[Enemy] C hit <name> id= dmg= ... -> host` / `[Enemy] H TakeDamage id= dmg= hp=a->b state=a->b` | A client's hit as sent and as the host applied it. `(downed, revive hp)`: native code wrote the revive HP. |
| `[Enemy] C client <id> <name> active= hostAlive= hostState=` | Client: what its puppet shows (Diagnostics). |
| `[Weapon] shot <weapon> mag= room=` / `local a -> b` | The local player's shots and weapon changes. |
| `[Cutscene] start '<name>' ... by=local trigger\|party` / `skip` / `end ... after N s: completed\|stopped, not completed\|object gone` | What ran on this peer. The wire side is `[Story] send\|apply CutsceneStart`, `[Interact] ... CutsceneStart\|CutsceneDone`. |
| `[Door] open\|close id= 'Inter' in <room>` / `apply open\|close ...` / `hold remote close` / `held close now` | Local door edges, applied remote ones, and a close held behind the local transit. |
| `[Story] commit flags=N [key=value, ...]` / `apply commit ... [keys]` | Story flags sent / applied (an incremental commit lists its keys). |
| `[Interact] request\|ok\|FAIL <Kind> id= <key=value>` | A client request and the host's verdict. |
| `[Music] inCombat= enemyPresence= here=` | The statics that drive combat music flipped (Diagnostics). |
| `[Hitch] ...` / `[Stall] ...` | Frame spikes and main-thread stalls (`docs/DEV_MACHINE.md`). After `[Session] application quit` a stall is Unity's teardown. |
