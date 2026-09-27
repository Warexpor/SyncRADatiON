## 0.5.37 — 2026-09-27

Protocol **v10**. Batch 42 ship (Dig R): RES_MusicBox ApplyMusicBox rising-edge `onSuccess` so peers + late-join FullRefresh get minimap `dimPOI`, not only SnapMusicBox opened pose.

### Fixed
- **ApplyMusicBox SnapMusicBox-only — peers miss onSuccess dimPOI** — ApplyMusicBox set `hasCassette` from Bool1 then `SnapMusicBox` (latch `opened`, force `hasCassette=true`, pose lid/CardPickup/BoxObs/tapeInteraction). Never Invoked `onSuccess`. Native Update (~0x4B4ED0) only fires `onSuccess` on `opened` false→true; after Apply latches, native retry permanently skipped. Asset `onSuccess` → `MinimapPOIObject.dimPOI`. Softlock: host opens music box → peers latch opened + pose but minimap POI stays bright. Late-join FullRefresh same. `LoadState` does not invoke. Melon fields `opened` / `hasCassette` / `onSuccess` verified (`onSuccess` camelCase). Fix (Dig R, mirror PEN_Reaktor 0.5.36 / RES_Shrine 0.5.35 rising-edge both-path): capture `was=opened`; preserve Bool1 `hasCassette`; if `!e.Bool0` return; if `!was` → BeginApply + `onSuccess.Invoke()` once + SnapMusicBox pose for both MutateWorld and FullRefresh (idempotent dimPOI; no separate onLoad). SnapMusicBox no longer overwrites wire cassette. Protocol 10 unchanged (reuse RES_MusicBox Bool0=opened + Bool1=hasCassette; no new ushort).

### Before → After (player)
- **Before:** Host opens the RES music box (`Update` → onSuccess dimPOI + opened pose). Peers / late-join FullRefresh SnapMusicBox pose the opened box, but never Invoke `onSuccess` — minimap POI stays bright.
- **After:** Live MutateWorld peer **and** late-join FullRefresh on false→true `opened` edge BeginApply-Invokes `onSuccess` (dimPOI) then SnapMusicBox (opened pose / pickups / interactions) as before; Bool1 hasCassette preserved from wire.

### Dig R residual

| Hole | Evidence | Status |
|------|----------|--------|
| ApplyMusicBox SnapMusicBox-only; never Invokes onSuccess → peers miss dimPOI; Snap forced hasCassette=true | Dig R: ApplyMusicBox Snap-only; Melon opened/hasCassette/onSuccess; Update ~0x4B4ED0 rising-edge only; Asset onSuccess=dimPOI; LoadState no invoke; PEN_Reaktor/RES_Shrine rising-edge | **SHIPPED** (!was onSuccess+SnapMusicBox both paths; Snap no longer overwrites Bool1; protocol 10 Bool0/Bool1 reuse) |

Protocol stays **10** (reuse RES_MusicBox Bool0 opened + Bool1 hasCassette; no new ushort). Host-authoritative; N-peer live + late-join Apply path.

## 0.5.36 — 2026-09-27

Protocol **v10**. Batch 41 ship (Dig Q): PEN_Reaktor ApplyReaktor rising-edge `onSuccess` so peers + late-join FullRefresh get Popup / door-spot markers (red blocked → green success), not only SnapReaktor door unlock.

### Fixed
- **ApplyReaktor SnapReaktor-only — peers miss onSuccess Popup / door-spot markers** — ApplyReaktor applied mid fields (`valid`/`current`/`Dvalue`/`Dtemp`/`total`) then `SnapReaktor` (latch `solved`, unlock `doorLock` / InteractiveLockSingle plates, disable `_event`). Never Invoked `onSuccess`. Native Update sets `solved` then starts `win`; `win.MoveNext` Invokes `onSuccess` then unlocks. Asset `onSuccess` → `Popup.SetActive(true)`; `E Door Spot Blocked.SetActive(false)`; `E Door Spot.SetActive(true)`. Softlock: host solves → peers latch solved + unlock doors but red blocked marker stays, green success marker / popup missing. Late-join FullRefresh same. Melon fields `solved` / `doorLock` / `onSuccess` / `_event` verified (`onSuccess` camelCase). Fix (Dig Q, mirror RES_Shrine 0.5.35 / DoorLockEvent 0.5.31 rising-edge both-path): capture `was=solved`; apply mid fields; if `!e.Bool0` return; if `!was` → BeginApply + `onSuccess.Invoke()` once + `SnapReaktor` for both MutateWorld and FullRefresh (idempotent SetActive final-pose; no separate onLoad). Existing SnapReaktor lock/plates/`_event` unlock logic retained. Protocol 10 unchanged (reuse PEN_Reaktor Bool0 solved + Bool1 valid + Int0..3; no new ushort).

### Before → After (player)
- **Before:** Host solves the PEN reactor (`win` → onSuccess popup + green door spot, unlock). Peers / late-join FullRefresh SnapReaktor unlock doors and disable `_event`, but never Invoke `onSuccess` — red blocked marker stays, green success marker / Popup missing.
- **After:** Live MutateWorld peer **and** late-join FullRefresh on false→true `solved` edge BeginApply-Invokes `onSuccess` (Popup on, blocked spot off, success spot on) then SnapReaktor (door unlock / plates / `_event` disable) as before.

### Dig Q residual

| Hole | Evidence | Status |
|------|----------|--------|
| ApplyReaktor fields+SnapReaktor only; never Invokes onSuccess → peers miss Popup / E Door Spot markers | Dig Q: ApplyReaktor Snap-only; Melon solved/doorLock/onSuccess/_event; native Update→win.MoveNext onSuccess then unlock; Asset onSuccess=Popup+door spots; RES_Shrine/DoorLockEvent rising-edge | **SHIPPED** (!was onSuccess+SnapReaktor both paths; Snap unlock retained; protocol 10 Bool0 reuse) |

Protocol stays **10** (reuse PEN_Reaktor Bool0 solved + Bool1 valid + Int0..3 mid-fidelity; no new ushort). Host-authoritative; N-peer live + late-join Apply path.

## 0.5.35 — 2026-09-27

Protocol **v10**. Batch 40 ship (Dig P): RES_Shrine ApplyShrine final-pose (doors / content / onSuccess) so peers + late-join FullRefresh latch the released shrine pose instead of sticky-solved softlock.

### Fixed
- **ApplyShrine latches solved then CheckSolve — peers miss delayedReactionToSolve final pose** — ApplyShrine set `solved`/`busy`/`big`/`mid`/`small` then called native `CheckSolve()`. Native CheckSolve (~0x4B6AF0) immediately exits when `solved` is already true → never starts `delayedReactionToSolve`. MoveNext (~0x6EEAA0) does solved transition, `content.SetActive(true)`, MoveTowards `doorPos`→1 (Update applies LeftDoor Z=`-OpenAngle*doorPos` / RightDoor Z=`+OpenAngle*doorPos`), `onSuccess.Invoke`, StartCoroutine(`release`). Asset `onSuccess` → `MinimapPOIObject.dimPOI` ×2. Softlock: live peers + late-join FullRefresh latch shrine solved but miss final door/release pose, content activation, minimap dimming; native re-check cannot recover (solved sticky). Melon fields `solved`/`busy`/`big`/`mid`/`small`/`onSuccess`/`LeftDoor`/`RightDoor`/`OpenAngle`/`doorPos`/`content` verified; LoadState when solved snaps `doorPos=1` + content (no onSuccess). Fix (Dig P, mirror RadioStationTutorial/Magpie final-pose + DoorLockEvent rising-edge Invoke): capture `was=solved`; latch fields; **never** CheckSolve after latching solved; if `!e.Bool0` return; if `!was` → BeginApply + `onSuccess.Invoke()` once + SnapShrineFinalPose (`doorPos=1`, LeftDoor Z=`-OpenAngle`, RightDoor Z=`+OpenAngle`, content active) for both MutateWorld and FullRefresh (dimPOI safe). Bool1 busy + Int0..2 plate apply preserved. Protocol 10 unchanged (reuse RES_Shrine Bool0/Bool1/Int0..2; no new ushort).

### Before → After (player)
- **Before:** Host solves the RES shrine (plates align → CheckSolve → delayedReaction opens doors, activates content, dims minimap POIs). Peers latch `solved=true` then call CheckSolve which no-ops — doors stay closed, content stays inactive, minimap POIs stay bright. Late joiner FullRefresh same. Sticky solved cannot recover.
- **After:** Live MutateWorld peer **and** late-join FullRefresh on false→true `solved` edge BeginApply-Invokes `onSuccess` (dimPOI ×2) and snaps doors open (`doorPos=1`, Left/Right ±OpenAngle) + content active. Plate Int0..2 + busy Bool1 still apply as before.

### Dig P residual

| Hole | Evidence | Status |
|------|----------|--------|
| ApplyShrine latched solved then CheckSolve; CheckSolve early-out skips delayedReaction → peers miss doors/content/onSuccess | Dig P: ApplyShrine CheckSolve-after-latch; Melon solved/busy/big/mid/small/onSuccess/LeftDoor/RightDoor/OpenAngle/doorPos/content; CheckSolve ~0x4B6AF0 early-out; delayedReaction ~0x6EEAA0 doorPos→1 + content + onSuccess; Asset onSuccess=dimPOI×2; LoadState doorPos=1 when solved | **SHIPPED** (!was onSuccess+SnapShrineFinalPose both paths; no CheckSolve after latch; protocol 10 Bool0/Bool1/Int0..2 reuse) |

Protocol stays **10** (reuse RES_Shrine Bool0 solved + Bool1 busy + Int0..2 plates; no new ushort). Host-authoritative; N-peer live + late-join Apply path.

## 0.5.34 — 2026-09-27

Protocol **v10**. Batch 39 ship (Dig O): LAB_Rings sync partial finger states via `PuzzleStateEntry.Int0` (4×2-bit) so N-peer place/take and late-join FullRefresh see in-progress rings, not only final solve.

### Fixed
- **LAB_Rings TryRead/Apply solved-only — peers diverge on partial finger place/take** — TryRead emitted only `solved` (Int0=0); ApplyLabRings ignored every unsolved entry; LabRingsPatch emitted only on `checkSolution` when solved. Native durable state is four `LAB_Rings_Finger.state` values (`empty/regent/serpent/bride` = 0..3) on Zeige/Mittel/Ring/Klein, with `placeRing` / `takeRing` / `setStates` / `LoadState`→`loadFinger`, plus expected `S_*`. Softlock: P1 places one ring, P2 places another → local fingers diverge; host receives no partial state; late joiner stays empty until final Bool0 solve. Melon fields `Zeige`/`Mittel`/`Ring`/`Klein`/`state`/`S_*`/`setStates`/`solved`/`solvedState`/`PlatePickup`/`FakePlate` verified; AssetStudio `S_Zeige=regent,S_Mittel=empty,S_Ring=bride,S_Klein=serpent`. Fix (Dig O, protocol 10 reuse): pack 4×2-bit into Int0 (Zeige|Mittel|Ring|Klein); TryRead emits pack + Bool0; placeRing/takeRing postfix EnvEmit.Read (Progressed when solved); Apply sanitizes nibbles 0..3, applies finger.state + `setStates` (idempotent / FullRefresh force), snaps plate path when Bool0 **or** pack matches `S_*`. IsProgressed holds Int0!=0 for remount. No new wire/type/protocol bump.

### Before → After (player)
- **Before:** P1 places a ring; P2 places another. Local finger states diverge; host never sees partial state; late joiner / host stay empty until someone fully solves — P2 cannot reliably continue an in-progress LAB rings puzzle.
- **After:** Each place/take emits packed Int0 finger states; host validates 0..3 and applies native-equivalent `setStates` visuals; peers + late-join FullRefresh see the same mid-puzzle fingers; Bool0 solved still snaps plate / solvedState as before.

### Dig O residual

| Hole | Evidence | Status |
|------|----------|--------|
| TryRead/Apply solved-only; place/take never emit partial fingers → N-peer diverge + late-join empty until solve | Dig O: TryRead Int0=0; ApplyLabRings Bool0-only Snap; LabRingsPatch solved-only; Melon finger.state/S_*/setStates/loadFinger; AssetStudio S_* | **SHIPPED** (Int0 4×2-bit pack; place/take Read; Apply setStates + S_* validate; Bool0 plate snap retained; protocol 10) |

Protocol stays **10** (reuse LAB_Rings Bool0 solved + Int0 finger pack; no new ushort). Host-authoritative; N-peer live + late-join Apply path.

## 0.5.33 — 2026-09-27

Protocol **v10**. Batch 38 ship (Dig N): RadioStationTutorial ApplyTutorial final-pose snap (Door Z=20 / returnStations / TutorialStation) + live EndCutscene.trigger so DET radio bunker tutorial peers exit softlock.

### Fixed
- **ApplyTutorial latch-only — peer skips OpenDoor final pose + EndCutscene** — ApplyTutorial only did `x.solved = e.Bool0`. Native Update (~0x4C3D70) when `completionTimer >= completionTime` → `EndCutscene.trigger` + latch `solved` + `StartCoroutine(OpenDoor)`. OpenDoor.MoveNext (~0x6EB910): `PlayOneShot(unlockedSFX)`, lerp Door localRotation Z **180→20** via `Quaternion.Euler(0,0,angle)`, `SetActive(returnStations,true)` / `SetActive(TutorialStation,false)`. Door pathId is NOT a Doorway_* — no DoorSync cover. OnEnable always `returnStations=false`, `TutorialStation=true` (unsolved pose) — no solved snap. Softlock: Host solves → peers Apply set `solved=true` only; local `completionTimer` stays 0 → Update never re-enters solve block; Door Transform stays closed; TutorialStation stays active. Late-join FullRefresh same. Melon fields `solved` / `Door` / `returnStations` / `TutorialStation` / `EndCutscene` / `unlockedSFX` verified. Fix (Dig N, mirror GunCase/Magpie final-pose class — NOT UnityEvent.Invoke): capture `was=solved`; latch solved; if `!e.Bool0` return; if `!was` → BeginApply; LIVE `MutateWorld` → `EndCutscene.trigger()` + optional `unlockedSFX` PlayOneShot + pose snap; FullRefresh → pose snap ONLY (skip cutscene remount / skip EndCutscene.trigger). Protocol 10 unchanged (reuse RadioStationTutorial Bool0 solved; no new ushort).

### Before → After (player)
- **Before:** Host completes the DET radio bunker tutorial (holds freq → timer done). Peers latch `solved=true` but Door stays closed and TutorialStation stays active — softlock stuck in the bunker tutorial pose. Late joiner FullRefresh same.
- **After:** Live MutateWorld peer on false→true `solved` edge BeginApply-triggers EndCutscene + plays unlockedSFX + snaps Door open (Z=20) / returnStations on / TutorialStation off. Late-join FullRefresh snaps the same final pose without remounting EndCutscene.

### Dig N residual

