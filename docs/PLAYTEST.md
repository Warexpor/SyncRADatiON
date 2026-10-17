# SyncRADation playtest / soak checklist

Target build: **0.5.75, protocol 21**. Everything is compile- and unit-tested, an unattended host+client join into `DET_Detention` / `MED_Medical` is smoke-tested (handshake, dump, matching WorldId checksum), and dual-box sessions have been played through `LOV_Reeducation` into `DET_Detention`; the rest of the game has not, so this list is the gate. Run it on **two instances** first (host + client), then repeat the marked items with **three** (host + two clients, `MaxPlayers` >= 3).

Conventions:

- **H** = host, **C** = client, **C2** = second client. **H->C** = the host initiates and the client must see it. **C->H** = the client initiates and the host must see it (plus C2 when three play).
- Every item is pass/fail and observable on screen or in the logs. Tick it only when both arrows were checked (where an item lists them) and, where it says **LJ**, a late joiner (join after the action happened) also sees the final state.
- Logs: host `.../SIGNALIS/MelonLoader/Latest.log`, client its own `Latest.log`; connected lines are prefixed `H ` / `C `. Always-on tags: `[Story]` `[Interact]` `[KeyRing]` `[StorageBox]` `[Scene]` `[Damage]` `[Door]` `[Puzzle]` `[Pickup]` `[Harmony]` `[Hitch]` `[Spawn]` `[Enemy]` `[Proxy]` `[Weapon]` `[Guard]`.
- Same DLL on every peer. Keep `VerboseLogging=false` unless a failure needs detail.
- A failure counts even if the game keeps running: grep `[Guard]` and `[Harmony]` at the end of each section and note anything new.

Session info to record: date, build (0.5.75), players, chapter, result notes.

## 0. Preflight

- [ ] Both logs show the boot banner with version 0.5.75, `[Harmony] audit: N ok, 0 missing` and `[Harmony] audit (late): shaders ok`; F2 shows the same game build line on both.
- [ ] After the client joins and goes to the host's chapter: both logs print `[WorldRegistry] scene='<chapter>' … checksum=X` with the **same X**, and there is no `[Scene] WorldId divergence` line. If there is one, copy the `missing:` / `extra:` lines into the notes.
- [ ] `[Hitch] stall` lines: if `renderGap` ≈ `dt` and `modUpdate`/`gameScripts` are a few ms, the frame was lost in present/compositor (seen with two unfocused Proton windows), not in the mod — note it, don't file it as a mod bug.
- [ ] Host Game then Connect: `Handshake OK` on both sides, F2 roster lists both players.
- [ ] Both players see each other's proxy, animation and held weapon.
- [ ] Offline (no session) `F2` menu works and the game plays like vanilla (see section 18 for the full pass).

## 1. Handshake, mismatch and roster

- [ ] **Protocol/schema mismatch UX:** build a client DLL with a different protocol or a patch-version bump (or edit `ProtocolVersion`) and try to join: the client F2 menu shows a rejection reason string, the host session is unaffected (existing peers keep playing), no exception storm in the host log.
- [ ] A patch-version-only mismatch (same protocol) is rejected too (schema hash includes the assembly version).
- [ ] A client that connects but never completes the handshake is dropped by the host after about 10 s without disturbing other peers.
- [ ] Connect to a dead address: the client gives up after about 12 s and the menu is usable again.
- [ ] **3-player roster:** H hosts, C joins, C2 joins. F2 on all three lists three names; all three see each other's proxies.
- [ ] C2 leaves mid-session (Disconnect): H and C drop C2's proxy within a few seconds; roster shows two; no stuck state for H or C.
- [ ] C2 rejoins mid-session: gets a fresh id, correct world state, everybody sees them again (id recycling works).
- [ ] The host joins a 4th player only when `MaxPlayers` >= 4; with `MaxPlayers=2` a third connect is refused with a reason.
- [ ] Client disconnect -> that client can move and take damage again (goes offline, input restored).
- [ ] Host disconnect/quit: clients go offline cleanly (input and UI restored, enemy/boss puppets cleared, no stuck dialogue or cutscene).

## 2. Join and late-join world state

