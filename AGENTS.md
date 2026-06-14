# SyncRADation — SIGNALIS Multiplayer Mod

## Project Overview
LAN multiplayer mod for SIGNALIS (rose-engine, 2022, Unity/IL2CPP).
Clones the local player GameObject and syncs position/rotation/animation over the network.

## Controls
- F2 — open/close multiplayer menu
- F3 — quick connect to saved IP/port
- F6 — item giver (cheat menu)
- F7 — entity spawner (cheat menu)
- G — drop currently selected inventory item at player position
- E — pick up dropped item (when near world pickup sprite)

## Architecture (Multi‑Player, 3+)

### Networking (LiteNetLib, host‑relay topology)
- `LanNetworkManager` — main net logic; host assigns player IDs (0=host, 1+ = clients). Host relays every received `PlayerState` to all other peers (not back to sender). `Dictionary<int, NetPeer>` for N connections.
- `NetMessages` — `HandshakeMessage`, `PlayerStateMessage`, `DoorStateMessage`, `FriendlyFireMessage`, `DropItemSpawnMessage`, `ItemPickedUpMessage`. Every state message carries `SenderPlayerId`.
- `WeaponUtils` — single source of truth for `ItemToWeaponType()` mapping (Items.itemlist → WeaponType), eliminating 3 copies.
- `DoorSyncService` — tracks `Doorway_Double.open`/`locked`, sends changes on change.
- `EntityStateBroadcastService` — scans enemies/doors/items via `FindObjectsOfType`, snapshots to all connected peers.
- `ClientEntityInterpolationService` — entity interpolation for non‑player entities (enemies, items). Dead code: `ApplyPlayerState()` no longer called (player position handled by `PlayerProxyManager`).

### Players
- `PlayerProxyBuilder.CreatePlayerClone` — model‑only clone (facing‑pivot child + SMR mesh/material fix for IL2CPP). Removes all MBs. Adds CapsuleCollider + non‑kinematic Rigidbody for push physics.
- `PlayerProxyManager` — `Dictionary<int, RemotePlayerProxy>` management. Per‑proxy position interpolation (smooth lerp + extrapolation, 0.3s timeout). Registers proxy CapsuleColliders for friendly fire lookup.
- `RemotePlayerProxy` — wrapper per remote player. Owns `AnimDriver`, `AudioSync`, `WeaponSync`. No singleton.
- `RemoteAnimatorDriver` — drives params (9 floats, 22 bools, 8 weapon bools, 16 triggers) + facing pivot + bone interpolation.
- `ProxyAudioSync` — positional FMOD sounds at proxy position via `PlayOneShotAttached`. Weapon cache from `AnWeapon` FMOD event refs.
- `RemoteWeaponSync` — weapon model clone with mesh/material fix. Damage cache from `AnWeapon.Damage`.
- `RemoteWeaponEffects` — per‑weapon FX: muzzle flash mesh, smoke PS, case eject PS, laser LineRenderer, ricochet/missed PS, slide transform.
- `BoneSyncManager` — armature root finding, bone collection, Euler read/apply (snap + lerp), 20Hz (every 3rd packet).
- `SourceAnimReader` — reads 9 floats + 22 bools from source player's Animator via individual `GetFloat`/`GetBool`, weapon type from `InventoryManager.EquippedWeapon`.

### Damage System (NEW)
- `NetworkDamageSystem` — `PlayerHP=100`, applies damage, play hurt sound (ElsterHurtSound), stagger (`charState=grabbed`), death (`charState=dead`), respawn after 5s.
- **Two detection paths:**
  1. *Shooter → proxy*: `ModRuntime.OnUpdate()` detects `PlayerState.shooting` leading edge, raycasts from player with `PlayerAttack.WallMask`, if hits proxy collider → sends `FriendlyFireMessage` to victim.
  2. *Proxy → local*: `RemoteWeaponEffects.DoImpactRaycast()` on victim's machine detects hit on local player → calls `NetworkDamageSystem.ApplyDamage()` directly.
- Weapon damage from `AnWeapon.Damage` cached per WeaponType.

### Item Drop System
- `DroppedItemManager` — static, persist/restore to file. Key formula: `(senderID << 16) | localIndex`.
- `WorldItem` — MonoBehaviour (billboard SpriteRenderer + BoxCollider trigger + kinematic Rigidbody). `DontDestroyOnLoad`.
- Broadcasts `DropItemSpawnMessage` / `ItemPickedUpMessage` to all peers.

### Sync
- `EntityProxy` — base MonoBehaviour for network entity proxy.
- `GameObjectEntityTracker` — thread‑safe ID assignment/matching.

### Cheats
- `ItemGiver` — IMGUI window, adds any game item to inventory.
- `EntitySpawner` — IMGUI window, finds all enemies, clones at player position.

## Protocol
- **Player ID**: host=0, clients=1,2,3...
- **Send rate**: 60Hz (`SendInterval=1f/60f`)
- **Bones**: every 3rd packet (20Hz), `ushort`‑encoded Euler angles
- **Delivery**: `ReliableOrdered` for all state messages
- **ProtocolVersion**: 1

## Current State (June 15 2026)

