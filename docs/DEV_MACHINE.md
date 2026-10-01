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

**Always-on tags:** `[Story]` `[Interact]` `[KeyRing]` `[StorageBox]` `[Scene]` `[Damage]` `[Door]` `[Puzzle]` `[Pickup]` `[Harmony]` `[Hitch]` `[Spawn]` `[Enemy]` `[Proxy]` `[Weapon]` `[Session]` `[Bag]`.

**Flicker trace** (`Sync/FlickerTrace.cs`, change-only, budgeted): `[Room] chunk ON|OFF … by=game|mod flips=N`, `[Room] enter`, `[Proxy] vis pN …` / `[Proxy] jump pN d=…` / `[Proxy] hips corrected d=… n=… proxyState=…` (the proxy Animator's hips were ≥ 0.1 m off the sender's and got overwritten; worst offset and count per 2 s), client `[Enemy] client <id> active=…` / `client snap-jump` / `client unknown <id>`, host `[Enemy] wake ok|REFUSED`. A `flapping:` line means that tag toggled more than 25 times in 5 s.

**Event cam** (`Sync/EventCamTrace.cs`): `[EventCam] click …` explains every click in a zoom-in puzzle (inputs, cursor, zoom, interaction under the cursor, `MOD-KILLED`).

**Move** (`Sync/MoveTrace.cs`): `[Move] BLOCKED …` when the local player holds a direction for 1 s without moving: every native movement gate (gameState, charState, suspendInput, hurt/stun, animating/useAnim/grappled, cutscene/eventScreen flags, overrideInput, MoveOverrideActive, controller/rigidbody state, foto mode, tuner) plus what the native forward wall raycasts (`ThirdPersonCharacter.CollisionDetection`, `WallMask`) hit and the solid 3D colliders within 1.2 units; `[Move] free` when walking works again.

**Bag** (`Sync/BagTrace.cs`): `[Bag] start/change` lines with the raw bag contents (not the ring masquerade).

**VerboseLogging** (`[SyncRADation]` pref, default `false`, set on **both** installs and restart): only when hunting FMOD Play/Stop, proxy clone/FX (`[Proxy]`/`[DRV]`) or incremental puzzle apply diffs.

## Hitch

`[Hitch]` prints only on a real spike.

| Tag / field | Meaning |
|-------------|---------|
| `frame dt=Nms` | Unscaled frame dt ≥ 50ms |
| `send gap=Nms` | Host send cadence gap ≥ 80ms |
| `recv pN gap=Nms` | Pose recv gap ≥ 80ms |
| `puzzle` / `enemy` / `boss` / `pickup N.Nms` | That TickHost ≥ 8ms. A trailing `gc=il2cpp+N` / `mono+N` means a garbage collection landed in that tick (the pause is the GC, not the tick's own work) |
| `weaponClone N.Nms` | Remote weapon clone ≥ 8ms |
| `5s sendHz=… cost=TAG Nms` | 5s anomaly summary (max gaps/dt + worst Cost tag) |
| `phase=NAME Nms` | A mod update phase or a patched Update/LateUpdate/FixedUpdate (`Sync/HarmonyPhaseTiming`) took > 50 ms |
| `stall dt=Nms modUpdate/gameScripts/modLate/modGui/renderGap/focus/background/vsync` | Frame ≥ 400 ms: where it went (render gap ~ dt means the frame was lost outside mod code: compositor / present / vsync) |