- [ ] Join in an idle room: world matches (doors, pickups, enemies) without pressing Resync.
- [ ] Join after H solved at least one door, picked up a key and killed an enemy: C sees the door open, the pickup gone, the enemy dead.
- [ ] Resync world (F2) on C corrects a deliberately wrong state (for example a door re-locked locally) and does not duplicate items.
- [ ] Rapid Resync presses (spam) do not flood the host: log shows coalesced requests, no hitch above the budget.
- [ ] Join while the host is mid-load (loading screen): C waits, then gets the dump (or seeds from defaults after about 6 s and Resync fixes it). Note which happened.
- [ ] Party save token on join: host has saved at least once; on join the client log shows the party save token received (`[Scene]`/party-save line) and `UserData/SyncRADation/host_saves.txt` exists on H.

## 3. Death and personal reload

Run each row for both H and C; with three players also run it with the second client.

- [ ] **H killed, C alive (same scene):** H plays the death (input off, dead pose, enemies lose sight of H), no game-over screen, and about 3.5 s later reloads the scene alone (`[Damage] death: reload '<scene>' at save room '<room>'`, then `placed at save room`, `back after death hp=100`). C is not moved and keeps playing; C sees H's proxy lie down, vanish for the load and come back.
- [ ] **C killed, H alive:** same from the other arrow. C comes back as a follower: it gets H's world dump, enemies H killed stay dead, doors and pickups match (`[Desync]` clean).
- [ ] **The world is not rolled back:** take an item and open a lock after the last save, then die: the item is still in your bag, the lock still open, the item still gone from the floor for everyone. Story flags set since the save stay set (`[Story]` on H, no authoritative clear).
- [ ] **No save in this scene** (die right after arriving through a chapter door): the reload lands at the scene's entrance (`at its entrance (no save here)`).
- [ ] Dead player is ignored by enemies and bosses (no damage, no re-targeting) and by friendly fire if enabled, until it is back.
- [ ] **Both die** (two players, or all three): each reloads on its own; nothing waits for the other, nobody's world is reverted.
- [ ] Death while alone in an area the host is not in: the client reloads that area, its owner is itself again on arrival.
- [ ] Death of a client owner with a follower there (3 players): the follower takes the area over during the load (`[Owners] ... -> p2`), the reloaded player comes back as its follower or owner and the world matches.
- [ ] H dies with C in its scene: the scene stays H's through the load (no `[Owners]` handoff), C's enemies stay puppets, and H's arrival dump matches what C saw.
- [ ] Equipped injector: the native auto-revive still fires instead of the death.
- [ ] Solo host (no client connected) dies: vanilla game over, not the party flow.
- [ ] Client disconnects while dead: it gets up offline with input restored; the host drops it from the party.
- [ ] Host disconnects while a client is dead: the client gets up offline with input restored.

## 4. Party save restore

- [ ] Manual save at a save room on H then quit and restart the session: `bag_snapshots.txt` and `host_saves.txt` updated; reconnect, token matches; the client's join restores its bag snapshot for that save.
- [ ] Clients without the matching snapshot (fresh install, no `bag_snapshots.txt`): the join does not crash; the client keeps what it has and logs why.
- [ ] A save on H or C sets that player's restart point (`[Damage] save: restart point '<scene>' room '<room>'`).

## 5. Scene and chapter flow

- [ ] **H loads another chapter** (F7 or story door): H goes alone, C stays where it is (`[Scene] C host in '<chapter>', this player stays in '<scene>'`), C keeps playing (enemies, doors, pickups, cutscenes of its own scene work).
- [ ] **C loads another chapter** (story door, cutscene, or F7 with AllowClientCheats): C goes alone (`[Scene] C own load`), H is not moved. Then C2 the same way.
- [ ] **H follows C into C's chapter**: on H's arrival C logs `host arrived in '<scene>': its world takes over`; C's enemies become puppets of H's, doors and pickups match (`[Desync]` clean after a few seconds). C's progress there (taken items, opened locks, watched cutscenes) is already on H.
- [ ] **C follows H into H's chapter**: C gets the host dump on arrival, nothing doubled.
- [ ] **3 players, C1 and C2 in a chapter H is not in**: C1 (lower id) logs `[Owners] C '<scene>' owner ... -> p1 (this player runs it)`, C2 `-> p1`. C2's enemies are puppets of C1's; C2 hits an enemy, opens a door, solves a puzzle, steps into an event zone, triggers a cutscene: C1 answers each (`[Interact] ok` on C1), both see the result. C1's flags still reach H (`[Story]` on H).
- [ ] **Owner leaves**: C1 loads elsewhere while C2 stays: C2 logs `owner p1 -> p2 (this player runs it)` and its enemies wake on their own AI (no frozen puppets). Then H walks in: owner `-> p0`, C2's enemies become H's puppets.
- [ ] A level intro on arrival (LOV_Reeducation from PEN_Hole) plays for each player on their own; nobody stays frozen in `gs=loading` (if the intro was already seen, `[Scene] gameState stuck at loading ... play restored` within ~4 s).
- [ ] **Join from another chapter**: C loads its own save elsewhere, then connects: C goes to H's chapter once (`[Scene] C join: ... going to the host`), its own save's story is replaced (`[Story] authoritative full commit ... (join)`).
- [ ] Penrose airlock / wreck<->hole: each player loads `PEN_Hole` on their own; a join on the other side of the split does not move C.
- [ ] After a load, inventory/menu input is not stuck (RestorePlay).
- [ ] Reload of a scene (leave and come back): held door unlocks stay open; flavor seals stay sealed.

