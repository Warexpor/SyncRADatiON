# SyncRADation

LAN/VPN co-op for **SIGNALIS**, for **2 to 8 players** (default 4). One player hosts and owns the world and story. Everyone else plays as a real Elster and their actions go through the host.

**Status: 0.5.62, protocol 16, pre-release.** This build is in the playtest phase. Nothing in it is proven in a real multi-player session yet, so expect bugs. Back up your saves before you try it.

## Requirements

- SIGNALIS (Steam, Windows build; Linux via Proton works, see below).
- **MelonLoader 0.5.7, exactly.** Newer or older versions do not work. The mod is built against the Unhollower assembly layout that 0.5.7 generates, and later MelonLoader versions changed it.
- **Every player runs the exact same mod build.** The connection handshake rejects any version or protocol mismatch, even a patch-level one (for example 0.5.61 vs 0.5.62).
- A network path between players: same LAN, or a VPN (see Hosting and joining).

## Install

The short version is in [INSTALL.md](INSTALL.md) (it ships inside the release zip).

1. Install **MelonLoader 0.5.7** into your SIGNALIS folder (the one with `SIGNALIS.exe`). Start the game once so MelonLoader generates its assemblies, then quit.
2. Copy `SyncRADation.dll` and `LiteNetLib.dll` into `SIGNALIS/Mods/`.
3. Start the game. Press **F2** in a chapter: the multiplayer menu should open.

### Linux / Proton

MelonLoader's `version.dll` is ignored by Proton unless the native one wins over Wine's built-in. Set the Steam launch options to:

```
WINEDLLOVERRIDES="version=n,b" %command%
```

### Two copies on one PC (testing)

Set `single-instance=0` in `boot.config` of both installs and use `127.0.0.1` as the address.

## Hosting and joining

