# Changelog

## 0.4.2-dev — 2026-08-15

Protocol **v7** (incompatible with v6). Join dump is the live SProgress slot. UnityEvents and world FMOD replay on the client. Native puzzle solve methods on the solved edge. Session roster for 3–4 players.

### Added
- Full `SProgress.progress` list dump (bool/int/float/string/vector) + mid-presentation WorldId on join
- `EventZone.onInRange` / MultiCondition / BookScreen / `CutsceneCut.Proceed` presentation relay
- `StudioEventEmitter` Play/Stop by WorldId (skips Elster + radio UI); sliding-door one-shots
- Native `openDoor` / `delayedOpen` / `StartShutdown` / `CheckSolve` / `useRing` on solve
- `EXC_Elevator` flags only (never `startRide`); Kolibri + Adler snapshots
- `PlayerRoster`: host broadcasts session ids; clients prune ghost proxies on leave; `GetRemotePlayerIds` works on clients
- Client ids recycled in `1..MaxPlayers-1` (cap 4 including host)

### Changed
- Proxy locomotion interpolates between pose snapshots ~45ms behind (Hermite, not exponential-lerp to the latest packet)
- Proxy bones send at 30 Hz and sample on the same snapshot clock as locomotion (quaternion slerp, no 50ms predicted hold)
- Version `0.4.2-dev`, protocol 7
- Join and F2 client resync world dump **unicast** to that peer (host F2 Resync is a no-op; scene-change dump still goes to all clients)

### Fixed
- Notes/documents/`EventScreen` inspect no longer open on every Elster (local camera only)
- Cryo codepad buttons stay disabled after solve (solved flag alone still left the pad usable)
- Cryo late room-enter stamps `doorPos`/`opened` so native Update shows the lid already open (no replay). Claimed pickups stay hidden after content wakes. Inspect pickups keep yes/no; claimed/inspect grants no longer duplicate the item
- Chapter machines that only set a bool now also run native world methods on the live edge (`MED_Pump`/`Drain`, `ROT_Pipes.TurnValve`, `EXC_Hatch.OpenHatch`, shutters, magpie, reactor, rings, biodome lock, meat blocker)
- Friendly fire no longer double-hits; storage put/take mutates the shared box, not the host bag
- StoryCommit replays presentation on join/resync only; host ignores client world-apply packets
- Client Dialoguer / UseItemMulti / keypad / cutscene proceed / books actually reach the host
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
- **Doors** apply via `DoorNative` → private `openDoors`/`closeDoors`/`cycle` + `ConnectedDoors.StartA`/`StartB` when possible
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
