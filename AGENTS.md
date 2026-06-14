# SyncRADation — SIGNALIS Multiplayer Mod

## Project Overview
LAN multiplayer mod for SIGNALIS (rose-engine, 2022, Unity/IL2CPP).
Clones the local player GameObject and syncs position/rotation/animation over the network.

## Controls
- F2 — open/close multiplayer menu
- F3 — quick connect to saved IP/port
- G — drop currently selected inventory item at player position
- E — pick up dropped item (when near world pickup sprite)

## Architecture

### Networking (LiteNetLib)
- `LanNetworkManager` — main net logic, host/client, send/receive
- `NetMessages` — protocol messages (Handshake, PlayerState, DoorState)
- `ClientEntityInterpolationService` — interpolation of received positions
- `PlayerPositionManager` — host-side position tracking
- `EntityStateBroadcastService` — entity sync (disabled for alpha)
- `DoorSyncService` — tracks Doorway_Double open/locked state, sends changes to peer

### Players
- `PlayerProxyBuilder` — clones source player GameObject, disables all MBs
- `RemotePlayerProxy` — wrapper around the cloned GameObject
- `RemoteAnimatorDriver` — drives Animator params + armature rotation
- `ProxyAudioSync` — plays positional audio at proxy position (weapon shots, footsteps, hurt, reload, swap)

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
- **Door state sync** — `Doorway_Double.open`/`locked` synced via `DoorSyncService` every 0.5s (native code plays FMOD open/close SFX and animates door panels)
- **Proxy positional audio** — `ProxyAudioSync` plays weapon shots (from `AnWeapon.shotSound` AudioClip), footsteps (noise), hurt/reload/swap tones at proxy world position via `AudioSource.PlayClipAtPoint`

### What's Working (New)
- **Door state sync**: `Doorway_Double.open`/`locked` synced via `DoorSyncService` every 0.5s
- **Proxy positional audio**: Weapon shots via `AnWeapon.shotSound` (Unity AudioClip), generated footsteps/hurt/reload/swap tones at proxy world position

### What's Broken
1. **Animation sync** — proxy plays wrong animations or twitches on connect
2. **Proxy renders wrong character model** — rarely, 3D model on proxy shows different visual than source player
3. **Door sync limited** — only `Doorway_Double`; `ConnectedDoors` traversal state (`inProgress`, `forwards`) and `EventSlidingDoor` not synced
4. **Audio is basic** — generated tones for most sounds, real weapon AudioClips only if `Resources.FindObjectsOfTypeAll<AnWeapon>()` succeeds
5. ~~**ProxyAudioSync crash**: `AudioClip.SetData()` throws `ObjectCollectedException` in IL2CPP — the managed `float[]` gets GC-collected before the native call completes.~~ **FIXED**: `GC.KeepAlive(data)` after `clip.SetData()` prevents premature collection.

### Item Drop System (v0.3.0-alpha)
- `DroppedItemManager` — static manager, spawns/despawns world pickups
- `WorldItem` — MonoBehaviour on dropped item sprites (billboard + trigger + interaction)
- `DropItemSpawnMessage` — sent from dropper to other player (includes senderID, localIndex, itemEnum, count, pos)
- `ItemPickedUpMessage` — sent from picker to other player (includes senderID, localIndex)
- Item ID scheme: `key = (senderID << 16) | localIndex` (senderID 0=host, 1=client)
- Pickup GameObject: empty GameObject + SpriteRenderer (AnItem.worldSprite) + BoxCollider trigger + Rigidbody(kinematic)
- Billboard: rotates to face Camera.main every frame
- Drop trigger: G key drops InventoryManager.CurrentItem at player position
- Pickup trigger: E key near WorldItem → adds to local inventory → sends ItemPickedUp to other
- No host authority — each player manages own inventory, sends notifications P2P
- No journal sharing yet — all items are exclusive pickups for v1

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
- `Tick()` — sets all Animator params: 9 floats + 22 bools from AnimBools bitfield + resets 8 weapon bools then sets active one
- `LateTick()` — calls `ApplyFacing()`: `_facingPivot.localEulerAngles = (0, _currentFacing, 0)`
- `Initialize()` — calls `FindFacingPivot(root)`: finds first child of root that has a SkinnedMeshRenderer in its subtree
- `ApplyState(PlayerStateMessage)` — snap sets all params (first packet) or just targets (subsequent)
- `PreTick()` — interpolates forward/turn/aimingTime/facing toward targets
- `ApplyPendingTriggers()` — sets all 16 trigger types

### SourceAnimReader
- `ReadFromPlayer(GameObject player, ref PlayerStateMessage msg)` — reads 9 floats + 22 bools from source player's Animator using individual GetFloat/GetBool calls (avoids IL2CPP TypeLoadException on a.parameters)
- Detects triggers from bool leading-edge transitions (false→true)
- `AccumulateTrigger(AnimTriggers)` — manual trigger injection
- `Reset()` — clears state for scene changes / disconnects

### Full Synced Animator Parameter Table (ElsterNewController)

