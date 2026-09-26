# Domains — where to fix what (0.5.10)

Composed `*SyncService` / `*NetHandlers` / `Patches/`. Namespaces stay `SyncRADation.Networking` / `.Patches` / `.Players` / `.ItemSystem`.

Authority + reverse-check: repo root `AGENTS.md`. Protocol **10** wire in `Networking/Messages/NetMessages.cs` (+ PluginInfo).

## Symptom → path

| Bug / symptom | Look here first | Wire / notes |
|---------------|-----------------|--------------|
| Door open/close / ConnectedDoors lock | `Doors/` (`DoorSyncService`, `DoorNative`, `DoorNetHandlers`, `Patches/`) | `DoorState`; never sync traverse |
| Puzzle door flags (Doorway_simple, Swing, seals) | `Puzzles/Doors/PuzzleDoorFlagsSyncService` | Still on `PuzzleState` poll |
| Cryo pods / cryo locks | `Puzzles/Cryo/CryoSyncService` | OnEnable rematch + snap |
| Codepad / PatternLock / overlay kill | `Puzzles/Codepad/` + `Puzzles/KeypadPatches.cs` | ClientMayEmit; consequences |
| Interactive locks / keypads / dials | `Puzzles/Locks/LockSyncService` | |
| Pump / flood / pipes / hatch | `Puzzles/PumpFlood`, `Pipes`, `Hatch` + `EnvironmentPatches` | Native Open/Drain/TurnValve live only |
| Elevators | `Puzzles/Elevator/` | Central targetFloor; EXC `startRide`/`stopInstant` + mover Y |
| Radio alignment / code lock | `Puzzles/Radio/` | Tuner freq local; RadioCodeLock snaps keypad + unlock doors |
| Chapter machines (card writer, shutters, magpie, …) | `Puzzles/Machines/ChapterMachineSyncService` | |
| Residency / key grid / photo / safe / drawer (64–72) | `Puzzles/Residency/ResidencyPuzzleSyncService` | |
| GunCase / AraNest / RifleQuest / Microfiche (73–76) | `Puzzles/ChapterExtras/ChapterExtraPuzzleSyncService` | Protocol 10 |
| Chapter extras GunCase / AraNest / RifleQuest / Microfiche (73–76) | `Puzzles/ChapterExtras/ChapterExtraPuzzleSyncService` | Protocol 10 |
| Storage **lid** open | `Puzzles/Storage/StorageLidSyncService` | Host poll + client emit |
| Storage **box items** blob | `Inventory/StorageBoxSyncService` | Shared box |
| UseItem world unlock / airlock card | `Puzzles/UseItem/` + `Inventory/Patches/UseItem*` | Party ring + PerPlayerUse |
| EventZone fire (in-room) | `Story/Patches/EventZonePatches` + `Puzzles/EventZone/` | Other-room must not Invoke |
| Cutscene / MultiCondition / Dialoguer | `Story/` (`StorySyncService`, `InteractionSyncService`, patches) | Host presentation |
| Airlock / PEN_Titles / wreck↔hole | `Scene/AirlockCinematic` + `Scene/Patches/` | Never SceneFollow wreck↔hole |
| Scene follow / F7 chapter | `Scene/SceneFollowService` + `Scene/Patches/SceneLoadPatches` | Host load |
| World authored pickup claim | `Pickups/WorldPickupSyncService` + `WorldPickupNetHandlers` | Host claim/grant; WorldId |
| Player-dropped prop (G / TAKE) | `Pickups/DroppedItem*` (Registry, Spawner, NetHandlers; Manager = call-site façade) | Peer spawn + host claim |
| Party key ring names / hasItem | `Inventory/PartyKeyRing` + `Inventory/Patches/PartyKeyRingPatches` | Key/Object only |
| Enemies / alert bits | `Enemies/` (+ `Patches/EnemySpawnerPatches`) | WorldId; wake sleeping chunks; **client never EnemySpawner.FixedUpdate**; host adopts `_Child` → `SR_Spawn_*` + `EnemySpawn` |
| Bosses (END/Chimera/Mynah/Kolibri/Adler) | `Bosses/` (+ `Patches/KolibriAdlerAuthPatches`) | Kolibri/Adler PuzzleState Hold; Falke Arenas/shields/corrupt snap; HaltBossController StopAllCoroutines |
| Avatar / bones / weapons | `Players/` + `AvatarNetHandlers` | Peer-authored |
| Friendly fire / death bag | `Combat/CombatNetHandlers` + `Combat/Patches` | Opt-in FF |
| FMOD world emitters | `Audio/` | Skip Music/Cutscenes/Ambience beds |
| Gunshot wake | `Combat/Patches/GunshotWakePatch` | Host wakes near shot |
| Handshake / roster | `Networking/LanNetworkManager` + `Dispatch/` | Peer map |
| Join / resync dump | `Session/SessionNetHandlers` | `_unicastPlayerId` via BeginUnicast/EndUnicast; **Puzzle ForceFullSend** + **Boss RequestFullSend** so mid-join unicast is complete |
| SceneHello / SceneFollow | `Scene/SceneNetHandlers` + `SceneFollowService` | |
| PartyKeyRing / Storage blob wire | `Inventory/InventoryNetHandlers` | |
| PuzzleState send/apply | `Puzzles/PuzzleNetHandlers` + coordinator | |

## Layers (do not conflate)

1. **NetHandlers** — serialize/send + inbound Handle + relay policy for that domain’s messages.
2. **SyncService** — scan/tick/apply world state (often WorldId-keyed).
3. **Patches** — Harmony emit/block; apply usually goes through SyncService/NetHandlers.

**Dropped items:** `DroppedItemManager` = legacy static façade → Registry (lookup/lifecycle) + Spawner (clone/floor) + NetHandlers (drop/claim wire). New code should call Registry/Spawner/NetHandlers directly when practical.

**Puzzles:** `PuzzleSyncService` = coordinator (scan, tick, held, `_mutateWorld`, host relay). Domain SyncServices own family TryRead/Apply/snap. Call sites may still use `PuzzleSyncService.Snap*` forwards.

## Empty / incomplete (do not assume)

- No empty `Networking/Session` or `Transport` dirs — session dump lives in `Domains/Session/SessionNetHandlers`.
- Wire ownership: check `Networking/LanNetworkManager.HandlerRegistry.cs` for the full handler list (Door, Avatar, Enemy, Boss, Fmod, Story, Interaction, Dropped, WorldPickup, Puzzle, Combat, Scene, Inventory, Session).
