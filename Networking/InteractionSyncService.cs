// Host validates client interaction intents and applies native world mutations.
using System.Reflection;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class InteractionSyncService
    {
        public static void HandleRequest(InteractionRequestMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host) return;

            ulong id = unchecked((ulong)msg.WorldId);
            bool ok = false;
            string reason = "";

            try
            {
                switch (msg.Kind)
                {
                    case InteractionKind.EventZone:
                        ok = ApplyEventZone(id);
                        break;
                    case InteractionKind.UseItem:
                        ok = ApplyUseItem(id, msg.SenderPlayerId, out string consumeReason);
                        if (!ok) reason = "no key";
                        else if (!string.IsNullOrEmpty(consumeReason)) reason = consumeReason;
                        break;
                    case InteractionKind.UseItemMulti:
                        ok = ApplyUseItemMulti(id, out string multiReason);
                        if (!ok) reason = "no key";
                        else if (!string.IsNullOrEmpty(multiReason)) reason = multiReason;
                        break;
                    case InteractionKind.KeypadSubmit:
                        ok = ApplyKeypad(id);
                        break;
                    case InteractionKind.DialogueStart:
                        if (id == 0 && msg.Int0 != 0)
                        {
                            NetGate.BeginApply();
                            try { Dialoguer.StartDialogue(msg.Int0); }
                            finally { NetGate.EndApply(); }
                            net.StorySync.BroadcastPresentation(StoryCmd.DialoguerStartId, 0, msg.Int0, "");
                            ok = true;
                        }
                        else
                            ok = ApplyDialogue(id, net);
                        break;
                    case InteractionKind.CutsceneStart:
                        ok = ApplyCutscene(id, net);
                        break;
                    case InteractionKind.EventScreenStart:
                    case InteractionKind.EventScreenExit:
                    case InteractionKind.BookMemory:
                    case InteractionKind.BookOpen:
                        ok = true;
                        break;
                    case InteractionKind.CutsceneSkip:
                        ok = ApplyCutsceneSkip(id, net);
                        break;
                    case InteractionKind.DialogueContinue:
                        NetGate.BeginApply();
                        try
                        {
                            if (msg.Int0 != 0) Dialoguer.ContinueDialogue(msg.Int0);
                            else Dialoguer.ContinueDialogue();
                        }
                        finally { NetGate.EndApply(); }
                        net.StorySync.BroadcastPresentation(StoryCmd.DialogueContinue, 0, msg.Int0, "");
                        ok = true;
                        break;
                    case InteractionKind.DialogueEnd:
                        NetGate.BeginApply();
                        try { Dialoguer.EndDialogue(); }
                        finally { NetGate.EndApply(); }
                        net.StorySync.BroadcastPresentation(StoryCmd.DialogueEnd, 0, 0, "");
                        ok = true;
                        break;
                    case InteractionKind.StoragePut:
                        ok = ApplyStorage(msg, put: true, out reason);
                        break;
                    case InteractionKind.StorageTake:
                        ok = ApplyStorage(msg, put: false, out reason);
                        break;
                    case InteractionKind.Gunshot:
                        ApplyGunshot(new Vector3(msg.Float0, msg.Float1, msg.Float2), net);
                        ok = true;
                        break;
                    case InteractionKind.MultiCondition:
                        ok = ApplyMultiCondition(id);
                        break;
                    case InteractionKind.SceneFollowRequest:
                        ok = SceneFollowService.TryApplyRequest(msg.Text);
                        if (!ok) reason = "unknown scene";
                        break;
                    case InteractionKind.CutsceneProceed:
                        ok = ApplyCutsceneProceed(id, net);
                        break;
                    case InteractionKind.DroppedPickup:
                        ok = net.TryClaimDropped(msg.Int0, msg.SenderPlayerId, out reason);
                        break;
                }
            }
            catch (System.Exception ex)
            {
                reason = ex.Message;
                ModRuntime.Log?.Warning("[Interact] " + msg.Kind + ": " + ex.Message);
            }

            if (msg.Kind != InteractionKind.Gunshot)
                PlaytestLog.Event("Interact", (ok ? "ok " : "FAIL ") + msg.Kind
                    + " from=" + msg.SenderPlayerId
                    + " id=" + unchecked((ulong)msg.WorldId).ToString("X16")
                    + (string.IsNullOrEmpty(reason) ? "" : " " + reason));
            net.SendInteractionAck(msg.SenderPlayerId, msg.WorldId, msg.Kind, ok, reason);
        }

        private static bool ApplyEventZone(ulong id)
        {
            var z = Find<EventZone>(id);
            if (z == null) return false;
            if (z.triggered) return true;
            z.triggered = true;
            try { if (z.onInRange != null) z.onInRange.Invoke(); } catch { }
            SyncRADation.Patches.EventZonePatch.MarkFired(id);
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.EventZoneFire, id, 0, "");
            return true;
        }

        private static bool ApplyUseItem(ulong id, int senderId, out string consumeReason)
        {
            consumeReason = "";
            var u = Find<UseItemInteraction>(id);
            if (u == null) return false;
            if (u.unlocked && !u.repeatable) return true;

            AnItem key = u.key;
            if (key != null && !PartyKeyRing.LocalOrRingHas(key))
                return false;

            u.unlocked = true;
            try { if (u.onSuccessful != null) u.onSuccessful.Invoke(); } catch { }
            PuzzleSyncService.UnlockLinked(u.gameObject);
            try
            {
                var netPuzzle = LanNetworkManager.Instance;
                if (netPuzzle != null)
                    netPuzzle.PuzzleSync.Emit(PuzzleType.UseItemInteraction, id, u);
            }
            catch { }

            bool consumes = false;
            try
            {
                var lockComp = u.GetComponent<InteractiveLock>();
                if (lockComp != null)
                {
                    lockComp.locked = false;
                    consumes = lockComp.ConsumesKey && key != null;
                }
            }
            catch { }

            if (consumes)
            {
                bool hostHad = PartyKeyRing.InLocalBag(key);
                ConsumeKey(key);
                if (!hostHad)
                    consumeReason = "consume:" + (int)key._item;
            }
            else
            {
                PartyKeyRing.Note(key);
            }
            PartyKeyRing.Broadcast();
            return true;
        }

        private static bool ApplyUseItemMulti(ulong id, out string consumeReason)
        {
            consumeReason = "";
            var m = Find<UseItemMultiInteraction>(id);
            if (m == null) return false;

            try
            {
                var list = m.Interactions;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var u = list[i];
                        if (u == null) continue;
                        AnItem key = u.key;
                        if (key != null && !PartyKeyRing.LocalOrRingHas(key))
                            return false;
                    }
                }
            }
            catch { return false; }

            var ready = typeof(UseItemMultiInteraction).GetMethod("ready",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ready == null) return false;

            NetGate.BeginApply();
            try { ready.Invoke(m, null); }
            finally { NetGate.EndApply(); }

            var parts = new System.Collections.Generic.List<string>();
            try
            {
                var list = m.Interactions;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var u = list[i];
                        if (u == null) continue;
                        AnItem key = u.key;
                        if (key == null) continue;
                        bool consumes = false;
                        try
                        {
                            var lockComp = u.GetComponent<InteractiveLock>();
                            if (lockComp != null)
                                consumes = lockComp.ConsumesKey;
                        }
                        catch { }
                        if (consumes)
                        {
                            bool hostHad = PartyKeyRing.InLocalBag(key);
                            ConsumeKey(key);
                            if (!hostHad)
                                parts.Add((int)key._item + ":1");
                        }
                        else
                            PartyKeyRing.Note(key);
                    }
                }
            }
            catch { }
            if (parts.Count > 0)
                consumeReason = "consume:" + string.Join("|", parts.ToArray());
            PartyKeyRing.Broadcast();
            return true;
        }

        private static bool ApplyCutsceneProceed(ulong id, LanNetworkManager net)
        {
            var cut = Find<CutsceneCut>(id);
            if (cut == null) return false;
            NetGate.BeginApply();
            try { cut.Proceed(); }
            finally { NetGate.EndApply(); }
            net.StorySync.BroadcastPresentation(StoryCmd.CutsceneProceed, id, 0, "");
            return true;
        }

        private static void ConsumeKey(AnItem key)
        {
            try
            {
                if (PartyKeyRing.InLocalBag(key))
                    InventoryManager.RemoveItem(key, 1);
            }
            catch { }
            try { PartyKeyRing.Remove(key._item); } catch { }
        }

        private static bool ApplyKeypad(ulong id)
        {
            var k = Find<Keypad3D>(id);
            if (k != null)
            {
                k.solved = true;
                k.opening = true;
                return true;
            }
            var r = Find<ROT_Keypad>(id);
            if (r != null)
            {
                r.solved = true;
                r.opening = true;
                return true;
            }
            var p = Find<PEN_Codepad>(id);
            if (p != null)
            {
                PuzzleSyncService.ApplyCodepadConsequences(p);
                return true;
            }
            return false;
        }

        private static bool ApplyDialogue(ulong id, LanNetworkManager net)
        {
            var d = Find<Dialogue>(id);
            if (d == null) return false;
            NetGate.BeginApply();
            try { d.StartDialogue(); }
            finally { NetGate.EndApply(); }
            net.StorySync.BroadcastPresentation(StoryCmd.DialogueStart, id, 0, "");
            return true;
        }

        private static bool ApplyCutscene(ulong id, LanNetworkManager net)
        {
            var c = Find<CutsceneManager>(id);
            if (c == null) return false;
            NetGate.BeginApply();
            try { c.StartCutscene(); }
            finally { NetGate.EndApply(); }
            net.StorySync.BroadcastPresentation(StoryCmd.CutsceneStart, id, 0, "");
            return true;
        }

        private static bool ApplyCutsceneSkip(ulong id, LanNetworkManager net)
        {
            net.StorySync.BroadcastPresentation(StoryCmd.CutsceneSkip, id, 0, "");
            var c = Find<CutsceneManager>(id);
            if (c != null)
            {
                NetGate.BeginApply();
                try
                {
                    if (c.skipper != null && c.skipper.skipEvent != null)
                        c.skipper.skipEvent.Invoke();
                }
                finally { NetGate.EndApply(); }
            }
            return true;
        }

        private static bool ApplyStorage(InteractionRequestMessage msg, bool put, out string reasonOut)
        {
            reasonOut = "";
            var item = InventoryManager.getItem((Items.itemlist)msg.Int0);
            if (item == null) return false;
            int n = msg.Int1 > 0 ? msg.Int1 : 1;
            NetGate.BeginApply();
            try
            {
                // Box only — never host Elster bag. Sender bag is adjusted via ack.
                if (put) InventoryManager.boxItem(item, n);
                else
                {
                    for (int i = 0; i < n; i++)
                        InventoryManager.unboxItem(item);
                }
            }
            finally { NetGate.EndApply(); }
            var net = LanNetworkManager.Instance;
            net?.StorageSync.RequestSend();
            net?.StorageSync.SendNow(net);
            int enumVal = 0;
            try { enumVal = (int)item._item; } catch { }
            reasonOut = put
                ? "consume:" + enumVal + ":" + n
                : "grant:" + enumVal + ":" + n;
            return true;
        }

        private static void ApplyGunshot(Vector3 pos, LanNetworkManager net)
        {
            float range = 25f;
            try
            {
                var settings = Object.FindObjectOfType<ElsterSettings>();
                if (settings != null) range = settings.hearRange;
            }
            catch { }
            float r2 = range * range;
            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null) continue;
                try
                {
                    if (e.state == EnemyController.enemystate.dead) continue;
                    if ((e.transform.position - pos).sqrMagnitude > r2) continue;
                    Transform t = FindShooterTransform(pos, net);
                    if (t != null) e.playerPos = t;
                    e.WakeUpfromGunShot();
                }
                catch { }
            }
        }

        private static Transform FindShooterTransform(Vector3 pos, LanNetworkManager net)
        {
            Transform best = null;
            float bestD = 4f;
            var local = net.GetLocalPlayer();
            if (local != null)
            {
                float d = (local.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = local.transform; }
            }
            var pm = net.ProxyManager;
            foreach (int pid in net.GetRemotePlayerIds())
            {
                var p = pm.GetProxy(pid);
                if (p == null || p.GameObject == null) continue;
                float d = (p.GameObject.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = p.GameObject.transform; }
            }
            return best;
        }

        private static bool ApplyMultiCondition(ulong id)
        {
            var m = Find<MultiConditionEvent>(id);
            if (m == null) return false;
            NetGate.BeginApply();
            try { m.TryOnce(); }
            finally { NetGate.EndApply(); }
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.MultiConditionFire, id, 0, "");
            return true;
        }

        private static T Find<T>(ulong worldId) where T : Component
        {
            if (worldId == 0) return null;
            try
            {
                var all = FindAll<T>();
                if (all == null) return null;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    if (WorldId.FromGameObject(all[i].gameObject) == worldId)
                        return all[i];
                }
            }
            catch { }
            return null;
        }

        private static T[] FindAll<T>() where T : Component
        {
            try { return Object.FindObjectsOfType<T>(true); }
            catch { return Object.FindObjectsOfType<T>(); }
        }
    }
}
