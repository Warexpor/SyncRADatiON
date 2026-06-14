# SyncRADation — SIGNALIS Multiplayer Mod

## Project Overview
LAN multiplayer mod for SIGNALIS (rose-engine, 2022, Unity/IL2CPP).
Clones the local player GameObject and syncs position/rotation/animation over the network.

## Controls
- F2 — open/close multiplayer menu
- F3 — debug (not implemented)

## Architecture

### Networking (LiteNetLib)
- `LanNetworkManager` — main net logic, host/client, send/receive
- `NetMessages` — protocol messages (Handshake, PlayerState)
- `ClientEntityInterpolationService` — interpolation of received positions
- `PlayerPositionManager` — host-side position tracking
- `EntityStateBroadcastService` — entity sync (disabled for alpha)

### Players
- `PlayerProxyBuilder` — clones source player GameObject, disables all MBs
- `RemotePlayerProxy` — wrapper around the cloned GameObject
- `RemoteAnimatorDriver` — drives Animator params + armature rotation

### Sync
- `EntityProxy` — base class for network proxies
- `GameObjectEntityTracker` — tracks entities by stable ID

## Current State (June 14 2026)

### What Works
- Hosting/connecting over LAN
- Proxy GameObject created by cloning source player
- Proxy follows network position (interpolated)
- All MonoBehaviours disabled on proxy (no script interference)
- Saved/restored PlayerState statics around cloning
- **Facing sync** via `_facingPivot.localEulerAngles = Vector3(0, angle, 0)` — child transform (same as `Character3DAnimator.trans`), not root. Root tilt preserved.
- **No freeze on alt-tab** — `Application.runInBackground = true`, `Time.deltaTime` clamped ≤ 0.1f, `try-catch` around network calls
- **Model stands correctly** — root tilt is NOT touched (preserves Euler(312, y, 90)), only facing child rotates

### What's Broken
1. **Animation sync** — proxy plays wrong animations or twitches on connect
2. **Proxy renders wrong character model** — rarely, 3D model on proxy shows different visual than source player

### Key Constraints
- SIGNALIS is **full 3D** (top-down 2.5D perspective, 3D character models with SkinnedMeshRenderers)
- IL2CPP game — decompiled C# shows empty method bodies (real code in native)
- **IL2CPP bug**: `Object.Instantiate` drops public `Transform` serialized references. `Character3DAnimator.trans` is null on proxy.
- `PlayerState.player` static field must never point to proxy (prevents sending proxy state instead of real player)
- All MonoBehaviours on proxy must be disabled to prevent static field corruption
- `Character3DAnimator.trans` is a **child** of root, NOT root itself. Root has tilt Euler(~312, y, ~90). `trans` gets pure Y: `localEulerAngles = Vector3(0, fAngle, 0)`.
- Root tilt on proxy affects bone world positions (SkinnedMeshRenderer deforms based on bone world transforms)
- **Root Euler angles approx (312, 326, 90)** on source — massive X (pitch) and Z (roll) cause degenerate LookRotation
- Root rotation on proxy affects bone world positions (SkinnedMeshRenderer deforms based on bone world transforms)
- **Root Euler angles approx (312, 326, 90)** on source — massive X (pitch) and Z (roll) cause degenerate LookRotation

## What We've Tried (and Results)

| Approach | Result |
|----------|--------|
| Clone player, disable root MBs only | PlayerState.player gets overwritten by proxy → position never syncs |
| `rotation = identity` | Children's world transforms break → model "in ground on side" |
| `rotation = identity` + restore children world transforms | Children keep source's X/Z tilt → Armature gets double Y rotation (source Y + network Y) |
| Keep source rotation (no zero) + disable all MBs | Tilt visible but model should be intact |
| Armature Y rotation only | Axis wrong (rotation around local Y ≠ world Y) |
| `armature.localEulerAngles.y = angle` | Model face-down |
| `modelRoot.rotation = Euler(0, angle, 0)` | Children underground (world position changes) |
| `_savedTilt * Euler(0, angle, 0)` | Axis correct but facing includes original Y (double facing) |
| `Quaternion.Euler(rootX, angle, rootZ)` | Double tilt (modelRoot is child of root, both have tilt) |
| `LookRotation(targetForward, parentUp)` | Wrong axis (parentUp is horizontal, Y≈0, LookRotation produces rolled rotation) |
| Strip root tilt + `root.eulerAngles.y = angle` | Model flat on ground (root tilt removed, camera shows flat) |
| **`_facingPivot.localEulerAngles = (0, angle, 0)` — child transform, not root** | **✓ WORKS! Root tilt preserved, facing correct** |

## Build & Deploy
```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "SyncRADation.csproj" /p:Configuration=Debug /t:Build
```
Auto-copies to:
- `C:\Program Files (x86)\Steam\...\SIGNALIS\Mods\` (host)
- `C:\SIGNALIS\Mods\` (client)

## Log Files
- Host: `C:\Program Files (x86)\Steam\...\SIGNALIS\MelonLoader\Logs\`
- Client: `C:\SIGNALIS\MelonLoader\Logs\`

## Decompiled Game Code
`C:\Users\Androidus\Desktop\SIGNALIS DECOMPILED\Scripts\Assembly-CSharp\`

Key files:
- `Character3DAnimator.cs` — has `trans` field (null on proxy due to IL2CPP)
- `AlternatePlayerController.cs` — has `fAngle`, `facing` (actual player controller used)
- `PlayerController8.cs` — has `fAngle`, `facing` (not used, pc8 is null)
- `PlayerState.cs` — static fields: `player`, `pcs`, `aiming`, `shooting`, `facing`, `charState`

## Important Code Details

### PlayerProxyBuilder.CreatePlayerClone
1. Save all PlayerState static fields
2. Instantiate source under inactive dummy (Awake runs)
3. Restore statics
4. Detach from dummy (`SetParent(null, true)`)
5. Disable ALL MonoBehaviours in full hierarchy
6. Set Animator root motion off, culling AlwaysAnimate
7. Make Rigidbody(2D) kinematic
8. Destroy dummy

### RemoteAnimatorDriver
- `Tick()` — sets Animator params (Speed, Forward, Turn, Aiming, Shooting, Running)
- `LateTick()` — calls `ApplyFacing()`: `_facingPivot.localEulerAngles = (0, _currentFacing, 0)`
- `Initialize()` — calls `FindFacingPivot(root)`: finds first child of root that has a SkinnedMeshRenderer in its subtree
- `ApplyState()` — snap sets `_currentFacing = rotY`, calls ApplyFacing
- `PreTick()` — interpolates `_currentFacing` toward `_targetFacing`

### ClientEntityInterpolationService
- `ApplyPlayerState(pos, rotY)` — stores target with interpolation timing
- `TickLateUpdate()` — for id==0 (player proxy): sets `proxy.position = pos` only (rotation handled by RemoteAnimatorDriver)

## Next Steps
- Fix "wrong model": could be animation state mismatch or material/shader issues on clone
- Fix animation sync/twitch: RemoteAnimatorDriver animation params may conflict with frozen Animator state