| Hole | Evidence | Status |
|------|----------|--------|
| ApplyTutorial latch-only; peer skips OpenDoor final pose + EndCutscene → bunker Door closed / TutorialStation stuck | Dig N: ApplyTutorial solved-only; Melon Door/returnStations/TutorialStation/EndCutscene/unlockedSFX; OpenDoor.MoveNext Z 180→20 + SetActives; Update EndCutscene.trigger; OnEnable unsolved pose; Door not Doorway_* | **SHIPPED** (MutateWorld&&!was EndCutscene+SFX+pose; FullRefresh pose-only; protocol 10 Bool0 reuse) |

Protocol stays **10** (reuse RadioStationTutorial Bool0 solved; no new ushort). Host-authoritative; N-peer live + late-join Apply path.

## 0.5.32 — 2026-09-27

Protocol **v10**. Batch 37 ship (Dig M): MultiConditionEvent ApplyMultiConditionEvent rising-edge `OnTryDone` so Proceed / ProceedDelayed / delayedEvent / SetTrigger fire for late-join FullRefresh (and live MutateWorld if StoryCmd missed); live StoryCmd path stays sole fire via rising-edge.

### Fixed
- **ApplyMultiConditionEvent latch-only — late-join skips OnTryDone** — ApplyMultiConditionEvent (~675–680) only latched `triedOnce` + `tried`. Never Invoked `OnTryDone`. Live `StoryCmd.MultiConditionFire` calls `TryOnce`/`TryTrigger` AND `OnTryDone.Invoke` — OK. Late-join FullRefresh latched `triedOnce=true` with no Invoke; native `TryOnce` early-outs on `triedOnce` → consequence never recoverable (cutscene/story softlock). Melon fields `triedOnce` / `tried` / `OnTryDone` verified (`OnTryDone` PascalCase UnityEvent). Assets: `OnTryDone` → Proceed / ProceedDelayed / delayedEvent / SetTrigger. Fix (Dig M, mirror ApplyDoorLockEvent 0.5.31 both-path): capture `was=triedOnce`; latch triedOnce+tried; if `!e.Bool0` return; if `!was` → BeginApply + `OnTryDone.Invoke()`. Live StoryCmd sets triedOnce first → Apply sees `was=true` → no double-fire. Protocol 10 unchanged (no new ushort).

### Before → After (player)
- **Before:** Host fires a MultiConditionEvent. Live peers get OnTryDone via StoryCmd (OK). Late joiner FullRefresh latches `triedOnce=true` but never Invokes OnTryDone — Proceed/ProceedDelayed/delayedEvent/SetTrigger never run; native TryOnce early-outs forever — cutscene/story softlock.
- **After:** Late-join FullRefresh **and** live MutateWorld Apply on false→true `triedOnce` edge BeginApply-Invokes `OnTryDone`. Live StoryCmd path still fires once; rising-edge skips Apply re-Invoke when StoryCmd already latched triedOnce.

### Dig M residual

| Hole | Evidence | Status |
|------|----------|--------|
| ApplyMultiConditionEvent latch-only; late-join skips OnTryDone → Proceed/delayedEvent softlock | Dig M: ApplyMultiConditionEvent ~675–680; Melon triedOnce/tried/OnTryDone; live StoryCmd MultiConditionFire already Invokes; native TryOnce early-out on triedOnce; DoorLockEvent 0.5.31 both-path rising-edge | **SHIPPED** (!was OnTryDone for both MutateWorld and FullRefresh; live StoryCmd double-fire avoided via rising-edge) |

Protocol stays **10** (reuse MultiConditionEvent Bool0 triedOnce + Int0 tried; no new ushort). Host-authoritative; N-peer late-join Apply path.

## 0.5.31 — 2026-09-27

Protocol **v10**. Batch 36 ship (Dig L): DoorLockEventInteraction ApplyDoorLockEvent rising-edge `onSolved` so DoorLockEvent GO SetActive(false) fires for live peers **and** late-join FullRefresh (LoadState ≡ onSolved; no onLoad).

### Fixed
- **ApplyDoorLockEvent only latches done — never Invokes onSolved** — ApplyDoorLockEvent (~406–409) only latched `done`. Melon fields `done` / `onSolved` verified (camelCase); **no** `onLoad` UnityEvent. Native LoadState and solved path both Invoke `onSolved`. Asset DET: `onSolved` → `SetActive(false)` on DoorLockEvent GO. Softlock: Host solves → peer `done=true` but DoorLockEvent GO stays active. Fix (Dig L): capture `was=done`; latch done; if `!e.Bool0` return; if `!was` → BeginApply + `onSolved.Invoke()`. Both live MutateWorld and FullRefresh use `onSolved` (LoadState ≡ onSolved). Optional `Door.SetActive(true)` deferred (Dig L optional soak). Protocol 10 unchanged (no new ushort).

### Before → After (player)
- **Before:** Host solves a DoorLockEvent. Live peer latches `done=true`, but the DoorLockEvent GO stays active (onSolved never Invoked) — softlock stuck on the lock UI / interaction.
- **After:** Live MutateWorld peer **and** late-join FullRefresh on false→true `done` edge BeginApply-Invokes `onSolved` (DoorLockEvent GO SetActive(false)).

### Dig L residual

| Hole | Evidence | Status |
|------|----------|--------|
| ApplyDoorLockEvent latch-only; peer skips onSolved → DoorLockEvent GO stays active | Dig L: ApplyDoorLockEvent ~406–409; Melon done/onSolved (no onLoad); native LoadState + solved both Invoke onSolved; Asset DET onSolved = SetActive(false); LoadState ≡ onSolved so FullRefresh also Invokes | **SHIPPED** (!was onSolved for both MutateWorld and FullRefresh; Door.SetActive optional soak deferred) |

Protocol stays **10** (reuse DoorLockEvent Bool0 done; no new ushort). Host-authoritative; N-peer live + late-join Apply path.

## 0.5.30 — 2026-09-27

