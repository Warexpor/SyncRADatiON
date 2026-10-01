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
| Adding / changing a puzzle type (reader, applier, scan, emit/durable/merge/progressed rules) | `Puzzles/PuzzleSpecs.cs` (one `PuzzleTypeSpec` row per type) | `PuzzleSpecTests` fails if an enum value has no row; merge logic in `Puzzles/PuzzleMerge` |
| Solved-edge rule (live rising edge vs join dump / held re-snap) | `Puzzles/PuzzleEdge` (`Solved`, `InRoom`, `ReplayDurable`) | Live onSolved only for a player in that room; everyone else gets the durable form |
| MultiCondition / SaveRoom / CutsceneCompleted / Dialogue playedOnce state | `Puzzles/StoryFlags/StoryFlagPuzzleSyncService` | On the PuzzleState poll |
| Storage **lid** open | `Puzzles/Storage/StorageLidSyncService` | Host poll + client emit |
| Storage **box items** (put/take, blob) | `Inventory/StorageService` (host put/take, `StorageTxn`, acks) + `Inventory/StorageBoxSyncService` (blob) + `Inventory/Patches/StorageBoxPatches` | Shared box; bag/box walks in `Inventory/ItemBag` |
| UseItem world unlock / airlock card | `Puzzles/UseItem/` + `Inventory/Patches/UseItem*` | Party ring + PerPlayerUse |
| EventZone fire (in-room) | `Story/Patches/EventZonePatches` + `Puzzles/EventZone/` | Other-room must not Invoke |
| Cutscene start / skip / proceed | `Story/CutsceneSync` + `Story/CutsceneStamps` (4 s start, 30 s skip windows) + `Story/Patches/CutscenePatches` | Host presentation; wreck/hole split runs locally |
| Story flags / commits (SProgress) / ending | `Story/StorySyncService` (+ `.End`, `.Presentation`) + `Story/ProgressSlot` + `Story/StorySlotPlan` | Incremental / full / authoritative; per-player keys stay local |
| MultiCondition / UseItem story side / gunshot | `Story/InteractionSyncService` (+ `.UseItem`) + `Story/Patches/MultiConditionPatches` | |
| Dialoguer (local only) | `Story/Patches/StoryPatches` (`DialoguerGate`) | Every dialogue line is local flavor; gates only bind key-ring names |
| Airlock / PEN_Titles / wreck↔hole | `Scene/AirlockCinematic` + `Scene/Patches/` | Never SceneFollow wreck↔hole |
| Scene follow / F7 chapter | `Scene/SceneFollowService` + `Scene/Patches/SceneLoadPatches` | Host load |
| World authored pickup claim | `Pickups/WorldPickupSyncService` (one `Take` record per in-flight take) + `WorldPickupNetHandlers` + `Pickups/Patches/ItemPickupPatches` | Host claim/grant; partial take releases the claim with the remainder (`Count` / `Remaining`) |
| Player-dropped prop (G / TAKE) | `Pickups/DroppedItem*` (Registry, Spawner, NetHandlers) + `Pickups/Patches/DroppedTakePatches` | Peer spawn + host claim; `DroppedItemManager` is a forward kept for a few outside callers |
| Party key ring names / hasItem / getCount (masquerade) | `Inventory/PartyKeyRing` + `Inventory/Patches/PartyKeyRingPatches` | Key/Object only; off inside `SaveManager.Save` and `ItemPickup.release`; never a physical bag copy |
| Client enemy actions (stomp Kill/KillSilent, Knockback, GetPushed, Burndown, WakeUp) | `Enemies/Patches/EnemyActionPatches` + `Enemies/EnemyNetHandlers` | `EnemyAction` (63); host sim applies |
| Enemies / alert bits | `Enemies/` (+ `Patches/EnemySpawnerPatches`) | WorldId; wake sleeping chunks; **client never EnemySpawner.FixedUpdate**; host adopts `_Child` → `SR_Spawn_*` + `EnemySpawn` |
| Bosses (END/Chimera/Mynah/Kolibri/Adler) | `Bosses/` (+ `Patches/KolibriAdlerAuthPatches`) | Kolibri/Adler Prefix+Postfix Hold; Falke snap stage/corrupt + SetBodySpearStates; HaltBossController StopAllCoroutines |
| Client boss hits (Falke Stab / TakeSpear, boss HP, Chimera rifle shot) | `Bosses/Patches/BossActionPatches` + `Bosses/BossNetHandlers` | `BossHit` (62) |
| Avatar / bones / weapons | `Players/` (`PlayerProxyBuilder` → `ProxyRig`, `ProxyPose` bones+hips, `ProxyMotion` root, `SnapshotRing`, `SnapClock`) + `AvatarNetHandlers` + `SourceAnimReader` | Peer-authored; the proxy has no Animator; per-sender scene check |
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
| Session state that must not leak between scenes / sessions / wipe reloads | `Sync/SessionReset` (scopes `Scene` / `Session` / `Connection`; `RunScene`, `RunAll("stop"/"wipe")`) + `Bootstrap/SessionResetRegistrations` | Register a clear, or `// persistent: <reason>` directly above the field; `StaticStateGuardTests` enforces it |
| Sync toggles agreed with the host (puzzles, pickups, vitals, FF, client cheats) | `Config/ModConfig` (`*Enabled`, `ClientCheatsAllowed`) | `PlayerRoster.HostFlags` |
| Developer traces on/off | `Config/ModConfig.DiagnosticsOn` (`Diagnostics` pref) | Gated inside each trace class |
| Client quit-to-menu / host→MainMenu ends the session | `Networking/LanNetworkManager.SessionEnd` (`EndSession`) | Uses `RejectPeer` / `RequestStop` / `_stopReason` |
| Shared-RVA (folded) patch firing for a foreign `this` | `Sync/Il2CppRealType` (`Is<T>`) + `docs/RVA_FOLDING.md` | Guard first statement; `RvaFoldingTests` pins the list |
| `[Hitch] phase=` / stall lines (per-phase ms, frame stall breakdown; Diagnostics only) | `Sync/HitchTrace` (`Begin`/`End`/`FrameBegin`) + `Sync/HarmonyPhaseTiming` | Phase markers in `ModRuntime.Update`/`LateUpdate`; `HarmonyPhaseTiming.Install` wraps every patched Update/LateUpdate/FixedUpdate when Diagnostics is on |
| Scene scan cost / `[World] scan` / `WorldId divergence` | `Sync/WorldScan` (one bucketed scene walk, warms the WorldId cache) + `Sync/WorldId` (scene cache: `FromGameObject` returns the load-time id; `Pin` / `Forget`) + `Sync/WorldLookup` + `Sync/WorldRegistry` | Ids are pinned at scene load, so a destroyed sibling never shifts them; `Invalidate<T>` forces a direct rescan |
| Story wire helpers (dirty-key commit entries, caps) | `Networking/Messages/StoryWire` | `StoryWireTests` |
| Handshake / roster | `Networking/LanNetworkManager` + `Dispatch/` | Peer map |
| Join / resync dump | `Session/SessionNetHandlers` + `Sync/DumpFlush` + `Bootstrap/DumpFlushRegistrations` | Every domain's `FlushDiffNow` runs first (pending diffs reach everyone), then BeginUnicast/EndUnicast; inside `UnicastActive` full sends never record "already sent" |
| SceneHello / SceneFollow | `Scene/SceneNetHandlers` + `SceneFollowService` | |
| PartyKeyRing / Storage blob wire | `Inventory/InventoryNetHandlers` (+ `Inventory/StorageService` acks) | |
| PuzzleState send/apply | `Puzzles/PuzzleNetHandlers` + coordinator | |

## Layers (do not conflate)

1. **NetHandlers** — serialize/send + inbound Handle + relay policy for that domain’s messages.
2. **SyncService** — scan/tick/apply world state (often WorldId-keyed).
3. **Patches** — Harmony emit/block; apply usually goes through SyncService/NetHandlers.

**Dropped items:** Registry (lookup/lifecycle) + Spawner (clone/floor) + NetHandlers (drop/claim wire). Call them directly; `DroppedItemManager` only forwards for the few callers that still use it.

**Puzzles:** `PuzzleSyncService` = coordinator (scan, tick, held, `_mutateWorld`, host relay), driven by the `PuzzleSpecs` table. Domain SyncServices own family TryRead/Apply/snap; patches emit through `EnvEmit.Edge`.

## Empty / incomplete (do not assume)

- No empty `Networking/Session` or `Transport` dirs — session dump lives in `Domains/Session/SessionNetHandlers`.
- Wire ownership: check `Networking/LanNetworkManager.HandlerRegistry.cs` for the full handler list (Door, Avatar, Enemy, Boss, Fmod, Story, Interaction, Dropped, WorldPickup, Puzzle, Combat, Party, Scene, Inventory, Session).
