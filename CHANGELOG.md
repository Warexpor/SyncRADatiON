# Changelog

## 0.5.11 — 2026-09-26

Protocol **v10**. Continuous Batch 11 dig.

### Fixed
- **Kolibri / ADLR Hold Postfix** — client `Update` recomputes intensity/progress/frequency from local radio after Prefix; Postfix re-applies the last host PuzzleState snap so phase cannot drift (0.5.9 Prefix alone lost the race).
- **Falke presentation mid-apply** — `SnapFalkePresentation` takes stage/corrupt from the BossState snap (not re-read fields), reclamps stage, rewrites field mirror, and calls `SetBodySpearStates` so stage regress / corrupt toggle cannot leave arenas/shields/meshes/BodySpears on a stale combo.
- **World pickup peer-gone mid-claim** — host ignores claims from disconnected peers; non-Key/Object orphan claims release + restore prop + broadcast untriggered on `NotePeerGone` (grant softlock when claimer drops after TryClaimOnHost). Client `ApplyHide` now restores on `Triggered=false` (was hide-only).

### Parked (Batch 12)
- Alarm `GlobalAlertStatus.triggerAlarm` / `EnemyManagerState` client latch-emit — MelonLoader `CallerCount(0)`, no UnityEvent bindings; alert stays host-poll only.
- Ending-flag peer merge (client `healedTime`/`doors`/`memoryTime` → host) — host-authoritative `END_Manager` by design; no safe merge without new wire.
- MeatBlocker tarot siblings (Lovers/Moon/Sun/Star/Tower) — assets `required=1/2/6`; only Death seals NG+ `KeyOfSacrifice` (wiki Artifact); no parallel softlock pair proven.
- Throw-mid-Harmony sticky beyond Dialoguer Finalizers — Cutscene `RememberStart`/`RememberSkip` intentional; Keypad `_sent` clears on scene/StopNetwork via `EventZonePatch`.
- Photo/document/eidetic unique story gates — flavor local by design; `ArianePhotoCode` / Microfiche already PuzzleState.
- Ladder / continuum / nowhere / fake wall co-op — traverse local by design (proxy SFX only).
- Domains code smell (double Broadcast / WorldId 0 dumps) — PartyKeyRing Broadcast is if/else once; KeyGrid/Ariane/RadioManager WorldId 0 are intentional statics.

## 0.5.10 — 2026-09-26

Protocol **v10**. Continuous Batch 10 dig.

### Fixed
- **Falke arena / invuln / corrupt mesh** — client `ApplyEND` now snaps `Arenas[i]` (`i == stage`), `HeadSpears[i]` (`i < stage`), `FloatShields` (`stage >= 3`, wiki phase 4), `FloatShields2` (`stage >= 5`, phase 6), and `CorruptedMesh`/`NormalMesh` from synced `corrupt`. Decompile: `END_Boss.Start`, `<Stabbed>d__129.MoveNext` (`stage++` then presentation), `Update` mesh gate. Stage was already on BossState wire since 0.5.x; disabled AI meant arenas/shields never followed.
- **Boss AI-disable coroutines** — `DisableLocalAI` now `StopAllCoroutines` before `enabled = false` on END/Chimera/Mynah so mid-fight `Bossfight`/`Stabbed`/`Airstrike` cannot keep running on the client (Kolibri/Adler stay enabled + Hold patches).

### Parked (Batch 11)
- Alarm `GlobalAlertStatus.triggerAlarm` / `EnemyManagerState` client latch-emit — MelonLoader `CallerCount(0)`, no UnityEvent bindings; alert stays host-poll only.
- Ending-flag peer merge (client `healedTime`/`doors`/`memoryTime` → host) — host-authoritative `END_Manager` by design; no safe merge without new wire.
- MeatBlocker tarot siblings (Lovers/Moon/Sun/Star/Tower) — assets `required=1/2/6`; only Death seals NG+ `KeyOfSacrifice` (wiki Artifact); no parallel softlock pair proven.
- Throw-mid-Harmony sticky beyond Dialoguer Finalizers — Cutscene `RememberStart`/`RememberSkip` intentional; Keypad `_sent` clears on scene/StopNetwork via `EventZonePatch`.
- Net reconnect mid-puzzle/storage/dialogue — ForceFull + sticky Reset chain covered 0.5.1–0.5.9; no new hole proven this dig.
- Photo/document/eidetic unique story gates — flavor local by design; `ArianePhotoCode` / Microfiche already PuzzleState.
- Ladder / continuum / nowhere / fake wall co-op — traverse local by design (proxy SFX only).
- Domains code smell (double Broadcast / WorldId 0 dumps) — PartyKeyRing Broadcast is if/else once; KeyGrid/Ariane/RadioManager WorldId 0 are intentional statics.

