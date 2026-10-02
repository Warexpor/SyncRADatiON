# Protocol history

Current: `Bootstrap/PluginInfo.cs` `ProtocolVersion`. Port default `7777`, connection key `SyncRADation`. Wire: `Networking/Messages/NetMessages.cs`, `PartyMessages.cs`, `NetWire.cs` (schema hash). Bump only when the wire changes, and log the bump here and in `CHANGELOG.md`.

- v6: full SProgress dump, UnityEvent presentation, FmodEmitter Play/Stop
- v7: `PlayerRoster` (3+ peers), recycled client ids, join/resync dump to the requester only
- v8: host-authored `EnemySpawn` (F11 templates by `AnEnemyType` from in-memory prefabs; does **not** additive-load chapters)
- v9: PuzzleType 64–72 (MusicBox…OpenableDrawer), `PuzzleStateEntry.Float1`, BossSnapshotNet Hp/Corrupt, END playstyle on StoryCommit, FMOD Guid/Attached/`fmod` one-shots, SceneManager.LoadScene gate
- v10: PuzzleType 73–76 (GunCase, AraNest, LAB_RifleQuest, LOV_Microfiche) + client emit for SwingDoor/DoorwaySimple/StorageBox/KeyGrid/ArianePhotoCode
- v11: PuzzleType 77 (`MED_Adler_EVdoors` DoorL/DoorR local X pose) + AdoptNativeSpawn template bank + death-bag ring Note
- v12: handshake gains `SchemaHash` + `ModVersion`; `PartyLife`/`PartySave`/`PartyRoom` (NetMessageType 40–42): downed/revive/wipe + party-save snapshots; N-player hardening (Sequenced channel 1 for enemy/boss snapshots, handshake-gated sends)
- v13: `PuzzleStateEntry` gains `Seq` + `Mask` (host-stamped version / client edit mask, cell-wise merge); PuzzleType 78–81 (`ROT_DiskManager`, `DET_WallCreature`, `MapReveal`, `MEM_ChecklistLogic`); StoryCmd 20–23 (`GoToPenny`, `PartyCheat`, `EndDelta`, `EndGraves`; requests ride `InspectFlag` Int0 = 100 + cmd); `WorldPickupDeny` 60, `AvatarOneShot` 61, `BossHit` 62, `EnemyAction` 63
- v14: `PartyLife` gains `Scene` (wipe reload target); `Handshake` gains `GameBuildHash` + `GameBuild` (rejects a different game build); `ItemPickedUp.ClaimerPlayerId`; `FmodEmitter.Comp` (emitter keyed by WorldId + component index) + client→host `FmodEmitterRequest` 67; host-only `DropRekey` 73 (departed peer's floor drops move to the host key space); `BonePose` clamp 1023; `Room` is a capped string; incremental `StoryCommit` carries only dirty keys; `SchemaHash` mixes the dll MVID
- v15: `SceneHello` + `SceneFollow` gain `Stats` (per WorldRegistry category: id count + FNV-1a64 checksum of the sorted WorldIds, `Sync/WorldChecksum.cs`); host-only `SceneDiff` 74 (host WorldIds of the differing categories, chunks of 256, at most 2048 per category). A mismatch logs one `[Scene] WorldId divergence` line, the client logs `[Scene] missing:` / `[Scene] extra:` (20 ids each) and requests one full dump; F2 shows `World: in sync` / `World: N ids differ`
- v16: `StoryCommit` gains `Authoritative` (last field): a full commit after a host `SaveManager.Load` / `NewGame` replaces the client's `SProgress` (absent keys are removed). The handshake game-build tail read is guarded (`AvailableBytes`) so an older peer reaches the readable protocol-version reject; the bump keeps every 0.5.60 peer out
- v17: `PlayerState` velocity is planar `VelX`/`VelY` (was `VelX`/`VelZ`; SIGNALIS walks XY, Z is height). Same layout, new meaning, so the bump keeps 0.5.62 peers out
- v18 (0.5.65):
  - `PlayerState` rebuilt for an Animator-less proxy (pose = sender's bones + humanoid hips): `SenderId, PosX/Y/Z, VelX/VelY, FacingX/Y/Z` (facing quaternion, w implied ≥ 0), `Weapon`, `PoseFlags` byte (Aiming, Running, EmptyClick, Step, Climbing, WearHat, HasHips, Scripted = cutscene state: proxy height not floor-locked), `ModelState`, `Forward` byte (footstep loudness), `HipsX/Y/Z` (hips `localPosition`), bones. 605 → 555 bytes with 83 bones. All Animator parameter fields (Turn, AimingTime, Stamina, Blend, IKwalk, InputX/Y, HurtTime, CharState, AnimBools, AnimTriggers, RotY/Root*) are gone.
  - `AvatarOneShot` carries an `AvatarCue` byte (Fire, Reload, Hurt) instead of `AnimTriggers`.
  - `PlayerRoster` gains a trailing `HostFlags` byte: the host's sync toggles, authoritative for clients while connected (1 puzzles, 2 world pickups, 4 vitals, 8 friendly fire, 16 client cheats).
  - `EnemyDamage` in native mode carries the client's measured HP loss in `Damage` (the host lowers `Hitbox.HP` by it before native `TakeDamage`).
  - `WorldPickupEntry.Count` (units left on an untriggered prop, also in the join dump) and `WorldPickupClaimMessage.Remaining` (> 0: a partial take's leftover, host releases the claim).
  - `StoryCommit` drops the never-read `ActiveGameState` byte.
  - Puzzle field meanings: `EXC_Elevator` `Float0` is the stopped `pos.z` (0 while riding).
