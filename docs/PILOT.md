# Test pilot (unattended multi-instance runs)

The pilot plays the game with 2 to 4 instances at once, with no one at the keyboard, and checks that every peer holds the same world. It has two halves:

- **In the mod** (`Sync/TestPilot*.cs`): off unless the game starts with `--sync-pilot <mode>`. `host:<Scene>` hosts and loads that chapter the F7 way, `join` connects to `127.0.0.1` and follows the host. In play it runs the lines appended to `<pilot dir>/cmd.txt` and writes what they did to `<pilot dir>/out.txt` and the log (`[Pilot]`). Every command goes through the game function a player's action reaches (the Interactor press, `ItemPickup.pickUp`, `EnemyController.TakeDamage`, `PlayerState.HurtElster`, F6 / F7 / F11), so the mod's patches see what they see in play.
- **Scripts** (`scripts/pilot/`): start the instances, send commands, take synchronized world digests and diff them, run scenarios, collect the logs.

## Where it runs

Every instance runs inside one headless omabox box (`~/Tools/omabox`) (`PILOT_BOX`, default `syncpilot`): its own Hyprland screen and loopback network (`--net isolated`), never your desktop, never your real `127.0.0.1:7777`. The instances are reflink clones of the client install (`~/Work/MyProjects/SIGNALIS`, which runs without Steam) with one Proton prefix each and a writable copy of Proton Experimental, under `~/.local/share/syncradation-pilot` (`setup.sh`, idempotent, `--fresh` rebuilds). The box mounts that tree as a discarded overlay: every run starts from the same files, nothing a run writes (saves, logs, prefs) survives it, and the clones are never written to. The game windows render black in box screenshots (DXVK presents outside the compositor's capture); the in-game `shot` command captures real frames.

**Memory.** An instance holds ~4.6 GB from MelonLoader init on, ~4.8 GB in RES and over 6.7 GB while RES_School loads, and the box's overlay and HOME are tmpfs, so everything the games write is RAM as well. Unfenced, three instances took a 31 GB desktop down (2026-10-09: the system OOM killer ended the Claude app's cgroup the games ran in, then swap thrash). So the box and every game run in their own scopes under `syncpilot.slice` (`PILOT_SLICE`), capped at `PILOT_MEM` with no swap: by default 7 GB per instance, or what is available less 2 GB when that is less. A spike past the cap kills a game inside the slice (a dead peer in the run, a `Memory cgroup out of memory` kernel line), never the desktop. `pilot-run.sh` refuses to start below 5 GB available per instance plus 2, and `mem-guard.sh` writes every instance's resident size to `artifacts/pilot/mem.txt` (peaks in the run report) and kills one past `PILOT_PROC_MAX_MB` (10 GB: a chapter load spikes an instance past 8 GB for a moment).

MelonLoader's console is off for pilot instances (`--melonloader.hideconsole`): a second console under Proton fails to allocate and stops the game.

## Scripts

| Script | What it does |
|--------|--------------|
| `setup.sh [--fresh]` | Make the instances `h`, `c1`, `c2`, `c3` and their prefixes; sets the pilot prefs (MaxPlayers 4, AllowClientCheats, VerboseLogging, Diagnostics) |
| `pilot-run.sh [Scene]` | Deploy `bin/stage/Debug/SyncRADation.dll` (`PILOT_DLL`) to every instance (md5 checked), start the box, host `Scene` (default `DET_Detention`), join `CLIENTS` clients (default 2 = three players) all at once (`JOIN=serial`: one after another), return when all are in the world |
| `pcmd.sh <h\|c1\|c2\|c3\|all> <command...>` | Send one command, print its reply |
| `check.sh <tag>` | Every peer writes its digest at the same moment (`at`), each client's is diffed against the host's (`digest-diff.py`); exit 1 on a difference |
| `coop-soak.sh` | One chapter, every arrow: a pickup each, all peers racing for one pickup, each peer killing an enemy (clients in rooms the host never entered), a client opening a door, a client downed and revived, a client leaving and rejoining late; a check after each step |
| `story-run.sh [chapters...]` | The chapters in story order; loads alternate between the host and a client (F7 follow request); in each: settle, skip the opening cutscene, check, a pickup each, each client kills an enemy, check |
| `soak-all.sh [chapters...]` | The full scope in one session: the chapters in story order (loads alternate as in `story-run.sh`), `coop-soak.sh` in each; a chapter that ends in a menu scene is loaded and checked, not soaked. One boot for every chapter |
| `pilot-quit.sh [tag]` | Collect every peer's `Latest.log`, transcript and digests into `artifacts/pilot/<tag>/`, print `report.sh`, quit and take the box down (`KEEP=1`: collect only) |
| `symbolize.py <log...>` | The `[Stall]` lines with native frames, one frame per line, GameAssembly frames named from the decompile's `@ RVA` lines |
| `report.sh <dir>` | Per peer: version, handshakes, scenes, registry checksums, warnings by tag, `[Guard]`, divergence, exceptions, Unity errors, failed pilot commands |

