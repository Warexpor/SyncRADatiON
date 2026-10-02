# Dual-box playtest machine and log diagnosis

## Setup (Linux + Proton, one PC)

**Steam = host. Copy = client.** Both are Windows SIGNALIS under Proton, MelonLoader **0.5.7** on both (`version.dll` + `MelonLoader/`), `boot.config` `single-instance=0` on both. F3 is `127.0.0.1:7777`.

| Role | Install | Launch | Prefs |
|------|---------|--------|-------|
| **Host** | `~/.local/share/Steam/steamapps/common/SIGNALIS` | Steam (Proton) | `UserData/MelonPreferences.cfg` |
| **Client** | `~/Work/MyProjects/SIGNALIS` | `secondsignalis` (→ `scripts/launch-client.sh`; own prefix `compatdata/syncradation-client`) | `UserData/MelonPreferences.cfg` |

Proton ignores MelonLoader's `version.dll` unless native wins. Host Steam launch options: `WINEDLLOVERRIDES="version=n,b" %command% -screen-fullscreen 0 -screen-width 1280 -screen-height 720` (also prefix `DllOverrides`); `secondsignalis` uses the same. Hyprland floats both as centered 1280x720 windows: Steam host top (`steam_app_1262350`), Proton client bottom (`SIGNALIS.exe`). The host size also lives in its prefix registry (`Screenmanager Resolution Width/Height`).

Debug deploy copies the DLL to **both** `Mods/` folders. csproj names: `SignalisDir` = copy, `ClientSignalisDir` = Steam (deploy labels, not playtest roles).

**Audio:** user service `signalis-volume` (`scripts/signalis-volume-watch.sh`) sets each new SIGNALIS stream once: host (prefix `compatdata/1262350`) 60%, client (`compatdata/syncradation-client`) muted. Off: `systemctl --user disable --now signalis-volume`.

**Unattended run:** `--sync-scene <Scene>` loads that chapter the F7 way 4 s after MainMenu (host or offline only). Host: `steam -applaunch 1262350 --sync-host --sync-scene DET_Detention`; ~60–75 s later client: `secondsignalis --sync-connect 127.0.0.1 7777`. Healthy join: `Handshake OK`, `[Puzzle] C client live`, identical `[WorldRegistry] … checksum=` on both, no `WorldId divergence`, no `[Guard]`. Stop: `pgrep -f '[S]IGNALIS\.exe' | xargs -r kill` (a bare `pkill -f SIGNALIS.exe` also matches your own shell).

## Logs

- Host: `~/.local/share/Steam/steamapps/common/SIGNALIS/MelonLoader/Latest.log`
- Client: `~/Work/MyProjects/SIGNALIS/MelonLoader/Latest.log`

Read with `grep -a` (non-UTF8 bytes). Connected lines are prefixed `H ` (host) / `C ` (client).

**Always-on tags:** `[Story]` `[Interact]` `[KeyRing]` `[StorageBox]` `[Scene]` `[Damage]` `[Door]` `[Puzzle]` `[Pickup]` `[Harmony]` `[Hitch]` (spikes + 5 s summary) `[Spawn]` `[Enemy]` `[Proxy]` `[Weapon]` `[Session]` `[Config]`.

**Diagnostics** (`[SyncRADation]` pref `Diagnostics`, default `false`, read at game start: restart after changing; **set it on both dev installs**). Turns on the instrumentation below; off, each gated trace costs one bool check. The boot banner prints `Diagnostics=…`, F2 shows "Harmony audit: off (Diagnostics pref)" when off.
- Flicker / Move / Bag / EventCam traces (below), the `[Hitch] phase=` / `stall` breakdown and the `Sync/HarmonyPhaseTiming` wrappers on patched Update methods (not installed when off), the full boot `PatchAudit` + late shader check.

**Scene scan line:** `[World] scan objects=N classes=M Xms warm gos=G cached=C Yms` — the WorldId scene cache (`Sync/WorldId`) is warmed at load (every scripted GameObject's id pinned before gameplay can destroy a sibling); `[Hitch] worldScan+warm` when it costs a spike.

**Flicker trace** (`Sync/FlickerTrace.cs`, Diagnostics, change-only, budgeted; proxy visibility sampled at 5 Hz): `[Room] chunk ON|OFF … by=game|mod flips=N`, `[Room] enter`, `[Proxy] vis pN …` / `[Proxy] jump pN d=…`, `[Proxy] bob pN rootZ=a..b drawnZ=a..b hipsH=a..b` (1 s window where the received root z, the drawn root z or the proxy hips height swung: rootZ wobble = sender root, drawnZ = what the floor lock let through, hipsH = pose) and the sender's own `[Move] self bob rootZ=… hipsH=…`, client `[Enemy] client <id> active=…` / `client snap-jump` / `client unknown <id>`, host `[Enemy] wake ok|REFUSED`. A `flapping:` line means that tag toggled more than 25 times in 5 s.

**Proxy init:** `[DRV] pose model=… bones=… hips=…` (always on) — the proxy has no Animator; its pose is the sender's synced bone rotations + humanoid hips.

**Story:** `[Story] apply commit … written=N`; `commit applied only in part` warns and requests one resync per scene.

**Event cam** (`Sync/EventCamTrace.cs`, Diagnostics): `[EventCam] click …` explains every click in a zoom-in puzzle (inputs, cursor, zoom, interaction under the cursor, `MOD-KILLED`).

**Move** (`Sync/MoveTrace.cs`, Diagnostics): `[Move] BLOCKED …` when the local player holds a direction for 1 s without moving: every native movement gate (gameState, charState, suspendInput, hurt/stun, animating/useAnim/grappled, cutscene/eventScreen flags, overrideInput, MoveOverrideActive, controller/rigidbody state, foto mode, tuner) plus what the native forward wall raycasts (`ThirdPersonCharacter.CollisionDetection`, `WallMask`) hit and the solid 3D colliders within 1.2 units; `[Move] free` when walking works again.

**Bag** (`Sync/BagTrace.cs`, Diagnostics): `[Bag] start/change` lines with the raw bag contents (not the ring masquerade).

**VerboseLogging** (`[SyncRADation]` pref, default `false`, set on **both** installs and restart): log volume only — FMOD Play/Stop, proxy clone/FX, incremental puzzle apply diffs. Instrumentation is `Diagnostics`.

## Hitch

`[Hitch]` prints only on a real spike.

| Tag / field | Meaning |
|-------------|---------|
| `frame dt=Nms` | Unscaled frame dt ≥ 50ms |
| `send gap=Nms` | Host send cadence gap ≥ 80ms |
| `recv pN gap=Nms` | Pose recv gap ≥ 80ms |
| `puzzle` / `enemy` / `boss` / `pickup N.Nms` | That TickHost ≥ 8ms. A trailing `gc=il2cpp+N` / `mono+N` means a garbage collection landed in that frame (counts since frame start: the pause is the GC, not the tick's own work) |
| `weaponClone N.Nms` | Remote weapon clone ≥ 8ms |
| `5s sendHz=… cost=TAG Nms` | 5s anomaly summary (max gaps/dt + worst Cost tag) |
| `phase=NAME Nms` (Diagnostics) | A mod update phase or a patched Update/LateUpdate/FixedUpdate (`Sync/HarmonyPhaseTiming`) took > 50 ms |
| (Diagnostics) `stall dt=Nms modUpdate/gameScripts/modLate/modGui/renderGap/focus/background/vsync` | Frame ≥ 400 ms: where it went (render gap ~ dt means the frame was lost outside mod code: compositor / present / vsync) |
