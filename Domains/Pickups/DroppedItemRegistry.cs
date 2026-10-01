// Player-dropped items: the key → floor object registry, lookup by object, inspect-safe despawn and play restore.
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
            try { return FindDrop(p.gameObject, out key, false); }
            catch { return false; }
        }

        /// <summary>The drop this object (or one of its parents) belongs to: SR_Drop_&lt;key&gt; name, else a registry match.</summary>
        static bool FindDrop(GameObject go, out int key, bool prefixCounts)
        {
            key = -1;
            if (go == null) return false;
            try
            {
                for (var t = go.transform; t != null; t = t.parent)
                {
                    var g = t.gameObject;
                    if (g == null) continue;
                    string n = g.name;
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
            }
            catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
            return pos;
        }

        public static int SanitizeStack(int n, bool unique)
        {
            if (unique) return 1;
            if (n < 1 || n > 99) return 1;
            return n;
        }

        /// <summary>Move a registered drop to a new key (and rename its GameObject). False when absent / newKey taken.</summary>
        public static bool Rekey(int oldKey, int newKey)
        {
            if (oldKey == newKey) return false;
            Drop drop;
            if (!_worldItems.TryGetValue(oldKey, out drop)) return false;
            if (_worldItems.ContainsKey(newKey)) return false;
            _worldItems.Remove(oldKey);
            drop.Key = newKey;
            _worldItems[newKey] = drop;
            try { if (drop.Go != null) drop.Go.name = NamePrefix + newKey; }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (_deferKey == oldKey) _deferKey = newKey;
            return true;
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
                catch (System.Exception e) { Guard.Swallow(e); }
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
                    if (outlines[i] != null) outlines[i].enabled = on;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static bool InspectLocked()
        {
            try
            {
                var gs = PlayerState.gameState;
                return gs == PlayerState.gameStates.dialogue
                    || gs == PlayerState.gameStates.eventScreen
                    || gs == PlayerState.gameStates.book
                    || PlayerState.paused || PlayerState.eventScreen || PlayerState.suspendInput;
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        public static void RestorePlay()
        {
            try
            {
                PlayerState.paused = false;
                PlayerState.eventScreen = false;
                PlayerState.suspendInput = false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            var all = WorldLookup.All<InventoryBase>();
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    var inv = all[i];
                    if (inv == null) continue;
                    try
                    {
                        if (inv.inventoryOpen) inv.inventoryOpen = false;
                        if (inv.intMenuOn) inv.ToggleInteractMenu();
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
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
            catch (System.Exception e) { Guard.Swallow(e); }
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
                    // EndDialogue fires the dialogue's end callbacks, which can start a load of their own (and
                    // IsApplying lets loads through): swallow loads for the duration, the follow's own load comes after.
                    NetGate.BeginApply();
                    SyncRADation.Networking.SceneFollowService.BeginSuppressLoads();
                    try { Dialoguer.EndDialogue(); }
                    finally
                    {
                        SyncRADation.Networking.SceneFollowService.EndSuppressLoads();
                        NetGate.EndApply();
                    }
                }
            }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] RestorePlayForLoad dialogue: " + ex.Message); }
            try { SyncRADation.Patches.DialoguerGate.ClearFlavor(); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] RestorePlayForLoad flavor: " + ex.Message); }
            try
            {
                PlayerState.cutscene = false;
                if (PlayerState.gameState == PlayerState.gameStates.cutscene)
                    PlayerState.gameState = PlayerState.gameStates.play;
                CutsceneSkippingUI.skippableCutscene = false;
            }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] RestorePlayForLoad cutscene: " + ex.Message); }
        }

        /// <summary>Make a claimed floor item untouchable now (colliders, interaction) and invisible unless an inspect is open on it.</summary>
        public static void HideForClaim(int netID)
        {
            var go = GetItem(netID);
            if (go == null) return;
            try
            {
                var cols = go.GetComponents<BoxCollider2D>();
                if (cols != null)
                    for (int i = 0; i < cols.Length; i++)
                        if (cols[i] != null) cols[i].enabled = false;
                var inter = go.GetComponent<Interaction>();
                if (inter != null)
                {
                    inter.triggered = true;
                    inter.inRange = false;
                    inter.enabled = false;
                }
                if (!InspectLocked())
                {
                    var rends = go.GetComponentsInChildren<Renderer>(true);
                    if (rends != null)
                        for (int i = 0; i < rends.Length; i++)
                            if (rends[i] != null) rends[i].enabled = false;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
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
            try { DroppedItemSpawner.TickRest(); } catch (System.Exception e) { Guard.Swallow(e); }
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
            if (!_worldItems.TryGetValue(netID, out drop)) return;
            if (drop.Go != null)
            {
                try
                {
                    drop.Go.SetActive(false);
                    Object.Destroy(drop.Go);
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            _worldItems.Remove(netID);
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
            try { scene = SceneManager.GetActiveScene().name ?? ""; } catch (System.Exception e) { Guard.Swallow(e); }
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
            return _worldItems.TryGetValue(netID, out drop) ? drop.Go : null;
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