### Working
- Hosting/connecting over LAN (N players)
- Proxy creation (model‑only clone, all MBs removed)
- Position sync (interpolated per‑proxy, 15m teleport threshold)
- Facing sync (child pivot `localEulerAngles = (0, angle, 0)`, root tilt preserved)
- Bone sync (20Hz, lerp interpolation, 50ms window)
- All 9 floats + 22 bools + 8 weapon params + 16 triggers on proxy Animator
- Door state sync (Doorway_Double open/locked, FMOD SFX)
- Proxy positional audio (weapon shots, footsteps, reload, hurt, swap, ladder)
- Weapon model clone on proxy (SMR + MeshFilter mesh/material fix)
- Weapon effects (muzzle flash, smoke, case eject, laser, ricochet, slide)
- Push physics (CapsuleCollider + non‑kinematic Rigidbody)
- Friendly fire detection + damage (HP, hurt sound, stagger, death, respawn)
- Item drop/pickup (persist across sessions)
- No freeze on alt‑tab (`runInBackground`, deltaTime ≤ 0.1f, try‑catch network)
- 3+ player support (host‑relay topology, per‑player proxies)
- F6/F7 cheat menus
- Command‑line auto‑host/connect

### Known Issues
1. **Animation sync** — proxy plays wrong animations or twitches on connect
2. **Proxy renders wrong character model** — rarely shows different visual than source
3. **Door sync limited** — only Doorway_Double; ConnectedDoors traversal state and EventSlidingDoor not synced
4. **Enemy entity sync** — EntityStateBroadcastService scans via `FindObjectsOfType`; no aggro/physics sync
5. **LiteNetLib** — no encryption, no NAT punchthrough (LAN only)

### Key Constraints
- IL2CPP: `Object.Instantiate` drops sharedMesh/sharedMaterial on SMR clones (fix: manual copy)
- IL2CPP: `Animator.parameters` throws `TypeLoadException` (fix: individual GetFloat/GetBool by name)
- IL2CPP: `Animator.GetBool` for weapon params always returns false (fix: weapon type from `InventoryManager.EquippedWeapon`)
- `PlayerState.player` must never point to proxy (guard in ModRuntime checks all proxy IDs)
- Root Euler ~(312, y, 90) — facing is child pivot with pure Y rotation
- Root tilt affects bone world transforms on proxy (SkinnedMeshRenderer deforms from bone world positions)

## Files

### Core
- `PluginInfo.cs` — constants: version, protocol, port, send interval
- `SyncRADationMod.cs` — MelonMod entry point, input bindings, command‑line
- `ModRuntime.cs` — orchestrator: proxy guard, charState watchdog, friendly fire, respawn tick, network lifecycle

### Networking
- `LanNetworkManager.cs` — host/client, peer management, message handling, relays
- `NetMessages.cs` — all protocol structs + serialization
- `WeaponUtils.cs` — `ItemToWeaponType()` single source of truth
- `DoorSyncService.cs` — Doorway_Double state tracking + broadcast
- `EntityStateBroadcastService.cs` — entity snapshots → all peers
- `ClientEntityInterpolationService.cs` — entity interpolation (player pos handled by ProxyManager)
- `PlayerPositionManager.cs` — (legacy, not used for 3+)

### Players
- `RemotePlayerProxy.cs` — wrapper per remote player, owns drivers
- `PlayerProxyBuilder.cs` — model‑only clone, MB removal, mesh fix, physics
- `PlayerProxyManager.cs` — N‑proxy management, position interp, collider lookup
- `NetworkDamageSystem.cs` — HP, damage, hurt/death/respawn
- `RemoteAnimatorDriver.cs` — Animator params, facing, triggers, bone interp
- `SourceAnimReader.cs` — reads local player Animator state
- `ProxyAudioSync.cs` — positional proxy audio via FMOD
- `RemoteWeaponSync.cs` — weapon clone, mesh fix, damage cache
- `RemoteWeaponEffects.cs` — muzzle flash, laser, ricochet, slide, case eject
- `BoneSyncManager.cs` — armature bone Euler read/apply

### Other
- `Items/WorldItem.cs`, `Items/DroppedItemManager.cs` — world item system
- `Sync/GameObjectEntityTracker.cs`, `Sync/Proxies/EntityProxy.cs` — entity tracking
- `Cheats/ItemGiver.cs`, `Cheats/EntitySpawner.cs` — debug menus
- `UI/MultiplayerMenu.cs` — IMGUI connection UI
- `Config/ModConfig.cs` — MelonPreferences (IP, port)
- `Patches/PlayerPatches.cs` — empty Harmony patch (reserved)

## Build & Deploy
```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "SyncRADation.csproj" /p:Configuration=Debug /t:Build
```
Auto-copies to:
- `C:\Program Files (x86)\Steam\steamapps\common\SIGNALIS\Mods\` (host)
- `C:\SIGNALIS\Mods\` (client)

## Log Files
- Host: `C:\Program Files (x86)\Steam\steamapps\common\SIGNALIS\MelonLoader\Logs\`
- Client: `C:\SIGNALIS\MelonLoader\Logs\`

## Decompiled Game Code
`C:\Users\Androidus\Desktop\SIGNALIS DECOMPILED\Scripts\Assembly-CSharp\`
