# Logging

Each install writes `MelonLoader/Latest.log` (paths: `docs/DEV_MACHINE.md`). A line reads `[HH:MM:SS.mmm] [SyncRADation] [Tag] H|C text`: `H` is the host's log, `C` a client's (no letter outside a session). Compare two logs by timestamp; on one machine they share a clock.

## Reading order

1. **`[Log] summary`** (at session end and at quit): how many lines, and every tag that warned or failed, loudest first. `no warnings` means a clean session.
2. **`[Desync] DESYNC`**: the world differed between host and a client. Each line names the object and both values.
3. **`[Unity]`** and **`[Call] ... THREW`**: the game's own errors, and a patched native that threw.
4. **`[Call] natives the mod skipped this session`** (just above the summary): what the mod kept the game from doing.
5. **`[State]`**: the nearest one before the moment you care about says where everyone was and what the world was doing.
6. Around that timestamp: `[Self]` (what the player did), `[Call]` (what the game did), `[Wire]` (what crossed the network), then the domain tags (what the mod did with it).

```bash
grep -a -E '\[Log\] summary|DESYNC|\[Unity\]|THREW|SKIPPED|\[State\]' Latest.log
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
| `Diagnostics` | false | The traces: `[Room]`, `[Proxy]`, `[Move]`, `[EventCam]`, `[Music]`, `[Hitch] phase=`, full Harmony audit (restart to change) |
| `WireTrace` | true | `[Wire]` lines |
| `CallTrace` | true | `[Call]` lines (restart to change) |
| `DesyncCheck` | true | Host: send the world digest for the live desync check |
| `DesyncCheckSeconds` | 20 | Seconds between checks (5-300) |

For a bug report: `VerboseLogging` and `Diagnostics` on, on every install, quit cleanly, send every player's `Latest.log`.

## Session picture

| Line | Meaning |
|------|---------|
| `[State] <scene> \| me pN '<room>' gs= hp= weapon= at= \| pM '<room>' d= ping= \| enemies A/T alive, W awake, H hunting, inCombat= presence= \| cutscene <names> \| fps=` | Written when any of scene, room, loading, a player down, a peer's room or scene, alive enemies, hunting, combat statics or the running cutscene changes (at most one every 2 s; a cutscene flipping cutscene/dialogue and a door transit do not count), and every 30 s as `(heartbeat)`. |
| `[Net] 30s tx N msg X KB top: <Type> n/KB ... \| rx ... \| pN ping= lost=` | Traffic by message type, top five by bytes each way, and the link. |
| `[Log] summary (session end\|last player left\|quit): N lines, warnings W (Tag n, ...), errors E (...)` | See above. |
| `[Owners] H scenes 0=<scene>;1=<scene>;...` / `[Owners] C '<scene>' owner pA -> pB (this player runs it)` / `send scene world to pN\|this scene` / `pN world dump from owner pM` | The host's player -> scene table as sent, and each peer's scene owner edges (handoffs). |
| `[Room] pN 'old' -> 'new'` | A remote player's room as this peer knows it. `[Room] enter` / `chunk ON\|OFF` are this peer's own (Diagnostics). |

## The three traces

Each answers one question about a moment, for every domain at once, so a domain without lines of its own is still covered.

| Line | Question | Source |
|------|----------|--------|
| `[Self] gs play -> dialogue in '<room>' prompt='<interaction>'` / `hp a -> b` / `suspendInput=` / `grappled=` / `cloaked=` / `player object GONE\|INACTIVE` | What did this player do, what happened to them? | `Sync/SelfWatch.cs`: native `PlayerState` statics, every frame, edges only |
| `[Call] <Type>.<method> '<object>' ran\|SKIPPED\|THREW ...` | What did the game do, and what did the mod stop? | `Sync/CallTrace.cs`: a postfix and a finalizer on every game method a `[HarmonyPatch]` targets. `SKIPPED` = a mod prefix returned false and the native body did not run. Per method 4 lines a second (8 for skips), then `(+N suppressed)`; over 25 calls a second it is counted: `x N in 30s (S SKIPPED)`. Left out: `Update` family, coroutine `MoveNext`, `Awake` / `OnEnable` / `OnDisable`, inventory queries, FMOD play / stop |
| `[Wire] tx\|rx pN <Type> <bytes>B Field=value ...` | What crossed the network? | `Sync/WireTrace.cs`: every message, decoded from its own struct by reflection (a new message type needs nothing). Arrays show their length and first three entries. Left out: `PlayerState`, `BonePose`, `EnemyState`, `BossState`, `PlayerVital`, `WorldDigest` (see `[Net]`). 10 lines per type per second, then `(+N suppressed)` |

A sync bug reads as a break in the chain: `[Self]` and `[Call]` on the acting peer, `[Wire] tx` there, `[Wire] rx` on the other, the domain's `apply` line, `[Call]` on the receiving peer. The first link that is missing is where it went wrong.

`[Unity] <Error|Exception|Assert> #n: <message>` + stack (`Sync/UnityLogTap.cs`) is the game's own log: the first two of a message with the stack, then at 10, 100, 1000. `(also offline)` marks one the base game also logs outside a session.