## 0.5.9 — 2026-09-26

Protocol **v10**. Continuous Batch 9 dig.

### Fixed
- **Kolibri / ADLR host-auth** — client `KolibriManager`/`BOS_Adler` Update no longer clobbers host PuzzleState intensity/progress/frequency/dead (glitch presentation still runs). END/Chimera/Mynah remain fully AI-disabled.
- **World pickup bag-full softlock** — no Prefix reservation / claim wire when the 6-slot bag has no room for non-Key/Object; host Prefix reservation released if native nospace/cancel; Postfix/NoteTaken gate Broadcast on successful claim. Key/Object still claim onto the party ring when full.

### Parked (Batch 10)
- Alarm `GlobalAlertStatus.triggerAlarm` / `EnemyManagerState` client latch-emit — MelonLoader `CallerCount(0)`, no UnityEvent `m_MethodName: triggerAlarm` in exported scenes/prefabs; alert stays host-poll only (unlike radio `moduleInstalled`).
- Ending-flag peer merge (client `healedTime`/`doors`/`memoryTime` → host) — host-authoritative END_Manager by design; no safe merge without new wire.
- MeatBlocker tarot siblings (Lovers/Moon/Sun/Star/Tower) — only Death seals NG+ KeyOfSacrifice (wiki); others required=1/2/6 with no parallel Artifact softlock proven.
- Falke arena-door / invuln beyond 0.5.7 join-transient BossState cache; ammo/heal/plate/thermite remain personal inventory; corpse loot no vanilla drop path; elevator/airlock/continuum no new hole beyond current snaps.

## 0.5.8 — 2026-09-26

Protocol **v10**. Continuous Batch 8 dig.

### Fixed
- **Death tarot / Key of Sacrifice softlock (NG+ Artifact)** — scene `ROT_MeatBlocker` ID `Death` seals the bookstore wing that holds `KeyOfSacrifice` (under `NGP_only`). Hold TarotDeath world take/claim + Death MeatBlocker emit/apply while a live unclaimed KeyOfSacrifice still exists; first playthrough (NGP off) unchanged. Wiki: get Sacrifice before Death.
- **MeatBlocker snap** — seal direction now activates Blockers / deactivates UnBlockers / locks ConnectedDoors (was unblock-only).
- **Host drop claim** — removed `_awaitingDrops.Clear()` on host-local FinishDroppedNative (host never stages awaiting-ack; Clear was a latent wipe).

### Parked (Batch 9)
- Alarm `GlobalAlertStatus` / `EnemyManagerState` client latch-emit — still host-only; no proven client raise path (unlike radio moduleInstalled).
- Broader adversarial soak: chapter load / Penrose / Falke / Kolibri / ADLR / inventory overflow / ammo-heal / corpse / elevator-airlock / ending-flag merge / N-peer late-join boss — no new clear CAN-fix beyond 0.5.7 transient cache.


## 0.5.1 — 2026-09-20

Protocol **v10**. Domains architecture + decompile coverage + diagnosis-ready dual-box soak. Dual-instance playtest still required before treating behavior as proven.

### Added
- **Domains layout** — `Bootstrap/`, `Networking/{Dispatch,Messages}`, `Domains/{Doors,Enemies,Bosses,Story,Scene,Audio,Pickups,Inventory,Players,Combat,Puzzles,Session}/`. Symptom→path map: `Domains/README.md`.
- Protocol **10** puzzles: GunCase / AraNest / LAB_RifleQuest / LOV_Microfiche; client-emit reverse arrows for SwingDoor / DoorwaySimple / StorageBox / MED_KeyGrid / ArianePhotoCode (+ protocol 9 residency/locks already in tree).
- Diagnosis: boot banner (host+client MelonLoader log paths, prefs, grep tags, Hitch glossary); AGENTS.md **Diagnosis** section; HitchTrace Cost tags `enemy` / `boss` / `pickup` (plus `puzzle` / `weaponClone`).
- Hard rule: **no park / no defer** (`.cursor/rules/no-park-no-defer.mdc`). Coverage ledger: `docs/DECOMPILE_COVERAGE.md`.

### Changed
- Product version **0.5.1** (`PluginInfo` / AssemblyInfo / README / Domains map). Protocol stays **10**.
- `LanNetworkManager` slimmed: HandlerRegistry + PublicApi + Dispatch; domain Send/Handle on NetHandlers.
- `PuzzleSyncService` coordinator + family SyncServices; InteractionPatches peeled into Domains patches; DroppedItemManager façade over Registry/Spawner.
- `VerboseLogging` MelonPreferences: OFF unless diagnosing; set on **both** installs.
- Hot-path: `WorldLookup` scene caches; recycled tick lists; FMOD HostEmit dedupe; join FullRefresh door/lock flag snaps; remount `HoldIfProgressed`.

