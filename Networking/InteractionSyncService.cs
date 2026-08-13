// Host validates client interaction intents and applies native world mutations.
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
                    case InteractionKind.KeypadSubmit:
                        ok = ApplyKeypad(id);
                        break;
                    case InteractionKind.DialogueStart:
                        ok = ApplyDialogue(id, net);
                        break;
                    case InteractionKind.CutsceneStart:
                        ok = ApplyCutscene(id, net);
                        break;
                    case InteractionKind.EventScreenStart:
                        ok = ApplyEventScreen(id, net, true);
                        break;
                    case InteractionKind.EventScreenExit:
                        ok = ApplyEventScreen(id, net, false);
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
                        ok = ApplyStorage(msg, put: true);
                        break;
                    case InteractionKind.StorageTake:
                        ok = ApplyStorage(msg, put: false);
                        break;
                    case InteractionKind.Gunshot:
                        ApplyGunshot(new Vector3(msg.Float0, msg.Float1, msg.Float2), net);
                        ok = true;
                        break;
                    case InteractionKind.MultiCondition:
                        ok = ApplyMultiCondition(id);
                        break;
                    case InteractionKind.SceneFollowRequest:
                        SceneFollowService.Apply(msg.Text);
                        ok = true;
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
                p.solved = true;
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

        private static bool ApplyEventScreen(ulong id, LanNetworkManager net, bool start)
        {
            var e = Find<EventScreenInteraction>(id);
            if (e == null) return false;
            NetGate.BeginApply();
            try
            {
                if (start) e.startEventInstant();
                else e.exitEvent();
            }
            finally { NetGate.EndApply(); }
            net.StorySync.BroadcastPresentation(start ? StoryCmd.EventScreenStart : StoryCmd.EventScreenExit, id, 0, "");
            return true;
        }

        private static bool ApplyStorage(InteractionRequestMessage msg, bool put)
        {
            var item = InventoryManager.getItem((Items.itemlist)msg.Int0);
            if (item == null) return false;
            int n = msg.Int1 > 0 ? msg.Int1 : 1;
            NetGate.BeginApply();
            try
            {
                if (put) InventoryManager.storeItem(item, n);
                else InventoryManager.retrieveItem(item, n);
            }
            finally { NetGate.EndApply(); }
            var net = LanNetworkManager.Instance;
            net?.StorageSync.RequestSend();
            net?.StorageSync.SendNow(net);
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
                var all = Object.FindObjectsOfType<T>();
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
    }
}
