# Decompile coverage (protocol 10)

Decompile root: `~/Archive/Windows-Desktop/Dev/SIGNALIS DECOMPILED`
(`~/Omarchy_Backup/Desktop/Dev/SIGNALIS DECOMPILED` is **gone** on this machine — Archive path is canonical.)

Prefer `05_CSharp_source/Assembly-CSharp_MelonLoader/` + `00_CODE_VIEW/dump.cs`.

**Rules:** no invented sync. New `PuzzleType` / Boss / StoryCommit fields = protocol bump. **No park tables** — close gaps or classify intentional local.

Last loop: 2026-09-20 (protocol 10). Shipped in product **0.5.1**.

## Implemented this loop (protocol 10)

| Fix | Reverse-check | Citation |
|-----|---------------|----------|
| `MultiKeyLock` Apply → `checkLock` + `TryUnlockDoors` | Client keys → host unlock; host → client | `MultiKeyLock.checkLock` |
| `ROT_DialLock` Apply unlocks `Door` / disables `lockObject` | Both arrows | `ROT_DialLock.Door` / `lockObject` |
| `NumberLockNew` / `DoorLockPuzzle` Apply unseals `door` when unlocked | Both arrows | `door.locked` + UnlockDoorObject |
| `MED_MultiLock` / `LAB_MultiLock` Apply → `TryUnlockDoors` | Both arrows | `unlocked` |
| `MED_VentPuzzle` Magpie cover snap + disable itemInter | Both arrows | `uncovered` / covers / `RemoveCover` |
| `EvidenceLockerLogicPuzzle` snaps sibling `EvidenceLockerDoor` | Host solve → client doors | `EvidenceLockerDoor.done` |
| Client emit: `SwingDoor` / `DoorwaySimple` / `StorageBox` | Client open → host relay | existing Apply |
| Client emit globals: `MED_KeyGrid` / `ArianePhotoCode` | Client solve → host → peers | statics WorldId 0 |
| `GunCase` opened snap (lid / pickup / inter) | Both arrows + late-join | MelonLoader `GunCase` |
| `AraNest` triggered/activated/dead + `TriggerTrap` | Both arrows | `AraNest` |
| `LAB_RifleQuest` awake/gone/rifle GO pose | Both arrows | `LAB_RifleQuest` |
| `LOV_Microfiche` hasFiche/IsaVisited/IsaGone | Both arrows | `LOV_Microfiche` |
| `PuzzleStateEntry` change-detect includes `Float1` | Kolibri/Adler incremental | entry wire |

## Still implemented (protocol 9 — prior loop)

PuzzleType 64–72 residency/key/safe/drawer; CentralElevator `targetFloor`; mural moon pack; RadioCodeLock keypad unlock; EXC elevator ride; END_Boss Hp/Corrupt; Kolibri/Adler Float1; END playstyle StoryCommit; FMOD Guid/Attached/`fmod`; SceneManager.LoadScene gate. See `CHANGELOG.md` (0.5.0-dev / 0.5.1).

## Intentional local (design — not deferred)

Books / EventScreen / EventOnlyRoom / lock-flavor Dialoguer (0,6,17,20–27,+13 Airlock) / PEN_Titles airlock / wreck↔hole SceneFollow / Music·Cutscenes·Ambience beds / Elster+radio UI emitters / ConnectedDoors traverse / ladders / radio tuner frequency / flavor `DialoguePlayedOnce` / BookScreen UI / SanctuaryFinale·ShootingScene presentation zones / `Interaction.triggered` poll (teleports) / UncoverDelayed cosmetic fog covers.

## How to re-run

1. Use Archive decompile path above (not Omarchy_Backup).
2. Diff MelonLoader `solved`/`opened`/`locked`/`triggered` types vs `PuzzleType` + Door/Story/FMOD/Scene/Enemy.
3. IMPLEMENT decompile-backed snaps; bump protocol when wire needs new types/fields.
4. Reverse-check host↔client + late-join.
5. Do not add park / verify-later tables.