### Fixed
- Join/remount door unlocks; EmitProgressed TryRead (no zeroed Reaktor/Pump Ints); CentralElevator client-emit; Cryo hierarchy-only pattern disable; MultiKeyLock poll without `checkLock`; KeyGrid/Ariane no double-emit. Detail under 0.5.0-dev notes below.

## 0.5.0-dev — 2026-09-20

Protocol **v10**. PuzzleType 73–76 (GunCase / AraNest / LAB_RifleQuest / LOV_Microfiche) + client-emit reverse arrows for SwingDoor / DoorwaySimple / StorageBox / MED_KeyGrid / ArianePhotoCode.

### Added (protocol 10)
- `GunCase` — Bool0 opened (inter disabled / pickup enabled); Magpie snap: disable inter/openBox, enable pickup, lid localEuler Y=-115 (Open coroutine), RevealPickups.
- `AraNest` — Bool0 triggered / Bool1 activated / Bool2 dead; Apply `TriggerTrap` once + dead `anim_LoadDead` on Nest/Ara.
- `LAB_RifleQuest` — Bool0 awake / Bool1 gone / Bool2 rifle; snap Isa/Rifle/FakeRifle/ObsHolder/UseItemHolder + `anim_Done` when awake/gone.
- `LOV_Microfiche` — Bool0 hasFiche / Bool1 IsaVisited / Bool2 IsaGone; snap Isa/IsaNote/IsaCutscene (+ book/ItemInter PersistentGameObject); **not** BookScreen UI.
- Client emit: SwingDoor, DoorwaySimple, StorageBox, MED_KeyGrid, ArianePhotoCode (plus the four new types).

### Added (protocol 9 puzzles)
- `RES_MusicBox` / `RES_LibraryPC` / `RES_Paternoster` — Magpie-style snaps; client emit + host relay.
- `MED_KeyGrid.solved` + `ArianePhotoCode.code` — WorldId-0 host globals (RadioManager pattern).
- `DET_ServiceLock_Key` / `SafeDoorSmall` / `MultiKeyLock` (keys→Int0 bits) / `OpenableDrawer`.
- `CentralElevator.targetFloor` → Int2; EvidenceLocker/FloodControls/RES_Power bool arrays packed into Ints; PEN_Reaktor extras; `ROT_Mural` up to 8 moons in Int0–Int3.
- `DET_RadioCodeLock` keypad.solved → Bool0 + TryUnlockDoors; `EXC_Elevator` startRide/stopInstant + mover Y; `SaveRoomEvent` disables eventInter; `DialoguePlayedOnce` skips LocalInspect flavor.
- Boss: `END_Boss` Hp/Corrupt; Kolibri frequency/radioIntensity; Adler progress (`PuzzleStateEntry.Float1`).
- Story: END_Manager NPC/healedTime/segments/memoryTime/doors on StoryCommit; client `CalculatePlaystyle` gated.
- FMOD: `PlayOneShot(Guid)`, `PlayOneShotAttached` string/Guid, `fmod.PlayOneShot`.
- Scene: `SceneManager.LoadScene` string/int gated (AirlockDoorLoadZone); PenroseAirlock already on AsyncLoader(int).
- Hard rule: **no park / no defer** (`.cursor/rules/no-park-no-defer.mdc` + AGENTS).

### Changed
- Product version `0.5.0-dev`; `ProtocolVersion` = **10**.
- Layout: `Bootstrap/`, `Networking/{Dispatch,Messages}`, `Domains/{Doors,Enemies,Bosses,Story,Scene,Audio,Pickups,Inventory,Players,Combat,Puzzles,Session}/`. Namespaces kept stable.
- `LanNetworkManager` slimmed (~2.1k → ~600 LOC): HandlerRegistry + PublicApi + Dispatch; domain Send/Handle on Door/Avatar/Enemy/Boss/Fmod/Story/Interaction/Dropped/WorldPickup/Puzzle/Combat/Scene/Inventory/Session NetHandlers. Join dump uses `_unicastPlayerId` via BeginUnicast/EndUnicast.
- `PuzzleSyncService` is a coordinator (scan/tick/held/`_mutateWorld`/relay); ApplyEntry/TryRead are one-line forwards to Cryo/Codepad/Locks/PumpFlood/Pipes/Hatch/Elevator/Machines/Residency/Radio/UseItem/Storage/EventZone/DoorFlags + Story/Boss/Enemy for bleed types. Dead `InteractionTriggered` switch cases removed (enum kept).
- `InteractionPatches` god split into Domains Story/Scene/Inventory/Puzzles/Combat patches; dead `ShouldHold*` / `Keep*Prompt` / `IsRemoteUnlock` chain removed.
- `DroppedItemManager` is a thin façade over Registry/Spawner; drop/claim wire in `DroppedItemNetHandlers`.
- `Domains/README.md` is the symptom→path fix map for bugfixes.
- `docs/DECOMPILE_COVERAGE.md` lists completed protocol-10 coverage + intentional locals only (no park / verify-later tables).

