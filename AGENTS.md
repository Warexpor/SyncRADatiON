# SyncRADation — SIGNALIS Multiplayer Mod

**Status:** v0.5.63 — protocol **v17**. Host-authoritative world/story + native presentation/FMOD. Dual-instance playtest required. Decompile: `~/Archive/Windows-Desktop/Dev/SIGNALIS DECOMPILED` (`~/Omarchy_Backup/Desktop/Dev/...` is gone on this machine).

## Product

LAN multiplayer MelonLoader mod for SIGNALIS (Unity IL2CPP / Unhollower-style Managed). Host-authoritative world + peer-authored avatars over LiteNetLib.

## Controls

- F2 — multiplayer menu (Host / Connect / Resync / status)
- F3 — quick connect (saved IP/port)
- F6 / F11 — item giver / entity spawner (any replika type, not current-scene clones)
- F7 — location teleporter (chapters + rooms in current level)

- G — drop selected item (inventory slot, or DROP in the item command list)

Walk up to a dropped prop for the native TAKE prompt (yes/no inspect, ammo count). There is no extra pickup key.

## What is synced (0.5.63)

| Area | Authority | Notes |
|------|-----------|--------|
| Avatar proxy + anim/bones/weapons | Peer | ~30 Hz state + bones |
| Enemies | Host | WorldId snaps; **native TakeDamage**; client hits Harmony→host; host **wakes sleeping-chunk** enemies when a peer is near. Contact ram damage stays native. F11 spawn is host-authored (`EnemySpawn` + `SR_Spawn_*` WorldId) |
| Doors (double / sliding) | Any peer emit, host relay | Visual open/close via native methods |
| ConnectedDoors (room links) | Lock only | **Never** sync traverse / StartA/B — room entry is local. Unique key doors: one solve (party key ring **Key/Object only**), both walk |
| Ladders | Local traverse | Climb is per-player; other peer only hears proxy SFX (no `Interaction.trigger`) |
| Chapter / scene load | Host | SceneFollow via `AsyncLoader` / `SceneHelper` / `LoadLevelZone` — int and string loads both gate. **Wreck↔hole never follows** (each Elster loads `PEN_Hole` when they finish the airlock). Host leaving Penrose still follows. Client F7 is a host load + follow, not `BeginApply`. Follow calls RestorePlay (clears inventory/menu sticky) before load |
| Story (SProgress, Dialoguer, cutscenes, END_Manager) | Host | Full slot dump on join; **books / notes / EventScreen / EventOnlyRoom / airlock (`PEN_Titles`) / lock-flavor lines stay local**. Story Dialoguer Start (all overloads) is client→host then presentation replay; Continue/End apply on clients. **Other-room cutscenes / EventZones do not Start/Invoke** on the observer |
| World FMOD | Host Play/Stop | StudioEventEmitter by WorldId; skip Elster + radio UI + **Music/Cutscenes/Ambience beds**; **far non-door Play** distance-gated like doors; tuner freq local |
| Puzzles / locks / elevators / radio module / storage / event zones / alert | Host + client emit | **WorldId-keyed**; client emits puzzle/lock types only (not `Interaction.trigger` / EventZone / combat); apply **snaps flags + doors**, never EventScreen / `trigger()`; cryo/codepad/pump/pipes/hatch live-apply native Open/Drain/TurnValve; unlocked location doors stay open for the party; **GunCase / AraNest / LAB_RifleQuest / LOV_Microfiche** (protocol 10); **MED_Adler_EVdoors** DoorL/R local X (protocol 11); PatternLock grid + Tarot cards + LibraryPC robotPos mid-hold; mural late-join Blocker snap (no cutscene replay); biodome KeyLevel + MultiCondition tried mid-hold |
| Host disconnect | Client goes offline | Restores play + input; clears enemy/boss puppets, EventZone/cutscene/airlock/SceneFollow/Dialoguer sticky |
| World ItemPickups | Host claim/grant | Claimer gets item; unique **Key/Object** go on the **party key ring**; ammo/docs do not. Client bag keys still unlock UseItem (hatch card). **TarotDeath** held while live unclaimed **KeyOfSacrifice** (NG+ Artifact softlock). Peer-gone mid-claim releases non-unique orphans |
| Player-dropped items | Peer + relay | G or inventory **DROP**; floor snap; native TAKE inspect (yes/no + count) then grant; join dump; bag-full reject; **peer-gone mid DroppedPickup / StorageTake rejected** (floor/box kept; Put still applies) |
| Death | Asymmetric | Client downed (ammo/docs floor bag; **all Key/Object Note onto party ring, never floor** — bag-only race closed); native `HurtElster` HP; host death `SaveManager.Load` for both |
| Bosses (END / Chimera / Mynah / Kolibri / Adler) | Host | `END_Boss.Elster` / `BOS_Adler.Elster`; Kolibri dead/intensity; **join dump ForceFull**; client skips apply while transient + refreshes empty boss cache; Kolibri/Adler Update **Prefix+Postfix** Hold; Falke `Arenas`/shields/corrupt/`SetBodySpearStates` from snap stage/corrupt; END/Chimera/Mynah HaltBossController (StopAllCoroutines) |
| Friendly fire | Opt-in | Default OFF |
| Inventories | Independent | 6-slot bags stay personal; box + key ring are shared |