Protocol **v10**. Batch 35 ship (Dig K #2 / Dig J #5): LAB_PatternLock ApplyPatternLock rising-edge `onSolved` so exitEvent + Play/SetActive fire for live peers (EventScreen dismiss).

### Fixed
- **LAB_PatternLock ApplyPatternLock Disable+doors only — never Invokes onSolved** — ApplyPatternLock (~159–166) only latched `solved` + `DisablePatternLock` + `TryUnlockDoors`. Native `delayed.MoveNext` (after `checkSolution` latches `solved` @ +0xA0, RVA 0x59CF80) loads `onSolved` @ +0xB0 and calls `UnityEvent$$Invoke` (RVA 0xDD7BF0). AssetStudio `LAB_PatternLock.onSolved` → `exitEvent` (EventScreen / DoorLockEvent) + `Play` + `SetActive` (PEN_CryoOverride / LAB ponds). Peer Apply disabled the pad + unlocked doors but EventScreen never exitEvents — softlock class Dig flagged. Melon fields `solved` / `onSolved` verified (camelCase); **no** `onLoad` UnityEvent. Fix (mirror ApplyMulti 0.5.29 live path / ApplyMural 0.5.26): capture `was=solved`; latch solved; if `!e.Bool0` return; keep `DisablePatternLock`; if `MutateWorld && !was` → BeginApply + `onSolved.Invoke()`; keep `TryUnlockDoors`. FullRefresh (`!MutateWorld`): keep skip (no onLoad soak; exitEvent remount unwanted unless soak asks). Do **not** ship DoorLockEvent yet (0.5.31 Dig L candidate). Protocol 10 unchanged (no new ushort).

### Before → After (player)
- **Before:** Host solves a LAB PatternLock (PEN cryo override / LAB pond). Live peer sees pad disabled + doors unlocked, but EventScreen never exitEvents — softlock stuck on the pattern UI / overlay.
- **After:** Live MutateWorld peer on false→true `solved` edge BeginApply-Invokes `onSolved` (exitEvent + Play/SetActive) after DisablePatternLock. Late-join FullRefresh still skips onSolved (no exitEvent remount) until soak asks for an onLoad-equivalent.

### Dig K #2 residual

| Hole | Evidence | Status |
|------|----------|--------|
| ApplyPatternLock latch+Disable+doors only; peer skips onSolved → EventScreen exitEvent softlock | Dig K #2 / Dig J #5: ApplyPatternLock ~159–166; Melon solved/onSolved (no onLoad); AssetStudio onSolved = exitEvent+Play+SetActive; native delayed.MoveNext Invoke @ UnityEvent$$Invoke after checkSolution; MultiLock/Mural rising-edge pattern | **SHIPPED** (MutateWorld&&!was onSolved; FullRefresh skip retained; DoorLockEvent deferred) |

Protocol stays **10** (reuse PatternLock Bool0 solved; no new ushort). Host-authoritative; N-peer live cinematic Apply path. DoorLockEvent deferred.

## 0.5.29 — 2026-09-27

Protocol **v10**. Batch 34 ship (Dig K #1): MED/LAB MultiLock ApplyMulti rising-edge `onUnlocked` (+ `onUnlockedLate`) so exitEvent + Inter SetActive fire for live peers; late-join uses `onLoadUnlocked`.

### Fixed
- **MED/LAB MultiLock ApplyMulti latch-only — never Invokes Melon UnityEvents** — ApplyMulti (~270–288) only latched `unlocked` + element bits + `TryUnlockDoors`. Native `UnlockKey` Invokes `onUnlocked` then `onUnlockedLate`; `OnEnable` (load) Invokes `onLoadUnlocked` when already unlocked. AssetStudio MED Elemental: `onUnlocked` → dimPOI + exitEvent (EventInter); `onUnlockedLate`/`onLoadUnlocked` → SetActive Inter/EventInter. LAB TreeLock: `onUnlocked` → exitEvent + Play; late/load → SetActive Inter/Event. Peer Apply unlocked+doors OK but EventScreen never exitEvents and Inter stayed stuck; late joiner same. Melon fields `unlocked` / `onUnlocked` / `onUnlockedLate` / `onLoadUnlocked` verified (camelCase); MED also has `onEarthKeyUnlocked` (mid-key dimPOI only — skipped). Inter SetActive is **only** on Late/Load — live path must Invoke both `onUnlocked` and `onUnlockedLate`. Fix (mirror ApplyRotKeypad 0.5.25 / ApplyPower 0.5.27 / ApplyPump 0.5.28): capture `was=unlocked`; latch unlocked+bits; if `!e.Bool0` return; if `MutateWorld && !was` → BeginApply + `onUnlocked.Invoke()` + `onUnlockedLate.Invoke()`; else if `!MutateWorld && !was` → BeginApply + `onLoadUnlocked.Invoke()`; keep `TryUnlockDoors`. Do **not** ship PatternLock (0.5.30). Protocol 10 unchanged (no new ushort).

### Before → After (player)
- **Before:** Host finishes MED Elemental / LAB TreeLock. Live peer sees unlocked + element bits + doors, but EventScreen never exitEvents and Inter stays active — softlock at the lock UI. Late joiner FullRefresh same Inter stuck.
- **After:** Live MutateWorld peer on false→true `unlocked` edge BeginApply-Invokes `onUnlocked` (exitEvent + dimPOI/Play) then `onUnlockedLate` (Inter/EventInter SetActive). Late-join FullRefresh Invokes `onLoadUnlocked` (same SetActive as Late; no exitEvent remount).

### Dig K #1 residual

| Hole | Evidence | Status |
|------|----------|--------|
| ApplyMulti latch-only; peer skips onUnlocked/onUnlockedLate → exitEvent + Inter softlock; late-join skips onLoadUnlocked | Dig K #1: ApplyMulti ~270–288; Melon onUnlocked/Late/onLoadUnlocked; AssetStudio MED/LAB wiring; Inter SetActive only on Late/Load; UnlockKey live / OnEnable load; RotKeypad/Power/Pump rising-edge pattern | **SHIPPED** (MutateWorld&&!was onUnlocked+onUnlockedLate; FullRefresh onLoadUnlocked; onEarthKeyUnlocked skipped) |

Protocol stays **10** (reuse MultiLock Bool0 unlocked + Int0 bits; no new ushort). Host-authoritative; N-peer live cinematic Apply path. PatternLock deferred.

## 0.5.28 — 2026-09-27

Protocol **v10**. Batch 33 ship (Dig J #3): MED_Pump ApplyPump rising-edge `onSolved` so StartCutscene + dimPOI + RecordSplit fire for live peers; late-join uses `onLoad` (dimPOI only).

### Fixed
- **MED_Pump SnapMedPump Drain only — never Invokes onSolved** — SnapMedPump (~94–105) only latched `solved` + `SnapFlood` + `TryUnlockDoors`. Native `checkSolved` Invokes `onSolved` + transfers/drain. AssetStudio `MED_Pump.onSolved` → `dimPOI` + `StartCutscene` + `RecordSplit`. Peer Apply drained flood but skipped cutscene/onSolved consequences. Melon fields `solved` / `onSolved` / `onLoad` verified (camelCase). `onLoad` → `dimPOI` only (no StartCutscene). Fix (mirror ApplyMural live path + ApplyRotKeypad late-join onLoad): capture `was=solved`; snap a/b/c; if `!e.Bool0` return; keep `SnapMedPump` drain; if `MutateWorld && !was` → BeginApply + `onSolved.Invoke()`; else if `!MutateWorld && !was` → BeginApply + `onLoad.Invoke()` (dimPOI soak, skip remount StartCutscene). Protocol 10 unchanged (no new ushort).

### Before → After (player)
- **Before:** Host solves the MED pump. Live peer sees water levels + flood drain, but dimPOI never dims, StartCutscene never runs, RecordSplit never records — story beat softlock after the pump.
- **After:** Live MutateWorld peer on false→true `solved` edge BeginApply-Invokes `onSolved` (dimPOI + StartCutscene + RecordSplit) after SnapMedPump drain. Late-join FullRefresh Invokes `onLoad` (dimPOI only) — no StartCutscene remount.

### Dig J #3 residual

| Hole | Evidence | Status |
|------|----------|--------|
| SnapMedPump drain-only; peer skips onSolved → StartCutscene/dimPOI/RecordSplit softlock | Dig J #3: SnapMedPump ~94–105; Melon solved/onSolved/onLoad; AssetStudio onSolved = dimPOI+StartCutscene+RecordSplit; onLoad = dimPOI only; ApplyMural/ApplyRotKeypad rising-edge pattern | **SHIPPED** (MutateWorld&&!was onSolved; FullRefresh onLoad) |

Protocol stays **10** (reuse MED_Pump Bool0 solved; no new ushort). Host-authoritative; N-peer live cinematic Apply path.

## 0.5.27 — 2026-09-27

Protocol **v10**. Batch 32 ship (Dig J #2): RES_Power ApplyPower rising-edge `OnSuccess` so residency setPower + Paternoster SetSpeed fire for live peers.

### Fixed
- **RES_Power ApplyPower flags-only — never Invokes OnSuccess** — ApplyPower (~119–125) only snapped `solved`/`powered`/fuse `states`. Native `TryMasterFlip` (RVA 0x4B5FB0) Invokes `OnSuccess` @ ~0x4B60D4 when TopVolt==800 && BotVolt==230, then latches `solved=1` @ +0x91. AssetStudio `RES_Power.OnSuccess` → `SetSpeed` (Paternoster) + `setPower`×3 + SetActive + dimPOI + Play. Peer Apply skipped the UnityEvent → residency power softlock / paternoster not driven. Fix (mirror ApplyMural / ApplyRotKeypad live path): capture `was=solved`; latch solved/powered/states; if `!e.Bool0` return; if `MutateWorld && !was` → BeginApply + `OnSuccess.Invoke()`; keep flag snaps. Melon field `OnSuccess` (PascalCase) verified — no `onLoad`. FullRefresh (`!MutateWorld`): rising-edge only for MutateWorld (thin; Dig J #2 — no onLoad soak; setPower/SetSpeed remount unwanted unless soak asks). Do **not** ship MED_Pump (0.5.28). Protocol 10 unchanged (no new ushort).

### Before → After (player)
- **Before:** Host solves the residency fuse puzzle (master flip). Live peer sees solved/powered/fuse flags latch, but setPower targets stay unpowered and the Paternoster never gets SetSpeed — softlock at residency power / elevator.
- **After:** Live MutateWorld peer on false→true `solved` edge BeginApply-Invokes `OnSuccess` (setPower×3 + Paternoster SetSpeed + SetActive/dimPOI/Play). Late-join FullRefresh still skips OnSuccess (no remount) until soak asks for an onLoad-equivalent.

### Dig notes (Batch 32)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| ApplyPower flags-only; peer skips OnSuccess → setPower/SetSpeed softlock | Dig J #2: ApplyPower ~119–125; TryMasterFlip RVA 0x4B5FB0 Invoke @ 0x4B60D4 then solved=1 @ +0x91; AssetStudio OnSuccess = SetSpeed+setPower×3+SetActive+dimPOI+Play; Melon OnSuccess Pascal; no onLoad | **SHIPPED** (MutateWorld&&!was OnSuccess; FullRefresh skip retained) |

Protocol stays **10** (reuse RES_Power Bool0 solved; no new ushort). Host-authoritative; N-peer live cinematic Apply path. MED_Pump deferred.

## 0.5.26 — 2026-09-27

Protocol **v10**. Batch 31 ship (Dig J #1): ApplyMural rising-edge `onSolved` so Blocker Entry + cutscene fire for live peers.

### Fixed
- **ROT_Mural Apply never Invokes onSolved** — ApplyMural latched `finished=true` then called `useRing()` only. Native Update Invokes `onSolved` only when finished was false, then finished=1. Asset `onSolved` → goBack / SetActive(false) Blocker Entry / setUnleavable / StartCutscene / FMOD. `useRing` only toggles RingInter/MissingRing — **no** onSolved. Peer Update rising-edge skipped after latch → no cutscene, Blocker Entry stayed. Fix (mirror ApplyRotKeypad live path): capture `was=finished`; latch finished/busy/moons; if `!e.Bool0` return; if `MutateWorld && !was` → BeginApply + `onSolved.Invoke()` then `useRing()` (ring prop pose); keep `TryUnlockDoors`. FullRefresh (`!MutateWorld`): keep skip for now (Dig J — no mural onLoad soak; cutscene replay unwanted unless soak asks). Prior park claiming useRing covers Blocker was wrong. Protocol 10 unchanged (no new ushort).

### Before → After (player)
- **Before:** Host solves the mural. Live peer sees moons/finished latch + ring prop via useRing, but Blocker Entry stays active and the mural cutscene never starts (onSolved never Invoked; native rising-edge already skipped).
- **After:** Live MutateWorld peer on false→true edge BeginApply-Invokes `onSolved` (Blocker Entry SetActive(false) + cutscene/FMOD/goBack) then `useRing` for ring prop. Late-join FullRefresh still skips onSolved (no cutscene replay) until soak asks for an onLoad-equivalent.

### Dig notes (Batch 31)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| ApplyMural latches finished + useRing only; peer Update rising-edge skipped; Blocker Entry + cutscene softlock | Dig J: native Update onSolved only when !finished then finished=1; asset onSolved → Blocker/cutscene; useRing ≠ onSolved; ApplyRotKeypad live MutateWorld&&!was BeginApply+Invoke pattern | **SHIPPED** (MutateWorld&&!was onSolved + useRing; FullRefresh skip retained) |

Protocol stays **10** (reuse ROT_Mural Bool0; no new ushort). Host-authoritative; N-peer live cinematic Apply path.

## 0.5.25 — 2026-09-27

Protocol **v10**. Batch 30 ship (Dig I): ApplyRotKeypad late-join FullRefresh `onLoad` mirror.

### Fixed
- **ApplyRotKeypad late-join FullRefresh skips onSuccess/onLoad** — 0.5.22 only Invokes `onSuccess` when `mutateWorld && !was`. FullRefresh sets `_mutateWorld=false`, latches `solved=true`, skips Invoke. Later `ReapplyHeld` has `was` already true → rising-edge skip. `TryUnlockDoors` FindInParents misses ConnectedDoors Unlock peel under DoorConnections. AssetStudio ROT_Keypad: `onSuccess` AND `onLoad` → Unlock (`onLoad` omits exitEvent — native LoadState path). Softlock: late joiner sees keypad solved but Door Connection locked. Fix (Dig I preferred — LoadState mirror): latch solved/opening/blocked; if `!e.Bool0` return; if `mutateWorld && !was` → BeginApply + `onSuccess.Invoke()` (live 0.5.22); else if `!mutateWorld && !was` → BeginApply + `onLoad.Invoke()` (FullRefresh / late-join); keep `TryUnlockDoors` backup. Do **not** UseItem-style don't-latch solved (worse UX). Melon field `onLoad` verified. Protocol 10 unchanged (no new ushort).

### Before → After (player)
- **Before:** Peer solves a ROT_Keypad while another player is mid-chapter. Late joiner FullRefresh shows keypad solved but Door Connection under DoorConnections stays locked — softlock at the door (ReapplyHeld rising-edge already skipped).
- **After:** Late-join FullRefresh Invokes Melon `onLoad` (Unlock + SetActive + dimPOI; no exitEvent) on the false→true edge before latch sticks for ReapplyHeld, so Door Connection unlocks for every joiner. Live cinematic peers still get `onSuccess` (with exitEvent) unchanged.

### Dig notes (Batch 30)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| FullRefresh ApplyRotKeypad latches solved + skips onSuccess; ReapplyHeld rising-edge skip; TryUnlockDoors misses DoorConnections peel | Dig I: ApplyRotKeypad mutateWorld&&!was onSuccess only (0.5.22); ApplyPuzzleState FullRefresh ⇒ `_mutateWorld=false`; ReapplyHeld was already true; Melon `onLoad` + native LoadState; AssetStudio onSuccess/onLoad → Unlock | **SHIPPED** (LoadState mirror: onLoad on !mutateWorld&&!was; keep latch) |

Protocol stays **10** (reuse ROT_Keypad Bool0; no new ushort). Live rising-edge onSuccess preserved.

## 0.5.24 — 2026-09-27

Protocol **v10**. Batch 29 ship (Dig H): ItemPickup.onPickup party Ensure for non-claimers.

### Fixed
- **ItemPickup.onPickup never fires for non-claimer peers (and not for host on client-claim)** — `ApplyGrant` Invoked claimer-only (~702–705). `ApplyHide` hide-only; `BroadcastTriggered` state-only. Comment “Host already Invoked” was false when a client claimed: Peer2 grant Invokes; Host+Peer3/4 hide-only → `ROT_Tarot.TakeCard` / `LAB_Rings.takeRing` / `END_Boss.takeSpear` (unparks Falke empty-slot) / `ROT_TrainLogic.StartOutro`+`UnJam` / MeatBlocker `pickup` never ran party-wide. AssetStudio `ItemPickup_#*` bind those methods on `onPickup` (dump.cs ~484299). Fix (Dig H): idempotent `EnsurePartyOnPickup(worldId, p)` (`HashSet` once-per-id); call from `ApplyHide` (Triggered) **and** host `BroadcastTriggered`; host-native `Postfix`/`NoteTaken` `NoteOnPickupFired(id)` after native `pickUp` so Broadcast does not double-fire; `ApplyGrant` switches to same Ensure (claimer de-duped vs State); `BeginApply` around Invoke; clear set on scene refresh / claim release. Protocol 10 unchanged. Presentation `OpenBook`/`StartCutscene`/`dimPOI` — same acceptance as UseItem remount; soak late-join book flash on FullRefresh remount.

### Before → After (player)
- **Before:** Peer2 picks a tarot card / ring / Falke spear / train key / MeatBlocker item. Peer2 sees TakeCard/takeRing/takeSpear/StartOutro/MeatBlocker. Host and Peer3/4 only hide the prop — empty tarot board, rings still present, Falke empty-slot parked, train outro jammed, MeatBlocker pickups counter stuck (host misses when client claims).
- **After:** Every peer applying Triggered hide (and host BroadcastTriggered on client-claim) runs the same `onPickup` bindings once — TakeCard/takeRing/takeSpear/Train StartOutro+UnJam/MeatBlocker pickup apply for the whole party. Host-native take Notes first so Broadcast does not double-fire.

### Dig notes (Batch 29)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| ApplyGrant claimer-only Invoke; ApplyHide/BroadcastTriggered no onPickup; host miss on client-claim | Dig H: ApplyGrant ~702–705; ApplyHide ~611–630; BroadcastTriggered ~523–555; false “Host already Invoked”; AssetStudio TakeCard/takeRing/takeSpear/StartOutro+UnJam; MeatBlocker pickup | **SHIPPED** (HashSet Ensure + NoteOnPickupFired) |

Protocol stays **10** (no new ushort). Late-join FullRefresh may flash OpenBook/StartCutscene once per claimed story pickup — same class of remount acceptance as UseItem 0.5.21/0.5.23; soak if noisy.

## 0.5.23 — 2026-09-27

Protocol **v10**. Batch 28 ship (Dig G #2): UseItem late-join / FullRefresh rising-edge preserve.

### Fixed
- **UseItem FullRefresh latches unlocked before ReapplyHeld can Invoke onSuccessful** — Late joiner FullRefresh PuzzleState Apply sets `_mutateWorld=false`. `SnapUseItemWorld` (0.5.21) latched `unlocked=true` + disabled inter, then early-returned before `onSuccessful.Invoke()`. Held UseItems (Disk/Tarot/Graves/Dissolve/Jam) survived in `_held`; later `ReapplyHeld` set `_mutateWorld=true` but `wasUnlocked` was already true → rising-edge skip. Joiners kept `ROT_DiskManager.red/blue` false, empty tarot board, unfired graves/dissolve/jam UnityEvents — softlock. Live MutateWorld path (host InteractionSync Apply + cinematic peer Apply) was fine. Fix (Dig G thinner): on `!MutateWorld` snap inter disable only — do **not** latch `unlocked=true` (PerPlayerUse still `NoteRemoteUnlock`). ReapplyHeld / cinematic MutateWorld path still latches + rising-edge Invokes. Host who already ran native Dialoguer keeps rising-edge skip (unlocked already true). Protocol 10 unchanged (no new ushort). ItemPickup onPickup is 0.5.24 — not this batch.

### Before → After (player)
- **Before:** Peer inserts Disk / places Tarot / solves Graves while others are mid-chapter. Late joiner FullRefresh shows UseItem unlocked flags but Disk content / tarot cards / graves consequences never appear — story gates stay closed for the joiner.
- **After:** Late-join FullRefresh keeps `unlocked=false` (inter disabled only); ReapplyHeld rising-edge fires the same `onSuccessful` bindings (InsertDisk*/PlaceCard*/etc.) so Disk, Tarot, Graves, Dissolve, Jam apply for every joiner.

### Dig notes (Batch 28)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| FullRefresh SnapUseItemWorld latches unlocked then returns; ReapplyHeld rising-edge skipped | Dig G #2: SnapUseItemWorld ~58–145 latch-then-early-return on !MutateWorld; ApplyPuzzleState ~408–411 FullRefresh ⇒ `_mutateWorld=false`; ReapplyHeld ~498–518 `_mutateWorld=true` but wasUnlocked already true | **SHIPPED** (thinner: inter-only on !MutateWorld; no sticky consequencesApplied set) |

Protocol stays **10** (reuse UseItemInteraction Bool0; no new ushort). ItemPickup onPickup deferred to 0.5.24.

## 0.5.22 — 2026-09-27

Protocol **v10**. Batch 27 ship (Dig G #1): ROT_Keypad Apply rising-edge `onSuccess`.

### Fixed
- **ApplyRotKeypad never Invokes onSuccess — peer door softlock** — Host Interaction `ApplyKeypad` (0.5.19) Invokes `onSuccess` for the host when a ROT_Keypad solves. Live peers + late-join remount take PuzzleState `ApplyRotKeypad`, which only latched `solved`/`opening`/`blocked` + `TryUnlockDoors(keypad.GO)`. `TryUnlockDoors` = FindInParents `ConnectedDoors` only — but ROT_Keypad `onSuccess` peels to `ConnectedDoors.Unlock` on Door Connection (35) under DoorConnections (separate tree from KeypadLogic) + Event.SetActive + exitEvent + dimPOI. Peers kept ConnectedDoors locked. Contrast: `ApplyKeypad3D` already rising-edge `openDoor` when `mutateWorld && !was`; Dispatch dropped `_mutateWorld` for the ROT path. Now pass `_mutateWorld` into `ApplyRotKeypad`; on `e.Bool0 && mutateWorld && !was` → BeginApply + `onSuccess.Invoke()`; keep `TryUnlockDoors` as backup. Protocol 10 unchanged (no new ushort).

### Before → After (player)
- **Before:** Peer or host solves a ROT_Keypad. Host door unlocks (ApplyKeypad onSuccess). Live peers see solved flags but ConnectedDoors stay locked — softlock at the door.
- **After:** Every peer applying the ROT_Keypad PuzzleState (cinematic / remount mutateWorld) runs the same `onSuccess` peel — ConnectedDoors unlock and exitEvent/SetActive/dimPOI fire for the whole party.

### Dig notes (Batch 27)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| ApplyRotKeypad flags+TryUnlockDoors only; misses onSuccess peel | Dig G #1: ApplyRotKeypad ~167–171; ApplyKeypad3D rising-edge openDoor; Dispatch ~128–129 drops _mutateWorld; host ApplyKeypad Invokes onSuccess; TryUnlockDoors ≠ DoorConnections peel | **SHIPPED** |

Protocol stays **10** (reuse ROT_Keypad Bool0; no new ushort). Late-join UseItem FullRefresh shipped in 0.5.23.

## 0.5.21 — 2026-09-27

Protocol **v10**. Batch 26 ship (Dig B+F): UseItemInteraction Apply rising-edge `onSuccessful`.

### Fixed
- **UseItemInteraction.onSuccessful never fired on remote Apply** — `InteractionSyncService.ApplyUseItem` / `UseItemWorldSyncService.SnapUseItemWorld` latched `unlocked` + doors/flags only and Emit'd PuzzleState. Peer-side Disk / Tarot assets bind `InsertDiskRed|Blue` and `PlaceCardHeimat|Buyan|Kitezh|Vineta|Leng|Rotfront` on Melon `onSuccessful` (UnityEvent) — that Invoke ran natively only on the peer who used the item. Host + other peers kept `ROT_DiskManager.red/blue` false, `Disk*Content` / MultiInter inactive, `ROT_Tarot.cards[]` empty → story softlock / desync. Magpie `opened` already synced (untouched; Disk≠Magpie). No new `PuzzleType.ROT_DiskManager`. Now `SnapUseItemWorld` (single host-auth path: InteractionSync Apply + PuzzleState Apply / remount) captures `wasUnlocked` before latch; when `MutateWorld` and false→true and not PerPlayerUse, `BeginApply` + `onSuccessful.Invoke()`. Host-local Dialoguer already Invoked natively and Emitting with unlocked latched → rising-edge skip (no double-fire). Acting client re-Apply same. Protocol 10 unchanged.

### Before → After (player)
- **Before:** Peer inserts the Red/Blue Disk or places a tarot card. That peer sees Disk content / board cards; host and other peers keep empty Disk / empty tarot board — story gates stay closed.
- **After:** Every peer applying the UseItem unlock (host Apply + PuzzleState Snap, including remount when first unlocked) runs the same `onSuccessful` bindings — Disk inserts and tarot cards appear for the whole party.

### Dig notes (Batch 26)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| UseItem onSuccessful skipped on Apply | Dig B+F: ApplyUseItem ~172–220 unlocked+Snap+Emit no Invoke; SnapUseItemWorld ~58–120 flags/doors only; ApplyTarot darkmode+FlipSwitchPos only; assets bind InsertDisk*/PlaceCard* on onSuccessful; Magpie opened already synced | **SHIPPED** |

Protocol stays **10** (reuse UseItemInteraction Bool0; no new ushort / PuzzleType).

## 0.5.20 — 2026-09-27

Protocol **v10**. Batch 25 ship (Dig D): ElevatorCallButton Apply rising-edge CallElevator.

### Fixed
- **ElevatorCallButton Apply sets `called` without `CallElevator()`** — `ElevatorSyncService.ApplyCallButton` only wrote `called=e.Bool0`. Peer lobby press runs native `CallElevator` locally (sets called + `StartCoroutine(elevatorMove|elevatorBroken)` @ RVA 0x7EDF60) and emits Bool0; host Apply latched `called=true` with **no** coroutine → host cabin never moves; later presses early-out (`if called ret`); late-join dump same dead latch. No Update on type — coroutine is the only mover. Contrast: `ApplyCentral` field snap OK; EXC already `startRide` on rising edge. Now false→true invokes Melon-exposed `CallElevator()` (sets called itself — do **not** pre-set called); true→false clears `called` only. Protocol 10 unchanged.

### Before → After (player)
- **Before:** Peer presses a lobby elevator call. Peer's cabin moves; host cabin stays put with `called` latched true — further presses do nothing; late joiners inherit the dead latch.
- **After:** Host ApplyCallButton rising edge runs the same `CallElevator` coroutine peers already started natively — host cabin moves with the party.

### Dig notes (Batch 25)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| ApplyCallButton flag-only skips CallElevator | Dig D: ApplyCallButton ~53–56 flag only; CallElevator @ 0x7EDF60 if called ret else called=1 + StartCoroutine; no Update; EXC/Central contrast | **SHIPPED** |

Protocol stays **10** (reuse ElevatorCallButton Bool0; no new ushort).

## 0.5.19 — 2026-09-27

Protocol **v10**. Batch 24 ship (Dig E): host KeypadSubmit self-Applies door consequences.

### Fixed
- **Host KeypadSubmit sealed doors** — `InteractionSyncService.ApplyKeypad` only set `solved`/`opening` on `Keypad3D` / `ROT_Keypad`. Host poll then Emit → peers ran `LockSyncService.ApplyKeypad3D` / `ApplyRotKeypad` (`openDoor` + `TryUnlockDoors`). Host never self-Applied Emit path → host `ConnectedDoors` / door mesh stayed sealed. Now mirrors puzzle Apply inside `ApplyKeypad`: BeginApply/`openDoor`+`TryUnlockDoors` for newly solved Keypad3D; BeginApply/`onSuccess.Invoke` (peel: `ConnectedDoors.Unlock` + exitEvent/SetActive/dimPOI) + `TryUnlockDoors` for newly solved ROT_Keypad; Emit Keypad PuzzleState after for late joiners. `PEN_Codepad` → `ApplyCodepadConsequences` unchanged. Protocol 10 unchanged.

### Before → After (player)
- **Before:** Client solves a Keypad3D / ROT_Keypad. Peers see the door unlock; host still sees a sealed ConnectedDoors / door mesh and cannot pass.
- **After:** Host ApplyKeypad runs the same openDoor / onSuccess / TryUnlockDoors consequences peers already got from PuzzleState — host door opens with the party.

### Dig notes (Batch 24)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| ApplyKeypad thinner than ApplyKeypad3D | Dig E: ApplyKeypad flags-only (~455–477); Puzzle Apply openDoor+TryUnlockDoors (~157–171); host Emit/poll no self-Apply; ROT onSuccess peel → ConnectedDoors.Unlock | **SHIPPED** |

Protocol stays **10** (no new ushort).

## 0.5.18 — 2026-09-27

Protocol **v10**. Batch 23 ship (Dig C): dead puppet hurtbox stays active on peers.

### Fixed
- **Dead puppet keeps living hurtbox GO** — Peer `Puppet` disables `EnemyController` so host-side `UpdateDataBlock` never runs locally. `ApplyEnemyState` wrote `state`/`HP`/`anim`/`staggerType` but never toggled `hurtbox` / `downedHitbox`. GameAssembly `UpdateDataBlock`: `state==dead` → `hurtbox.SetActive(false)`; `downedHitbox` only when `staggerType` in `{critical,fire}` and not dead. `Hurtbox.OnTriggerEnter` has **no** dead-state gate → peer walks into corpse and takes damage. Now `ApplyEnemyState` (full + off-chunk early path) mirrors those SetActive toggles gated on `!snap.Alive` / `State==dead` and wired `HurtState`. N-peer join dump (`RequestFullSend` → `Apply`) covered. **No** `KillSilent`. Protocol 10 unchanged (`Alive` already on wire).

### Before → After (player)
- **Before:** Host kills an enemy the peer is puppeting. Host corpse is inert; peer corpse still has an active hurtbox child → walking over it damages the peer.
- **After:** Peer apply deactivates the living hurtbox when the snapshot says dead (and only enables `downedHitbox` for critical/fire while alive), matching host UpdateDataBlock — corpse is safe to walk over.

### Dig notes (Batch 23)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| Dead puppet living hurtbox | Dig C: Puppet disables AI; Apply never toggles hurtbox/downedHitbox; UpdateDataBlock + Hurtbox.OnTriggerEnter no dead gate; Alive on wire unread for hitboxes | **SHIPPED** |

Protocol stays **10** (reuse `EnemySnapshotNet.Alive` / `HurtState`; no new ushort).

## Dig — Batch 22 (no 0.5.18) — 2026-09-27

Protocol **v10** / tip `7501bc0` / **0.5.17**. Continuous dig for a **non-revoke** CAN-FIX + missing-coverage invent→prove. **No ship** — plateau.

### Goal
Stretch beyond the ConsumesKey / EnsureInBag strip family (0.5.13–0.5.17). Prefer host-local Apply* skips of a different class, durable story/elevator/join holes, or systems with **zero** Domain/Harmony.

### Dig notes (Batch 22)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| Host-local InteractionRequest twins (Pickup/Storage/Drop/Puzzle/Door/Keypad/EventZone/MultiCondition/Cutscene/Dialoguer) | Keypad Host Prefix `return true` + poll Emit; EventZone/MultiCondition Host Postfix `BroadcastPresentation`; Cutscene Host Broadcast; Dialoguer Host Broadcast + Finalizers on all Start/Continue/End overloads; Storage Host `RequestSend`+Postfix blob; WorldPickup Host TryClaim+BroadcastTriggered+PartyKeyRing.Note | **OK** — no UseItem-class skip of Apply side-effects beyond 0.5.17 |
| FreeDoorController / ConnectedDoorLockController ConsumesKey residual | OnEnable/Awake copy onto IL/CD only (Batch 21 OK); UnlockInteractiveLocks + CD path 0.5.14–0.5.16 | **OK** — not a new hole |
| Elevator boarding N-peer (`NewElevator` / `ElevatorController`) | Dump fields `playerIsInElevator` / `going`; **0 scene/prefab hits** outside `path_id_map.json`; CentralElevator/CallButton/EXC already PuzzleType 23/24/46 | **OK** — dead/unused stubs; live elevators covered |
| `PenroseAirlockNew.unlocked` missing PuzzleType | Only FMOD skip parent-walk in `FmodEmitterSync`; **0 level hits**; airlock remains PEN_Titles personal (wreck↔hole never SceneFollow) | **OK** — unused / intentional local |
| `InventoryHelper` Add/Remove* — **no Harmony** | Cutscene UnityEvents in LOV/MED/RES/PEN + CutsceneManager JSON; **all 10 bindings** are `ArianePhoto` / `AlinaPhoto` only; AlinaPhoto asset `type=0` (AnItemType.None) — photos/docs stay flavor local (AGENTS + prior park) | **OK** — missing patch, but **no Key/Object softlock**; do not party-sync photos |
| Wiki Key Items off PartyKeyRing | Ring = Key/Object only; no new missable unique proven not Key/Object | **OK** / prior |
| HasPeer-without-apply beyond DroppedPickup/StorageTake | UseItem/door/Put party-benefit by design (0.5.12) | **OK** |
| Dialoguer Finalizer gaps | All 4 Start + 2 Continue + End Finalizers present; End always `ClearFlavor` | **OK** |
| MorseReceiver / EideticModule / DarknessInteraction / AirlockInside / DET_ServiceHatch | path_id_map or Pregame/UI only; no durable co-op softlock proven | **OK** — not Batch 22 ship |
| SceneFollow RestorePlay / Adler EV / KillSilent / Mural late-join / Alarm latch / ending-flag merge / MeatBlocker non-Death / Falke empty-slot | parked list; no **new** hard proof | **park** |

### Missing-coverage list for Batch 23+ (not proven softlock)
- `InventoryHelper` string API — watch if future content passes Key/Object names (today photos only).
- `NewElevator` / `ElevatorController` / `PenroseAirlockNew` — re-check if a DLC/patch reintroduces scene refs.
- CutsceneHelper `HoleDrop` / `MoveElsterTo` / `Cheat` — presentation-local; soak if N=3–4 desync mid-cinematic without SceneFollow.
- Residual ConsumesKey twins exhausted for FreeDoor/CDLC/IL/CD/UseItem host-local.

### Plateau
Revoke/strip class exhausted for known entry points. No thin non-revoke CAN-FIX with hard code proof this dig. Version stays **0.5.17**; protocol **10**. Dig peers (story/combat/join/Domains) may still surface Batch 23 candidates.

## 0.5.17 — 2026-09-27

Protocol **v10**. Continuous Batch 21 dig → ship (host-local UseItem ConsumesKey ring revoke).

### Fixed
- **Host-local UseItem / UseItemMulti ConsumesKey PartyKeyRing revoke** — `UseItemInteractionPatch.OnLocalUnlocked` only `SendInteractionRequest` when `NetGate.Client`. Host unlocks via native Dialoguer (`onMessageEvent` RemoveItem + `dialogueOver`) and returned after `_sent.Add` with **no** `ApplyUseItem` → no `UnlockInteractiveLocks` / `RevokeConsumed`. Puzzle poll still Emited `unlocked`, so N=3–4 peers kept ring + `EnsureInBag` bag ghosts / `InLocalBag` after the host spent a unique. Same hole for host `UseItemMulti.ready` (Prefix allows Host, never `ApplyUseItemMulti`). Now host calls `HostRevokeIfConsumed` / `HostRevokeUseItemMulti` (same ConsumesKey detect + CraftRevokeSentinel fan-out as 0.5.14–0.5.16). `ApplyUseItem` early-out on already-unlocked also `TryRevokeUseItemKey` (idempotent).

### Before → After (player)
- **Before:** Host uses AirlockKey (or other ConsumesKey unique) on a door/lock. Host bag loses the key (native RemoveItem), door unlocks for the party, but ring never drops → Peer2–Peer4 keep `EnsureInBag` mirrors and can still UseItem a consumed unique.
- **After:** Host-local unlock runs the same `RevokeConsumed` path as client→host `ApplyUseItem`; sentinel strips bag mirrors on every peer (protocol 10, no new message).

### Dig notes (Batch 21)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| Host UseItem ConsumesKey never RevokeConsumed | `OnLocalUnlocked`: `_sent.Add` then `if (!NetGate.Client) return` — no ApplyUseItem; `RemoveItem` unpatched; Puzzle Emit is unlocked-bool only | **SHIPPED** |
| Host UseItemMulti.ready same hole | Prefix `if (NetGate.Host) return true` runs native ready; no consume loop | **SHIPPED** (Postfix) |
| FreeDoorController.ConsumesKey | Awake @ RVA 0x899520 copies key→IL+0x38, ConsumesKey→IL+0x48; UnlockInteractiveLocks already reads IL | **OK** — setup helper |
| ConnectedDoorLockController.ConsumesKey | OnEnable-only setup onto CD/ILS; CD covered 0.5.16 | **OK** |
| ApplyUseItem already-unlocked skip revoke | `if (u.unlocked && !u.repeatable) return true` before UnlockInteractiveLocks — host-first then client request skipped revoke | **SHIPPED** (early-out TryRevoke) |
| Remaining EnsureInBag outside craft/use/drop/CD | death / storage / Offer-after-floor covered prior | **OK** |
| InteractionRequest apply-when-gone beyond DroppedPickup/StorageTake | UseItem/door/Put party-benefit (0.5.12) | **OK** |
| Sticky Dialoguer / EventZone / airlock / Boss / DoorNative unload / join dump / Radio FMOD / wiki off-ring / Domains HasPeer-without-apply | no new hard proof this dig | **park** |
| SceneFollow RestorePlay / Adler EV / KillSilent / Mural Blocker late-join / Alarm / ending merge / MeatBlocker / Falke empty-slot | parked list | **park** |

Protocol stays **10** (reuse CraftRevokeSentinel / `RevokeConsumed`; no new ushort).

## 0.5.16 — 2026-09-27

Protocol **v10**. Continuous Batch 20 dig → ship (ConnectedDoors.ConsumesKey ring revoke).

### Fixed
- **ConnectedDoors.ConsumesKey PartyKeyRing revoke** — `UnlockInteractiveLocks` (host `ApplyUseItem` / `ApplyUseItemMulti`) now also reads parent `ConnectedDoors.ConsumesKey` + matching-key `InteractiveLockSingle` under that door. Native `ConnectedDoors.Unlock` (via `DoorNative.ApplyConnectedDoors`) only copies ConsumesKey onto AutoTraverseDoor ILS siblings and never `RemoveItem`; UseItem's GetComponentInParent/Children cannot see those siblings → door unlocked while ring + EnsureInBag bag ghosts stayed (same class as InteractiveLock.ConsumesKey 0.5.14). Existing `RevokeConsumed` / CraftRevokeSentinel fan-out strips mirrors (protocol 10, no new message).

### Before → After (player)
- **Before:** Host (or peer) uses a unique on a ConnectedDoors that ConsumesKey. Door unlocks for the party, but ring never drops → N=3–4 peers keep `EnsureInBag` bag ghosts / `InLocalBag` / can still UseItem a consumed unique.
- **After:** Host ApplyUseItem detects CD/ILS ConsumesKey (key-enum matched), `RevokeConsumed` removes ring + strips bag mirrors on every peer.

### Dig notes (Batch 20)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| ConnectedDoors.ConsumesKey missed by UnlockInteractiveLocks | GameAssembly `Unlock` @ RVA 0x2D24C0: `locked=false` + copy ConsumesKey/key onto ILS; no RemoveItem. Assets: 42 CD with ConsumesKey+key. ApplyUseItem TryUnlockDoors→Unlock then only IL/ILS on UseItem tree | **SHIPPED** |
| Remaining EnsureInBag strip: death / storage TAKE / Offer-after-floor | Death skip-floor intentional; storage put/take party-share (0.5.15); floor TAKE Offer after Detach strip correct | **OK** |
| InteractionRequest apply-when-gone beyond DroppedPickup/StorageTake | UseItem/door/Put party-benefit (0.5.12) | **OK** |
| Sticky Dialoguer/EventZone/airlock beyond SceneFollow | no new durable field | **park** |
| Boss/PuzzleState / DoorNative unload / elevators / Radio FMOD / join dump N=3–4 / wiki Key Items off ring / Domains HasPeer-without-apply | no new hard proof this dig | **park** |
| SceneFollow RestorePlay / Adler EV / KillSilent / Mural Blocker / Alarm / ending merge / MeatBlocker / Falke empty-slot | parked list | **park** |

Protocol stays **10** (reuse CraftRevokeSentinel fan-out).

## 0.5.15 — 2026-09-27

Protocol **v10**. Continuous Batch 19 dig → ship (G-drop EnsureInBag bag-mirror strip).

### Fixed
- **G-drop PartyKeyRing bag-mirror strip** — `DetachDroppedKey` (local G-drop, remote `DropItemSpawn`, join dump) now `Remove` + `StripBagMirrors` so peers who `EnsureInBag`-mirrored a unique clear their bag ghosts while the unique sits on the floor. Same class as craft/UseItem 0.5.14; fan-out is existing `DropItemSpawn` BroadcastRaw (no new message / protocol stays 10).

### Before → After (player)
- **Before:** Host (or peer) G-drops AirlockKey. Ring clears, floor prop appears, but Peer2/Peer3 who `EnsureInBag`-mirrored the key still hold bag ghosts → `InLocalBag` / UseItem while the key is claimable on the floor → dual-claim softlock. Claim by Peer2 then leaves Peer3 with a leftover ghost until craft/consume revoke.
- **After:** Every peer that sees the drop spawn strips those bag mirrors. Floor prop is the sole holder until TAKE Notes the ring again.

### Dig notes (Batch 19)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| G-drop unique while N=3–4 peers hold EnsureInBag mirrors | `DetachDroppedKey` only `PartyKeyRing.Remove` + Broadcast; snapshot `ApplyMessage` never `RemoveItem`; `InLocalBag` stays true on non-droppers (craft 0.5.14 comment: “Snapshot Broadcast alone leaves peer bag ghosts”) | **SHIPPED** |
| Non-Key craft ingredients / multi-combine / offer-before-revoke | Batch 18 table | **OK** — unchanged |
| Storage put unique while peers hold mirrors | Put does not `Remove` ring (box + ring both shared); UseItem via ring while boxed is intentional party share | **OK** — not dual floor claim |
| Death-bag unique on ring | Already skip-floor + clear local mirror only; ring stays (0.5.8+) | **OK** |
| InteractionRequest apply-when-gone beyond DroppedPickup/StorageTake | UseItem/door/Put party-benefit design (0.5.12) | **OK** |
| Sticky gameState/dialoguer/SceneFollow new durable field | no new field found beyond parked SceneFollow RestorePlay | **park** |
| Boss / PuzzleState / DoorNative / elevator / airlock / Radio FMOD / dump mid-handshake / wiki Key Items off ring / Domains HasPeer-without-apply | no new hard proof this dig | **park** |
| SceneFollow RestorePlay / Adler EV / KillSilent / Mural Blocker late-join / Alarm / ending merge / MeatBlocker / Falke empty-slot | parked list | **park** |

Protocol stays **10** (reuse drop spawn + `StripBagMirrors`; no new ushort sentinel).

## 0.5.14 — 2026-09-27

Protocol **v10**. Continuous Batch 18 dig → ship (craft/UseItem EnsureInBag mirror strip).

### Fixed
- **Craft / UseItem PartyKeyRing bag-mirror strip** — `CraftRevokeSentinel` now applies on **every** peer (not only host): `Remove` + `StripBagMirrors` clears `EnsureInBag` bag ghosts. Host fans the sentinel out to clients, then Broadcasts the ring snapshot. `ConsumeCraftIngredients` strips locally before send; `ConsumeKey` (UseItem ConsumesKey) uses new `RevokeConsumed` so the same fan-out covers non-sender peers who mirrored the unique into their bag.

### Before → After (player)
- **Before:** Host (or peer) combines Tape + BrokenKey → AirlockKey. Ring ingredients drop (0.5.13), but a peer who `EnsureInBag`-mirrored Tape/BrokenKey still holds them in inventory → `InLocalBag` ghosts → can still UseItem / softlock with consumed uniques. Same hole when UseItem ConsumesKey while another peer holds an EnsureInBag mirror.
- **After:** Craft revoke and UseItem consume strip those bag mirrors on all peers via host-fanned `CraftRevokeSentinel` (protocol 10, same ushort[] prefix).

### Dig notes (Batch 18)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| Host craft while peer holds ingredient in hand | `EnsureInBag` AddItem mirrors; 0.5.13 only `PartyKeyRing.Remove` + snapshot Broadcast; client `ApplyMessage` never `RemoveItem`; `InLocalBag` stays true | **SHIPPED** |
| UseItem ConsumesKey + peer EnsureInBag mirror | `ConsumeKey` stripped host bag only; `consume:` ack only to sender; other peers kept mirrors | **SHIPPED** (same sentinel fan-out) |
| Non-Key/Object craft ingredients | `CollectCraftRevoke` / `IsKeyOrObject` skip; ammo/tools never on ring | **OK** — not a ring hole |
| Multi-step recipes | each `combine` Postfix fires revoke per step | **OK** |
| Client craft race / Offer before Revoke | AddItem Postfix during combine Notes result before combine Postfix revoke; ReliableOrdered; host offer-then-revoke | **OK** |
| Interaction orphans beyond DroppedPickup/StorageTake | UseItem/door unlock when peer-gone still party-benefit; StoragePut keeps item | **OK** — prior design |
| Dialoguer late-join / Elevator / EventZone other-room / Boss ForceFull / death-bag vs ring / wiki missables / PathID | prior coverage or parked soak | **park** — no new hard proof |
| SceneFollow RestorePlay / Adler EV / KillSilent / Mural Blocker late-join / Alarm / ending merge / MeatBlocker / Falke empty-slot | parked list | **park** — no new durable field |

Protocol stays **10** (reuse `PartyKeyRing` `0xFFFF` revoke; host fan-out is the same message type).

# Changelog

## 0.5.13 — 2026-09-27

Protocol **v10**. Continuous Batch 17 dig → ship (craft Remove-on-combine).

### Fixed
- **Craft PartyKeyRing Remove-on-combine** — successful `CombineRecipes.combine` now drops Key/Object **ingredients** from the party ring (host Broadcast; client sends `CraftRevokeSentinel`/`0xFFFF` + enums on existing `PartyKeyRing` message so host drops + Broadcast). `NoteCraftedKey` still Offers the **result** only. Example: Tape + BrokenKey → AirlockKey no longer leaves Tape/BrokenKey as phantom `hasItem` OR-true for peers.

### Before → After (player)
- **Before:** Host (or peer) combines Tape + BrokenKey into AirlockKey. Result appears on the party ring, but Tape and BrokenKey stay on the ring. Peers still `hasItem` those ghosts → EnsureInBag can re-materialize consumed uniques → softlock / dual-claim risk.
- **After:** Successful combine removes Key/Object ingredients from the ring for everyone (host-authoritative Broadcast). Result still shared via Offer as before.

### Dig notes (Batch 17)
| Candidate | Prove | Verdict |
|-----------|-------|---------|
| Craft Remove-on-combine | `NoteCraftedKey` Offers result only; `PartyKeyRing.Remove` only ConsumeKey / death-bag / drop; no Harmony on `combine`; Tape/BrokenKey are Object (type 6), AirlockKey Key (5) — all `IsKeyOrObject` | **SHIPPED** |
| SceneFollow RestorePlay | `SceneFollowService.Apply` → `AsyncLoader.LoadLevel` only; no `RestorePlay` / inventory close | **park** — soak mid-inventory freeze |
| Adler EV doors | UnityEvent Open/CloseDoors only; not PuzzleState | **park** — no durable field for snap-on-join |
| Enemy KillSilent / Mural Blocker late-join / Alarm / ending merge / MeatBlocker / Falke empty-slot | prior soak list | **park** — no new hard proof |

Protocol stays **10** (reuse `PartyKeyRing` ushort[] with leading `0xFFFF` revoke; not a new message type).


## 0.5.12 — 2026-09-26

Protocol **v10**. Continuous Batch 12 dig (scenario-first).

### Fixed
- **DroppedPickup peer-gone** — host rejects `InteractionKind.DroppedPickup` when claimer `!HasPeer` (mirrors WorldPickup 0.5.11). Prevents `TryClaimDropped` despawn when grant/ack cannot land → floor prop stays for remaining peers.
- **StorageTake peer-gone** — host rejects `StorageTake` when taker `!HasPeer` before `unboxItem`. Prevents shared-box stock loss with nobody receiving `grant:` ack. **StoragePut still applies** when putter is gone (boxing preserves the item for the party).

### Dig report (scenarios → verdict) — parked Batch 13 unless proven CAN-fix

| Rank | Scenario | Prove path | Verdict |
|------|----------|------------|---------|
| CRITICAL→FIXED | Peer claims dropped floor item then disconnects before host `TryClaimDropped` | `InteractionSyncService.HandleRequest` DroppedPickup + `DroppedItemNetHandlers.TryClaimDropped` despawn w/o remote AddItem | Was item loss; now reject |
| CRITICAL→FIXED | Peer `StorageTake` then disconnect before `grant:` ack | `ApplyStorage` unbox + `InteractionNetHandlers.ApplyBagAck` | Was box loss; now reject |
| — | Peer `StoragePut` then disconnect before `consume:` ack | same ApplyStorage put | **OK by design** — box keeps item; putter bag leaves session |
| — | Peer UseItem/door unlock then gone | `ApplyUseItem` unlocks + PuzzleState snap | OK — party benefits; consume ack irrelevant |
| MED | Host SceneFollow while client in `inventory`/`menu`/`paused` | `SceneFollowService.Apply` → `AsyncLoader.LoadLevel`; `DroppedItemRegistry` ClearInspectLocks skips `inventory` | Likely scene-unload clears UI; no proven sticky. Defer until dual-box freeze repro |
| MED | Enemy puppet mid-death / mid-hurt | `EnemySyncService.ApplyEnemyState` snaps state+AnimHash+HP; no native `Die`/`deadFire` | Anim snap usually enough; no clear zombie without soak |
| — | One peer triggers cutscene; other mid-walk other-room | `LocalInspect.InLocalRoom` skip Start/Invoke; `CutsceneCompleted` PuzzleState | Intentional; flags via Story dump |
| — | One peer triggers EventZone; other other-room | `triggered=true` w/o Invoke; doors/puzzles via poll | Intentional (AGENTS) |
| — | Peer opens book/note (local UI) while other needs that read for a gate | `SProgressPatches.IsInspectOrigin` → `InspectFlag` syncs SetBool/Int/… during book/eventScreen | Flags sync; UI stays local (AGENTS). Not softlock |
| — | Books/notes as softlock vs PartyKeyRing | wiki Key Items (80) are Key/Object → `PartyKeyRing.IsKeyOrObject`; Death/Sacrifice already 0.5.8 | No new unique softlock proven |
| — | Penrose wreck↔hole / cryo / airlock personal | `AirlockCinematic` + CryoSyncService | Covered; intentional personal loads |
| — | Mid-dialogue + mid-cutscene + mid-pickup race | Dialoguer Finalizers; RememberStart/Skip; keyed `_awaitingDrops` | Covered 0.5.x |
| — | Early cutscene flag breaks later co-op door | StoryCommit dump + PuzzleState; other-room skips presentation only | No clear hole without named flag |
| — | N-peer (3–4) drop/storage double-claim | keyed `_awaitingDrops`; host serial HandleRequest | Covered |
| — | Alarm / ending-flag merge / MeatBlocker tarot siblings / Harmony sticky / photo flavor / ladder-continuum-nowhere | prior parked | unchanged |
| — | Weak Domains: camera/aim/prompts local; heal=personal Injector; SaveRoomEvent synced; elevators synced; FakeWall/Continuum/Gestalt not typed | DECOMPILE_COVERAGE intentional locals | no invent sync |

### Parked (Batch 13)
- Alarm `GlobalAlertStatus.triggerAlarm` / `EnemyManagerState` client latch-emit — MelonLoader `CallerCount(0)`.
- Ending-flag peer merge — host-authoritative `END_Manager` by design.
- MeatBlocker tarot siblings — only Death seals NG+ `KeyOfSacrifice`.
- Throw-mid-Harmony sticky beyond Dialoguer Finalizers — Cutscene Remember* intentional; Keypad `_sent` clears on scene/StopNetwork.
- Photo/document/eidetic unique story gates — flavor local; ArianePhotoCode / Microfiche PuzzleState.
- Ladder / continuum / nowhere / fake wall co-op — traverse local (proxy SFX only).
- SceneFollow mid-inventory/menu sticky — needs dual-box freeze repro before CloseInventory yank.
- Enemy mid-death native Die/deadFire beyond AnimHash snap — needs soak zombie repro.

### Batch 16 dig (no-ship) — 2026-09-26 — parked Batch 17

Protocol **v10** / `e5d5754` / 0.5.12. PathID dig on AssetStudio `DelayedCutsceneEvent*` + `CutsceneCut*` UnityEvent `SetActive`/`set_enabled` vs AssetRipper scene YAML (same-file `!u!1 &PathID` names). Tool: `04_AssetRipper_UnityProject/AuxiliaryFiles/path_id_map.json` has **no GameObjects** (Mesh/MonoScript/Texture only) — resolve PathIDs via scene YAML fileIDs (AssetStudio `m_PathID` == ripper `&fid`).

#### Primary — cutscene SetActive gates

| Target | PathID / scene | Owner event | Co-op scenario | Verdict |
|--------|----------------|-------------|----------------|---------|
| `Door Connection (31)` (`ConnectedDoors`, unlocked) | 32250 / `MED_Medical` | `CutsceneCut@Enter` onCutStart `SetActive(false)`; `CutsceneManager@MED Intro` onCutsceneEnd `SetActive(true)` (`unskippable:1`, scenes=[Enter]) | Host plays MED intro; client other-room or late-join | **harmless** — cinematic temp disable then restore; default `m_IsActive=1`; skip cannot leave disabled; PuzzleState tracks `locked` not GO active (not needed here) |
| `ButterflyBoxEvent` (`Interaction` + `EventScreenInteraction`) | 1226 / `DET_Detention` | `CutsceneCut@DET_Radio_1 Meat` + RadioStation* UnityEvents `SetActive(false)` | Peer misses meat cutscene; still sees butterfly EventScreen | **intentional / harmless** — EventScreen flavor local (`DECOMPILE_COVERAGE`); not a story collider gate |
| Presentation-only (Ambience_*, LocalSpace, Photo, Type, Muzzle, BloodSpray, Chunk/Cell stream, Ellie_Repair, Crippled/Armored, IsaRemains, CombatMusic, ImposterFog `set_enabled`, …) | 40/47 named cutscene calls | DelayedCutsceneEvent / CutsceneCut | Visual/audio/fog | **harmless** |

#### Primary — similar UnityEvent SetActive (non-cutscene) gate-like

| Target | Owner | Sync today | Verdict |
|--------|-------|------------|---------|
| `Blocker Entry` (BoxCollider L22) | `MuralLogic` / `ROT_Mural.onSolved` → `SetActive(false)` | `ApplyMural` sets `finished` + `useRing()` only when `MutateWorld`; FullRefresh skips `useRing` | **soak** — live peers get blocker off via `useRing`; late-join may keep collider until soak proves softlock. Do **not** invent GO-active PuzzleState without repro |
| `E Door Spot Blocked` / `E Door Spot` | `PEN_Reaktor` / Reaktor Logic | `SnapReaktor` unlocks `doorLock` ConnectedDoors + plates; spots are presentation SetActive | **harmless / covered** — real gate is `doorLock` |
| `DET_DoorS`, `DoorLockEvent`, `Event`/`EventInter` (TreeLock/Elemental/Keypad) | Memo / Interaction / puzzle logics | PuzzleType snaps + door flags | **intentional** — already PuzzleState |

No cutscene SetActive → MeatBlocker / ladder / elevator / story seal **without** existing PuzzleState path proven. No CAN-FIX softlock this dig.

#### Secondary (document only)

**Craft `PartyKeyRing.Remove` gap**
- `NoteCraftedKey` (`ItemPickupPatches`) notes craft **result** on `InventoryManager.AddItem` / overload Postfix (`OfferToHost` if Key/Object).
- `CombineRecipes.combine` has **no** Harmony patch; ingredients (e.g. Tape + BrokenKey → AirlockKey) leave the bag via native `RemoveItem` but **never** `PartyKeyRing.Remove`.
- `PartyKeyRing.Remove` call sites only: `InteractionSyncService.ConsumeKey` (UseItem consume), `NetworkDamageSystem` (death-bag), `DroppedItemNetHandlers.DetachDroppedKey` / `ConsumeDropped`.
- Phantom ring entries for consumed ingredients are stale `hasItem` OR-true — soak before Remove-on-combine.

**Wiki missable unique vs ring**
- Ring rule: `AnItemType.Key` / `Object` only (`IsKeyOrObject`).
- Wiki: Gold Key (cassette cutscene AddItem → NoteCraftedKey), elemental keys, Blank Key, ending Love/Eternity/Sacrifice, KeyOfSacrifice (0.5.8 hold). Docs/photos/eidetic remain flavor local (prior parked).
- No new missable unique **not** Key/Object proven as softlock.

**HasPeer-class InteractionKind orphans**
- Already gated (0.5.11–0.5.12): WorldPickup claim, `DroppedPickup`, `StorageTake`.
- `StoragePut` — apply when gone by design (box keeps item).
- `UseItem` / `UseItemMulti` — apply when gone unlocks world for party; reject would softlock door. Not orphan-loss class.
- Presentation kinds (Cutscene*/Dialogue*/Book*/EventScreen*/Gunshot/InspectFlag) + puzzle emits (Keypad/MultiCondition/EventZone) — no shared-stock orphan pattern.
- **No new HasPeer-class CAN-FIX** → no 0.5.13 ship.

#### Parked (Batch 17)
- Adler EV / SceneFollow RestorePlay / KillSilent — prior soak gates (mission).
- Craft `PartyKeyRing.Remove` on `CombineRecipes.combine` ingredients — document-only until soak.
- `ROT_Mural` FullRefresh `Blocker Entry` GO active (useRing skipped) — soak late-join wall.
- SceneFollow mid-inventory / Alarm / MeatBlocker tarot siblings / photo flavor / ladder-continuum — unchanged prior park.
- Cutscene GO-active inventory (Door Connection pattern) only if dual-box leaves a link permanently disabled.

Sources: `03_AssetStudio_export/MonoBehaviour/DelayedCutsceneEvent*.json`, `CutsceneCut*.json`; `04_AssetRipper_UnityProject/ExportedProject/Assets/Scenes/Levels/*.unity`; `path_id_map.json`; `Domains/Puzzles/Machines/ChapterMachineSyncService.cs` ApplyMural/SnapReaktor; `Domains/Story/InteractionSyncService.cs`; `Domains/Inventory/PartyKeyRing.cs`; `Domains/Pickups/Patches/ItemPickupPatches.cs` NoteCraftedKey; signalis.fandom / wiki.gg Gold Key / Key Items.

## 0.5.11 — 2026-09-26

Protocol **v10**. Continuous Batch 11 dig.

### Fixed
- **Kolibri / ADLR Hold Postfix** — client `Update` recomputes intensity/progress/frequency from local radio after Prefix; Postfix re-applies the last host PuzzleState snap so phase cannot drift (0.5.9 Prefix alone lost the race).
- **Falke presentation mid-apply** — `SnapFalkePresentation` takes stage/corrupt from the BossState snap (not re-read fields), reclamps stage, rewrites field mirror, and calls `SetBodySpearStates` so stage regress / corrupt toggle cannot leave arenas/shields/meshes/BodySpears on a stale combo.
- **World pickup peer-gone mid-claim** — host ignores claims from disconnected peers; non-Key/Object orphan claims release + restore prop + broadcast untriggered on `NotePeerGone` (grant softlock when claimer drops after TryClaimOnHost). Client `ApplyHide` now restores on `Triggered=false` (was hide-only).

### Parked (Batch 12)
- Alarm `GlobalAlertStatus.triggerAlarm` / `EnemyManagerState` client latch-emit — MelonLoader `CallerCount(0)`, no UnityEvent bindings; alert stays host-poll only.
- Ending-flag peer merge (client `healedTime`/`doors`/`memoryTime` → host) — host-authoritative `END_Manager` by design; no safe merge without new wire.
- MeatBlocker tarot siblings (Lovers/Moon/Sun/Star/Tower) — assets `required=1/2/6`; only Death seals NG+ `KeyOfSacrifice` (wiki Artifact); no parallel softlock pair proven.
- Throw-mid-Harmony sticky beyond Dialoguer Finalizers — Cutscene `RememberStart`/`RememberSkip` intentional; Keypad `_sent` clears on scene/StopNetwork via `EventZonePatch`.
- Photo/document/eidetic unique story gates — flavor local by design; `ArianePhotoCode` / Microfiche already PuzzleState.
- Ladder / continuum / nowhere / fake wall co-op — traverse local by design (proxy SFX only).
- Domains code smell (double Broadcast / WorldId 0 dumps) — PartyKeyRing Broadcast is if/else once; KeyGrid/Ariane/RadioManager WorldId 0 are intentional statics.

## 0.5.10 — 2026-09-26

Protocol **v10**. Continuous Batch 10 dig.

### Fixed
- **Falke arena / invuln / corrupt mesh** — client `ApplyEND` now snaps `Arenas[i]` (`i == stage`), `HeadSpears[i]` (`i < stage`), `FloatShields` (`stage >= 3`, wiki phase 4), `FloatShields2` (`stage >= 5`, phase 6), and `CorruptedMesh`/`NormalMesh` from synced `corrupt`. Decompile: `END_Boss.Start`, `<Stabbed>d__129.MoveNext` (`stage++` then presentation), `Update` mesh gate. Stage was already on BossState wire since 0.5.x; disabled AI meant arenas/shields never followed.
- **Boss AI-disable coroutines** — `DisableLocalAI` now `StopAllCoroutines` before `enabled = false` on END/Chimera/Mynah so mid-fight `Bossfight`/`Stabbed`/`Airstrike` cannot keep running on the client (Kolibri/Adler stay enabled + Hold patches).

### Parked (Batch 11)
- Alarm `GlobalAlertStatus.triggerAlarm` / `EnemyManagerState` client latch-emit — MelonLoader `CallerCount(0)`, no UnityEvent bindings; alert stays host-poll only.
- Ending-flag peer merge (client `healedTime`/`doors`/`memoryTime` → host) — host-authoritative `END_Manager` by design; no safe merge without new wire.
- MeatBlocker tarot siblings (Lovers/Moon/Sun/Star/Tower) — assets `required=1/2/6`; only Death seals NG+ `KeyOfSacrifice` (wiki Artifact); no parallel softlock pair proven.
- Throw-mid-Harmony sticky beyond Dialoguer Finalizers — Cutscene `RememberStart`/`RememberSkip` intentional; Keypad `_sent` clears on scene/StopNetwork via `EventZonePatch`.
- Net reconnect mid-puzzle/storage/dialogue — ForceFull + sticky Reset chain covered 0.5.1–0.5.9; no new hole proven this dig.
- Photo/document/eidetic unique story gates — flavor local by design; `ArianePhotoCode` / Microfiche already PuzzleState.
- Ladder / continuum / nowhere / fake wall co-op — traverse local by design (proxy SFX only).
- Domains code smell (double Broadcast / WorldId 0 dumps) — PartyKeyRing Broadcast is if/else once; KeyGrid/Ariane/RadioManager WorldId 0 are intentional statics.

## 0.5.9 — 2026-09-26

Protocol **v10**. Continuous Batch 9 dig.

### Fixed
- **Kolibri / ADLR host-auth** — client `KolibriManager`/`BOS_Adler` Update no longer clobbers host PuzzleState intensity/progress/frequency/dead (glitch presentation still runs). END/Chimera/Mynah remain fully AI-disabled.
- **World pickup bag-full softlock** — no Prefix reservation / claim wire when the 6-slot bag has no room for non-Key/Object; host Prefix reservation released if native nospace/cancel; Postfix/NoteTaken gate Broadcast on successful claim. Key/Object still claim onto the party ring when full.

### Parked (Batch 10)
- Alarm `GlobalAlertStatus.triggerAlarm` / `EnemyManagerState` client latch-emit — MelonLoader `CallerCount(0)`, no UnityEvent `m_MethodName: triggerAlarm` in exported scenes/prefabs; alert stays host-poll only (unlike radio `moduleInstalled`).
- Ending-flag peer merge (client `healedTime`/`doors`/`memoryTime` → host) — host-authoritative END_Manager by design; no safe merge without new wire.
- MeatBlocker tarot siblings (Lovers/Moon/Sun/Star/Tower) — only Death seals NG+ KeyOfSacrifice (wiki); others required=1/2/6 with no parallel Artifact softlock proven.
- Falke arena-door / invuln beyond 0.5.7 join-transient BossState cache; ammo/heal/plate/thermite remain personal inventory; corpse loot no vanilla drop path; elevator/airlock/continuum no new hole beyond current snaps.

## 0.5.8 — 2026-09-26

Protocol **v10**. Continuous Batch 8 dig.

### Fixed
- **Death tarot / Key of Sacrifice softlock (NG+ Artifact)** — scene `ROT_MeatBlocker` ID `Death` seals the bookstore wing that holds `KeyOfSacrifice` (under `NGP_only`). Hold TarotDeath world take/claim + Death MeatBlocker emit/apply while a live unclaimed KeyOfSacrifice still exists; first playthrough (NGP off) unchanged. Wiki: get Sacrifice before Death.
- **MeatBlocker snap** — seal direction now activates Blockers / deactivates UnBlockers / locks ConnectedDoors (was unblock-only).
- **Host drop claim** — removed `_awaitingDrops.Clear()` on host-local FinishDroppedNative (host never stages awaiting-ack; Clear was a latent wipe).

### Parked (Batch 9)
- Alarm `GlobalAlertStatus` / `EnemyManagerState` client latch-emit — still host-only; no proven client raise path (unlike radio moduleInstalled).
- Broader adversarial soak: chapter load / Penrose / Falke / Kolibri / ADLR / inventory overflow / ammo-heal / corpse / elevator-airlock / ending-flag merge / N-peer late-join boss — no new clear CAN-fix beyond 0.5.7 transient cache.


## 0.5.1 — 2026-09-20

Protocol **v10**. Domains architecture + decompile coverage + diagnosis-ready dual-box soak. Dual-instance playtest still required before treating behavior as proven.

### Added
- **Domains layout** — `Bootstrap/`, `Networking/{Dispatch,Messages}`, `Domains/{Doors,Enemies,Bosses,Story,Scene,Audio,Pickups,Inventory,Players,Combat,Puzzles,Session}/`. Symptom→path map: `Domains/README.md`.
- Protocol **10** puzzles: GunCase / AraNest / LAB_RifleQuest / LOV_Microfiche; client-emit reverse arrows for SwingDoor / DoorwaySimple / StorageBox / MED_KeyGrid / ArianePhotoCode (+ protocol 9 residency/locks already in tree).
- Diagnosis: boot banner (host+client MelonLoader log paths, prefs, grep tags, Hitch glossary); AGENTS.md **Diagnosis** section; HitchTrace Cost tags `enemy` / `boss` / `pickup` (plus `puzzle` / `weaponClone`).
- Hard rule: **no park / no defer** (`.cursor/rules/no-park-no-defer.mdc`). Coverage ledger: `docs/DECOMPILE_COVERAGE.md`.

### Changed
- Product version **0.5.1** (`PluginInfo` / AssemblyInfo / README / Domains map). Protocol stays **10**.
- `LanNetworkManager` slimmed: HandlerRegistry + PublicApi + Dispatch; domain Send/Handle on NetHandlers.
- `PuzzleSyncService` coordinator + family SyncServices; InteractionPatches peeled into Domains patches; DroppedItemManager façade over Registry/Spawner.
- `VerboseLogging` MelonPreferences: OFF unless diagnosing; set on **both** installs.
- Hot-path: `WorldLookup` scene caches; recycled tick lists; FMOD HostEmit dedupe; join FullRefresh door/lock flag snaps; remount `HoldIfProgressed`.

### Fixed
- Join/remount door unlocks; EmitProgressed TryRead (no zeroed Reaktor/Pump Ints); CentralElevator client-emit; Cryo hierarchy-only pattern disable; MultiKeyLock poll without `checkLock`; KeyGrid/Ariane no double-emit. Detail under 0.5.0-dev notes below.

## 0.5.0-dev — 2026-09-20

Protocol **v10**. PuzzleType 73–76 (GunCase / AraNest / LAB_RifleQuest / LOV_Microfiche) + client-emit reverse arrows for SwingDoor / DoorwaySimple / StorageBox / MED_KeyGrid / ArianePhotoCode.

### Added (protocol 10)
- `GunCase` — Bool0 opened (inter disabled / pickup enabled); Magpie snap: disable inter/openBox, enable pickup, lid localEuler Y=-115 (Open coroutine), RevealPickups.
- `AraNest` — Bool0 triggered / Bool1 activated / Bool2 dead; Apply `TriggerTrap` once + dead `anim_LoadDead` on Nest/Ara.
- `LAB_RifleQuest` — Bool0 awake / Bool1 gone / Bool2 rifle; snap Isa/Rifle/FakeRifle/ObsHolder/UseItemHolder + `anim_Done` when awake/gone.
- `LOV_Microfiche` — Bool0 hasFiche / Bool1 IsaVisited / Bool2 IsaGone; snap Isa/IsaNote/IsaCutscene (+ book/ItemInter PersistentGameObject); **not** BookScreen UI.
- Client emit: SwingDoor, DoorwaySimple, StorageBox, MED_KeyGrid, ArianePhotoCode (plus the four new types).

### Added (protocol 9 puzzles)
- `RES_MusicBox` / `RES_LibraryPC` / `RES_Paternoster` — Magpie-style snaps; client emit + host relay.
- `MED_KeyGrid.solved` + `ArianePhotoCode.code` — WorldId-0 host globals (RadioManager pattern).
- `DET_ServiceLock_Key` / `SafeDoorSmall` / `MultiKeyLock` (keys→Int0 bits) / `OpenableDrawer`.
- `CentralElevator.targetFloor` → Int2; EvidenceLocker/FloodControls/RES_Power bool arrays packed into Ints; PEN_Reaktor extras; `ROT_Mural` up to 8 moons in Int0–Int3.
- `DET_RadioCodeLock` keypad.solved → Bool0 + TryUnlockDoors; `EXC_Elevator` startRide/stopInstant + mover Y; `SaveRoomEvent` disables eventInter; `DialoguePlayedOnce` skips LocalInspect flavor.
- Boss: `END_Boss` Hp/Corrupt; Kolibri frequency/radioIntensity; Adler progress (`PuzzleStateEntry.Float1`).
- Story: END_Manager NPC/healedTime/segments/memoryTime/doors on StoryCommit; client `CalculatePlaystyle` gated.
- FMOD: `PlayOneShot(Guid)`, `PlayOneShotAttached` string/Guid, `fmod.PlayOneShot`.
- Scene: `SceneManager.LoadScene` string/int gated (AirlockDoorLoadZone); PenroseAirlock already on AsyncLoader(int).
- Hard rule: **no park / no defer** (`.cursor/rules/no-park-no-defer.mdc` + AGENTS).

### Changed
- Product version `0.5.0-dev`; `ProtocolVersion` = **10**.
- Layout: `Bootstrap/`, `Networking/{Dispatch,Messages}`, `Domains/{Doors,Enemies,Bosses,Story,Scene,Audio,Pickups,Inventory,Players,Combat,Puzzles,Session}/`. Namespaces kept stable.
- `LanNetworkManager` slimmed (~2.1k → ~600 LOC): HandlerRegistry + PublicApi + Dispatch; domain Send/Handle on Door/Avatar/Enemy/Boss/Fmod/Story/Interaction/Dropped/WorldPickup/Puzzle/Combat/Scene/Inventory/Session NetHandlers. Join dump uses `_unicastPlayerId` via BeginUnicast/EndUnicast.
- `PuzzleSyncService` is a coordinator (scan/tick/held/`_mutateWorld`/relay); ApplyEntry/TryRead are one-line forwards to Cryo/Codepad/Locks/PumpFlood/Pipes/Hatch/Elevator/Machines/Residency/Radio/UseItem/Storage/EventZone/DoorFlags + Story/Boss/Enemy for bleed types. Dead `InteractionTriggered` switch cases removed (enum kept).
- `InteractionPatches` god split into Domains Story/Scene/Inventory/Puzzles/Combat patches; dead `ShouldHold*` / `Keep*Prompt` / `IsRemoteUnlock` chain removed.
- `DroppedItemManager` is a thin façade over Registry/Spawner; drop/claim wire in `DroppedItemNetHandlers`.
- `Domains/README.md` is the symptom→path fix map for bugfixes.
- `docs/DECOMPILE_COVERAGE.md` lists completed protocol-10 coverage + intentional locals only (no park / verify-later tables).

### Preserved
- Host-authoritative world/story rules from AGENTS.md (party key ring Key/Object only, native TAKE for drops, wreck↔hole never follows, ClientMayEmit allowlist, join dump `_mutateWorld=false`).
- Reverse-check both arrows remains required for playtest claims (playtest itself out of scope for this structural release).

### Fixed (code-only solidify — no dual-box)
- Join FullRefresh now snaps door/lock flags (`DoorwaySimple` / `SwingDoor` / `DoorLockControl` / `InteractiveLockSingle`); `TryUnlockDoors` / `UnlockDoorObject` are flag snaps (flavor still gated by `AllowUnlock` / `IsFlavorSeal`).
- Remount hold: `IsProgressed` covers unlocked locks (Interactive/Number/DoorLockPuzzle), open Swing/FoldingShutter, CentralElevator cabin, Waage weight, RadioAlignment; ProgressedBool0 gains Dial/Multi/Vent/RadioTutorial/Power/Incinerator/Shrine/Tarot/MultiCondition/Cutscene/FloodSwitch/ElevatorCall.
- Host/client Tick now `HoldIfProgressed` so poll solves survive room remount (not only Emit/Apply).
- `EmitProgressed` TryReads live component then forces Bool0 (no longer zeroes Reaktor/Pump Ints); recycled `_emitScratch`.
- Client emit: `CentralElevator`. KeyGrid/Ariane no longer double-emit instance + WorldId 0.
- Cryo pattern-lock disable: hierarchy/sibling parent only (no radius heuristic). MultiKeyLock poll derives unlocked from `keys[]` (no `checkLock` on tick).
- Mural `useRing` mutate-gated; FloodControls `dlc.locked` snaps on join.
- Hot-path: FMOD HostEmit dedupe before local/door walks; RadioManager + gunshot `ElsterSettings` via WorldLookup; proxy LateUpdate `_staleScratch`; StorageBox recycled read buffer; EnvEmit.ReadOnce for Update-polled Magpie/Shutters/CardWriter/Biodome.

### Fixed (decompile coverage loop)
- `MultiKeyLock` Apply calls `checkLock` + TryUnlockDoors; dial/number/DoorLockPuzzle unseal doors; MultiLock TryUnlockDoors; Vent Magpie cover snap; EvidenceLocker snaps `EvidenceLockerDoor`.
- Doorway_simple / DoorLockControl Apply now unseal when unlocked (flavor seals still sealed). Host unlock ↔ client unlock.
- Dialoguer Airlock (13) treated as local flavor with PEN_Titles.
- Client CutsceneStart no longer runs native locally (SProgress writes were blocked → flag hole); host-only Start + presentation.
- Enemy staggerType applied from HurtState; MultiLock element bits in Int0; LAB_Waage no longer writes peer inventory content.
- Client EnemySpawner blocked; host adopts native spawn into EnemySpawn. Client world-pickup grant Invokes onPickup.
- `PuzzleStateEntry` change-detect includes `Float1`.

### Changed (structural beauty)
- PuzzleSyncService table-dispatch (`PuzzleSyncService.Dispatch.cs`); main hub ~650 LOC. Tick lists Clear()+reuse; Send* accept IList (no per-tick ToArray).
- `Sync/WorldLookup.cs` — scene-scoped All/Find caches; presentation/cutscene/scene-follow/key-ring/FMOD title/crawl paths no longer FoT every call.
- Enemy/Boss/WorldPickup ticks: recycled snap lists; scene-cached Basic/Cook/boss arrays; enemy one nearest-target per tick; Boss Apply `Play(hash)` (no clip enum alloc).
- Dropped items: `DroppedItemTemplateCache` (template + scene ItemPickup cache); Registry `_keyScratch`; Anchor LateUpdate early-out then disable.
- Apply paths: Puzzle ApplyEntry skips missing WorldId components; null/destroyed guards on enemy/boss/pickup/story presentation.
- SceneFollow / PartyKeyRing / SourceAnimReader / Fmod PEN_Titles use WorldLookup.

## 0.4.3-dev — 2026-09-13

### Changed
- Linux playtest port: csproj defaults to `~/Work/MyProjects/SIGNALIS` + Steam under `~/.local/share/Steam/...`; `scripts/launch-client.sh` / `secondsignalis` runs the second box under its own Proton prefix. Steam host install gets MelonLoader 0.5.7 + `single-instance=0`.
- Proton MelonLoader: force native `version.dll` (`WINEDLLOVERRIDES=version=n,b` on Steam launch options + client launcher; prefix `DllOverrides` too). Without this, Steam Play boots vanilla and never writes `MelonLoader/Latest.log`.
- Dual-box display: windowed 2560x720 + Hyprland float rules — Steam host top half, `secondsignalis` bottom half (not fullscreen).
- `.gitattributes` enforces LF so Windows/Proton editors don’t churn the tree.

### Fixed
- Door open/close SFX: remote apply no longer plays native door emitters ungated (host→client and client→host). Distance-gated `DoorNative` SFX only; `event:/Environment/Doors/*` no longer world-relays via FMOD OneShot/emitter sync.

## 0.4.3-dev — 2026-08-19

Protocol **v8** (same wire as 0.4.2). Client join no longer mutates authored sealed-door faces.

### Added
- Player-dropped items (v1): G / inventory **DROP** clones a native floor `ItemPickup`; walk-up TAKE inspect (yes/no + count) then grant. Unique Key/Object go on the party ring; join dump; bag-full reject. Dual-instance verified both drop/TAKE arrows.

### Changed
- World FMOD no longer relays Music / Cutscenes / Ambience beds (each Elster plays those locally). Unique Key/Object claims survive chapter loads so copies stay hidden.
- Dead-code trim: unused overlay/drop leftovers, no-op dialogue apply, unused vital fields, bone-send divider, and one-line aliases. Shared `WorldLookup.All` / `WeaponUtils.EquippedWeaponType` / `LocalInspect.InspectScreen`.
- Logs: always-on session/world/story edges with `H`/`C` role prefix; identical lines collapse for 3s. Hitch prints only on a real spike. FMOD Play/Stop, proxy clone/FX dumps, and incremental puzzle apply sit behind `VerboseLogging` (default off).

### Fixed
- Client chapter request (F7 / cutscene load) no longer double-`LoadLevel`s while the host is on `LoadingScreen`. In-flight target is coalesced; dumps from the loading screen are skipped; `LastCmd` CutsceneStart does not ride into the next chapter.
- Pickup inspect no longer shows the last UseItem name (Airlock Key) for ammo/cards/books. Dialoguer s0/s3 bind to the catalog item before `pickUp`; story XML dumps restore the local name instead of stomping it.
- Inspect grants now `AddItem` into the real 6-slot bag (`InLocalBag`), not ring-patched `hasItem`. Keys still open doors; they also show in the inventory.
- Other-room `CutsceneStart` / `EventZone` / `CutsceneProceed` / `MultiCondition` no longer Invoke on the observer (that was the leaked traverse / cinematic yank). Initiator still plays locally; host relays.
- Host wakes a sleeping-chunk enemy when a peer is in range or a hit arrives, then logs WorldId misses. Client puppets still use native contact hurtboxes (ramming an enemy is real SIGNALIS damage).
- `CutsceneManager.Skip` no longer NREs when `cutscene` is null (host never started it). Skip/Start are once per WorldId; join dump only replays a cutscene that is still running in this scene.
- Client unlock of `InteractiveLockSingle` now emits to the host. Non-flavor `Doorway_Double` opens are honored even if the host lock bit is still set; flavor seals still ignore.
- Dropped TAKE no longer calls `Dialoguer.EndDialogue` (that broadcast a WorldId-0 `DialogueEnd` storm and crashed). Play flags restore without Dialoguer. Clone `release()` is skipped; grant + despawn still run.
- Client TAKE no longer destroys the inspect pickup mid-callback (that NRE'd `dialoguerCallback` and froze Elster). Claim still goes to the host; local despawn waits until play restores.
- Join `InteractiveLockSingle` / `DoorLockControl` no longer `setLock`s flavor seals. ConnectedDoors only `Unlock`s when a key / `externalUnlocker` / hint exists. `Doorway_Double.locked=false` is not written onto a sealed face.
- Entity spawner no longer `FindObjectsOfType` every IMGUI frame or clone the current room’s corpses. Spawn uses native `ResetEnemy` / `WakeUp`, registers a stable `SR_Spawn_*` WorldId, and host-broadcasts so the client instantiates the same type. Client F11 is a host request at the **client** Elster’s position.
- F11 no longer additive-loads a whole chapter to steal a prefab. That looped `LOV_Reeducation` (~30 loads), never banked STAR, and killed the session. Templates come from in-memory `EnemyController` + `EnemySpawner.EnemyType` only. Missing types: load that chapter once with F7.
- Client world pickups that only have `_itemEnum` (`_item` still null) no longer grant `Nothing` / `!!MISSING STRING`.
- Client `Used !!MISSING STRING` on party-ring keys: `useItemDialogue` substitutes Dialoguer `keyName` (`<s3>`). Join XML leaves that empty; catalog `getName` is written to `s3` on Start/Continue. Scene `AnItem` copies always resolve through `InventoryManager.getItem`.
- Pickup/use loc: `getName` Prefix skips the scene copy and runs native on the catalog SO (IL2CPP postfix `ref string` was a no-op). `AddItem(None)` is ignored.
- Party cutscenes under `EventOnlyRoom` now start the native skipper. Escape hold skips instead of opening pause; airlock/PEN_Titles stay local.
- Airlock split only covers wreck↔hole during `PEN_Titles`. Host loading `LOV_Reeducation` no longer leaves the client frozen in `PEN_Hole` (pause-only). Puzzle dumps skip while scenes mismatch.
- Client `CutsceneStart` plays locally and notifies the host; missing WorldId is an ack, not a reject. `PEN_HoleSnowblind` / `PEN_CodeRoomEnd` skippers get Esc instead of pause.
- Story commit no longer repeats every 0.75s (that was Dialoguer XML spam + hitch). Host LOV load hitch is still a real chapter load.
- Host skip of the Penrose airlock no longer SceneFollows the client into `PEN_Hole`. Wreck↔hole is always per-Elster (not only while local `PEN_Titles` is running). Host leaving Penrose still follows. Peer hole-load requests do not teleport the host.
- Airlock split: other-scene UseItem acks instead of `no key`; host proxy is despawned (no extrapolate ghost); FMOD from the other chapter is ignored. CutsceneSkip is once per WorldId and no-ops if already `completed` (LOV intro no longer double-Skip hitches).
- Host/client keycard USE at a slot (Penrose airlock): Interactor was highlighting the PC zoom (`ViewPoint`) over `UseItemInteraction` while the repaired `AirlockKey` was selected, so the “want to use this item?” prompt never started. Held-key use inter is preferred; scene `key` / `InteractItem` bind to the bag instance; `getCount` matches by enum. Host EventZones under the airlock still run native.

### Added
- Template bank (DDOL) harvested from loaded controllers and native `EnemySpawner` prefab refs
- `EnemySpawn` net message; join dump includes live F11 spawns

## 0.4.2-dev — 2026-08-15

Protocol **v7** (incompatible with v6). Join dump is the live SProgress slot. UnityEvents and world FMOD replay on the client. Native puzzle solve methods on the solved edge. Session roster for 3–4 players.

### Correctness pass
- Party key ring is **Key/Object only** (ammo/weapons no longer lie in `hasItem`). Dropped and crafted keys (client Tape+BrokenKey) note the ring; host merges client ring deltas
- HP is native `HurtElster` only (no parallel pool). Host AI no longer double-hits the host. Parameterless `TakeDamage` reads `PlayerAttack.sneaking`. Host `SaveManager.Load` wipes peers. Disconnect resets downed state
- World pickup claims by **WorldId**; unique-enum hide is Key/Object copies only. Grant is `AddItem` + hide, never a second `pickUp()`. `InteractiveLockSingle` consume/unlock including host-self sender 0
- One `setInRange` prefix. `index:` chapter loads gate SceneFollow (transient is `LoadingScreen` only). Penrose defer is cinematic (`PEN_Titles.started` / local ViewPoint, 3s latch then clear if `started` never rose)
- Dialoguer `StartDialogue` (including callback overloads) sends to the host. Continue/End replay on clients; flavor/inspect lines stay fully local. Inspect-originated `SProgress.Set*` notes the host. Client `EvaluateEnding` blocked. EventZone idle timer runs; keypad Update polling removed
- Elevator snaps `riding`/`stopped` (still no remote `startRide`). Radio lock applies `frequency`. EventZone join fires `onInRange` on the false→true edge only. Overlay kill is per WorldId
- FMOD join dump scans live `IsPlaying()` including inactive room chunks. WorldId hashes `go.scene`. Host session is live with zero peers. Boss keyed by full WorldId. Drops: stack count, bag-full reject (raw bag, not ring-patched `hasItem`), no fake save file
- `ExperimentalPuzzles` migrates into `SyncPuzzles` and never forces puzzles off

### Added
- Full `SProgress.progress` list dump (bool/int/float/string/vector) + mid-presentation WorldId on join
- `EventZone.onInRange` / MultiCondition / BookScreen / `CutsceneCut.Proceed` presentation relay
- `StudioEventEmitter` Play/Stop by WorldId (skips Elster + radio UI); sliding-door one-shots
- Native `openDoor` / `delayedOpen` / `StartShutdown` / `CheckSolve` / `useRing` on solve
- `EXC_Elevator` flags only (never `startRide`); Kolibri + Adler snapshots
- `PlayerRoster`: host broadcasts session ids; clients prune ghost proxies on leave; `GetRemotePlayerIds` works on clients
- Client ids recycled in `1..MaxPlayers-1` (cap 4 including host)

### Changed
- LiteNetLib **1.3.5** as checked-in `lib/LiteNetLib.dll` (`net472`). NuGet 1.3.5 is `netstandard2.0` and MelonLoader 0.5.7 Mono cannot load it
- Proxy locomotion interpolates between pose snapshots ~45ms behind (Hermite, not exponential-lerp to the latest packet)
- Proxy bones send at 30 Hz and sample on the same snapshot clock as locomotion (quaternion slerp, no 50ms predicted hold)
- Version `0.4.2-dev`, protocol 7
- Join and F2 client resync world dump **unicast** to that peer (host F2 Resync is a no-op; scene-change dump still goes to all clients)

### Fixed
- Notes/documents/`EventScreen` inspect no longer open on every Elster (local camera only)
- Hatch / unique-key doors: one party-ring solve, both walk. Airlock/EventOnlyRoom cinematic stays on the user; chapter load still SceneFollows
- Client UseItem (Penrose hatch card) is accepted on the host when that Elster has the item. The ring used to check only the host bag, so `UseItem … no key` rejected the repaired `AirlockKey`. Combine Tape+BrokenKey also notes the result on the ring.
- Client skip/load of `PEN_Hole` no longer yanks the host, and host remaining on the wreck no longer yanks the client back. If a peer is already in the airlock cinematic, Escape/skip is not blocked.
- Client walking the Penrose airlock into `PEN_Hole` is a real chapter follow. Host used to reject it as an unknown scene (no `LoadLevelZone` on the wreck), so the client sat in the hatch for ~12s until the host loaded. LoadingScreen no longer hellos/dumps/applies puzzles or FMOD.
- Locked room-links that are not a real key-hint (`GiveKeyHint` + a key) stay on the red NO ENTRY plate for both Elsters. Yellow padlock / blue Open prompts are suppressed while `ConnectedDoors.locked` (reactor, external unlock, no-key). Real key doors still show the padlock.
- G drop works from play and inventory (selected slot / equipped tool / weapon). Dropped props use world-space distance pickup.
- Proxy run footsteps set FMOD Run/Speed on `ElsterStep` and read `AlternatePlayerController.running` (animator `Running` was stuck off)
- `InventoryManager.hasItem(Items.itemlist)` also honors the party key ring (native use checks that overload)
- Penrose `ObservationDialogue` flavor (control-panel look-at, loc `PEN_Controls*`) stays local. Remoting it ran `Dialogue.StartDialogue` without the loc string, so the other Elster got a different line (or the sealed-door text) for the same WorldId
- `InteractiveLockSingle` now also syncs `AutoTraverseDoor.blocker` (the red “cannot be opened” plate). `door.locked` alone left the plate/inspect mismatch
- Proxy Hermite no longer uses XZ velocity on Y / extra Y (that was the occasional inches-off-floor hop after a hitch)
- Cryo codepad buttons stay disabled after solve (solved flag alone still left the pad usable)
- Penrose cryo override panel is `LAB_PatternLock`, not `PEN_Codepad`. `LAB_PatternLock` has no `OnEnable` (Harmony skipped the patch). Disable now runs from `Lab_PatternLockControl.OnEnable` and turns off the panel EventObject
- Room enter no longer `FindObjectsOfType` the whole puzzle map (~200ms hitch). Reapply uses the existing WorldId scan
- Door/move `Interaction.triggered` is no longer polled across peers (that was yanking the other Elster when someone used a ConnectedDoors link)
- `InteractiveLockSingle` no longer treats key-use cooldown (`timedOut`) as `door.locked` — that was relocking the door the other player needed
- Spent cryo/pad `Interaction`s stay dead across `reset()` / room-chunk wake (prompt + EventScreen start)
- Claiming the cryo `BrokenKey` no longer hides the cockpit `PhotoPickup` (sibling wipe under `Pen_CockpitEvent3D`). Unique-item hide still removes the cockpit `KeyCardBroken` copy only
- First-person (`EventScreen3DCam` / `EventOnlyRoom`) look-at dialogues stay on the inspecting Elster; they no longer pop “nothing” on the other player
- Chapter machines that only set a bool now also run native world methods on the live edge (`MED_Pump`/`Drain`, `ROT_Pipes.TurnValve`, `EXC_Hatch.OpenHatch`, shutters, magpie, reactor, rings, biodome lock, meat blocker)
- Friendly fire no longer double-hits; storage put/take mutates the shared box, not the host bag
- StoryCommit replays presentation on join/resync only; host ignores client world-apply packets
- Client Dialoguer / UseItemMulti / keypad / cutscene proceed / books actually reach the host
- Dialoguer Continue/End no longer freeze after the first page (presentation applies them; flavor stays local). Callback `StartDialogue` overloads take the host path
- Host world-pickup reserves the WorldId in the prefix (same-room race). `SaveManager.Load` only wipes peers on host death
- Int chapter loads use the build-index name first. Client F7/scene requests load on the host without `BeginApply` so `SendSceneFollow` reaches the requester
- `MultiConditionEvent.TryTrigger` broadcasts once. FMOD dump includes inactive emitters
- Dropped E pickup is host-claimed; pickup deny no longer hides the prop
- Sequenced pose no longer embeds the WeaponMount tree (261 eulers blew the 1020-byte LiteNetLib cap and crashed `Network.Update` every bone tick). Bind bones only; overflow goes as `BonePose` chunks
- Proxy weapon hold/aim uses Elster controller names (`Weapon/Pistol`, not `Pistol`) so ADS actually poses the arms
- Proxy dry-fire no longer plays bang/flash/shells on the observer (empty click uses `emptyMod`; live Fire is ammo-spent only)
- Empty click / hip Fire1 ignored unless ADS (`PlayerState.aiming`) so door/use clicks are not gunshots
- Reload sound from `PlayerState.reloading` / mag refill (animator Reload bool never rose)
- Proxy laser point unparented + red sprite fallback (was magenta missing-mat, stretched with the gun)
- First weapon clone no longer nested-scans every renderer (that was the ~1s aim-then-walk hitch)
- Proxy shot no longer layers CombatSfx slide/eject (that was the extra empty-click); case-land is `Pistol/Case` etc., shotgun still pumps
- Proxy laser stays local-space on the weapon (follows recoil) instead of a world-space 90° guess
- Proxy ADS plays `WeaponDraw`; wall hits play `ricochetSound`
- Puzzle poll no longer TryReads every inactive-room component every 0.5s (~70ms main-thread hitch that starved pose)
- Proxy movement interpolates between received snapshots instead of exponential-lerping at the live packet (that retarget was the remaining metronome hitch)
- Scene-follow requests only apply known current-scene names; F7 rooms and client F11 spawn are gated while connected
- Downed clients stay downed across SceneFollow; sliding doors emit on `cycle`; FMOD join dump + reset
- Harmony still per-class; one bad patch cannot abort the mod
- Playtest traces: `[Story]` `[Interact]` `[FMOD]` `[KeyRing]` `[StorageBox]` `[Scene]` `[Damage]`; VerboseLogging default on
- `.gitignore` now drops bin/obj/dist, NuGet packages, IDE files, logs, and secrets (stop tracking build artifacts)

## 0.4.1-dev — 2026-08-13

Protocol **v5** (incompatible with v4). Host-authoritative full-game pass: SceneFollow, interaction bus, story lockstep, shared storage, party keys, death policy.

### Added
- SceneFollow via `AsyncLoader` / `SceneHelper` / `LoadLevelZone` (client follows host chapter)
- Interaction request/ack for EventZone, UseItem, keypad, Dialoguer, cutscenes, EventScreen, storage, gunshot wake
- StoryCommit (`SProgress` + Dialoguer XML + `END_Manager`) and StoryPresentation native replay
- Shared `InventoryManager.boxItems` blob; party key ring for unique keys
- Puzzle types: tarot, mural, incinerator, scale, shrine, radio alignment, radio code lock
- Client downed vs host save-reload death

### Changed
- Version `0.4.1-dev`, protocol 5
- Enemy `playerPos` retarget every non-dead state; `END_Boss.Elster`; client AI puppeted at handshake
- Radio sync is `moduleInstalled` only (no tuner clobber)
- Off-chunk enemy pose snaps skipped when renderers are disabled (room isolation)

### Publish
- GitHub zip: `dist/SyncRADation-0.4.1-dev.zip` (dll + LiteNetLib + README). Nexus not in this release.

## 0.4.0-dev — 2026-08-13

Version reset: former `1.2.x-dev` is now **0.4.0-dev**. Protocol still **v4** (not a wire change).

### Changed
- Product version `0.4.0-dev` (honest pre-1.0 numbering)
- README / AGENTS.md aligned with current controls, WorldId authority table, and room-isolation rules

## 1.2.2-dev — 2026-08-03

### Changed
- **Entity spawner** moved **F7 → F11**
- **F7** opens **Location Teleporter** (chapter `lvl` jumps + in-level room `goto`/spawn). Removed native debug-console unlocker.
- **Location Teleporter:** Reeducation split into **normal** (`LOV_Reeducation`) and **corrupted** (`BIO_Reeducation`) entries.

## 1.2.2-dev — 2026-07-19

### Fixed
- **Remote weapon VFX:** shot pulse from equipped `magAmmo` decrease → `AnimTriggers.Fire` (full-auto safe; no longer stuck on held Fire1 edge)
- **Muzzle flash / case eject / smoke:** longer flash, source `MuzzleFlash`/`ReloadCaseEject` path match, no random PS fallback for cases
- **Aim laser:** world-space `LineRenderer`, material path-copy after IL2CPP Instantiate, real muzzle origin
- **Shell/action SFX:** delayed CombatSfx paths for pistol/revolver/rifle on remote shot (plus existing shotgun pump / flak eject)

### Changed
- Version `1.2.2-dev` (protocol still v4)

## 1.2.1-dev — 2026-07-19

### Fixed
- **Room pull bug:** remote players no longer call `ConnectedDoors.StartA`/`StartB` when someone else uses a door. Room traverse is **local-only**; network only syncs **lock** state on `ConnectedDoors`.

## 1.2.0-dev — 2026-07-19

### Fixed (decompile-aligned)

- **Enemy damage** uses native `EnemyController.TakeDamage(fire, crit, hurt, noSneak)` on host; clients no longer DIY raycast + raw `hitbox.HP` math
- **Harmony** prefixes on both `TakeDamage` overloads → client hits report to host, host sim is authoritative
- **Puzzles** keyed by **WorldId** (FNV scene+hierarchy), not `FindObjectsOfType` index (cross-peer identity fix)
- **Doors** apply via `DoorNative` → private `openDoors`/`closeDoors`/`cycle`. ConnectedDoors lock/plates only — never `StartA`/`StartB`
- **World ItemPickup** host claim/grant: only claimer gets inventory; everyone else only hides; race-safe reservation

### Added

- Protocol **v4**: extended `EnemyDamage` (native chances), `WorldPickupClaim` / `WorldPickupGrant`, puzzles use `WorldId` in `PuzzleStateEntry`
- `Patches/EnemyTakeDamagePatches.cs`, `Patches/ItemPickupPatches.cs`, `Networking/DoorNative.cs`

### Changed

- Version `1.2.0-dev`, protocol 4 (incompatible with v3 clients)
- Removed multiplayer enemy DIY raycast from `ModRuntime` (FF raycast remains if enabled)

### Notes

- Connected room transitions still imperfect if both peers load different room chunks
- Cutscenes/Dialoguer still not co-op scripted
- Both installs must run this build

## 1.1.0-dev — 2026-07-19

### Added
- Protocol v3: SnapshotRequest, WorldPickupState, PlayerVital
- Join resync, world pickup hide, vitals, puzzles default on
