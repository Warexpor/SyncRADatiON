# SyncRADation — SIGNALIS Multiplayer Mod

**Status:** v0.4.3-dev — protocol v8. Host-authoritative world/story + native presentation/FMOD. Dual-instance playtest required. Decompile (backup): `~/Omarchy_Backup/Desktop/Dev/SIGNALIS DECOMPILED`.

## Product

LAN multiplayer MelonLoader mod for SIGNALIS (Unity IL2CPP / Unhollower-style Managed). Host-authoritative world + peer-authored avatars over LiteNetLib.

## Controls

- F2 — multiplayer menu (Host / Connect / Resync / status)
- F3 — quick connect (saved IP/port)
- F6 / F11 — item giver / entity spawner (any replika type, not current-scene clones)
- F7 — location teleporter (chapters + rooms in current level)

- G — drop selected item (inventory slot, or DROP in the item command list)

Walk up to a dropped prop for the native TAKE prompt (yes/no inspect, ammo count). There is no extra pickup key.

## What is synced (0.4.3)

| Area | Authority | Notes |
|------|-----------|--------|
| Avatar proxy + anim/bones/weapons | Peer | ~30 Hz state + bones |
| Enemies | Host | WorldId snaps; **native TakeDamage**; client hits Harmony→host; host **wakes sleeping-chunk** enemies when a peer is near. Contact ram damage stays native. F11 spawn is host-authored (`EnemySpawn` + `SR_Spawn_*` WorldId) |
| Doors (double / sliding) | Any peer emit, host relay | Visual open/close via native methods |
| ConnectedDoors (room links) | Lock only | **Never** sync traverse / StartA/B — room entry is local. Unique key doors: one solve (party key ring **Key/Object only**), both walk |
| Ladders | Local traverse | Climb is per-player; other peer only hears proxy SFX (no `Interaction.trigger`) |
| Chapter / scene load | Host | SceneFollow via `AsyncLoader` / `SceneHelper` / `LoadLevelZone` — int and string loads both gate. **Wreck↔hole never follows** (each Elster loads `PEN_Hole` when they finish the airlock). Host leaving Penrose still follows. Client F7 is a host load + follow, not `BeginApply` |
| Story (SProgress, Dialoguer, cutscenes, END_Manager) | Host | Full slot dump on join; **books / notes / EventScreen / EventOnlyRoom / airlock (`PEN_Titles`) / lock-flavor lines stay local**. Story Dialoguer Start (all overloads) is client→host then presentation replay; Continue/End apply on clients. **Other-room cutscenes / EventZones do not Start/Invoke** on the observer |
| World FMOD | Host Play/Stop | StudioEventEmitter by WorldId; skip Elster + radio UI + **Music/Cutscenes/Ambience beds**; tuner freq local |
| Puzzles / locks / elevators / radio module / storage / event zones / alert | Host + client emit | **WorldId-keyed**; client emits puzzle/lock types only (not `Interaction.trigger` / EventZone / combat); apply **snaps flags + doors**, never EventScreen / `trigger()`; cryo/codepad/pump/pipes/hatch live-apply native Open/Drain/TurnValve; unlocked location doors stay open for the party |
| Host disconnect | Client goes offline | Restores play + input (no freeze) |
| World ItemPickups | Host claim/grant | Claimer gets item; unique **Key/Object** go on the **party key ring**; ammo/docs do not. Client bag keys still unlock UseItem (hatch card) |
| Player-dropped items | Peer + relay | G or inventory **DROP**; floor snap; native TAKE inspect (yes/no + count) then grant; join dump; bag-full reject |
| Death | Asymmetric | Client downed (drops bag); native `HurtElster` HP; host death `SaveManager.Load` for both |
| Bosses (END / Chimera / Mynah / Kolibri / Adler) | Host | `END_Boss.Elster` / `BOS_Adler.Elster`; Kolibri dead/intensity |
| Friendly fire | Opt-in | Default OFF |
| Inventories | Independent | 6-slot bags stay personal; box + key ring are shared |

## Architecture