## Architecture (0.5.19 Domains)

Composition over endless partials. Domain folders hold `*SyncService` / `*NetHandlers` / `Patches/`; namespaces stay stable (`SyncRADation.Networking`, `.Patches`, `.Players`, `.ItemSystem`) so call sites do not churn.

**Bugfix map:** `Domains/README.md` (symptom → folder).

```
Bootstrap/                 # SyncRADationMod, ModRuntime, PluginInfo
Networking/
  LanNetworkManager*.cs    # thin transport + HandlerRegistry + PublicApi
  Dispatch/                # TryDispatch* → domain NetHandlers
  Messages/                # NetMessages (protocol 17)
Domains/
  Doors/ Enemies/ Bosses/ Story/ Scene/ Audio/
  Pickups/ Inventory/ Players/ Combat/ Puzzles/ Session/
Sync/                      # WorldId, WorldRegistry, NetGate, LocalInspect, Guard, SessionReset, Il2CppRealType
UI/ Config/ Cheats/
```

- `LanNetworkManager` — LiteNetLib peers, ids 0..N-1 (recycled 1..MaxPlayers-1), host relay, join snapshot **unicast** via `_unicastPlayerId` / BeginUnicast; `PlayerRoster`; domain Send/Handle on composed NetHandlers (see HandlerRegistry)
- `WorldId` / `WorldRegistry` — `hash(scene + hierarchy path)` — never `GetInstanceID()`
- `PuzzleSyncService` — coordinator façade; family SyncServices under `Domains/Puzzles/*`; ApplyEntry/TryRead are forwards
- Boss Kolibri/Adler + EnemyManager/GlobalAlert + Cutscene/Dialogue/MultiCondition/SaveRoom puzzle flags owned by Boss/Enemy/Story (same `PuzzleStateMessage` wire)
- `DroppedItemManager` — thin façade over Registry/Spawner; claim/drop wire in `DroppedItemNetHandlers`
- Reverse-check both arrows (host↔client initiate) and late-join dump before calling a path fixed

## Protocol

- **ProtocolVersion = 17** (SceneHello/SceneFollow registry checksum, SceneDiff 74; v14 retained: PartyLife.Scene, Handshake GameBuildHash/GameBuild, ItemPickedUp.ClaimerPlayerId, FmodEmitter.Comp, FmodEmitterRequest 67, DropRekey 73; v13 retained)
- Port default `7777`, key `SyncRADation`
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

## This machine (dual-instance, Linux + Proton)

Same PC. **Steam = host. Copy = client.** Both are Windows SIGNALIS under Proton. F3 is `127.0.0.1:7777`. `VerboseLogging` off unless hunting FMOD / proxy clone. `boot.config` `single-instance=0` on both. MelonLoader **0.5.7** on both (`version.dll` + `MelonLoader/`).

Proton will ignore MelonLoader’s `version.dll` unless native wins over Wine’s builtin. Host Steam launch options: `WINEDLLOVERRIDES="version=n,b" %command% -screen-fullscreen 0 -screen-width 1280 -screen-height 720` (also prefix `DllOverrides`). `secondsignalis` uses the same. Hyprland floats both as centered 16:9 half-height windows (1280x720 physical): **Steam host top** (`steam_app_1262350`), **Proton client bottom** (`SIGNALIS.exe`); the host size also lives in its prefix registry (`Screenmanager Resolution Width/Height`) for dual-box playtest.

