# Decompile coverage (protocol 11)

Decompile root: `~/Archive/Windows-Desktop/Dev/SIGNALIS DECOMPILED`
(`~/Omarchy_Backup/Desktop/Dev/SIGNALIS DECOMPILED` is **gone** on this machine — Archive path is canonical.)

Prefer `05_CSharp_source/Assembly-CSharp_MelonLoader/` + `00_CODE_VIEW/dump.cs` for types; method bodies in `07_Ghidra_pseudoC/` (game types at the top, FMODUnity / A* / Timeline / UI / engine under `_lib/`, folded functions marked `// folded with`). FMOD GUID ↔ path: `06_FMOD_banks/Master.strings.tsv`.

**Rules:** no invented sync. New `PuzzleType` / Boss / StoryCommit fields = protocol bump. **No park tables** — close gaps or classify intentional local.

Last loop: 2026-10-06 (protocol 19). Shipped in product **0.5.66**.

## Implemented this loop (protocol 19, 0.5.66): expanded decompile

Six audits against the new pseudo-C (FMODUnity, A*, DialoguerCore, PlatformManagement, folded classes, inlined literals). See `CHANGELOG.md` 0.5.66 for the full list; citations live in the code comments.

| Fix | Citation |
|-----|----------|
| Chase swap covers the enemy coroutines (LOS / Attack Linecast / hit direction) | `EnemyController.c` `<TrackAndAttack>d__151` / `<Attack>d__172` / `<Hurt>` / `<Stagger>` / `<Critical>` / `<Fire>` MoveNext |
| Enemy snapshot carries Forward / Turn / HitFromX / HitFromY | `ThirdPersonCharacter.c` UpdateAnimator, `EnemyController.c` ctor hashes, `Pathfinding.AIBase.c` velocity |
| Combat music statics stay per listener | `EnemyManager.c` `<CheckIfLeft>` (currentRoom), `CombatMusic.c` |
| One-shot GUID paths + 2D one-shots; radio stations local; loop restart; ObjectEnable Stop; TriggerOnce; snapshots | `FMODUnity.RuntimeManager.c` PlayOneShot, `FMODUnity.StudioEventEmitter.c` Play / PlayInstance / HandleGameEvent, `RadioStation.c` SuspendCheck, `Master.strings.tsv` |
| Client quit-to-title leaves the session | `PauseMenu.c` quit → quitToScene |
| Wipe reload places clients at the save point | `LoadingManager.c` Start, `Room.c` Awake, `CutsceneManager.c` Start |
| Party save token fingerprint | `SaveManager.c` Save / Load (sid, Saves), `SProgress.c`, `ProfileManager.c` |
| Dialoguer globals not sent | `ItemPickup.c` dialoguerCallback / release, `MedicationCheck.c`, `DialoguerCore.*` |
| Cutscene `cut <id>` persisted + onGameLoad; OnDisable clears `cutscene` | `CutsceneManager.c` OnEnable / Load / Save / OnDisable |
| UseItemMulti ready() not an item use | `UseItemMultiInteraction.c` ready / callback |
| DoorLockEvent latched before PuzzleStatus.solved | `DoorLockEventInteraction.c` Update / `<solved>d__10` |
| DynamicSupply / DynamicResupplyPickup host-decided | `DynamicSupply.c` Entered, `DynamicResupplyPickup.c` OnEnable |

## Implemented (protocol 11)

| Fix | Reverse-check | Citation |
|-----|---------------|----------|
| `MED_Adler_EVdoors` PuzzleType 77 — DoorL/DoorR local X (Float0/Float1) + Bool0 open; Open/Close emit projected end pose; Apply StopAllCoroutines + snap X | Both arrows + late-join dump | Melon `DoorL`/`DoorR`/`Distance`; dump.cs TypeDef 9811; RES_School closed (0,0,0) → open ±20 |
| `AdoptNativeSpawn` banks live/`EnemySpawner.EnemyType` via Stash; `TypeKeyOf` uses vanilla `Preset.Type` (not F11 TypeKeys-only); SR_Spawn_* + EnemySpawn when template exists | Host Instantiates → client FinishSpawn same WorldId | `EnemySpawner._Child`; `EntitySpawner.AdoptNativeSpawn` |
| Death bag: all Key/Object Note onto party ring, never floor (closes bag-not-on-ring race) | Host wipe + client downed | `PartyKeyRing.Note` / `OfferToHost`; `NetworkDamageSystem.DropInventoryOnDeath` |
| `BiodomeDoorLock` IsProgressed Bool0\|\|Int0; Update emit Read/Progressed (not ReadOnce) | Partial KeyLevel remount + late-join | Melon `KeyLevel` / `hasLock` |
| `MultiConditionEvent` IsProgressed Bool0\|\|Int0 (`tried`) | Partial tries remount + late-join | Melon `tried` / `triedOnce` |
| `PEN_Reaktor` added to ClientEmitTypes | Client rod mid → host Tick | Dig AG positions pack |

## Still implemented (protocol 10 — prior loop)

GunCase / AraNest / RifleQuest / Microfiche; PatternLock grid; Tarot cards; mural Blocker+useRing FullRefresh; RES_Power OnSuccess both-path; DoorLockEvent Door.SetActive; LibraryPC robotPos; SafeDoor onSolved dimPOI; SceneFollow RestorePlay; HideClaimed→EnsurePartyOnPickup. See CHANGELOG 0.5.55 / 0.5.54.

## Still implemented (protocol 9 — prior loop)

PuzzleType 64–72 residency/key/safe/drawer; CentralElevator `targetFloor`; mural moon pack; RadioCodeLock keypad unlock; EXC elevator ride; END_Boss Hp/Corrupt; Kolibri/Adler Float1; END playstyle StoryCommit; FMOD Guid/Attached/`fmod`; SceneManager.LoadScene gate. See `CHANGELOG.md` (0.5.0-dev / 0.5.1).

## Intentional local (design — not deferred)

Books / EventScreen / EventOnlyRoom / lock-flavor Dialoguer (0,6,17,20–27,+13 Airlock) / PEN_Titles airlock / wreck↔hole SceneFollow / Music·Cutscenes·Ambience beds / Elster+radio UI emitters / ConnectedDoors traverse / ladders / radio tuner frequency / flavor `DialoguePlayedOnce` / BookScreen UI / SanctuaryFinale·ShootingScene presentation zones / `Interaction.triggered` poll (teleports) / UncoverDelayed cosmetic fog covers / PatternLock FullRefresh `onSolved` exitEvent (EventScreen local; pad+doors still snap) / `LAB_Waage.content` (independent bags) / FakeWall·continuum·nowhere·Gestalt (no durable bool puzzle state).

## How to re-run

1. Use Archive decompile path above (not Omarchy_Backup).
2. Diff MelonLoader `solved`/`opened`/`locked`/`triggered` types vs `PuzzleType` + Door/Story/FMOD/Scene/Enemy.
3. IMPLEMENT decompile-backed snaps; bump protocol when wire needs new types/fields.
4. Reverse-check host↔client + late-join.
5. Do not add park / verify-later tables.