## 6. Doors and locks

- [ ] **H opens** a double/sliding door -> C sees it open. **C opens** one -> H (and C2) see it. Both directions.
- [ ] A unique key door: the player without the key gets the native missing-item dialogue; the holder opens it and both walk through (neither is pulled through).
- [ ] Penrose airlock: H holds the card, H and C each run their own airlock (C without the card in its bag).
- [ ] Falke: C holds the spear; the boss still gets downed for the stab and C can stab.
- [ ] Key handover: the holder G-drops a key, the other player picks it up and can now use it; the dropper can't.
- [ ] A no-key locked link stays red/NO ENTRY for everybody; a client cannot force it open (client door opens are rejected unless a real key/code lock is solved on the host).
- [ ] ConnectedDoors (room links): traversal is local; H going through does not move C.
- [ ] **LJ:** a door solved before C joined is open on C.
- [ ] Cryo door and pattern-lock door: open on a late joiner.
- [ ] Ladders: climbing is local, the other player only hears the proxy sound.

## 7. Puzzles: one scenario per family, both arrows and late-join

For each family: (a) H starts/solves part or all, C verifies; (b) C does the same, H verifies; (c) **LJ** with a join after the puzzle was left mid-state or solved. Mid-state means the half-done board is visible to the late joiner, solved means its door/result is final.

- [ ] **Cryo** pods/locks: pattern and door state match; late join sees the door.
- [ ] **Codepad / PatternLock:** button grid (lights) matches mid-solve, solved state final; an EventScreen does not replay on the observer.
- [ ] **Interactive locks / keypads / dials (Keypad3D, InteractiveLock):** digits/state mid-entry match, solving unlocks for everyone.
- [ ] **Pump / flood / pipes / hatch:** valve turns, water level and hatch state match; native Open/Drain/TurnValve plays on the observer only for live edges.
- [ ] **Elevators:** target floor and mover position match, no remote ride cinematic replayed.
- [ ] **Radio alignment / code lock:** the lock state and unlocked doors sync; tuner frequency stays local while turning.
- [ ] **Chapter machines** (card writer, shutters, magpie and similar): state matches.
- [ ] **Residency puzzles** (music box, key grid, photo code, safe, openable drawer; types 64-72): mid and solved state match; safe's dimmed POI follows.
- [ ] **Chapter extras** (GunCase, AraNest, RifleQuest, Microfiche; 73-76): state matches.
- [ ] **Adler EV doors** (type 77): DoorL/DoorR pose matches.
- [ ] **World-object puzzles (78-81):** `ROT_DiskManager` disks, `DET_WallCreature` HP (min-merge), `MapReveal`, `MEM_ChecklistLogic` checked items (concurrent edits from H and C both survive, OR-merged).
- [ ] **Concurrent edit merge:** H and C change different cells of the same puzzle within about a second: both edits survive (cell merge via Seq/Mask), neither is overwritten.
- [ ] **Tarot cards, mural (Blocker Entry snap, no cutscene replay), power (paternoster), biodome KeyLevel, MultiCondition tried count:** remount/late-join keeps the mid value.
- [ ] Solved puzzle seen by a late joiner does not re-run its onSolved (no duplicate cutscene, sound or spawn).
- [ ] **Storage box:** H puts an item in -> C takes it; C puts -> H takes; contents shared; lid state syncs.
- [ ] **Storage double-press:** press interact twice quickly on a box / pickup-from-box prompt (both H and C): item is moved once, no duplicate, no ghost item, no stuck prompt.
- [ ] UseItem unlocks (hatch card, airlock card): a key on the ring opens the lock for both.

## 8. Pickups and dropped items