| Role | Install | Launch | MelonLoader log |
|------|---------|--------|-----------------|
| **Host** | `~/.local/share/Steam/steamapps/common/SIGNALIS` | Steam (Proton) | `.../SIGNALIS/MelonLoader/Latest.log` |
| **Client** | `~/Work/MyProjects/SIGNALIS` | `secondsignalis` (or `scripts/launch-client.sh`; own Proton prefix `compatdata/syncradation-client`) | `~/Work/MyProjects/SIGNALIS/MelonLoader/Latest.log` |

Prefs: `.../SIGNALIS/UserData/MelonPreferences.cfg` on each install.

### Diagnosis (dual-box soak)

**Logs (grep both):**
- Host: `~/.local/share/Steam/steamapps/common/SIGNALIS/MelonLoader/Latest.log`
- Client: `~/Work/MyProjects/SIGNALIS/MelonLoader/Latest.log`

Role prefix on connected lines: `H ` = host, `C ` = client.

**Always-on tags:** `[Story]` `[Interact]` `[KeyRing]` `[StorageBox]` `[Scene]` `[Damage]` `[Door]` `[Puzzle]` `[Pickup]` `[Harmony]` `[Hitch]` `[Spawn]` `[Enemy]` `[Proxy]` `[Weapon]`. Session / scene follow / story send-apply / interact ok|FAIL / key ring / pickup claim / door open-close / puzzle solve-snap / death / MISS|warn.

**VerboseLogging** (`[SyncRADation]` in MelonPreferences.cfg on **both** installs, default `false`):
- Leave **off** for normal soak (noise).
- Turn **on** when hunting FMOD Play/Stop, proxy clone/FX (`[Proxy]`/`[DRV]`), or incremental puzzle apply diffs.
- Restart both games after flipping the pref (or re-Host/Connect so boot banner shows `VerboseLogging=true`).

**Hitch** (`[Hitch]` — prints only on a real spike, not healthy 5s spam):
| Tag / field | Meaning |
|-------------|---------|
| `frame dt=Nms` | Unscaled frame dt ≥ 50ms |
| `send gap=Nms` | Host send cadence gap ≥ 80ms |
| `recv pN gap=Nms` | Pose recv gap ≥ 80ms |
| `puzzle N.Nms` | Puzzle TickHost ≥ 8ms |
| `enemy N.Nms` | Enemy TickHost ≥ 8ms |
| `boss N.Nms` | Boss TickHost ≥ 8ms |
| `pickup N.Nms` | WorldPickup TickHost ≥ 8ms |
| `weaponClone N.Nms` | Remote weapon clone ≥ 8ms |
| `5s sendHz=… cost=TAG Nms` | 5s anomaly summary (max gaps/dt + worst Cost tag) |
| `phase=NAME Nms` | A mod update phase (`ModRuntime.Update`/`LateUpdate`) or a patched Update/LateUpdate/FixedUpdate (`Sync/HarmonyPhaseTiming`) took > 50 ms |
| `stall dt=Nms modUpdate/gameScripts/modLate/modGui/renderGap/focus/background/vsync` | Frame ≥ 400 ms: where it went (render gap ~ dt means the frame was lost outside mod code: compositor / present / vsync) |

Debug build copies the DLL to **both** `Mods/` folders. csproj names: `SignalisDir` = copy, `ClientSignalisDir` = Steam (deploy labels, not playtest roles).

On this machine the Unity Editor ships the SDK used for builds:

```bash
export DOTNET_ROOT="$HOME/Unity/Hub/Editor/6000.6.0f1/Editor/Data/DotNetSdk"
export PATH="$DOTNET_ROOT:$PATH"
```

## Decompile reference

`~/Archive/Windows-Desktop/Dev/SIGNALIS DECOMPILED/`

Decompile coverage (implemented + intentional locals; no park tables): `docs/DECOMPILE_COVERAGE.md`.

## Build / deploy