## Desync check (`Domains/Session/DesyncCheck.cs`)

Every `DesyncCheckSeconds` the host sends its digest of the shared world; each client builds its own at that moment and compares. The digest is the pilot's (`docs/PILOT.md`, Digest): scene, shared story flags, key ring, storage box, enemies (alive + HP), doors, pickups, puzzles, cutscenes. A key that differs in two checks in a row is a desync:

```
[Desync] C DESYNC door2 0941EAC23D2F387C: host=open=False locked=False client=open=True locked=False
[Desync] C resolved door2 0941EAC23D2F387C
[Desync] C check #15: 0 desync open, 0 differing now, 284 lines compared, 31 not compared (asleep / per player), 4 ms
[Desync] H p1 DESYNC door2 0941EAC23D2F387C: host=... client=...
```

On top of the shared world the check compares **views** (`ViewLines`): `player pN room= down= weapon= pos=` as that player's own peer has it and as the other shows its proxy, and `enemyv <id> mode=sleep|hunt pos=` for every living enemy. A position may differ by `DesyncCompare.ViewPosTolerance` (3 units); a view one side does not have (a player in another scene, an enemy in a room asleep there) is not compared; `proxy=missing` is a player in this scene with no proxy.

```
[Desync] C DESYNC player p0: host=room=Mensa down=False weapon=Pistol pos=10.0,4.0,0.0 client=room=Kitchen down=False weapon=None pos=55.1,4.0,0.0
[Desync] C DESYNC enemyv 5B21...: host=mode=hunt pos=12.0,3.0,0.0 client=mode=sleep pos=40.0,3.0,0.0
```

The host's log carries each client's lines as `[Desync] H pN ...`. Two in a row because the digests are taken a network hop apart (a door mid-swing, a hit in flight). Not compared, by design: a room one peer never woke (`asleep`), per-player puzzle state (`EnemyManagerState`), the AraNest activated bit, enemy display names, how a taken pickup is gone (`claimed` / `hidden` / `gone`). The rules are `Domains/Session/DesyncCompare.cs`, unit tested, the same as `scripts/pilot/digest-diff.py`. No check runs while loading, in a menu scene, with a scene mismatch or during a join dump.

## Domain lines worth knowing

| Line | Meaning |
|------|---------|
| `[Enemy] H state <name> id= a->b stagger= target=host\|pN\|- hp= room=` | Host: an enemy woke, died, staggered or switched player (`->` marks what changed). Pursuit / attack flips are one line per enemy per 5 s with the count. A sleeping enemy is not logged. |
| `[Enemy] C hit <name> id= dmg= ... -> host` / `[Enemy] H TakeDamage id= dmg= hp=a->b state=a->b` | A client's hit as sent and as the host applied it. `(downed, revive hp)`: native code wrote the revive HP. |
| `[Enemy] C puppet <name> id= a->b target= hp= chunk=on\|OFF at=` / `puppet ... jumped N to the host's position` | Client: the host's snapshot changed what this enemy is, who it hunts or whether its room is awake here (`chunk=OFF`: the host fights it in a room this client has asleep). The mirror of `H state`. |
| `[Enemy] C client <id> <name> active= hostAlive= hostState=` | Client: what its puppet shows (Diagnostics). |
| `[Bag] change +Item a->b -Item a->b in '<room>'` | The local inventory changed (polled twice a second). |
| `[Weapon] shot <weapon> mag= room=` / `local a -> b` | The local player's shots and weapon changes. |
| `[Cutscene] start '<name>' ... by=local trigger\|party` / `skip` / `end ... after N s: completed\|stopped, not completed\|object gone` | What ran on this peer. The wire side is `[Story] send\|apply CutsceneStart`, `[Interact] ... CutsceneStart\|CutsceneDone`. |
| `[Door] open\|close id= 'Inter' in <room>` / `apply open\|close ...` / `hold remote close` / `held close now` | Local door edges, applied remote ones, and a close held behind the local transit. |
| `[Story] commit flags=N [key=value, ...]` / `apply commit ... [keys]` | Story flags sent / applied (an incremental commit lists its keys). |
| `[Interact] request\|ok\|FAIL <Kind> id= <key=value>` | A client request and the host's verdict. |
| `[Music] inCombat= enemyPresence= here=` | The statics that drive combat music flipped (Diagnostics). |
| `[Hitch] ...` / `[Stall] ...` | Frame spikes and main-thread stalls (`docs/DEV_MACHINE.md`). After `[Session] application quit` a stall is Unity's teardown. |
