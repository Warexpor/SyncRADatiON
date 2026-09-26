# SyncRADation — SIGNALIS Multiplayer Mod

**Status:** v0.5.7 — protocol **v10**. Host-authoritative world/story + native presentation/FMOD. Dual-instance playtest required. Decompile: `~/Archive/Windows-Desktop/Dev/SIGNALIS DECOMPILED` (`~/Omarchy_Backup/Desktop/Dev/...` is gone on this machine).

## Product

LAN multiplayer MelonLoader mod for SIGNALIS (Unity IL2CPP / Unhollower-style Managed). Host-authoritative world + peer-authored avatars over LiteNetLib.

## Controls

- F2 — multiplayer menu (Host / Connect / Resync / status)
- F3 — quick connect (saved IP/port)
- F6 / F11 — item giver / entity spawner (any replika type, not current-scene clones)
- F7 — location teleporter (chapters + rooms in current level)

- G — drop selected item (inventory slot, or DROP in the item command list)

Walk up to a dropped prop for the native TAKE prompt (yes/no inspect, ammo count). There is no extra pickup key.

## What is synced (0.5.7)

| Area | Authority | Notes |
|------|-----------|--------|
| Avatar proxy + anim/bones/weapons | Peer | ~30 Hz state + bones |
| Enemies | Host | WorldId snaps; **native TakeDamage**; client hits Harmony→host; host **wakes sleeping-chunk** enemies when a peer is near. Contact ram damage stays native. F11 spawn is host-authored (`EnemySpawn` + `SR_Spawn_*` WorldId) |
| Doors (double / sliding) | Any peer emit, host relay | Visual open/close via native methods |
| ConnectedDoors (room links) | Lock only | **Never** sync traverse / StartA/B — room entry is local. Unique key doors: one solve (party key ring **Key/Object only**), both walk |
| Ladders | Local traverse | Climb is per-player; other peer only hears proxy SFX (no `Interaction.trigger`) |
| Chapter / scene load | Host | SceneFollow via `AsyncLoader` / `SceneHelper` / `LoadLevelZone` — int and string loads both gate. **Wreck↔hole never follows** (each Elster loads `PEN_Hole` when they finish the airlock). Host leaving Penrose still follows. Client F7 is a host load + follow, not `BeginApply` |
| Story (SProgress, Dialoguer, cutscenes, END_Manager) | Host | Full slot dump on join; **books / notes / EventScreen / EventOnlyRoom / airlock (`PEN_Titles`) / lock-flavor lines stay local**. Story Dialoguer Start (all overloads) is client→host then presentation replay; Continue/End apply on clients. **Other-room cutscenes / EventZones do not Start/Invoke** on the observer |
| World FMOD | Host Play/Stop | StudioEventEmitter by WorldId; skip Elster + radio UI + **Music/Cutscenes/Ambience beds**; **far non-door Play** distance-gated like doors; tuner freq local |
| Puzzles / locks / elevators / radio module / storage / event zones / alert | Host + client emit | **WorldId-keyed**; client emits puzzle/lock types only (not `Interaction.trigger` / EventZone / combat); apply **snaps flags + doors**, never EventScreen / `trigger()`; cryo/codepad/pump/pipes/hatch live-apply native Open/Drain/TurnValve; unlocked location doors stay open for the party; **GunCase / AraNest / LAB_RifleQuest / LOV_Microfiche** (protocol 10) |
| Host disconnect | Client goes offline | Restores play + input; clears enemy/boss puppets, EventZone/cutscene/airlock/SceneFollow/Dialoguer sticky |
| World ItemPickups | Host claim/grant | Claimer gets item; unique **Key/Object** go on the **party key ring**; ammo/docs do not. Client bag keys still unlock UseItem (hatch card) |
| Player-dropped items | Peer + relay | G or inventory **DROP**; floor snap; native TAKE inspect (yes/no + count) then grant; join dump; bag-full reject |
| Death | Asymmetric | Client downed (ammo/docs floor bag; **party-ring Key/Object stay on ring**, no ghost unique); native `HurtElster` HP; host death `SaveManager.Load` for both |
| Bosses (END / Chimera / Mynah / Kolibri / Adler) | Host | `END_Boss.Elster` / `BOS_Adler.Elster`; Kolibri dead/intensity; **join dump ForceFull**; client skips apply while transient + refreshes empty boss cache |
| Friendly fire | Opt-in | Default OFF |
| Inventories | Independent | 6-slot bags stay personal; box + key ring are shared |