```bash
export DOTNET_ROOT="$HOME/Unity/Hub/Editor/6000.6.0f1/Editor/Data/DotNetSdk"
export PATH="$DOTNET_ROOT:$PATH"
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
5. Reverse-check both arrows (and late-join) before calling a sync path fixed
6. **No park / no defer** — implementable coverage is finished in-place; bump protocol when wire requires it (`.cursor/rules/no-park-no-defer.mdc`). Intentional locals in the sync table stay local by design.

## Release / scripts

**Single-source version:** `Bootstrap/PluginInfo.cs` `Version` is the only place to bump. `SyncRADation.csproj` reads it at build time (regex over the file) and sets `Version`/`AssemblyVersion`/`FileVersion`/`InformationalVersion` (assembly info is SDK-generated; `Properties/AssemblyInfo.cs` only keeps `ComVisible`/`Guid`). `MelonInfo` in `Bootstrap/SyncRADationMod.cs` reads `PluginInfo.Version` directly. The build errors if the constant cannot be parsed. Still bump `CHANGELOG.md` and `README.md` status line by hand; bump `ProtocolVersion` only when the wire changes.

| Script | What it does |
|--------|--------------|
| `scripts/build.sh` | Pins `DOTNET_ROOT` to the Unity SDK, Release build to `bin/stage/Release`, **no deploy** (`-p:NoDeploy=true` skips the csproj `CopyToMods` target). `--debug` = Debug config, `--deploy` = default csproj copy into `SignalisDir/Mods` + `ClientSignalisDir/Mods`. Prints DLL path + version and warns if the DLL file version differs from `PluginInfo.Version`. Env: `UNITY_DOTNET_SDK`, `MELONLOADER_DIR`, `OUT_DIR` |
| `scripts/test.sh` | `dotnet test tests/SyncRADation.Tests` (prints a note and exits 0 if the folder does not exist) |
| `scripts/package.sh` | Release build (no deploy) then `dist/SyncRADation-<ver>.zip`: `Mods/SyncRADation.dll`, `Mods/LiteNetLib.dll`, `INSTALL.md`, `LICENSE`, `CHANGELOG.md`, `LiteNetLib.LICENSE.txt`. `dist/` is gitignored |

**Reproducible dll (MVID):** `NetSchema.Hash` mixes in the module's MVID, so the handshake rejects a stale/modified dll of the same version. The csproj builds `Deterministic` with `PathMap=$(MSBuildProjectDirectory)=/src` and `EnableSourceLink=false` (SourceLink would embed the checkout path + commit hash in the PDB id, hence the MVID), so the MVID depends on the source and references only, not on the checkout directory: the same commit built in two directories (or two worktrees) gives a byte-identical dll and two separately built installs are not rejected. Check with `md5sum` of the two dlls. A different dotnet SDK / reference set can still change it; give both players the same dll when in doubt.

**Unattended dual-box run (this machine):** `--sync-scene <Scene>` loads that chapter the F7 way 4 s after MainMenu (host or offline only; a client follows the host). Host: `steam -applaunch 1262350 --sync-host --sync-scene DET_Detention`; ~60–75 s later client: `secondsignalis --sync-connect 127.0.0.1 7777`. Read both `Latest.log` with `grep -a` (non-UTF8 bytes). Healthy join: `Handshake OK`, `[Puzzle] C client live`, identical `[WorldRegistry] … checksum=` on both, no `WorldId divergence`, no `[Guard]`. Stop the games with `pgrep -f '[S]IGNALIS\.exe' | xargs -r kill` (a bare `pkill -f SIGNALIS.exe` also matches your own shell).

`LiteNetLib.dll` is **not** merged into the mod DLL; it is a separate reference (`Private=true`, from `lib/`) that ships next to it in `Mods/`.

Player docs: `README.md`, `INSTALL.md`. Soak checklist: `docs/PLAYTEST.md` (run it on two or three instances before any public release). Publish path stays a GitHub zip until a full playtest pass is enjoyable; Nexus waits on that.

Old README playtest-gate list (protocol 10 era, superseded by `docs/PLAYTEST.md`): Penrose photo inspect local / cryo pattern / BrokenKey; key door one key both traverse; ammo pickup partner `hasItem` false; enemy HP not doubled; client downed then disconnect restores control; notes/books local; int + string chapter loads; elevator flags, radio lock, FMOD loop on late join; then Chapter 1 (Reeducation to Mines elevator) with damage, door, Dialoguer, cutscene and storage box checks.
