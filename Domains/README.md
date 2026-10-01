# Domains — where to fix what

Composed `*SyncService` / `*NetHandlers` / `Patches/`. Namespaces stay `SyncRADation.Networking` / `.Patches` / `.Players` / `.ItemSystem`.

Authority: `docs/SYNC.md`; reverse-check rule: repo root `AGENTS.md`. Wire in `Networking/Messages/NetMessages.cs` + `PartyMessages.cs` + `NetWire.cs` (schema hash). Version/protocol constants: `Bootstrap/PluginInfo.cs` (version is single-sourced from there).

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
| World-object puzzles (ROT_DiskManager, DET_WallCreature, MapReveal, MEM_ChecklistLogic; 78–81) | `Puzzles/ChapterExtras/WorldObjectPuzzleSyncService` | Protocol 13; `Seq`/`Mask` cell merge |
| Solved-edge rule (live rising edge vs join dump / held re-snap) | `Puzzles/PuzzleEdge` | Live runs native onSolved; dump/held runs idempotent durable form |
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
| Client enemy actions (stomp Kill/KillSilent, Knockback, GetPushed, Burndown, WakeUp) | `Enemies/Patches/EnemyActionPatches` + `Enemies/EnemyNetHandlers` | `EnemyAction` (63); host sim applies |
| Enemies / alert bits | `Enemies/` (+ `Patches/EnemySpawnerPatches`) | WorldId; wake sleeping chunks; **client never EnemySpawner.FixedUpdate**; host adopts `_Child` → `SR_Spawn_*` + `EnemySpawn` |
| Bosses (END/Chimera/Mynah/Kolibri/Adler) | `Bosses/` (+ `Patches/KolibriAdlerAuthPatches`) | Kolibri/Adler Prefix+Postfix Hold; Falke snap stage/corrupt + SetBodySpearStates; HaltBossController StopAllCoroutines |
| Client boss hits (Falke Stab / TakeSpear, boss HP, Chimera rifle shot) | `Bosses/Patches/BossActionPatches` + `Bosses/BossNetHandlers` | `BossHit` (62) |
| Avatar / bones / weapons | `Players/` + `AvatarNetHandlers` | Peer-authored |
| Party vitals: downed / revive / wipe, party-life state | `Players/PartyVitals` + `Combat/PartyNetHandlers` + `Combat/Patches/DeathPatches` | `PartyLife`/`PartyRoom` (40, 42); `DownedRespawnDelay` |
| Party save token / key-ring + bag snapshots (host_saves.txt, bag_snapshots.txt) | `Session/PartySaveService` + `Combat/PartyNetHandlers` | `PartySave` (41); `SaveManager.Save/Load` hooks |
| Client → player/enemy damage checks (hurtbox / melee vs proxies) | `Combat/ClientDamageService` | Host-side; skips downed peers; friendly fire opt-in |
| Friendly fire / death bag | `Combat/CombatNetHandlers` + `Combat/Patches` | Opt-in FF |
| FMOD world emitters | `Audio/` | Skip Music/Cutscenes/Ambience beds |
| Gunshot wake | `Combat/Patches/GunshotWakePatch` | Host wakes near shot |
| Swallowed exceptions / `[Guard]` log lines | `Sync/Guard.cs` | Throttled per tag (first hit, then one line / 30 s with count) |
| Party wipe host reload (scene + save reload, watchdog, peers follow) | `Session/HostReload` + `Combat/PartyNetHandlers` | `PartyLife.Scene`; watchdog 45 s |
| Boot patch audit / reflected-member table (`[Harmony] audit:` line, F2 status) | `Bootstrap/PatchAudit` | Add every new `GetField/GetMethod/GetProperty/Find("...")` literal to its table |
| Game build hash in handshake (other game build → rejected) | `Bootstrap/GameBuild` + `Networking/LanNetworkManager` | `Handshake.GameBuildHash`/`GameBuild` |
| Session state that must not leak between sessions / wipe reloads | `Sync/SessionReset` + `Bootstrap/SessionResetRegistrations` | Register a clear, or comment why the static is persistent |
| Client quit-to-menu / host→MainMenu ends the session | `Networking/LanNetworkManager.SessionEnd` (`EndSession`) | Uses `RejectPeer` / `RequestStop` / `_stopReason` |
| Shared-RVA (folded) patch firing for a foreign `this` | `Sync/Il2CppRealType` (`Is<T>`) + `docs/RVA_FOLDING.md` | Guard first statement; `RvaFoldingTests` pins the list |
| `[Hitch] phase=` / stall lines (per-phase ms, frame stall breakdown) | `Sync/HitchTrace` (`Begin`/`End`/`FrameBegin`) + `Sync/HarmonyPhaseTiming` | Phase markers in `ModRuntime.Update`/`LateUpdate`; `HarmonyPhaseTiming.Install` wraps every patched Update/LateUpdate/FixedUpdate |
| Scene scan cost / `[World] scan` / enemy `id drift` / `WorldId divergence` | `Sync/WorldScan` (one bucketed scene walk) + `Sync/WorldLookup` + `Sync/WorldRegistry` (`StickyEnemyId`) | Enemy ids pinned per instance per scene; `Invalidate<T>` forces a direct rescan |
| Dialogue.CallDialogue (host-authored dialogue gate, unique RVA) | `Story/Patches/StoryPatches` (`DialogueCallDialoguePatch`) + `Story/DialoguerGate` | Not folded; `rva_fold_scan.py --check` |
| Story wire helpers (dirty-key commit entries, caps) | `Networking/Messages/StoryWire` | `StoryWireTests` |
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
- Wire ownership: check `Networking/LanNetworkManager.HandlerRegistry.cs` for the full handler list (Door, Avatar, Enemy, Boss, Fmod, Story, Interaction, Dropped, WorldPickup, Puzzle, Combat, Party, Scene, Inventory, Session).