### Preserved
- Host-authoritative world/story rules from AGENTS.md (party key ring Key/Object only, native TAKE for drops, wreck↔hole never follows, ClientMayEmit allowlist, join dump `_mutateWorld=false`).
- Reverse-check both arrows remains required for playtest claims (playtest itself out of scope for this structural release).

### Fixed (code-only solidify — no dual-box)
- Join FullRefresh now snaps door/lock flags (`DoorwaySimple` / `SwingDoor` / `DoorLockControl` / `InteractiveLockSingle`); `TryUnlockDoors` / `UnlockDoorObject` are flag snaps (flavor still gated by `AllowUnlock` / `IsFlavorSeal`).
- Remount hold: `IsProgressed` covers unlocked locks (Interactive/Number/DoorLockPuzzle), open Swing/FoldingShutter, CentralElevator cabin, Waage weight, RadioAlignment; ProgressedBool0 gains Dial/Multi/Vent/RadioTutorial/Power/Incinerator/Shrine/Tarot/MultiCondition/Cutscene/FloodSwitch/ElevatorCall.
- Host/client Tick now `HoldIfProgressed` so poll solves survive room remount (not only Emit/Apply).
- `EmitProgressed` TryReads live component then forces Bool0 (no longer zeroes Reaktor/Pump Ints); recycled `_emitScratch`.
- Client emit: `CentralElevator`. KeyGrid/Ariane no longer double-emit instance + WorldId 0.
- Cryo pattern-lock disable: hierarchy/sibling parent only (no radius heuristic). MultiKeyLock poll derives unlocked from `keys[]` (no `checkLock` on tick).
- Mural `useRing` mutate-gated; FloodControls `dlc.locked` snaps on join.
- Hot-path: FMOD HostEmit dedupe before local/door walks; RadioManager + gunshot `ElsterSettings` via WorldLookup; proxy LateUpdate `_staleScratch`; StorageBox recycled read buffer; EnvEmit.ReadOnce for Update-polled Magpie/Shutters/CardWriter/Biodome.

### Fixed (decompile coverage loop)
- `MultiKeyLock` Apply calls `checkLock` + TryUnlockDoors; dial/number/DoorLockPuzzle unseal doors; MultiLock TryUnlockDoors; Vent Magpie cover snap; EvidenceLocker snaps `EvidenceLockerDoor`.
- Doorway_simple / DoorLockControl Apply now unseal when unlocked (flavor seals still sealed). Host unlock ↔ client unlock.
- Dialoguer Airlock (13) treated as local flavor with PEN_Titles.
- Client CutsceneStart no longer runs native locally (SProgress writes were blocked → flag hole); host-only Start + presentation.
- Enemy staggerType applied from HurtState; MultiLock element bits in Int0; LAB_Waage no longer writes peer inventory content.
- Client EnemySpawner blocked; host adopts native spawn into EnemySpawn. Client world-pickup grant Invokes onPickup.
- `PuzzleStateEntry` change-detect includes `Float1`.

### Changed (structural beauty)
- PuzzleSyncService table-dispatch (`PuzzleSyncService.Dispatch.cs`); main hub ~650 LOC. Tick lists Clear()+reuse; Send* accept IList (no per-tick ToArray).
- `Sync/WorldLookup.cs` — scene-scoped All/Find caches; presentation/cutscene/scene-follow/key-ring/FMOD title/crawl paths no longer FoT every call.
- Enemy/Boss/WorldPickup ticks: recycled snap lists; scene-cached Basic/Cook/boss arrays; enemy one nearest-target per tick; Boss Apply `Play(hash)` (no clip enum alloc).
- Dropped items: `DroppedItemTemplateCache` (template + scene ItemPickup cache); Registry `_keyScratch`; Anchor LateUpdate early-out then disable.
- Apply paths: Puzzle ApplyEntry skips missing WorldId components; null/destroyed guards on enemy/boss/pickup/story presentation.
- SceneFollow / PartyKeyRing / SourceAnimReader / Fmod PEN_Titles use WorldLookup.