1. Everyone loads the same chapter. (Clients follow the host's chapter automatically if it differs; if it gets stuck, load the same chapter by hand.)
2. **Host:** F2, then **Host Game**. The game listens on **UDP 7777**. Forward/allow that port if you are behind a firewall.
3. **Clients:** F2, type the host address, **Connect**. Or press **F3** to reconnect to the saved address.
4. The host sends the current world state to each joiner. If doors or pickups look wrong, press **Resync world** in the F2 menu.
5. For play over the internet use a VPN that gives everyone a shared virtual LAN: **Tailscale**, **ZeroTier** or **Radmin VPN**. Clients connect to the host's VPN address. There is no relay server and no direct internet hosting support.

The F2 menu shows a roster of everyone connected. The host can cap the session with `MaxPlayers` (applies the next time you press Host Game).

## Controls

| Key | Action |
| --- | --- |
| F2 | Multiplayer menu (Host, Connect, Disconnect, Resync world, roster, status) |
| F3 | Quick connect to the saved address |
| G | Drop the highlighted inventory slot (or DROP in the item command list) |
| F6 | Item giver (cheat tool) |
| F7 | Location teleporter: chapters and rooms in the current level. As a client this loads the chapter on the host and everyone follows |
| F11 | Entity spawner (cheat tool, host-authoritative) |

To pick up something another player dropped, walk up to it and use the normal TAKE prompt. There is no extra pickup key.

## What is shared and what stays personal

**Shared (the host decides, everyone sees the same result):**

- Enemies and bosses: one set of enemies, hits from any player count on the same enemy.
- Story progress, dialogue, cutscenes, endings.
- Chapter loads (everyone follows the host).
- Doors, locks and puzzles. A door or lock solved by one player is open for everyone, including people who join later.
- Elevators, radio, pumps and pipes, storage boxes (box contents are shared).
- Items lying in the world. Each one exists once: whoever picks it up gets it. Unique keys and key objects go onto a **party key ring**, so one key opens the door for the whole party.
- Sounds from world objects.
- Death and revive (see below).

**Personal (intentionally local):**

- Your 6-slot inventory, ammo and health items.
- Books, notes, photos and other pure reading or inspect screens.
- Walking through room-to-room doors and climbing ladders (each player does their own).
- Airlock and wreck/hole entry in Penrose: each player loads the next area when they finish it.
- Cutscenes and event zones in a room you are not in do not play for you.
- Radio tuner frequency while you turn it.

**Death:** a dead player is **downed**, not game over. They revive next to the nearest living teammate after `DownedRespawnDelay` seconds (default 20). The party only loses when **everyone is down at once**: the host then reloads its last save and clients get their bag back from the last save snapshot. Friendly fire is off by default.

A solo game (no session running) behaves like vanilla SIGNALIS.

## Known limitations

- Pre-release: the whole 0.5.57 to 0.5.62 feature set (death/revive, party save, 3+ players, boss and enemy client hits, puzzle merging) is compile- and unit-tested; host+client join into a real chapter is smoke-tested, gameplay is not.
- A client's "quit to menu" is blocked silently.
- If a client joins while loading, it may wait up to 6 seconds for the host's state and then start from local defaults. Use **Resync world**.
- A Falke spear held only by a client may not count as held for the Falke fight.
- Bag snapshots restore items but not per-weapon ammo.
- Revive falls back to in-place if no teammate is in the same scene.
- Another player's dropped items can still be despawned by a client in some edge cases.
- World sounds triggered locally by a client are not always relayed to other clients.
- The mod keeps party-save bookkeeping in `UserData/SyncRADation/` (`host_saves.txt`, `bag_snapshots.txt`). Delete those files if a restore after a wipe behaves strangely.

## Configuration

Edited in `SIGNALIS/UserData/MelonPreferences.cfg` under `[SyncRADation]` (created on first launch). Restart the game after changing anything except where noted.

| Key | Default | Meaning |
| --- | --- | --- |
| `ConnectAddress` | `127.0.0.1` | Address used by F3 quick connect |
| `ConnectPort` | `7777` | UDP port |
| `MaxPlayers` | `4` | Host only. Session size including the host, 2 to 8. Applies the next time you Host Game |
| `DownedRespawnDelay` | `20` | Seconds a downed player waits before reviving next to a teammate (1 to 600) |
| `FriendlyFire` | `false` | Allow players to damage each other |
| `SyncPuzzles` | `true` | Sync puzzles, locks, elevators, radio, storage, interactions |
| `SyncWorldPickups` | `true` | Sync items lying in the world |
| `SyncPlayerVitals` | `true` | Share HP, death and game state for remote player display |
| `VerboseLogging` | `false` | Extra log noise for diagnosing sound, player-model and puzzle issues. Turn on for **both** installs only when hunting a bug |
| `ExperimentalPuzzles` | `true` | Deprecated alias of `SyncPuzzles`, kept for old config files |

## Troubleshooting

**Logs:** `SIGNALIS/MelonLoader/Latest.log`. When connected, lines are prefixed `H ` (host) or `C ` (client). When reporting a bug, send both players' logs.

- The mod loaded if you see `[Harmony] patched` lines and a boot banner with the version.
- `Handshake OK` means the connection was accepted. A rejection shows a reason in the client's F2 menu (version, protocol or schema mismatch: everyone needs the same build).
- `[Guard]` lines are errors the mod caught and swallowed so the game keeps running. A few are harmless; the same tag repeating every 30 seconds with a growing count is worth reporting.
- `[Harmony]` lines about a failed patch usually mean a wrong MelonLoader or game version.
- `[Hitch]` lines appear only on a real stutter (frame time spikes, send or receive gaps, slow sync ticks). Include them if the game stutters in multiplayer.

**Common problems**

| Problem | Fix |
| --- | --- |
| No F2 menu | MelonLoader is not loading. Check it is 0.5.7 and the DLLs are in `Mods/`. On Proton set `WINEDLLOVERRIDES="version=n,b"` |
| Cannot connect | Host pressed Host Game? UDP 7777 open on the host? Correct VPN address? |
| Connect is rejected | Different mod builds. Install the same zip on every machine |
| Doors or pickups look wrong after joining | Press **Resync world** in the F2 menu |
| "SCENE MISMATCH" in the menu | Your game is following the host chapter. Wait, or load the same chapter manually |
| Stuck after a disconnect | The client goes offline and restores play on its own. If input is stuck, open F2 and Disconnect |

## Building from source

See `AGENTS.md` (Release / scripts section). Short form: `scripts/build.sh` builds without deploying, `scripts/package.sh` makes the release zip.

## Credits and license

By **Warexpor**. Networking by [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (MIT, see `lib/LiteNetLib.LICENSE.txt`). Built on MelonLoader and Harmony. SIGNALIS is by rose-engine; this is an unofficial fan mod and is not affiliated with them.

Copyright (C) 2026 Warexpor. Licensed under the GNU General Public License v3 only, without any warranty. The full text is in [LICENSE](LICENSE).
