// Dynamic difficulty supply props, decided once for the party by the host.
using System.Collections.Generic;
using HarmonyLib;
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    /// <summary>
    /// DynamicSupply (39 props: ROT / LAB / MED / RES / EXC) and DynamicResupplyPickup (BOS_Adler) pick what a room
    /// offers from the local player's own stock (Ghidra DynamicSupply.c Entered: first entry sets SProgress "dyn: id",
    /// then switches the pickup off when that player has heals / ammo to spare; DynamicResupplyPickup.c OnEnable: an
    /// unset SProgress int picks one of `pickups` from the weapons the player holds, 1000 = fallback). The key rides the
    /// story commit, the choice did not: each peer decided from its own bag, so a prop was there for one and gone for
    /// the other. The host decides for the party.
    /// </summary>
    public static class DynamicSupplySync
    {
        // instance id -> the choice shown here (client)
        static readonly Dictionary<int, int> _shown = new Dictionary<int, int>();

        public static void Reset() => _shown.Clear();

        static bool ClientInParty => NetGate.ClientRole && NetGate.Party;

        // ------------------------------------------------------------------ DynamicSupply

        /// <summary>Entered prefix: a client never decides (the host's withhold reaches it as a pickup state).</summary>
        internal static bool BeforeEntered(DynamicSupply d, out bool wasActive)
        {
            wasActive = false;
            if (ClientInParty) return false;
            try { wasActive = d != null && d.pickup != null && d.pickup.gameObject.activeSelf; }
            catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }

        internal static void AfterEntered(DynamicSupply d, bool wasActive)
        {
            if (!wasActive || !NetGate.Host) return;
            try
            {
                if (d.pickup != null && !d.pickup.gameObject.activeSelf)
                    LanNetworkManager.Instance?.PickupSync.NoteWithheld(d.pickup);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        // ------------------------------------------------------------------ DynamicResupplyPickup

        /// <summary>OnEnable prefix (client): a choice made here stays local until the host's arrives.</summary>
        internal static void BeforeResupply() { if (ClientInParty) StorySyncService.BeginSuppressForward(); }

        internal static void AfterResupply(DynamicResupplyPickup r)
        {
            if (!ClientInParty) return;
            StorySyncService.EndSuppressForward();
            int k;
            if (r != null && TryChoice(r, out k)) _shown[r.GetInstanceID()] = k;
        }

        static bool TryChoice(DynamicResupplyPickup r, out int choice)
        {
            choice = -1;
            try
            {
                var uid = r.GetComponent<UniqueId>();
                if (uid == null || string.IsNullOrEmpty(uid.id)) return false;
                choice = SProgress.GetInt(uid.id, -1);
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
            return choice != -1;
        }

        /// <summary>Client, after a story commit applied: show the host's choice where it differs from the local one.</summary>
        public static void ReconcileResupply()
        {
            if (!ClientInParty) return;
            var all = WorldLookup.All<DynamicResupplyPickup>();
            if (all == null) return;
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                int k, shown;
                if (r == null || !TryChoice(r, out k)) continue;
                int key = r.GetInstanceID();
                if (_shown.TryGetValue(key, out shown) && shown == k) continue;
                _shown[key] = k;
                Show(r, k);
            }
        }

        // Native OnEnable for a set choice: fallback on for 1000, else only pickups[k] on. A prop already taken stays off.
        static void Show(DynamicResupplyPickup r, int k)
        {
            try
            {
                if (r.fallback != null) r.fallback.SetActive(k == 1000);
                var list = r.pickups;
                int n = list != null ? list.Count : 0;
                for (int i = 0; i < n; i++)
                {
                    var p = list[i];
                    if (p == null) continue;
                    bool on = i == k && !p.triggered && !(LanNetworkManager.Instance?.PickupSync.IsClaimedPickup(p) ?? false);
                    p.gameObject.SetActive(on);
                }
                PlaytestLog.Event("Pickup", "resupply choice from host: " + (k == 1000 ? "fallback" : "#" + k));
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(DynamicSupply), nameof(DynamicSupply.Entered))]
    public static class DynamicSupplyEnteredPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(DynamicSupply __instance, out bool __state)
            => Networking.DynamicSupplySync.BeforeEntered(__instance, out __state);

        [HarmonyPostfix]
        public static void Postfix(DynamicSupply __instance, bool __state)
            => Networking.DynamicSupplySync.AfterEntered(__instance, __state);
    }

    [HarmonyPatch(typeof(DynamicResupplyPickup), "OnEnable")]
    public static class DynamicResupplyEnablePatch
    {
        [HarmonyPrefix]
        public static void Prefix() => Networking.DynamicSupplySync.BeforeResupply();

        [HarmonyFinalizer]
        public static System.Exception Finalizer(DynamicResupplyPickup __instance, System.Exception __exception)
        {
            Networking.DynamicSupplySync.AfterResupply(__instance);
            return __exception;
        }
    }
}