## 0.4.3-dev — 2026-09-13

### Changed
- Linux playtest port: csproj defaults to `~/Work/MyProjects/SIGNALIS` + Steam under `~/.local/share/Steam/...`; `scripts/launch-client.sh` / `secondsignalis` runs the second box under its own Proton prefix. Steam host install gets MelonLoader 0.5.7 + `single-instance=0`.
- Proton MelonLoader: force native `version.dll` (`WINEDLLOVERRIDES=version=n,b` on Steam launch options + client launcher; prefix `DllOverrides` too). Without this, Steam Play boots vanilla and never writes `MelonLoader/Latest.log`.
- Dual-box display: windowed 2560x720 + Hyprland float rules — Steam host top half, `secondsignalis` bottom half (not fullscreen).
- `.gitattributes` enforces LF so Windows/Proton editors don’t churn the tree.

### Fixed
- Door open/close SFX: remote apply no longer plays native door emitters ungated (host→client and client→host). Distance-gated `DoorNative` SFX only; `event:/Environment/Doors/*` no longer world-relays via FMOD OneShot/emitter sync.

## 0.4.3-dev — 2026-08-19

Protocol **v8** (same wire as 0.4.2). Client join no longer mutates authored sealed-door faces.

### Added
- Player-dropped items (v1): G / inventory **DROP** clones a native floor `ItemPickup`; walk-up TAKE inspect (yes/no + count) then grant. Unique Key/Object go on the party ring; join dump; bag-full reject. Dual-instance verified both drop/TAKE arrows.

### Changed
- World FMOD no longer relays Music / Cutscenes / Ambience beds (each Elster plays those locally). Unique Key/Object claims survive chapter loads so copies stay hidden.
- Dead-code trim: unused overlay/drop leftovers, no-op dialogue apply, unused vital fields, bone-send divider, and one-line aliases. Shared `WorldLookup.All` / `WeaponUtils.EquippedWeaponType` / `LocalInspect.InspectScreen`.
- Logs: always-on session/world/story edges with `H`/`C` role prefix; identical lines collapse for 3s. Hitch prints only on a real spike. FMOD Play/Stop, proxy clone/FX dumps, and incremental puzzle apply sit behind `VerboseLogging` (default off).

### Fixed
- Client chapter request (F7 / cutscene load) no longer double-`LoadLevel`s while the host is on `LoadingScreen`. In-flight target is coalesced; dumps from the loading screen are skipped; `LastCmd` CutsceneStart does not ride into the next chapter.
- Pickup inspect no longer shows the last UseItem name (Airlock Key) for ammo/cards/books. Dialoguer s0/s3 bind to the catalog item before `pickUp`; story XML dumps restore the local name instead of stomping it.
- Inspect grants now `AddItem` into the real 6-slot bag (`InLocalBag`), not ring-patched `hasItem`. Keys still open doors; they also show in the inventory.
- Other-room `CutsceneStart` / `EventZone` / `CutsceneProceed` / `MultiCondition` no longer Invoke on the observer (that was the leaked traverse / cinematic yank). Initiator still plays locally; host relays.
- Host wakes a sleeping-chunk enemy when a peer is in range or a hit arrives, then logs WorldId misses. Client puppets still use native contact hurtboxes (ramming an enemy is real SIGNALIS damage).
- `CutsceneManager.Skip` no longer NREs when `cutscene` is null (host never started it). Skip/Start are once per WorldId; join dump only replays a cutscene that is still running in this scene.
- Client unlock of `InteractiveLockSingle` now emits to the host. Non-flavor `Doorway_Double` opens are honored even if the host lock bit is still set; flavor seals still ignore.
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
- Host skip of the Penrose airlock no longer SceneFollows the client into `PEN_Hole`. Wreck↔hole is always per-Elster (not only while local `PEN_Titles` is running). Host leaving Penrose still follows. Peer hole-load requests do not teleport the host.
- Airlock split: other-scene UseItem acks instead of `no key`; host proxy is despawned (no extrapolate ghost); FMOD from the other chapter is ignored. CutsceneSkip is once per WorldId and no-ops if already `completed` (LOV intro no longer double-Skip hitches).
- Host/client keycard USE at a slot (Penrose airlock): Interactor was highlighting the PC zoom (`ViewPoint`) over `UseItemInteraction` while the repaired `AirlockKey` was selected, so the “want to use this item?” prompt never started. Held-key use inter is preferred; scene `key` / `InteractItem` bind to the bag instance; `getCount` matches by enum. Host EventZones under the airlock still run native.

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
