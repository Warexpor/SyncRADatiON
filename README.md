# SyncRADation

LAN multiplayer MelonLoader mod for **SIGNALIS**.  
**v0.4.2-dev** — protocol **v7**. Host owns world and story; the client is a real Elster whose interactions go to the host and apply via native game methods (including UnityEvents and world FMOD).

Formerly labeled `1.2.x-dev`. That was optimistic. This is still early co-op.

## Requirements

- SIGNALIS (Steam or a second install)
- MelonLoader with Managed assemblies (`MelonLoader\Managed`, Unhollower-style)
- Same chapter/scene on every peer
- Same mod DLL on every peer (protocol 7)

## Install

1. Install MelonLoader in the SIGNALIS folder.
2. Build or copy `SyncRADation.dll` + `LiteNetLib.dll` → `SIGNALIS/Mods/`.
3. Launch once so assemblies generate if needed.

### Build

```powershell
cd "C:\MyProjects\SyncRADation (SIGNALIS MP REMAKE)"
dotnet build SyncRADation.csproj -c Debug
```

Override install paths:

```powershell
dotnet build -p:SignalisDir="C:\MyProjects\SIGNALIS" -p:ClientSignalisDir="C:\Program Files (x86)\Steam\steamapps\common\SIGNALIS"
```

Debug builds copy into both `$(SignalisDir)\Mods` and `$(ClientSignalisDir)\Mods` when those dirs exist.

## Play (up to 4 players)

1. Everyone loads the **same chapter scene**.
2. Host: **F2 → Host Game** (UDP `7777`, key `SyncRADation`).
3. Each client: **F2 → IP → Connect**, or **F3** with the saved address.
4. Host dumps world state **to that joiner**. If doors/pickups look wrong → **Resync world**.
5. **SCENE MISMATCH** means the client is loading the host chapter automatically (SceneFollow). If it sticks, load the same chapter manually.

## Controls

| Key | Action |
|-----|--------|
| F2 | Multiplayer menu (Host / Connect / Resync / status) |
| F3 | Quick connect (saved IP/port) |
| G | Drop selected inventory item |
| E | Pick up a nearby **player-dropped** item |
| F6 | Item giver |
| F7 | Location teleporter (chapters + rooms in the current level) |
| F11 | Entity spawner |

## What is synced

| Area | Authority | Notes |
|------|-----------|--------|
| Avatar proxy, anim, bones, weapons | Peer | State ~30 Hz, bones ~15 Hz |
| Enemies | Host | WorldId snaps; native `TakeDamage`; client hits Harmony → host |
| Doors (double / sliding) | Any peer emit, host relay | Visual open/close via native methods |
| ConnectedDoors (room links) | Lock only | **Never** sync traverse / `StartA`/`StartB` — room entry is local |
| Story (Dialoguer, cutscenes, EventScreen, SProgress) | Host | Host commits flags; peers play the same native presentation |
| Puzzles / locks / elevators / radio module / storage / event zones | Host | WorldId-keyed; storage **contents** shared |
| World ItemPickups | Host claim/grant | Claimer gets the item; unique keys go on the **party key ring** so either Elster can use them |
| Player-dropped items | Peer + relay | G drop / E pickup |
| Death | Asymmetric | Client downed (drops bag, world continues); host death reloads last save for both |
| Bosses (END / Chimera / Mynah / Kolibri / Adler) | Host | Light-field sync |
| Friendly fire | Opt-in | Default off |
| Inventories | Independent | By design |

World objects are identified by `hash(scene + hierarchy path)` — never `GetInstanceID()`.

## Still unverified

Code for protocol **7** is in this build. Dual-instance playtest has **not** been run. Do not treat any of this as proven until you play it.

GitHub zip: `dist/SyncRADation-0.4.2-dev.zip` (`SyncRADation.dll` + `LiteNetLib.dll` + this README). Nexus is out of scope until that run is enjoyable.

## Config (`MelonPreferences`)

| Key | Default | Meaning |
|-----|---------|---------|
| ConnectAddress | 127.0.0.1 | F3 IP |
| ConnectPort | 7777 | UDP |
| FriendlyFire | false | PvP damage |
| SyncPuzzles | true | Puzzles / radio / elevators / etc. |
| SyncWorldPickups | true | Scene ItemPickup |
| SyncPlayerVitals | true | HP / death packets |
| VerboseLogging | false | Extra log spam |

## Playtest gate (before Nexus)

Dual-instance LAN, same protocol-7 build. **This is the remaining work.**

Chapter 1 (Reeducation → Mines elevator):

- both deal damage; neither is yanked through doors
- one Dialoguer **with VO**
- one Cutscene **with audio**
- one EventZone/EventScreen **with audio**
- key from the client bag via the party ring; storage box host-put / client-take
- host death rewinds both; client death does not rewind the world
- elevator both reach Mines

Then the same notepad for Ch2 / Ch3 / endings. Failures are apply bugs in this stack, not a new sync engine.

GitHub zip is the publish path until that run is enjoyable. Nexus waits on that approval.

## Logs

`SIGNALIS/MelonLoader/Latest.log` — look for `Handshake OK`, `[Harmony] patched`, `WorldRegistry`, `full world snapshot`.