**Floats (9)** — synced every packet:
| Param | Source | Description |
|-------|--------|-------------|
| Forward | `ThirdPersonCharacter.m_ForwardAmount` (reflection) | Movement speed blend |
| Turn | `ThirdPersonCharacter.m_TurnAmount` (reflection) | Turn blend |
| AimingTime | `PlayerAttack.anim_AimingTime` | Aim transition blend |
| Stamina | `PlayerAttack.anim_Stamina` | Stamina level |
| Blend | `LAB_PatternPond.anim_Blend` | Animation blend |
| IKwalk | `ShieldBreaker.anim_IKwalk` | IK walking blend |
| X | `ElsterHurtAnimation.anim_X` | Input/hurt direction X |
| Y | `ElsterHurtAnimation.anim_Y` | Input/hurt direction Y |
| HurtTime | `ElsterHurtAnimation.anim_HurtTime` | Hurt animation blend |

**Bools from AnimBools bitfield (22)** — synced every packet:
| Bit | Param | Class | Description |
|-----|-------|-------|-------------|
| 0 | Aiming | PlayerAttack | Is aiming |
| 1 | Shooting | PlayerAttack | Is shooting |
| 2 | Running | PlayerState.charState | Is running |
| 3 | Grounded | ThirdPersonCharacter | Is on ground |
| 4 | Crouch | TPC (inferred) | Is crouching |
| 5 | Blocked | PlayerAttack | Weapon blocked by wall |
| 6 | Dead | ValidTarget | Is dead |
| 7 | Inventory | PlayerAttack | Inventory is open |
| 8 | Attack | PlayerAttack | Is attacking |
| 9 | Injured | PlayerAttack / ElsterHurtAnimation | Is injured |
| 10 | Stomp | PlayerAttack / ElsterHurtAnimation | Stomp attack |
| 11 | Push | PlayerAttack | Push |
| 12 | Melee | PlayerAttack | Melee weapon equipped |
| 13 | Snap | ThirdPersonCharacter | Snap turning |
| 14 | Reload | PlayerAttack | Is reloading |
| 15 | Swap | PlayerAttack | Is swapping weapons |
| 16 | Burst | PlayerAttack | Burst fire |
| 17 | Taser | ToolManager | Taser equipped |
| 18 | Random | RandomSubstate | Random substate selection |
| 19 | Hugged | ElsterHurtAnimation | Grabbed by enemy |
| 20 | ReloadRounds | PlayerAttack | Reload phase — rounds |
| 21 | ReloadChamber | PlayerAttack | Reload phase — chamber |

**Weapon bools (8, mutually exclusive)** — reset all then set active per packet:
Handgun, Pistol, Revolver, Shotgun, Rifle, SMG, Flare, CAR.  
Melee and Taser handled via AnimBools bits above.  
WeaponType → Anim bool mapping via `InventoryManager.EquippedWeapon.parentItem._item`.

**Triggers via AnimTriggers bitfield (16)** — detected from bool leading edges + AccumulateTrigger:
| Bit | Trigger | Source Class | Detected From |
|-----|---------|-------------|---------------|
| 0 | Hurt | ElsterHurtAnimation | Injured false→true |
| 1 | Die | ElsterDeathHandler | Dead false→true |
| 2 | Fire | EnemyController | (manual) |
| 3 | Pickup | ItemPickup / ToolManager | (manual) |
| 4 | Radio | LAB_PatternPond | (manual) |
| 5 | Drop | CutsceneHelper / Ladder | (manual) |
| 6 | Sleep | EnemyManager | (manual) |
| 7 | Injector | ToolManager | (manual) |
| 8 | InjectorCancel | ToolManager | (manual) |
| 9 | Reload | PlayerAttack | Reload false→true |
| 10 | Attack | PlayerAttack | Attack false→true |
| 11 | Swap | PlayerAttack | Swap false→true |
| 12 | Burst | PlayerAttack | Burst false→true |
| 13 | Stomp | PlayerAttack | Stomp false→true |
| 14 | Push | PlayerAttack | Push false→true |
| 15 | Snap | ThirdPersonCharacter | Snap false→true |

### ClientEntityInterpolationService
- `ApplyPlayerState(pos, rotY)` — stores target with interpolation timing
- `TickLateUpdate()` — for id==0 (player proxy): sets `proxy.position = pos` only (rotation handled by RemoteAnimatorDriver)

### ProxyAudioSync
- `Tick(state, bools, triggers)` — called from `RemotePlayerProxy.ApplyState`
- Detects shooting (bool leading edge), plays weapon shot at proxy pos (`_shootTone` fallback or real clip from cache)
- Detects moving (Running || Forward>0.1 && Grounded), plays footstep noise every 0.45s
- Detects ReloadTrigger / Hurt trigger with cooldowns
- Detects weapon swap (change in `state.Weapon`), plays swap tone
- `BuildWeaponCache()` scans `Resources.FindObjectsOfTypeAll<AnWeapon>()` once, maps `parentItem._item` enum to `WeaponType`, caches `shotSound`/`reloadSound`
- Clip generation uses `AudioClip.Create + SetData()` with `GC.KeepAlive(data)` to prevent IL2CPP `ObjectCollectedException`
- All generated clips stored in instance fields to keep them alive
- Uses `AudioSource.PlayClipAtPoint()` for all sounds (creates temp GameObject + AudioSource automatically)

## Next Steps
- Test comprehensive sync in multiplayer — verify all weapons, walk/run, crouch, inventory, death states
- Add enemy aggro: re-add `Player` tag and enable colliders on proxy, verify doors still work
- Push to GitHub if user wants remote backup