## Architecture (0.5.7 Domains)

Composition over endless partials. Domain folders hold `*SyncService` / `*NetHandlers` / `Patches/`; namespaces stay stable (`SyncRADation.Networking`, `.Patches`, `.Players`, `.ItemSystem`) so call sites do not churn.

**Bugfix map:** `Domains/README.md` (symptom → folder).

```
Bootstrap/                 # SyncRADationMod, ModRuntime, PluginInfo
Networking/
  LanNetworkManager*.cs    # thin transport + HandlerRegistry + PublicApi
  Dispatch/                # TryDispatch* → domain NetHandlers
  Messages/                # NetMessages (protocol 10)
Domains/
  Doors/ Enemies/ Bosses/ Story/ Scene/ Audio/
  Pickups/ Inventory/ Players/ Combat/ Puzzles/ Session/
Sync/                      # WorldId, WorldRegistry, NetGate, LocalInspect
UI/ Config/ Cheats/
```

- `LanNetworkManager` — LiteNetLib peers, ids 0..N-1 (recycled 1..MaxPlayers-1), host relay, join snapshot **unicast** via `_unicastPlayerId` / BeginUnicast; `PlayerRoster`; domain Send/Handle on composed NetHandlers (see HandlerRegistry)
- `WorldId` / `WorldRegistry` — `hash(scene + hierarchy path)` — never `GetInstanceID()`
- `PuzzleSyncService` — coordinator façade; family SyncServices under `Domains/Puzzles/*`; ApplyEntry/TryRead are forwards
- Boss Kolibri/Adler + EnemyManager/GlobalAlert + Cutscene/Dialogue/MultiCondition/SaveRoom puzzle flags owned by Boss/Enemy/Story (same `PuzzleStateMessage` wire)
- `DroppedItemManager` — thin façade over Registry/Spawner; claim/drop wire in `DroppedItemNetHandlers`
- Reverse-check both arrows (host↔client initiate) and late-join dump before calling a path fixed

## Protocol

- **ProtocolVersion = 10** (PuzzleType 73–76 GunCase/AraNest/RifleQuest/Microfiche; v9 fields retained)
- Port default `7777`, key `SyncRADation`
- v6: full SProgress dump, UnityEvent presentation, FmodEmitter Play/Stop
- v7: `PlayerRoster` (3+ peers), recycled client ids, join/resync dump to the requester only
- v8: host-authored `EnemySpawn` (F11 templates by `AnEnemyType` from in-memory prefabs; does **not** additive-load chapters)
- v9: PuzzleType 64–72 (MusicBox…OpenableDrawer), `PuzzleStateEntry.Float1`, BossSnapshotNet Hp/Corrupt, END playstyle on StoryCommit, FMOD Guid/Attached/`fmod` one-shots, SceneManager.LoadScene gate
- v10: PuzzleType 73–76 (GunCase, AraNest, LAB_RifleQuest, LOV_Microfiche) + client emit for SwingDoor/DoorwaySimple/StorageBox/KeyGrid/ArianePhotoCode

## This machine (dual-instance, Linux + Proton)

Same PC. **Steam = host. Copy = client.** Both are Windows SIGNALIS under Proton. F3 is `127.0.0.1:7777`. `VerboseLogging` off unless hunting FMOD / proxy clone. `boot.config` `single-instance=0` on both. MelonLoader **0.5.7** on both (`version.dll` + `MelonLoader/`).

Proton will ignore MelonLoader’s `version.dll` unless native wins over Wine’s builtin. Host Steam launch options: `WINEDLLOVERRIDES="version=n,b" %command% -screen-fullscreen 0 -screen-width 2560 -screen-height 720` (also prefix `DllOverrides`). `secondsignalis` uses the same. Hyprland floats **Steam host top-half** (`steam_app_1262350`) and **Proton client bottom-half** (`SIGNALIS.exe`) for dual-box playtest.

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
