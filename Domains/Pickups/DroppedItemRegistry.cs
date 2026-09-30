// Dropped-item world registry + claim/inspect helpers (Domains peel from DroppedItemManager).
using System.Collections.Generic;
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.ItemSystem
{
    public static class DroppedItemRegistry
    {
        public const string NamePrefix = "SR_Drop_";

        public struct Drop
        {
            public GameObject Go;
            public Items.itemlist Item;
            public int Count;
            public int Key;
            public string Scene;
            public Vector3 Pos;
        }

        private static readonly Dictionary<int, Drop> _worldItems = new Dictionary<int, Drop>();
        private static readonly List<int> _keyScratch = new List<int>(32);

        static int _deferKey = -1;
        static float _deferAt;

        public static IEnumerable<Drop> All()
        {
            foreach (var kvp in _worldItems)
                yield return kvp.Value;
        }

        public static bool TryGetExisting(int netID, out Drop drop)
        {
            return _worldItems.TryGetValue(netID, out drop);
        }

        public static void Put(int netID, Drop drop)
        {
            _worldItems[netID] = drop;
        }

        public static void Remove(int netID)
        {
            _worldItems.Remove(netID);
        }

        public static bool IsDropped(ItemPickup p)
        {
            if (p == null) return false;
            try { return IsDroppedGo(p.gameObject); }
            catch { return false; }
        }

        public static bool IsDroppedGo(GameObject go)
        {
            int key;
            return FindDrop(go, out key, true);
        }

        public static bool TryKeyOf(ItemPickup p, out int key)
        {
            key = -1;
            if (p == null) return false;
            try { return TryKeyOfGo(p.gameObject, out key); }
            catch { return false; }
        }

        public static bool TryKeyOfGo(GameObject go, out int key)
        {
            return FindDrop(go, out key, false);
        }

        static bool FindDrop(GameObject go, out int key, bool prefixCounts)
        {
            key = -1;
            Transform t = null;
            try { if (go != null) t = go.transform; } catch { return false; }
            while (t != null)
            {
                GameObject g = null;
                try { g = t.gameObject; } catch { break; }
                if (g != null)
                {
                    string n = null;
                    try { n = g.name; } catch { }
                    if (!string.IsNullOrEmpty(n) && n.StartsWith(NamePrefix, System.StringComparison.Ordinal))
                    {
                        int parsed;
                        if (int.TryParse(n.Substring(NamePrefix.Length), out parsed))
                        {
                            key = parsed;
                            return true;
                        }
                        if (prefixCounts)
                            return true;
                    }
                    foreach (var kvp in _worldItems)
                    {
                        if (kvp.Value.Go == g)
                        {
                            key = kvp.Key;
                            return true;
                        }
                    }
                }
                try { t = t.parent; } catch { break; }
            }
            return false;
        }

        public static Vector3 FloorDropPos(Transform player)
        {
            Vector3 pos = player != null ? player.position : Vector3.zero;
            try
            {
                var f = PlayerState.facing;
                if (f == PlayerState.face.E || f == PlayerState.face.NE || f == PlayerState.face.SE)
                    pos.x += 0.55f;
                else if (f == PlayerState.face.W || f == PlayerState.face.NW || f == PlayerState.face.SW)
                    pos.x -= 0.55f;
                else if (f == PlayerState.face.N)
                    pos.y += 0.35f;
            }
            catch { }
            return pos;
        }

        public static int SanitizeStack(int n, bool unique)
        {
            if (unique) return 1;
            if (n < 1 || n > 99) return 1;
            return n;
        }

        public static int CountInBag(Items.itemlist id)
        {
            if (id == Items.itemlist.None) return 0;
            try
            {
                var dict = InventoryManager.elsterItems;
                if (dict == null) return 0;
                var en = dict.GetEnumerator();
                int n = 0;
                while (en.MoveNext())
                {
                    var held = en.Current.key;
                    if (held == null || held._item != id) continue;
                    int v = en.Current.value;
                    if (v > 0 && v <= 99) n += v;
                    else if (v > 99) n += 1;
                }
                en.Dispose();
                return n;
            }
            catch { return 0; }
        }

        public static Interaction NearbyInteraction(Vector3 pos, float maxDist)
        {
            Interaction best = null;
            float bestD = maxDist;
            foreach (var kvp in _worldItems)
            {
                var go = kvp.Value.Go;
                if (go == null) continue;
                try
                {
                    if (!go.activeInHierarchy) continue;
                    var inter = go.GetComponent<Interaction>();
                    if (inter == null || inter.triggered) continue;
                    float dx = go.transform.position.x - pos.x;
                    float dy = go.transform.position.y - pos.y;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = inter;
                    }
                }
                catch { }
            }
            return best;
        }

        public static void SetHighlight(GameObject go, bool on)
        {
            if (go == null) return;
            try
            {
                var outlines = go.GetComponentsInChildren<cakeslice.Outline>(true);
                if (outlines == null) return;
                for (int i = 0; i < outlines.Length; i++)
                {
                    if (outlines[i] == null) continue;
                    try { outlines[i].enabled = on; } catch { }
                }
            }
            catch { }
        }

        public static bool InspectLocked()
        {
            try
            {
                var gs = PlayerState.gameState;
                if (gs == PlayerState.gameStates.dialogue
                    || gs == PlayerState.gameStates.eventScreen
                    || gs == PlayerState.gameStates.book)
                    return true;
            }
            catch { }
            try { if (PlayerState.paused || PlayerState.eventScreen || PlayerState.suspendInput) return true; }
            catch { }
            return false;
        }

        public static void RestorePlay()
        {
            try { PlayerState.paused = false; } catch { }
            try { PlayerState.eventScreen = false; } catch { }
            try { PlayerState.suspendInput = false; } catch { }
            try
            {
                var all = WorldLookup.All<InventoryBase>();
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var inv = all[i];
                        if (inv == null) continue;
                        try
                        {
                            if (inv.inventoryOpen)
                                inv.inventoryOpen = false;
                        }
                        catch { }
                        try
                        {
                            if (inv.intMenuOn)
                                inv.ToggleInteractMenu();
                        }
                        catch { }
                    }
                }
            }
            catch { }
            try
            {
                var gs = PlayerState.gameState;
                if (gs == PlayerState.gameStates.dialogue
                    || gs == PlayerState.gameStates.eventScreen
                    || gs == PlayerState.gameStates.book
                    || gs == PlayerState.gameStates.paused
                    || gs == PlayerState.gameStates.inventory
                    || gs == PlayerState.gameStates.menu)
                    PlayerState.gameState = PlayerState.gameStates.play;
            }
            catch { }
        }

        /// <summary>
        /// RestorePlay + story state, for a follow load / disconnect: a peer that was mid-cutscene or mid-Dialoguer
        /// when the host loaded another scene kept gameStates.cutscene / PlayerState.cutscene / dialogue sticky
        /// (the coroutine that would clear them dies with the unloaded scene). Not used by pickup inspect paths.
        /// </summary>
        public static void RestorePlayForLoad()
        {
            RestorePlay();
            try
            {
                if (PlayerState.gameState == PlayerState.gameStates.dialogue)
                {
                    SyncRADation.Sync.NetGate.BeginApply();
                    try { Dialoguer.EndDialogue(); }
                    finally { SyncRADation.Sync.NetGate.EndApply(); }
                }
            }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] RestorePlayForLoad dialogue: " + ex.Message); }
            try { SyncRADation.Patches.DialoguerGate.ClearFlavor(); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] RestorePlayForLoad flavor: " + ex.Message); }
            try { PlayerState.cutscene = false; } catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] RestorePlayForLoad cutscene: " + ex.Message); }
            try
            {
                if (PlayerState.gameState == PlayerState.gameStates.cutscene)
                    PlayerState.gameState = PlayerState.gameStates.play;
            }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] RestorePlayForLoad gameState: " + ex.Message); }
            try { CutsceneSkippingUI.skippableCutscene = false; } catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] RestorePlayForLoad skippable: " + ex.Message); }
        }

        public static void HideForClaim(int netID)
        {
            var go = GetItem(netID);
            if (go == null) return;
            try
            {
                var cols = go.GetComponents<BoxCollider2D>();
                if (cols != null)
                {
                    for (int i = 0; i < cols.Length; i++)
                    {
                        if (cols[i] != null) cols[i].enabled = false;
                    }
                }
            }
            catch { }
            try
            {
                var inter = go.GetComponent<Interaction>();
                if (inter != null)
                {
                    inter.triggered = true;
                    inter.inRange = false;
                    inter.enabled = false;
                }
            }
            catch { }
            if (!InspectLocked())
            {
                try
                {
                    if (go == null) return;
                    var rends = go.GetComponentsInChildren<Renderer>(true);
                    if (rends != null)
                    {
                        for (int i = 0; i < rends.Length; i++)
                        {
                            if (rends[i] != null) rends[i].enabled = false;
                        }
                    }
                }
                catch { }
            }
            SetHighlight(go, false);
        }

        public static void DespawnWhenIdle(int netID)
        {
            HideForClaim(netID);
            if (InspectLocked())
            {
                _deferKey = netID;
                _deferAt = Time.unscaledTime;
                return;
            }
            DespawnItem(netID);
            _deferKey = -1;
        }

        public static void TickDeferred()
        {
            try { SyncRADation.Patches.ItemPickupPatches.TickPendingDrop(); } catch { }
            if (_deferKey < 0) return;
            bool wait = InspectLocked();
            if (wait && Time.unscaledTime - _deferAt < 2.5f) return;
            if (wait)
            {
                RestorePlay();
                ModRuntime.Log?.Msg("[Drop] restored play after inspect (deferred despawn)");
            }
            DespawnItem(_deferKey);
            _deferKey = -1;
        }

        public static void DespawnItem(int netID)
        {
            Drop drop;
            if (_worldItems.TryGetValue(netID, out drop))
            {
                if (drop.Go != null)
                {
                    try { drop.Go.SetActive(false); } catch { }
                    try { Object.Destroy(drop.Go); } catch { }
                }
                _worldItems.Remove(netID);
            }
        }

        public static void ClearVisuals()
        {
            _keyScratch.Clear();
            foreach (var k in _worldItems.Keys)
                _keyScratch.Add(k);
            for (int i = 0; i < _keyScratch.Count; i++)
            {
                int k = _keyScratch[i];
                Drop drop = _worldItems[k];
                if (drop.Go != null) Object.Destroy(drop.Go);
                drop.Go = null;
                _worldItems[k] = drop;
            }
            DroppedItemSpawner.InvalidateCaches();
        }

        public static void RespawnCurrentScene()
        {
            string scene = "";
            try { scene = SceneManager.GetActiveScene().name ?? ""; } catch { }
            _keyScratch.Clear();
            foreach (var k in _worldItems.Keys)
                _keyScratch.Add(k);
            for (int i = 0; i < _keyScratch.Count; i++)
            {
                int k = _keyScratch[i];
                Drop drop = _worldItems[k];
                if (drop.Go != null) continue;
                if (!string.IsNullOrEmpty(drop.Scene) && drop.Scene != scene) continue;
                _worldItems.Remove(k);
                DroppedItemSpawner.SpawnLocalItem(drop.Item, drop.Count, drop.Key, drop.Pos);
            }
        }

        public static void ClearAll()
        {
            foreach (var drop in _worldItems.Values)
            {
                if (drop.Go != null) Object.Destroy(drop.Go);
            }
            _worldItems.Clear();
            _keyScratch.Clear();
            _deferKey = -1;
            DroppedItemSpawner.InvalidateCaches();
        }

        public static GameObject GetItem(int netID)
        {
            Drop drop;
            if (_worldItems.TryGetValue(netID, out drop))
                return drop.Go;
            return null;
        }

        public static bool TryGet(int netID, out Items.itemlist item, out int count)
        {
            Drop drop;
            if (_worldItems.TryGetValue(netID, out drop))
            {
                item = drop.Item;
                count = drop.Count;
                return true;
            }
            item = Items.itemlist.None;
            count = 0;
            return false;
        }
    }
}
