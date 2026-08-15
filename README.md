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
| Avatar proxy, anim, bones, weapons | Peer | State + bones ~30 Hz |
| Enemies | Host | WorldId snaps; native `TakeDamage`; client hits Harmony → host |
| Doors (double / sliding) | Any peer emit, host relay | Visual open/close via native methods |
| ConnectedDoors (room links) | Lock only | **Never** sync traverse / `StartA`/`StartB` — room entry is local |
| Story (Dialoguer, cutscenes, SProgress) | Host | Flags commit; books/notes/EventScreen inspect stay local; story Dialoguer Start/Continue/End from the client plays on the host |
| Puzzles / locks / elevators / radio module / storage / event zones | Host | WorldId-keyed; storage **contents** shared |
| World ItemPickups | Host claim/grant | Claimer gets the item; unique **Key/Object** go on the **party key ring** |
| Player-dropped items | Peer + relay | G drops the selected stack; E pickup if bag has room |
| Death | Asymmetric | Native `HurtElster`; client downed (drops bag); host death reloads last save for both |
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

Dual-instance LAN, same protocol-7 build. Steam host + copy client, same chapter, Verbose on. **This is the remaining work.** Do not treat any of this as proven until you play it.

After this correctness pass:

1. Penrose: photo inspect local; cryo pattern; BrokenKey does not hide photo; Tape+BrokenKey on **client**; both walk airlock independently; host staying in wreck does not yank
2. Key door: one unique key, both traverse; no-key locked links stay red NO ENTRY
3. Ammo/health pickup: partner `hasItem` stays false
4. Enemy: host HP not double; client hit registers on host; host death reloads both
5. Client downed → disconnect → can move and take damage again
6. Notes/books stay local; a real story Dialoguer line started by the client plays on host
7. Chapter load via int and string; LoadingScreen does not dump
8. Elevator flags (no remote ride cinematic); radio lock frequency; FMOD loop present on late join

Then Chapter 1 (Reeducation → Mines elevator) notepad: both deal damage; neither yanked through doors; one Dialoguer with VO; one cutscene with audio; storage box host-put / client-take.

GitHub zip is the publish path until that run is enjoyable. Nexus waits on that approval.

## Logs

`SIGNALIS/MelonLoader/Latest.log` — look for `Handshake OK`, `[Harmony] patched`, `WorldRegistry`, `full world snapshot`.

## License

Copyright (C) 2026 Warexpor.

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, version 3.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

The full license text is in [LICENSE](LICENSE).
