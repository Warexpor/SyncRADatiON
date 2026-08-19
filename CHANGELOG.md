# Changelog

## 0.4.3-dev — 2026-08-19

Protocol **v8** (same wire as 0.4.2). Client join no longer mutates authored sealed-door faces.

### Added
- Player-dropped items (v1): G / inventory **DROP** clones a native floor `ItemPickup`; walk-up TAKE inspect (yes/no + count) then grant. Unique Key/Object go on the party ring; join dump; bag-full reject. Dual-instance verified both drop/TAKE arrows.

### Changed
- Dead-code trim: unused overlay/drop leftovers, no-op dialogue apply, unused vital fields, bone-send divider, and one-line aliases. Shared `WorldLookup.All` / `WeaponUtils.EquippedWeaponType` / `LocalInspect.InspectScreen`.

### Fixed
- Dropped TAKE no longer calls `Dialoguer.EndDialogue` (that broadcast a WorldId-0 `DialogueEnd` storm and crashed). Play flags restore without Dialoguer. Clone `release()` is skipped; grant + despawn still run.
- Client TAKE no longer destroys the inspect pickup mid-callback (that NRE'd `dialoguerCallback` and froze Elster). Claim still goes to the host; local despawn waits until play restores.
- Join `InteractiveLockSingle` / `DoorLockControl` no longer `setLock`s flavor seals. ConnectedDoors only `Unlock`s when a key / `externalUnlocker` / hint exists. `Doorway_Double.locked=false` is not written onto a sealed face.
- Entity spawner no longer `FindObjectsOfType` every IMGUI frame or clone the current room’s corpses. Spawn uses native `ResetEnemy` / `WakeUp`, registers a stable `SR_Spawn_*` WorldId, and host-broadcasts so the client instantiates the same type. Client F11 is a host request at the **client** Elster’s position.
- F11 no longer additive-loads a whole chapter to steal a prefab. That looped `LOV_Reeducation` (~30 loads), never banked STAR, and killed the session. Templates come from in-memory `EnemyController` + `EnemySpawner.EnemyType` only. Missing types: load that chapter once with F7.
- Client world pickups that only have `_itemEnum` (`_item` still null) no longer grant `Nothing` / `!!MISSING STRING`.
- Client `Used !!MISSING STRING` on party-ring keys: `useItemDialogue` substitutes Dialoguer `keyName` (`<s3>`). Join XML leaves that empty; catalog `getName` is written to `s3` on Start/Continue. Scene `AnItem` copies always resolve through `InventoryManager.getItem`.
- Pickup/use loc: `getName` Prefix skips the scene copy and runs native on the catalog SO (IL2CPP postfix `ref string` was a no-op). `AddItem(None)` is ignored.
- Party cutscenes under `EventOnlyRoom` now start the native skipper. Escape hold skips instead of opening pause; airlock/PEN_Titles stay local.
- Airlock split only covers wreck↔hole during `PEN_Titles`. Host loading `LOV_Reeducation` no longer leaves the client frozen in `PEN_Hole` (pause-only). Puzzle dumps skip while scenes mismatch.
- Client `CutsceneStart` plays locally and notifies the host; missing WorldId is an ack, not a reject. `PEN_HoleSnowblind` / `PEN_CodeRoomEnd` skippers get Esc instead of pause.
- Story commit no longer repeats every 0.75s (that was Dialoguer XML spam + hitch). Host LOV load hitch is still a real chapter load.
- Crawl (`PEN_CodeRoom.crawlPlayer`): `Climbing` is sent and applied on the proxy (`Climbing`/`Crawl`/`Crouch`).

### Added
- Template bank (DDOL) harvested from loaded controllers and native `EnemySpawner` prefab refs
- `EnemySpawn` net message; join dump includes live F11 spawns

## 0.4.2-dev — 2026-08-15

Protocol **v7** (incompatible with v6). Join dump is the live SProgress slot. UnityEvents and world FMOD replay on the client. Native puzzle solve methods on the solved edge. Session roster for 3–4 players.

### Correctness pass
- Party key ring is **Key/Object only** (ammo/weapons no longer lie in `hasItem`). Dropped and crafted keys (client Tape+BrokenKey) note the ring; host merges client ring deltas
- HP is native `HurtElster` only (no parallel pool). Host AI no longer double-hits the host. Parameterless `TakeDamage` reads `PlayerAttack.sneaking`. Host `SaveManager.Load` wipes peers. Disconnect resets downed state
- World pickup claims by **WorldId**; unique-enum hide is Key/Object copies only. Grant is `AddItem` + hide, never a second `pickUp()`. `InteractiveLockSingle` consume/unlock including host-self sender 0
- One `setInRange` prefix. `index:` chapter loads gate SceneFollow (transient is `LoadingScreen` only). Penrose defer is cinematic (`PEN_Titles.started` / local ViewPoint, 3s latch then clear if `started` never rose)
- Dialoguer `StartDialogue` (including callback overloads) sends to the host. Continue/End replay on clients; flavor/inspect lines stay fully local. Inspect-originated `SProgress.Set*` notes the host. Client `EvaluateEnding` blocked. EventZone idle timer runs; keypad Update polling removed
- Elevator snaps `riding`/`stopped` (still no remote `startRide`). Radio lock applies `frequency`. EventZone join fires `onInRange` on the false→true edge only. Overlay kill is per WorldId
- FMOD join dump scans live `IsPlaying()` including inactive room chunks. WorldId hashes `go.scene`. Host session is live with zero peers. Boss keyed by full WorldId. Drops: stack count, bag-full reject (raw bag, not ring-patched `hasItem`), no fake save file
- `ExperimentalPuzzles` migrates into `SyncPuzzles` and never forces puzzles off

### Added
- Full `SProgress.progress` list dump (bool/int/float/string/vector) + mid-presentation WorldId on join
- `EventZone.onInRange` / MultiCondition / BookScreen / `CutsceneCut.Proceed` presentation relay
- `StudioEventEmitter` Play/Stop by WorldId (skips Elster + radio UI); sliding-door one-shots
- Native `openDoor` / `delayedOpen` / `StartShutdown` / `CheckSolve` / `useRing` on solve
- `EXC_Elevator` flags only (never `startRide`); Kolibri + Adler snapshots
- `PlayerRoster`: host broadcasts session ids; clients prune ghost proxies on leave; `GetRemotePlayerIds` works on clients
- Client ids recycled in `1..MaxPlayers-1` (cap 4 including host)

### Changed
- LiteNetLib **1.3.5** as checked-in `lib/LiteNetLib.dll` (`net472`). NuGet 1.3.5 is `netstandard2.0` and MelonLoader 0.5.7 Mono cannot load it
- Proxy locomotion interpolates between pose snapshots ~45ms behind (Hermite, not exponential-lerp to the latest packet)
- Proxy bones send at 30 Hz and sample on the same snapshot clock as locomotion (quaternion slerp, no 50ms predicted hold)
- Version `0.4.2-dev`, protocol 7
- Join and F2 client resync world dump **unicast** to that peer (host F2 Resync is a no-op; scene-change dump still goes to all clients)

### Fixed
- Notes/documents/`EventScreen` inspect no longer open on every Elster (local camera only)
- Hatch / unique-key doors: one party-ring solve, both walk. Airlock/EventOnlyRoom cinematic stays on the user; chapter load still SceneFollows
- Client UseItem (Penrose hatch card) is accepted on the host when that Elster has the item. The ring used to check only the host bag, so `UseItem … no key` rejected the repaired `AirlockKey`. Combine Tape+BrokenKey also notes the result on the ring.
- Client skip/load of `PEN_Hole` no longer yanks the host, and host remaining on the wreck no longer yanks the client back. If a peer is already in the airlock cinematic, Escape/skip is not blocked.
- Client walking the Penrose airlock into `PEN_Hole` is a real chapter follow. Host used to reject it as an unknown scene (no `LoadLevelZone` on the wreck), so the client sat in the hatch for ~12s until the host loaded. LoadingScreen no longer hellos/dumps/applies puzzles or FMOD.
- Locked room-links that are not a real key-hint (`GiveKeyHint` + a key) stay on the red NO ENTRY plate for both Elsters. Yellow padlock / blue Open prompts are suppressed while `ConnectedDoors.locked` (reactor, external unlock, no-key). Real key doors still show the padlock.
- G drop works from play and inventory (selected slot / equipped tool / weapon). Dropped props use world-space distance pickup.
- Proxy run footsteps set FMOD Run/Speed on `ElsterStep` and read `AlternatePlayerController.running` (animator `Running` was stuck off)
- `InventoryManager.hasItem(Items.itemlist)` also honors the party key ring (native use checks that overload)
- Penrose `ObservationDialogue` flavor (control-panel look-at, loc `PEN_Controls*`) stays local. Remoting it ran `Dialogue.StartDialogue` without the loc string, so the other Elster got a different line (or the sealed-door text) for the same WorldId
- `InteractiveLockSingle` now also syncs `AutoTraverseDoor.blocker` (the red “cannot be opened” plate). `door.locked` alone left the plate/inspect mismatch
- Proxy Hermite no longer uses XZ velocity on Y / extra Y (that was the occasional inches-off-floor hop after a hitch)
- Cryo codepad buttons stay disabled after solve (solved flag alone still left the pad usable)
- Penrose cryo override panel is `LAB_PatternLock`, not `PEN_Codepad`. `LAB_PatternLock` has no `OnEnable` (Harmony skipped the patch). Disable now runs from `Lab_PatternLockControl.OnEnable` and turns off the panel EventObject
- Room enter no longer `FindObjectsOfType` the whole puzzle map (~200ms hitch). Reapply uses the existing WorldId scan
- Door/move `Interaction.triggered` is no longer polled across peers (that was yanking the other Elster when someone used a ConnectedDoors link)
- `InteractiveLockSingle` no longer treats key-use cooldown (`timedOut`) as `door.locked` — that was relocking the door the other player needed
- Spent cryo/pad `Interaction`s stay dead across `reset()` / room-chunk wake (prompt + EventScreen start)
- Claiming the cryo `BrokenKey` no longer hides the cockpit `PhotoPickup` (sibling wipe under `Pen_CockpitEvent3D`). Unique-item hide still removes the cockpit `KeyCardBroken` copy only
- First-person (`EventScreen3DCam` / `EventOnlyRoom`) look-at dialogues stay on the inspecting Elster; they no longer pop “nothing” on the other player
- Chapter machines that only set a bool now also run native world methods on the live edge (`MED_Pump`/`Drain`, `ROT_Pipes.TurnValve`, `EXC_Hatch.OpenHatch`, shutters, magpie, reactor, rings, biodome lock, meat blocker)
- Friendly fire no longer double-hits; storage put/take mutates the shared box, not the host bag
- StoryCommit replays presentation on join/resync only; host ignores client world-apply packets
- Client Dialoguer / UseItemMulti / keypad / cutscene proceed / books actually reach the host
- Dialoguer Continue/End no longer freeze after the first page (presentation applies them; flavor stays local). Callback `StartDialogue` overloads take the host path
- Host world-pickup reserves the WorldId in the prefix (same-room race). `SaveManager.Load` only wipes peers on host death
- Int chapter loads use the build-index name first. Client F7/scene requests load on the host without `BeginApply` so `SendSceneFollow` reaches the requester
- `MultiConditionEvent.TryTrigger` broadcasts once. FMOD dump includes inactive emitters
- Dropped E pickup is host-claimed; pickup deny no longer hides the prop
- Sequenced pose no longer embeds the WeaponMount tree (261 eulers blew the 1020-byte LiteNetLib cap and crashed `Network.Update` every bone tick). Bind bones only; overflow goes as `BonePose` chunks
- Proxy weapon hold/aim uses Elster controller names (`Weapon/Pistol`, not `Pistol`) so ADS actually poses the arms
- Proxy dry-fire no longer plays bang/flash/shells on the observer (empty click uses `emptyMod`; live Fire is ammo-spent only)
- Empty click / hip Fire1 ignored unless ADS (`PlayerState.aiming`) so door/use clicks are not gunshots
- Reload sound from `PlayerState.reloading` / mag refill (animator Reload bool never rose)
- Proxy laser point unparented + red sprite fallback (was magenta missing-mat, stretched with the gun)
- First weapon clone no longer nested-scans every renderer (that was the ~1s aim-then-walk hitch)
- Proxy shot no longer layers CombatSfx slide/eject (that was the extra empty-click); case-land is `Pistol/Case` etc., shotgun still pumps
- Proxy laser stays local-space on the weapon (follows recoil) instead of a world-space 90° guess
- Proxy ADS plays `WeaponDraw`; wall hits play `ricochetSound`
- Puzzle poll no longer TryReads every inactive-room component every 0.5s (~70ms main-thread hitch that starved pose)
- Proxy movement interpolates between received snapshots instead of exponential-lerping at the live packet (that retarget was the remaining metronome hitch)
- Scene-follow requests only apply known current-scene names; F7 rooms and client F11 spawn are gated while connected
- Downed clients stay downed across SceneFollow; sliding doors emit on `cycle`; FMOD join dump + reset
- Harmony still per-class; one bad patch cannot abort the mod
- Playtest traces: `[Story]` `[Interact]` `[FMOD]` `[KeyRing]` `[StorageBox]` `[Scene]` `[Damage]`; VerboseLogging default on
- `.gitignore` now drops bin/obj/dist, NuGet packages, IDE files, logs, and secrets (stop tracking build artifacts)

## 0.4.1-dev — 2026-08-13

Protocol **v5** (incompatible with v4). Host-authoritative full-game pass: SceneFollow, interaction bus, story lockstep, shared storage, party keys, death policy.

### Added
- SceneFollow via `AsyncLoader` / `SceneHelper` / `LoadLevelZone` (client follows host chapter)
- Interaction request/ack for EventZone, UseItem, keypad, Dialoguer, cutscenes, EventScreen, storage, gunshot wake
- StoryCommit (`SProgress` + Dialoguer XML + `END_Manager`) and StoryPresentation native replay
- Shared `InventoryManager.boxItems` blob; party key ring for unique keys
- Puzzle types: tarot, mural, incinerator, scale, shrine, radio alignment, radio code lock
- Client downed vs host save-reload death

### Changed
- Version `0.4.1-dev`, protocol 5
- Enemy `playerPos` retarget every non-dead state; `END_Boss.Elster`; client AI puppeted at handshake
- Radio sync is `moduleInstalled` only (no tuner clobber)
- Off-chunk enemy pose snaps skipped when renderers are disabled (room isolation)

### Publish
- GitHub zip: `dist/SyncRADation-0.4.1-dev.zip` (dll + LiteNetLib + README). Nexus not in this release.

## 0.4.0-dev — 2026-08-13

Version reset: former `1.2.x-dev` is now **0.4.0-dev**. Protocol still **v4** (not a wire change).

### Changed
- Product version `0.4.0-dev` (honest pre-1.0 numbering)
- README / AGENTS.md aligned with current controls, WorldId authority table, and room-isolation rules

## 1.2.2-dev — 2026-08-03

### Changed
- **Entity spawner** moved **F7 → F11**
- **F7** opens **Location Teleporter** (chapter `lvl` jumps + in-level room `goto`/spawn). Removed native debug-console unlocker.
- **Location Teleporter:** Reeducation split into **normal** (`LOV_Reeducation`) and **corrupted** (`BIO_Reeducation`) entries.

## 1.2.2-dev — 2026-07-19

### Fixed
- **Remote weapon VFX:** shot pulse from equipped `magAmmo` decrease → `AnimTriggers.Fire` (full-auto safe; no longer stuck on held Fire1 edge)
- **Muzzle flash / case eject / smoke:** longer flash, source `MuzzleFlash`/`ReloadCaseEject` path match, no random PS fallback for cases
- **Aim laser:** world-space `LineRenderer`, material path-copy after IL2CPP Instantiate, real muzzle origin
- **Shell/action SFX:** delayed CombatSfx paths for pistol/revolver/rifle on remote shot (plus existing shotgun pump / flak eject)

### Changed
- Version `1.2.2-dev` (protocol still v4)

## 1.2.1-dev — 2026-07-19

### Fixed
- **Room pull bug:** remote players no longer call `ConnectedDoors.StartA`/`StartB` when someone else uses a door. Room traverse is **local-only**; network only syncs **lock** state on `ConnectedDoors`.

## 1.2.0-dev — 2026-07-19

### Fixed (decompile-aligned)

- **Enemy damage** uses native `EnemyController.TakeDamage(fire, crit, hurt, noSneak)` on host; clients no longer DIY raycast + raw `hitbox.HP` math
- **Harmony** prefixes on both `TakeDamage` overloads → client hits report to host, host sim is authoritative
- **Puzzles** keyed by **WorldId** (FNV scene+hierarchy), not `FindObjectsOfType` index (cross-peer identity fix)
- **Doors** apply via `DoorNative` → private `openDoors`/`closeDoors`/`cycle`. ConnectedDoors lock/plates only — never `StartA`/`StartB`
- **World ItemPickup** host claim/grant: only claimer gets inventory; everyone else only hides; race-safe reservation

### Added

- Protocol **v4**: extended `EnemyDamage` (native chances), `WorldPickupClaim` / `WorldPickupGrant`, puzzles use `WorldId` in `PuzzleStateEntry`
- `Patches/EnemyTakeDamagePatches.cs`, `Patches/ItemPickupPatches.cs`, `Networking/DoorNative.cs`

### Changed

- Version `1.2.0-dev`, protocol 4 (incompatible with v3 clients)
- Removed multiplayer enemy DIY raycast from `ModRuntime` (FF raycast remains if enabled)

### Notes

- Connected room transitions still imperfect if both peers load different room chunks
- Cutscenes/Dialoguer still not co-op scripted
- Both installs must run this build

## 1.1.0-dev — 2026-07-19

### Added
- Protocol v3: SnapshotRequest, WorldPickupState, PlayerVital
- Join resync, world pickup hide, vitals, puzzles default on
