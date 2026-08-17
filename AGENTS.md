# SyncRADation — SIGNALIS Multiplayer Mod

**Status:** v0.4.3-dev — protocol v8. Host-authoritative world/story + native presentation/FMOD. Dual-instance playtest required. Decompile: `C:\Users\amicu\Desktop\Dev\SIGNALIS DECOMPILED`.

## Product

LAN multiplayer MelonLoader mod for SIGNALIS (Unity IL2CPP / Unhollower-style Managed). Host-authoritative world + peer-authored avatars over LiteNetLib.

## Controls

- F2 — multiplayer menu (Host / Connect / Resync / status)
- F3 — quick connect (saved IP/port)
- F6 / F11 — item giver / entity spawner (any replika type, not current-scene clones)
- F7 — location teleporter (chapters + rooms in current level)

- G — drop selected item (inventory or play)
- E — pick up nearby **dropped** (player-dropped) item

## What is synced (0.4.3)

| Area | Authority | Notes |
|------|-----------|--------|
| Avatar proxy + anim/bones/weapons | Peer | ~30 Hz state + bones |
| Enemies | Host | WorldId snaps; **native TakeDamage**; client hits Harmony→host; `playerPos` all non-dead states. F11 spawn is host-authored (`EnemySpawn` + `SR_Spawn_*` WorldId) |
| Doors (double / sliding) | Any peer emit, host relay | Visual open/close via native methods |
| ConnectedDoors (room links) | Lock only | **Never** sync traverse / StartA/B — room entry is local. Unique key doors: one solve (party key ring **Key/Object only**), both walk |
| Ladders | Local traverse | Climb is per-player; other peer only hears proxy SFX (no `Interaction.trigger`) |
| Chapter / scene load | Host | SceneFollow via `AsyncLoader` / `SceneHelper` / `LoadLevelZone` — int and string loads both gate. Penrose defer is **cinematic** (`PEN_Titles.started` / local ViewPoint, 3s latch). Skip/load does not pull the other out of the wreck. Client F7 is a host load + follow, not `BeginApply` |
| Story (SProgress, Dialoguer, cutscenes, END_Manager) | Host | Full slot dump on join; **books / notes / EventScreen / EventOnlyRoom / airlock (`PEN_Titles`) / lock-flavor lines stay local**. Story Dialoguer Start (all overloads) is client→host then presentation replay; Continue/End apply on clients |
| World FMOD | Host Play/Stop | StudioEventEmitter by WorldId; skip Elster + radio UI; tuner freq local |
| Puzzles / locks / elevators / radio module / storage / event zones / alert | Host + client emit | **WorldId-keyed**; client emits puzzle/lock types only (not `Interaction.trigger` / EventZone / combat); apply **snaps flags + doors**, never EventScreen / `trigger()`; cryo/codepad/pump/pipes/hatch live-apply native Open/Drain/TurnValve; unlocked location doors stay open for the party |
| Host disconnect | Client goes offline | Restores play + input (no freeze) |
| World ItemPickups | Host claim/grant | Claimer gets item; unique **Key/Object** go on the **party key ring**; ammo/docs do not. Client bag keys still unlock UseItem (hatch card) |
| Player-dropped items | Peer + relay | G drops the selected stack; E pickup rejects a full bag; shared “Object” type grants |
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

## This machine (dual-instance)

Same PC. **Steam = host. Copy = client.** F3 is `127.0.0.1:7777`. `VerboseLogging` on. `boot.config` `single-instance=0` on both.

| Role | Install | Launch | MelonLoader log |
|------|---------|--------|-----------------|
| **Host** | `C:\Program Files (x86)\Steam\steamapps\common\SIGNALIS` | Steam | `...\SIGNALIS\MelonLoader\Latest.log` |
| **Client** | `C:\MyProjects\SIGNALIS` | `SIGNALIS.exe` (not Steam) | `C:\MyProjects\SIGNALIS\MelonLoader\Latest.log` |

Prefs: `...\SIGNALIS\UserData\MelonPreferences.cfg` on each install.

Debug build copies the DLL to **both** `Mods\` folders. csproj names: `SignalisDir` = copy, `ClientSignalisDir` = Steam (deploy labels, not playtest roles).

Grep logs: `[Harmony]` `[Story]` `[Interact]` `[FMOD]` `[KeyRing]` `[StorageBox]` `[Scene]` `[Damage]` `[Door]` `[Puzzle]` `[Pickup]` `[Hitch]` `[Spawn]`.

## Decompile reference

`C:\Users\amicu\Desktop\Dev\SIGNALIS DECOMPILED\`

## Build / deploy

```powershell
dotnet build "C:\MyProjects\SyncRADation (SIGNALIS MP REMAKE)\SyncRADation.csproj" -c Debug
```

Copies to:

- `$(SignalisDir)\Mods` → `C:\MyProjects\SIGNALIS\Mods` (client copy)
- `$(ClientSignalisDir)\Mods` → Steam install (host)

## Hard rules

1. No cross-peer identity via `GetInstanceID()`
2. Prefer host authority for world state
3. Independent inventories unless explicitly designed otherwise
4. Claims of “works” need dual-instance playtest

## Cursor Cloud specific instructions

The Cloud VM is Linux (Ubuntu). This repo is a **Windows-only** MelonLoader mod, so what a cloud agent can do here is limited to source-level work — you **cannot build or run** the mod in the cloud.

- **Toolchain:** .NET 8 SDK (`dotnet`). `dotnet restore SyncRADation.csproj` succeeds (only pulls the `Microsoft.NETFramework.ReferenceAssemblies.net472` package). This is handled by the environment update script; no manual install needed for source edits.
- **Build is blocked in cloud, by design.** `dotnet build` fails with ~1100 `CS0246` errors (99+ missing types like `GameObject`, `Vector3`, `SProgress`, `Dialoguer`, `ItemPickup`, `PEN_Reaktor`). The csproj references the game's `MelonLoader\Managed` assemblies (`Assembly-CSharp(.dll/-firstpass)`, IL2CPP-unhollowed `UnityEngine.*`, `UnhollowerBaseLib`, `Il2Cppmscorlib`, `FMODUnity`, `AstarPathfindingProject`, `Sirenix.Serialization`) plus `MelonLoader.dll` / `0Harmony.dll`. Those are per-install, proprietary, generated by MelonLoader against the copyrighted game — not in the repo and not obtainable in cloud. Only `lib\LiteNetLib.dll` and the net472 ref pack resolve on Linux.
- **HintPaths are Windows paths** (`$(SignalisDir)` → `C:\MyProjects\SIGNALIS\...`). To attempt a build off-Windows you would have to place a real SIGNALIS `MelonLoader\Managed` folder on the machine and override `-p:SignalisDir=<dir>` — this is not set up in cloud.
- **Running / playtesting is Windows-only:** Steam + SIGNALIS + MelonLoader, dual-instance (host + copy). See the tables above. None of this is possible in the Linux cloud VM.
- **No tests, no lint config, no `.sln`.** A single project (`SyncRADation.csproj`); correctness is proven only by dual-instance playtest on Windows.
- **Cloud agents should scope work to source edits + review**; verify build/playtest on a Windows machine per the sections above.