- [ ] **World pickup, H takes:** item disappears for C; only H has it (partner `hasItem` stays false for ammo/health).
- [ ] **World pickup, C takes:** same from the other arrow; unique Key/Object lands in C's bag only (H can't use it); with a full bag C's take is refused.
- [ ] **Simultaneous grab:** H and C take the same world item in the same second: exactly one gets it, the loser's claim is rolled back (`WorldPickupDeny`), no duplicate, no ghost.
- [ ] **Pickup with an existing stack** (ammo or a stackable item you already hold): stack increases once, slot count correct, no second slot.
- [ ] **Inspect-path pickup decline:** walk up to an inspect-style pickup, answer No/decline: item stays in the world for everyone, claim released, nothing in the bag; accepting afterwards still works. Try from H and from C.
- [ ] Bag full: pickup is rejected cleanly on both arrows.
- [ ] **Player drop (G / inventory DROP):** H drops -> C sees the prop and can TAKE it (yes/no inspect, count); C drops -> H can take. One copy exists.
- [ ] **LJ:** a dropped item on the floor before join appears for the late joiner; a claimed pickup is gone.
- [ ] Peer leaves during an in-flight claim/drop/storage take: item stays on the floor/box for others, no orphan.
- [ ] Unique key items are never floored on death; KeyOfSacrifice softlock guard (TarotDeath) behaves in NG+ if reachable.

## 9. Enemies

- [ ] H kills an enemy: dead on C. C kills one: dead on H. HP is not double-counted (two shots kill, not one).
- [ ] Enemy puppets on C move smoothly between 15 Hz snapshots (no teleport jitter, no restarting attack animations).
- [ ] **Stomp / burn from client:** C stomps a downed enemy (Kill/KillSilent), pushes (Knockback/GetPushed) and burns one (Burndown): result matches on H and C. Wake-up of a sleeping enemy triggered by C wakes it on H.
- [ ] Sleeping enemies in far chunks wake when a peer comes near (host wake).
- [ ] Contact damage to a client lands once per hit (no double damage from sim + hurtbox), radius feels right (note slack).
- [ ] Gunshot by C wakes enemies near the shot.
- [ ] **F11 spawn** by H (and by C, host-authored): appears once on all peers.
- [ ] Personal scenes (wreck/hole, airlock): enemies run natively for each player.
- [ ] **LJ:** a join after a fight shows the right living/dead enemies and positions.

## 10. Bosses

- [ ] **Falke from client: hit** (normal damage): host HP decreases, the boss reacts on both screens. Also verify H hits.
- [ ] **Falke from client: stab** (melee finisher/Stab): counted by the host, presentation plays on both; HP rolled back if the local drop was wrong.
- [ ] **Falke from client: spear** (TakeSpear / spear pickup): spear taken once, host sees it; `CheckDowned` works when the spear is on a client bag (note: known risk).
- [ ] **Chimera with Isa rifle:** client shoots: rifle shot shows on host and the other clients; host shooting shows on clients.
- [ ] Mynah, END boss, Kolibri, Adler: boss HP/stage identical on H and C; snapshots do not stutter (Sequenced channel).
- [ ] **LJ:** join mid-boss: boss state (stage/corrupt/arena/shields) correct on the joiner.
- [ ] Boss kills a downed player's target choice: downed peers are not targeted.

## 11. Story: dialogue, cutscenes, SProgress

- [ ] **Dialogue double-advance:** H and C both press interact/advance in the same second on a story Dialoguer: it advances once per line, no skipped line, no double VO. Test with C starting the dialogue and with H starting it.
- [ ] Story Dialoguer started by C plays for H with presentation replay; books, notes and lock-flavor lines stay local.
- [ ] **Cutscene started by C** (walk into the trigger as the client): plays once on H and C (and C2), audio once, no double start; skip from either peer skips for the party once.
- [ ] Cutscene in another room does not start/replay on the observer.
- [ ] SProgress flags set by C show up on H after the commit; flags set on H show on C; END_Manager counters merge (deltas, one tally).
- [ ] Scripted cheats `goto` / `sethp` from a cutscene gather the whole party.
- [ ] Story commands `GoToPenny`, `PartyCheat`, `EndDelta`, `EndGraves` work from C (request rides `InspectFlag`).
- [ ] **LJ:** late join keeps SProgress/Dialoguer state: no re-play of an already-finished cutscene.

## 12. Audio

