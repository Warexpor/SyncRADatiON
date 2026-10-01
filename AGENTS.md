# SyncRADation — SIGNALIS Multiplayer Mod

LAN co-op MelonLoader mod for SIGNALIS (Unity IL2CPP, Unhollower Managed, MelonLoader **0.5.7**, Harmony, LiteNetLib). Host-authoritative world/story, peer-authored avatars, native presentation/FMOD.

Version and protocol: `Bootstrap/PluginInfo.cs` (single source).

## Where to look

| Need | File |
|------|------|
| What is synced, by whom, and what stays local | `docs/SYNC.md` |
| Symptom → code folder | `Domains/README.md` |
| Wire changes per protocol version | `docs/PROTOCOL.md` |
| Dual-box setup, log paths, log tags, `[Hitch]` fields | `docs/DEV_MACHINE.md` |
| Decompile coverage (implemented + intentional locals) | `docs/DECOMPILE_COVERAGE.md` |
| RVA folding check | `docs/RVA_FOLDING.md` |
| Soak checklist before any release | `docs/PLAYTEST.md` |
| Player docs | `README.md`, `INSTALL.md` |

Decompile: `~/Archive/Windows-Desktop/Dev/SIGNALIS DECOMPILED/` (`07_Ghidra_pseudoC/<Class>.c`, AssetRipper scenes and script stubs).

## Layout

```
Bootstrap/   SyncRADationMod, ModRuntime, PluginInfo, SessionResetRegistrations
Networking/  LanNetworkManager* (transport, HandlerRegistry), Dispatch/, Messages/
Domains/     Doors Enemies Bosses Story Scene Audio Pickups Inventory Players Combat Puzzles Session
             each: *SyncService / *NetHandlers / Patches/
Sync/        WorldId, WorldRegistry, NetGate, Guard, SessionReset, traces
UI/ Config/ Cheats/
```

Namespaces stay `SyncRADation.Networking` / `.Patches` / `.Players` / `.ItemSystem` regardless of folder. New mutable static state gets a reset in `Bootstrap/SessionResetRegistrations.cs` (scope `Scene`, `Session` and/or `Connection`) or a `// persistent: <reason>` line directly above it; `StaticStateGuardTests` fails otherwise. A domain whose full dump records "already sent" state registers a `FlushDiffNow` in `Bootstrap/DumpFlushRegistrations.cs`.

## Hard rules

1. Cross-peer identity is `WorldId` (`hash(scene + hierarchy path)`), never `GetInstanceID()`.
2. Host authority for world state; inventories stay independent (box and key ring are shared).
3. Reverse-check both arrows (host↔client initiator, in-room vs late-join) before calling a sync path fixed (`.cursor/rules/gamedev-reverse-check.mdc`).
4. No park / no defer: finish the work in place, bump the protocol when the wire needs it (`.cursor/rules/no-park-no-defer.mdc`). Intentional locals in `docs/SYNC.md` are design, not parked work.
5. Only sync vanilla fields/methods backed by the decompile.
6. "Works" needs a dual-instance playtest.

## Build / test / deploy

```bash
scripts/build.sh --debug --deploy     # Debug build, copies to both installs' Mods/
scripts/test.sh                       # unit tests
python3 scripts/rva_fold_scan.py --check
scripts/package.sh                    # release zip in dist/
```

`build.sh` pins `DOTNET_ROOT` to the Unity Editor SDK (`~/Unity/Hub/Editor/6000.6.0f1/Editor/Data/DotNetSdk`); without `--deploy` it does not copy. After a deploy, check both installs' `Mods/SyncRADation.dll` have the same md5.

Release bump: `PluginInfo.Version` (csproj reads it), plus `CHANGELOG.md` and the `README.md` status line by hand. `ProtocolVersion` only when the wire changes (log it in `docs/PROTOCOL.md`).

The build is deterministic (`PathMap`, no SourceLink) because `NetSchema.Hash` mixes the module MVID: the same commit gives a byte-identical dll anywhere, and a different dll of the same version is rejected at handshake. `LiteNetLib.dll` ships separately next to the mod in `Mods/`.
