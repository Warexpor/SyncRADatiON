# SyncRADation install guide

SyncRADation adds LAN/VPN co-op (2 to 8 players) to SIGNALIS. Everyone in the session must install the **same build** of this zip.

## Requirements

- SIGNALIS on Windows (Steam). Linux works through Proton, see below.
- **MelonLoader 0.5.7, exactly.** Other versions will not work.

## Steps

1. Install MelonLoader **0.5.7** into your SIGNALIS folder (next to `SIGNALIS.exe`). Run the game once, then quit. A `MelonLoader` folder and a `Mods` folder now exist.
2. Copy the contents of this zip's `Mods/` folder (`SyncRADation.dll` and `LiteNetLib.dll`) into `SIGNALIS/Mods/`.
3. Start the game and load a chapter. Press **F2**: the multiplayer menu opens.

## Linux / Proton

Set the Steam launch options for SIGNALIS to:

```
WINEDLLOVERRIDES="version=n,b" %command%
```

## Playing together

- **Host:** F2, Host Game (UDP port 7777).
- **Clients:** F2, enter the host address, Connect (F3 reconnects to the last address).
- Over the internet, everyone joins a VPN (Tailscale, ZeroTier or Radmin VPN) and clients use the host's VPN address.
- F2 also has **Resync world** if doors or pickups look wrong after joining.

## Settings

`SIGNALIS/UserData/MelonPreferences.cfg`, section `[SyncRADation]`: `MaxPlayers` (2 to 8, default 4), `DownedRespawnDelay` (seconds, default 20), `FriendlyFire`, `ConnectAddress`, `ConnectPort`, `VerboseLogging`.

## Problems

- No F2 menu: check MelonLoader is 0.5.7 and both DLLs are in `Mods/`.
- Connection rejected: everyone needs the identical build.
- Logs: `SIGNALIS/MelonLoader/Latest.log`. Send both players' logs when reporting a bug.

This is a pre-release playtest build. Back up your saves first. Licensed GPL-3.0, see `LICENSE`. LiteNetLib is MIT, see `LiteNetLib.LICENSE.txt`.
