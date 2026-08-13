# SyncRADation — SIGNALIS Multiplayer Mod

**Status:** v0.4.2-dev — protocol v6. Host-authoritative world/story + native presentation/FMOD. Dual-instance playtest required. Decompile: `C:\Users\amicu\Desktop\Dev\SIGNALIS DECOMPILED`.

## Product

LAN multiplayer MelonLoader mod for SIGNALIS (Unity IL2CPP / Unhollower-style Managed). Host-authoritative world + peer-authored avatars over LiteNetLib.

## Controls

- F2 — multiplayer menu (Host / Connect / Resync / status)
- F3 — quick connect (saved IP/port)
- F6 / F11 — item giver / entity spawner
- F7 — location teleporter (chapters + rooms in current level)

- G — drop selected item
- E — pick up nearby **dropped** (player-dropped) item

## What is synced (0.4.2)

| Area | Authority | Notes |
|------|-----------|--------|
| Avatar proxy + anim/bones/weapons | Peer | ~30 Hz state, bones ~15 Hz |
| Enemies | Host | WorldId snaps; **native TakeDamage**; client hits Harmony→host; `playerPos` all non-dead states |
| Doors (double / sliding) | Any peer emit, host relay | Visual open/close via native methods |
| ConnectedDoors (room links) | Lock only | **Never** sync traverse / StartA/B — room entry is local |
| Chapter / scene load | Host | SceneFollow via `AsyncLoader` / `SceneHelper` / `LoadLevelZone` |
| Story (SProgress, Dialoguer, cutscenes, EventScreen, END_Manager) | Host | Full slot dump on join; clients Invoke the same UnityEvents |
| World FMOD | Host Play/Stop | StudioEventEmitter by WorldId; skip Elster + radio UI; tuner freq local |
| Puzzles / locks / elevators / radio module / storage / event zones / alert | Host | **WorldId-keyed**; storage **contents** shared; radio **module only** (no freq clobber) |
| World ItemPickups | Host claim/grant | Claimer gets item; unique keys go on the **party key ring** |
| Player-dropped items | Peer + relay | G drop / E pickup; shared “Object” type grants |
| Death | Asymmetric | Client downed (drops bag); host death `SaveManager.Load` for both |
| Bosses (END / Chimera / Mynah / Kolibri / Adler) | Host | `END_Boss.Elster` / `BOS_Adler.Elster`; Kolibri dead/intensity |
| Friendly fire | Opt-in | Default OFF |
| Inventories | Independent | 6-slot bags stay personal; box + key ring are shared |

## Architecture

- `LanNetworkManager` — LiteNetLib, ids 0..N-1, host relay, join snapshot
- `WorldId` / `WorldRegistry` — `hash(scene + hierarchy path)` — never `GetInstanceID()`
- `EnemySyncService` / `DoorSyncService` / `PuzzleSyncService` / `BossSyncService` / `WorldPickupSyncService`
- `PlayerProxyBuilder` — visual clone; `NetworkDamageSystem` — local HP/death/drop

## Protocol

- **ProtocolVersion = 6**
- Port default `7777`, key `SyncRADation`
- v6: full SProgress dump, UnityEvent presentation, FmodEmitter Play/Stop

## Build / deploy

```powershell
dotnet build "C:\MyProjects\SyncRADation (SIGNALIS MP REMAKE)\SyncRADation.csproj" -c Debug
```

Copies to:

- `$(SignalisDir)\Mods` default `C:\MyProjects\SIGNALIS\Mods`
- `$(ClientSignalisDir)\Mods` Steam install if present

## Decompile reference

`C:\Users\amicu\Desktop\Dev\SIGNALIS DECOMPILED\`

## Hard rules

1. No cross-peer identity via `GetInstanceID()`
2. Prefer host authority for world state
3. Independent inventories unless explicitly designed otherwise
4. Claims of “works” need dual-instance playtest