- `LanNetworkManager` — LiteNetLib, ids 0..N-1 (recycled 1..MaxPlayers-1), host relay, join snapshot **unicast**, `PlayerRoster` leave/join
- `WorldId` / `WorldRegistry` — `hash(scene + hierarchy path)` — never `GetInstanceID()`
- `EnemySyncService` / `DoorSyncService` / `PuzzleSyncService` / `BossSyncService` / `WorldPickupSyncService`
- `PlayerProxyBuilder` — visual clone; `NetworkDamageSystem` — local HP/death/drop

## Protocol

- **ProtocolVersion = 8**
- Port default `7777`, key `SyncRADation`
- v6: full SProgress dump, UnityEvent presentation, FmodEmitter Play/Stop
- v7: `PlayerRoster` (3+ peers), recycled client ids, join/resync dump to the requester only
- v8: host-authored `EnemySpawn` (F11 templates by `AnEnemyType` from in-memory prefabs; does **not** additive-load chapters)

## This machine (dual-instance, Linux + Proton)

Same PC. **Steam = host. Copy = client.** Both are Windows SIGNALIS under Proton. F3 is `127.0.0.1:7777`. `VerboseLogging` off unless hunting FMOD / proxy clone. `boot.config` `single-instance=0` on both. MelonLoader **0.5.7** on both (`version.dll` + `MelonLoader/`).

Proton will ignore MelonLoader’s `version.dll` unless native wins over Wine’s builtin. Host Steam launch options: `WINEDLLOVERRIDES="version=n,b" %command% -screen-fullscreen 0 -screen-width 2560 -screen-height 720` (also prefix `DllOverrides`). `secondsignalis` uses the same. Hyprland floats **Steam host top-half** (`steam_app_1262350`) and **Proton client bottom-half** (`SIGNALIS.exe`) for dual-box playtest.

| Role | Install | Launch | MelonLoader log |
|------|---------|--------|-----------------|
| **Host** | `~/.local/share/Steam/steamapps/common/SIGNALIS` | Steam (Proton) | `.../SIGNALIS/MelonLoader/Latest.log` |
| **Client** | `~/Work/MyProjects/SIGNALIS` | `secondsignalis` (or `scripts/launch-client.sh`; own Proton prefix `compatdata/syncradation-client`) | `~/Work/MyProjects/SIGNALIS/MelonLoader/Latest.log` |

Prefs: `.../SIGNALIS/UserData/MelonPreferences.cfg` on each install.

Debug build copies the DLL to **both** `Mods/` folders. csproj names: `SignalisDir` = copy, `ClientSignalisDir` = Steam (deploy labels, not playtest roles).

Grep logs: `[Harmony]` `[Story]` `[Interact]` `[FMOD]` `[KeyRing]` `[StorageBox]` `[Scene]` `[Damage]` `[Door]` `[Puzzle]` `[Pickup]` `[Hitch]` `[Spawn]` `[Enemy]` `[Proxy]` `[Weapon]`. Always-on: session, scene follow, story send/apply, interact ok/FAIL, key ring, pickup claim, door open/close, puzzle solve/snap, death, MISS/warn. `VerboseLogging`: FMOD Play/Stop, clone/FX internals, incremental puzzle apply. Hitch prints only on a real spike.

## Decompile reference

`~/Omarchy_Backup/Desktop/Dev/SIGNALIS DECOMPILED/`

## Build / deploy

```bash
dotnet build "$HOME/Work/MyProjects/SyncRADation (SIGNALIS MP REMAKE)/SyncRADation.csproj" -c Debug
```

Copies to:

- `$(SignalisDir)/Mods` → `~/Work/MyProjects/SIGNALIS/Mods` (client copy)
- `$(ClientSignalisDir)/Mods` → Steam install (host)

Client launch (second box):

```bash
secondsignalis
```

(`~/.local/bin/secondsignalis` → `scripts/launch-client.sh`)

## Hard rules

1. No cross-peer identity via `GetInstanceID()`
2. Prefer host authority for world state
3. Independent inventories unless explicitly designed otherwise
4. Claims of “works” need dual-instance playtest