Typical run:

```bash
scripts/build.sh --debug
scripts/pilot/pilot-run.sh DET_Detention
scripts/pilot/coop-soak.sh
scripts/pilot/pilot-quit.sh soak
```

Full scope (every chapter, ~3 min each):

```bash
scripts/pilot/pilot-run.sh PEN_Wreck
scripts/pilot/soak-all.sh
scripts/pilot/pilot-quit.sh full
```

With Diagnostics on (the pilot prefs), every instance runs the stall watch: `[Stall] main thread stuck Nms phase after=… harmony=… lastPatched=… msg=…` once a second while a frame does not end (a scene load shows a few of these), from the second second on (and at once on a memory runaway, over 1 GB in one stall) it appends the main thread's native frames, `native GameAssembly.dll+0x… < UnityPlayer.dll+0x…`. `scripts/pilot/symbolize.py <log>` names the GameAssembly frames from the decompile's `@ RVA` lines.

Read a run in this order: the script's output (MATCH / DIFF per step), `report.txt`, then the `[Pilot]` lines and Unity errors in each `*-pilot.txt`, then `*-Latest.log` around a failing step (`note` puts `==== <step>` marker lines in every peer's log).

## Digest

`digest <tag>` writes `digest-<tag>.txt`: one sorted `section key value` line per object.

| Section | Key | Value |
|---------|-----|-------|
| `scene` | `name` | active scene |
| `story` | SProgress key | shared value (per-player keys never ride a commit and are not listed) |
| `ring` / `box` | `keys` / `items` | party key ring / storage box contents |
| `enemy` | WorldId | `alive hp=N` / `dead` |
| `door2` / `doorS` / `doorC` | WorldId | double door `open` + `locked` (only while its room is awake: it mirrors its link), sliding door `open`, room link `locked` |
| `pickup` | WorldId | `here` or `claimed` / `triggered` / `hidden` / `gone` (compared as here / taken) |
| `puzzle` | `Type:WorldId` | the entry the puzzle sync reads (`b=` bools, `i=` ints, `f=` floats), `asleep` when the component's room chunk never woke on this peer |
| `cutscene` | WorldId | `completed` |
| `self` | | role, room, position, hp, bag: shown, never compared |

`digest-diff.py` reports, per client: MATCH or DIFF, the line count per section with `!N` differing, and what it did not compare: values that differ only where one peer never woke that room (native `Start` / the held re-snap settles them on entry) and per-player state by design (`EnemyManagerState`, docs/SYNC.md).

## Commands

Player: `status`, `net`, `god [0]`, `hurt <n>`, `die`, `tp <x> <y> [z]`, `walk <x> <y> <sec>`, `rooms`, `room <label|#i>`.
Session: `scene <Scene>`, `save`, `load`, `resync`, `leave`, `connect [addr port]`.
World: `enemies [r]`, `hit <id|name|near> [dmg] [hurt%]`, `kill <id|name|near>`, `spawn <type>`, `path <id|name>`, `pickups [r]`, `take <id|name|item|near>`, `inters [r] [filter]`, `use <name>`, `doors [r]`, `door <id|name> [open|close]`, `atd [r]`, `cuts`, `cut <id|name>`, `skip`, `comps <player|name>`.
Inventory / state: `give <item> [n]`, `inv`, `story [filter]`, `digest <tag>`, `dlg [choice]`, `autodlg [0|1] [choice]`.
Run: `wait <sec>`, `at <unix ms> <command...>`, `say <text>`, `errors`, `shot [name]`, `fps [n]`, `quit`.

Actions on an object in another room stand the player beside it, enter that room, and run once the object has been awake for half a second (a room that just woke runs its components' `OnEnable` / `Start` over the next frames). `autodlg` (on by default, choice 0 = yes) continues any line or yes/no prompt that stays open for over a second. `enemies` marks each enemy's EnemyManager `mgr=on|off|none` (`off`: the manager's object is inactive, story-gated, so the enemy cannot wake), an ARAR of a `remoteActivationOnly` or 0-range nest `nest=remote` (only a boss script drops it), and `pickups` marks a prop that is switched off itself or under a switched-off holder (not the room chunk Room.EnterRoom wakes), or under an `NGP_only` holder while the NGP pref is off (its Start only switches it off on the first wake), or in a Room that is itself switched off, `self=off` (`doors` marks a double door the same way); the story run and the soak skip these as targets. `kill` keeps hitting: native `TakeDamage` at 0 HP only downs an enemy (critical, HP back to its revive value) and the next hit finishes it.