- [ ] A world sound triggered by H (door creak, machine) is heard by C once, not doubled.
- [ ] Repeat one-shots replay; repeated Stop is dropped; a looping emitter is present on a late joiner.
- [ ] No music/cutscene/ambience bed gets duplicated (beds skipped).
- [ ] Far non-door sounds are distance-gated (quiet when far).

## 13. Friendly fire

- [ ] Default `FriendlyFire=false`: no damage between players; a client cannot damage the host by modified packets (rejected).
- [ ] With `FriendlyFire=true` on the host: players can damage each other, dead players still skipped.

## 14. DeadMenu / MEM_Memory

- [ ] **DeadMenu -> MEM_Memory:** when a game-over path leads to the DeadMenu (solo host, or a real game over), each player gets the memory sequence when their own run reaches it; the other player is not moved and the menu is usable.
- [ ] Party (2+ live) does not show the native game-over: a dead player reloads its scene instead.
- [ ] After MEM_Memory the session continues (peers still connected, roster intact).

## 15. End credits

- [ ] **EndCredits:** each player watches their own credits when they reach them. C's credits ending takes only C to the title (offline); H's credits ending ends the session for everyone.
- [ ] After the credits everyone returns to the menu/next scene without a stuck state; session survives or ends cleanly (note which).
- [ ] `EndDelta` / `EndGraves` endings trigger once.

## 16. Client quit-to-menu

- [ ] **Client quit-to-menu** (pause menu Quit → `PauseMenu.quitToScene("MainMenu")`): the client goes offline and loads the title; the host stays in game, its session keeps running for any other client.
- [ ] Host quit-to-menu: clients go offline cleanly.

## 17. Performance and hitches

- [ ] Play 15 minutes with two players: `grep "\[Hitch\]"` in both logs. `frame dt` >= 50 ms should be rare; `send gap` / `recv pN gap` >= 80 ms should not repeat in bursts.
- [ ] `puzzle` / `enemy` / `boss` / `pickup` / `weaponClone` tick costs: any line >= 8 ms is investigated (note worst `5s ... cost=TAG`).
- [ ] Repeat with three players in a busy room (fight + puzzle): no sustained spikes.
- [ ] Resync storm (several Resync presses, join/leave loops): no hitch spike above the budget.
- [ ] `[Guard]` lines: count distinct tags, anything repeating every 30 s is a bug to file.

## 18. Solo (offline) sanity per chapter start

With the mod loaded but **no session running**, start each chapter from its start (F7 or normal progress) and check vanilla behaviour: no errors in the log, nothing multiplayer-only visible, death is a normal game over, saves work.

- [ ] Penrose (wreck/hole, airlock)
- [ ] Chapter 1 Reeducation / Mines elevator
- [ ] Residency / Library
- [ ] Laboratory
- [ ] Medical
- [ ] Mines / Redoubt (later chapters)
- [ ] Final chapters (Falke, Chimera, END) and credits
- [ ] After hosting then disconnecting, the same game behaves vanilla again (no leftover gates, death is game over again).

## Sign-off

- [ ] All sections above pass with two instances.
- [ ] Sections 1, 3, 4, 7 (one family), 8 and 11 also pass with three instances.
- [ ] `[Guard]`, `[Harmony]` and `[Hitch]` lines reviewed on both logs; new findings filed.

Known risks to watch (from CHANGELOG 0.5.57/0.5.58 "Open risks"): Harmony detours on `HurtElster`, `GameOverHandler.hurt`, `SaveManager.Load`, `EnemyController.Hit`, `END_Graves`, `SceneHelper`/`CreditsEnd`; whether a direct `SaveManager.Load()` reloads the scene like the native game-over; Falke spear held only by a client; client `Hitbox.HP` rollback; bag snapshots without ammo; client-authored puzzle Mask/Seq merge; hurtbox radius and pulse timing; `ItemPickedUp` has no owner field (a client can still despawn other players' drops); recycled dropped-item id collisions; client-local world FMOD not relayed; no distance filtering of enemy snapshots.

## Automated runs

The test pilot (`docs/PILOT.md`) runs host + two clients unattended and diffs every peer's world against the host's after each step. `scripts/pilot/coop-soak.sh` covers sections 2 (late join), 3 (a client's death and reload), 6 (one door arrow), 8 (pickups, simultaneous grab) and 9 (client kills, including rooms the host never entered) for three players in one chapter; `scripts/pilot/story-run.sh` covers section 5 (host and client chapter loads) across the story. A pilot pass does not replace the hands-on items (presentation, audio, feel), it gates them: run it first and fix what it finds.
