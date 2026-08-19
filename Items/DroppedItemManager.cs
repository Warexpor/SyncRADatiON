// Player-dropped props: clone a native ItemPickup so TAKE / mesh / room-chunk hide match authored pickups.
using System.Collections.Generic;
using SyncRADation.Networking;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace SyncRADation.ItemSystem
{
    public static class DroppedItemManager
    {
        public const string NamePrefix = "SR_Drop_";
        public static int NearbyID = -1;

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

        public static IEnumerable<Drop> All()
        {
            foreach (var kvp in _worldItems)
                yield return kvp.Value;
        }

        public static bool IsDropped(ItemPickup p)
        {
            if (p == null) return false;
            try { return IsDroppedGo(p.gameObject); }
            catch { return false; }
        }

        public static bool IsDroppedGo(GameObject go)
        {
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
                        return true;
                    foreach (var kvp in _worldItems)
                    {
                        if (kvp.Value.Go == g) return true;
                    }
                }
                try { t = t.parent; } catch { break; }
            }
            return false;
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

        public static GameObject SpawnLocalItem(Items.itemlist item, int count, int netID, Vector3 pos)
        {
            if (_worldItems.TryGetValue(netID, out var existing) && existing.Go != null)
                return existing.Go;

            GameObject go = null;
            try { go = TryCloneNative(item, count, netID, pos); }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] clone failed: " + ex.Message);
            }
            if (go == null)
            {
                try { go = SpawnFallbackPickup(item, count, netID, pos); }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.Warning("[Drop] fallback failed: " + ex.Message);
                    return null;
                }
            }

            LogSpawn(go, item, pos);

            Vector3 stored = pos;
            try { if (go != null) stored = go.transform.position; } catch { }
            string scene = "";
            try
            {
                scene = SceneManager.GetActiveScene().name ?? "";
                if (SceneFollowService.IsTransient(scene)) scene = "";
            }
            catch { }

            _worldItems[netID] = new Drop
            {
                Go = go,
                Item = item,
                Count = SanitizeStack(count, PartyKeyRing.IsKeyOrObject(item)),
                Key = netID,
                Scene = scene,
                Pos = stored
            };
            return go;
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

        public static void TickNearby()
        {
            var player = PlayerState.player;
            if (player == null)
            {
                NearbyID = -1;
                return;
            }
            Vector3 p = player.transform.position;
            int nearest = -1;
            float best = 1.8f;
            foreach (var kvp in _worldItems)
            {
                var go = kvp.Value.Go;
                if (go == null) continue;
                try
                {
                    if (!go.activeInHierarchy) continue;
                }
                catch { continue; }
                float dx = go.transform.position.x - p.x;
                float dy = go.transform.position.y - p.y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist < best)
                {
                    best = dist;
                    nearest = kvp.Key;
                }
            }
            NearbyID = nearest;
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

        static int _deferKey = -1;
        static float _deferAt;

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
                var gs = PlayerState.gameState;
                if (gs == PlayerState.gameStates.dialogue
                    || gs == PlayerState.gameStates.eventScreen
                    || gs == PlayerState.gameStates.book
                    || gs == PlayerState.gameStates.paused)
                    PlayerState.gameState = PlayerState.gameStates.play;
            }
            catch { }
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

        static void EnsureOutlines(GameObject go)
        {
            if (go == null) return;
            Renderer[] rends = null;
            try { rends = go.GetComponentsInChildren<Renderer>(true); } catch { return; }
            if (rends == null) return;
            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i];
                if (r == null) continue;
                try { if (!r.enabled) continue; } catch { continue; }
                try
                {
                    if (r.GetComponent<cakeslice.Outline>() != null) continue;
                    var outline = r.gameObject.AddComponent<cakeslice.Outline>();
                    if (outline != null)
                    {
                        outline.color = 0;
                        outline.enabled = false;
                    }
                }
                catch { }
            }
        }

        public static void DespawnItem(int netID)
        {
            Drop drop;
            if (_worldItems.TryGetValue(netID, out drop))
            {
                if (drop.Go != null)
                {
                    try { drop.Go.SetActive(false); } catch { }
                    Object.Destroy(drop.Go);
                }
                _worldItems.Remove(netID);
            }
            if (NearbyID == netID)
                NearbyID = -1;
        }

        public static void ClearVisuals()
        {
            var keys = new List<int>(_worldItems.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                int k = keys[i];
                Drop drop = _worldItems[k];
                if (drop.Go != null) Object.Destroy(drop.Go);
                drop.Go = null;
                _worldItems[k] = drop;
            }
            NearbyID = -1;
        }

        public static void RespawnCurrentScene()
        {
            string scene = "";
            try { scene = SceneManager.GetActiveScene().name ?? ""; } catch { }
            var keys = new List<int>(_worldItems.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                int k = keys[i];
                Drop drop = _worldItems[k];
                if (drop.Go != null) continue;
                if (!string.IsNullOrEmpty(drop.Scene) && drop.Scene != scene) continue;
                _worldItems.Remove(k);
                SpawnLocalItem(drop.Item, drop.Count, drop.Key, drop.Pos);
            }
        }

        public static void ClearAll()
        {
            foreach (var drop in _worldItems.Values)
            {
                if (drop.Go != null) Object.Destroy(drop.Go);
            }
            _worldItems.Clear();
            NearbyID = -1;
            _deferKey = -1;
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

        static void LogSpawn(GameObject go, Items.itemlist item, Vector3 pos)
        {
            if (go == null)
            {
                ModRuntime.Log?.Warning("[Drop] spawn failed " + item);
                return;
            }
            int rends = 0;
            try
            {
                var rs = go.GetComponentsInChildren<Renderer>(true);
                if (rs != null)
                {
                    for (int i = 0; i < rs.Length; i++)
                    {
                        try { if (rs[i] != null && rs[i].enabled) rends++; } catch { }
                    }
                }
            }
            catch { }
            string parent = "null";
            try { if (go.transform.parent != null) parent = go.transform.parent.name; } catch { }
            bool active = false;
            try { active = go.activeInHierarchy; } catch { }
            bool trig = false;
            float cx = 0f, cy = 0f;
            int layer = -1;
            try
            {
                layer = go.layer;
                var col = go.GetComponent<BoxCollider2D>();
                if (col != null)
                {
                    trig = col.isTrigger;
                    var b = col.bounds.size;
                    cx = Mathf.Max(col.size.x, b.x);
                    cy = Mathf.Max(col.size.y, b.y);
                }
            }
            catch { }
            ModRuntime.Log?.Msg("[Drop] spawn " + go.name + " " + item
                + " pos=" + go.transform.position.x.ToString("F1") + ","
                + go.transform.position.y.ToString("F1") + ","
                + go.transform.position.z.ToString("F1")
                + " parent=" + parent
                + " active=" + active
                + " rends=" + rends
                + " layer=" + layer
                + " trigger=" + trig
                + " col=" + cx.ToString("F1") + "x" + cy.ToString("F1"));
        }
        static GameObject TryCloneNative(Items.itemlist item, int count, int netID, Vector3 pos)
        {
            ItemPickup src = FindTemplate(item);
            if (src == null || src.gameObject == null) return null;

            GameObject holder = new GameObject("SR_DropHold");
            holder.SetActive(false);
            GameObject go;
            try { go = Object.Instantiate(src.gameObject, holder.transform, false); }
            catch
            {
                Object.Destroy(holder);
                return null;
            }
            if (go == null)
            {
                Object.Destroy(holder);
                return null;
            }

            go.name = NamePrefix + netID;
            StripUniqueId(go, src);
            StripInspectJunk(go);
            try { go.transform.SetParent(null, true); } catch { }
            Object.Destroy(holder);
            try { go.transform.localScale = Vector3.one; } catch { }

            bool same = ResolveItem(src) == item;
            ApplyPickupFields(go, item, count, same);
            if (!same)
                ApplyCatalogMesh(go, item);
            PlaceInWorld(go, pos);
            try { go.SetActive(true); } catch { }
            if (!same)
            {
                var m3 = go.transform.Find("Model3D");
                if (m3 != null) FitMeshToNative(m3.gameObject);
            }
            RestOnFloor(go);
            try { FinishInteractable(go); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] finish: " + ex.Message); }
            try { Physics2D.SyncTransforms(); } catch { }
            return go;
        }

        static void ApplyPickupFields(GameObject go, Items.itemlist item, int count, bool sameVisual)
        {
            var p = go.GetComponent<ItemPickup>();
            if (p == null) return;

            AnItem catalog = null;
            try { catalog = InventoryManager.getItem(item); } catch { }

            try { p.triggered = false; } catch { }
            try { p.slave = false; } catch { }
            try { p.count = SanitizeStack(count, PartyKeyRing.IsKeyOrObject(item)); } catch { }
            try { p.focusCamera = true; } catch { }
            try { p.pauseGame = true; } catch { }
            try { p.showItemView = true; } catch { }
            try { p.fadeOnPickup = false; } catch { }
            try { p.playPickupAnimation = false; } catch { }
            try { p.dontDestroyOnPickup = true; } catch { }
            try { p.message = ""; } catch { }
            try { p.onPickup = new UnityEvent(); } catch { }
            if (catalog != null)
            {
                try { p._item = catalog; } catch { }
                try { p._itemEnum = catalog._item; } catch { }
            }

            var inter = go.GetComponent<Interaction>();
            if (inter != null)
            {
                try { inter.triggered = false; } catch { }
                try { inter.inRange = false; } catch { }
                try { inter.enabled = true; } catch { }
                try { inter.anyAngle = true; } catch { }
                try { inter.type = Interaction.interType.take; } catch { }
                try { p.inter = inter; } catch { }
            }

            var col = go.GetComponent<BoxCollider2D>();
            if (col == null) col = go.AddComponent<BoxCollider2D>();
            try { col.enabled = true; } catch { }
            try { col.isTrigger = false; } catch { }
            try
            {
                if (col.size.x < 4f || col.size.y < 4f)
                    col.size = new Vector2(6.4f, 6.4f);
            }
            catch { }

            int layer = LayerMask.NameToLayer("Interactables");
            if (layer < 0) layer = 19;
            SetLayer(go, layer);
        }

        static void FinishInteractable(GameObject go)
        {
            if (go == null) return;
            try { go.SetActive(true); } catch { }
            var p = go.GetComponent<ItemPickup>();
            if (p != null)
            {
                try { p.enabled = true; } catch { }
                try { p.triggered = false; } catch { }
                try { p.slave = false; } catch { }
            }
            var inter = go.GetComponent<Interaction>();
            if (inter != null)
            {
                try { inter.enabled = true; } catch { }
                try { inter.triggered = false; } catch { }
                try { inter.inRange = false; } catch { }
                try { inter.anyAngle = true; } catch { }
                try { inter.type = Interaction.interType.take; } catch { }
                if (p != null)
                {
                    try { p.inter = inter; } catch { }
                }
            }
            var col = go.GetComponent<BoxCollider2D>();
            if (col != null)
            {
                try { col.enabled = true; } catch { }
                try { col.isTrigger = false; } catch { }
            }
            RebuildCollider(go);
            int layer = LayerMask.NameToLayer("Interactables");
            if (layer < 0) layer = 19;
            SetLayer(go, layer);
            EnsureOutlines(go);
            try
            {
                if (go.GetComponent<DroppedItemAnchor>() == null)
                    go.AddComponent<DroppedItemAnchor>();
            }
            catch { }
        }

        static void ApplyCatalogMesh(GameObject go, Items.itemlist item)
        {
            AnItem catalog = null;
            try { catalog = InventoryManager.getItem(item); } catch { }
            if (catalog == null || go == null) return;

            GameObject prefab = null;
            try { prefab = catalog.Image3D; } catch { }
            if (prefab == null) return;

            Transform slot = FindModel(go.transform);
            Vector3 lp = new Vector3(0f, 0.07f, 0f);
            Quaternion lr = Quaternion.Euler(-90f, 0f, -31f);
            Vector3 ls = Vector3.one * 0.5f;
            if (slot != null)
            {
                lp = slot.localPosition;
                lr = slot.localRotation;
                ls = slot.localScale;
            }

            GameObject vis;
            try { vis = Object.Instantiate(prefab, go.transform, false); }
            catch { return; }
            if (vis == null) return;
            vis.name = "Model3D";
            vis.transform.localPosition = lp;
            vis.transform.localRotation = lr;
            vis.transform.localScale = ls;
            SetLayer(vis, go.layer);
            StripInspectJunk(vis);
            FitMeshToNative(vis);

            if (slot != null)
            {
                try { slot.gameObject.SetActive(false); } catch { }
            }
        }

        static void FitMeshToNative(GameObject vis)
        {
            if (vis == null) return;
            Renderer r = vis.GetComponentInChildren<Renderer>();
            if (r == null) return;
            float m = Mathf.Max(r.bounds.size.x, Mathf.Max(r.bounds.size.y, r.bounds.size.z));
            if (m < 0.05f) return;
            float target = 1.2f;
            try
            {
                var near = NearestNativeModel(vis.transform.position);
                if (near != null)
                {
                    var nr = near.GetComponent<Renderer>();
                    if (nr == null) nr = near.GetComponentInChildren<Renderer>();
                    if (nr != null)
                    {
                        var s = nr.bounds.size;
                        float n = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
                        if (n > 0.2f && n < 4f) target = n;
                    }
                }
            }
            catch { }
            if (m > 8f || m > target * 1.4f || m < target * 0.4f)
                vis.transform.localScale *= target / m;
        }

        public static void RestOnFloor(GameObject go)
        {
            if (go == null) return;
            Renderer r = null;
            try { r = go.GetComponentInChildren<Renderer>(); } catch { return; }
            if (r == null) return;
            try
            {
                var s = r.bounds.size;
                if (Mathf.Max(s.x, Mathf.Max(s.y, s.z)) > 3f) return;
            }
            catch { return; }
            float dy = go.transform.position.y - r.bounds.min.y;
            if (dy <= 0.04f) return;
            if (dy > 0.7f) dy = 0.7f;
            Transform vis = go.transform.Find("Model3D");
            if (vis == null) vis = FindModel(go.transform);
            Vector3 lift = new Vector3(0f, dy, 0f);
            try
            {
                if (vis != null) vis.position += lift;
                else go.transform.position += lift;
            }
            catch { }
        }

        static void RebuildCollider(GameObject go)
        {
            if (go == null) return;
            try
            {
                var all = go.GetComponents<BoxCollider2D>();
                if (all == null || all.Length == 0)
                {
                    var created = go.AddComponent<BoxCollider2D>();
                    if (created == null) return;
                    created.offset = Vector2.zero;
                    created.size = new Vector2(6.4f, 6.4f);
                    created.isTrigger = false;
                    created.enabled = true;
                    return;
                }
                for (int i = 0; i < all.Length; i++)
                {
                    var col = all[i];
                    if (col == null) continue;
                    col.offset = Vector2.zero;
                    col.size = new Vector2(6.4f, 6.4f);
                    col.isTrigger = false;
                    col.enabled = true;
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] collider: " + ex.Message);
            }
        }

        static Transform FindModel(Transform root)
        {
            if (root == null) return null;
            var tfs = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < tfs.Length; i++)
            {
                if (tfs[i] != null && tfs[i] != root && tfs[i].name == "Model")
                    return tfs[i];
            }
            return null;
        }

        static void FitSpriteToNative(SpriteRenderer sr, Transform vis)
        {
            if (sr == null || sr.sprite == null || vis == null) return;
            float target = 1.1f;
            try
            {
                var near = NearestNativeModel(vis.position);
                if (near != null)
                {
                    var r = near.GetComponent<Renderer>();
                    if (r == null) r = near.GetComponentInChildren<Renderer>();
                    if (r != null)
                    {
                        var s = r.bounds.size;
                        float m = Mathf.Max(s.x, s.y);
                        if (m > 0.2f && m < 4f) target = m;
                    }
                }
            }
            catch { }

            Vector3 ext = sr.sprite.bounds.size;
            float src = Mathf.Max(ext.x, ext.y);
            if (src < 0.001f) return;
            float k = target / src;
            vis.localScale = Vector3.one * k;
        }

        static Transform NearestNativeModel(Vector3 pos)
        {
            ItemPickup[] all = null;
            try { all = Object.FindObjectsOfType<ItemPickup>(true); } catch { return null; }
            if (all == null) return null;
            Transform bestT = null;
            float best = 20f;
            for (int i = 0; i < all.Length; i++)
            {
                var p = all[i];
                if (p == null || IsDropped(p)) continue;
                var model = FindModel(p.transform);
                if (model == null) continue;
                float d = Vector3.Distance(p.transform.position, pos);
                if (d < best)
                {
                    best = d;
                    bestT = model;
                }
            }
            return bestT;
        }

        static Vector3 SnapToFloor(Vector3 pos)
        {
            ItemPickup near = NearestNativePickup(pos, 4f);
            if (near != null && near.transform != null)
            {
                pos.y = near.transform.position.y;
                pos.z = near.transform.position.z;
                return pos;
            }

            ItemPickup any = NearestNativePickup(pos, 80f);
            if (any != null && any.transform != null)
                pos.z = any.transform.position.z;
            else
                pos.z = 0f;
            return pos;
        }

        static ItemPickup NearestNativePickup(Vector3 pos, float maxDist)
        {
            ItemPickup[] all = null;
            try { all = Object.FindObjectsOfType<ItemPickup>(true); } catch { return null; }
            if (all == null) return null;
            ItemPickup best = null;
            float bestD = maxDist;
            for (int i = 0; i < all.Length; i++)
            {
                var p = all[i];
                if (p == null || IsDropped(p) || p.transform == null) continue;
                try { if (p.triggered) continue; } catch { }
                float d = Vector3.Distance(p.transform.position, pos);
                if (d < bestD)
                {
                    bestD = d;
                    best = p;
                }
            }
            return best;
        }

        static Sprite SpriteOf(AnItem catalog)
        {
            if (catalog == null) return null;
            try { if (catalog.worldSprite != null) return catalog.worldSprite; } catch { }
            try { if (catalog.Image != null) return catalog.Image; } catch { }
            try { if (catalog.Icon != null) return catalog.Icon; } catch { }
            return null;
        }

        static GameObject SpawnFallbackPickup(Items.itemlist item, int count, int netID, Vector3 pos)
        {
            var go = new GameObject(NamePrefix + netID);
            try { go.SetActive(false); } catch { }
            int layer = LayerMask.NameToLayer("Interactables");
            if (layer >= 0) go.layer = layer;

            var col = go.AddComponent<BoxCollider2D>();
            col.isTrigger = false;
            col.size = new Vector2(6.4f, 6.4f);

            var inter = go.AddComponent<Interaction>();
            try { inter.type = Interaction.interType.take; } catch { }
            try { inter.anyAngle = true; } catch { }

            var p = go.AddComponent<ItemPickup>();
            ApplyPickupFields(go, item, count, false);
            try { go.AddComponent<ItemPickupName>(); } catch { }
            ApplyCatalogMesh(go, item);
            PlaceInWorld(go, pos);
            try { go.SetActive(true); } catch { }
            var m3 = go.transform.Find("Model3D");
            if (m3 != null) FitMeshToNative(m3.gameObject);
            RestOnFloor(go);
            try { FinishInteractable(go); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] finish: " + ex.Message); }
            try { Physics2D.SyncTransforms(); } catch { }
            return go;
        }

        static ItemPickup FindTemplate(Items.itemlist item)
        {
            ItemPickup[] all = null;
            try { all = Resources.FindObjectsOfTypeAll<ItemPickup>(); }
            catch
            {
                try { all = Object.FindObjectsOfType<ItemPickup>(true); }
                catch { try { all = Object.FindObjectsOfType<ItemPickup>(); } catch { return null; } }
            }
            if (all == null) return null;

            ItemPickup matchLive = null;
            ItemPickup matchScene = null;
            ItemPickup matchAny = null;
            ItemPickup anyLive = null;
            ItemPickup anyScene = null;
            for (int i = 0; i < all.Length; i++)
            {
                var p = all[i];
                if (p == null || IsDropped(p)) continue;
                try { if (p.slave) continue; } catch { }
                if (VisualTooBig(p)) continue;
                bool inScene = false;
                bool live = false;
                bool spent = false;
                try { inScene = p.gameObject != null && p.gameObject.scene.IsValid(); } catch { }
                try { live = inScene && p.gameObject.activeInHierarchy; } catch { }
                try { spent = p.triggered; } catch { }
                if (live && !spent && anyLive == null) anyLive = p;
                if (inScene && !spent && anyScene == null) anyScene = p;
                if (ResolveItem(p) == item)
                {
                    if (live) { matchLive = p; break; }
                    if (inScene && matchScene == null) matchScene = p;
                    if (matchAny == null) matchAny = p;
                }
            }
            if (matchLive != null) return matchLive;
            if (matchScene != null) return matchScene;
            if (matchAny != null) return matchAny;
            if (anyLive != null) return anyLive;
            return anyScene;
        }

        static bool VisualTooBig(ItemPickup p)
        {
            try
            {
                var r = p.GetComponentInChildren<Renderer>();
                if (r == null) return false;
                var s = r.bounds.size;
                return Mathf.Max(s.x, Mathf.Max(s.y, s.z)) > 8f;
            }
            catch { return false; }
        }

        static void PlaceInWorld(GameObject go, Vector3 pos)
        {
            if (go == null) return;
            pos = SnapToFloor(pos);
            try { go.transform.SetParent(null, true); } catch { }
            try { go.transform.rotation = Quaternion.identity; } catch { }
            Transform room = RoomAt(pos);
            if (room != null)
            {
                try { go.transform.SetParent(room, true); } catch { }
            }
            try { go.transform.position = pos; } catch { }
        }

        static Transform RoomAt(Vector3 pos)
        {
            Room[] rooms = null;
            try { rooms = Object.FindObjectsOfType<Room>(true); } catch { }
            Transform best = null;
            float bestD = 80f;
            if (rooms != null)
            {
                for (int i = 0; i < rooms.Length; i++)
                {
                    var room = rooms[i];
                    if (room == null || room.transform == null) continue;
                    float dx = room.transform.position.x - pos.x;
                    float dy = room.transform.position.y - pos.y;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = room.transform;
                    }
                }
            }
            if (best != null) return best;
            try
            {
                if (PlayerState.currentRoom != null)
                    return PlayerState.currentRoom.transform;
            }
            catch { }
            return null;
        }

        static void StripInspectJunk(GameObject go)
        {
            if (go == null) return;
            try
            {
                var cams = go.GetComponentsInChildren<Camera>(true);
                for (int i = 0; i < cams.Length; i++)
                {
                    if (cams[i] != null) cams[i].enabled = false;
                }
            }
            catch { }
            try
            {
                var tfs = go.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < tfs.Length; i++)
                {
                    var t = tfs[i];
                    if (t == null || t == go.transform) continue;
                    string n = t.name ?? "";
                    if (n.IndexOf("Image3D", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("EventScreen", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Inspect", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try { t.gameObject.SetActive(false); } catch { }
                    }
                }
            }
            catch { }
        }

        static Items.itemlist ResolveItem(ItemPickup p)
        {
            if (p == null) return Items.itemlist.None;
            try
            {
                if (p._item != null && p._item._item != Items.itemlist.None)
                    return p._item._item;
            }
            catch { }
            try { return p._itemEnum; } catch { return Items.itemlist.None; }
        }

        static void ParentToRoom(GameObject go, Vector3 pos)
        {
            if (go == null) return;
            Transform parent = null;
            float best = 40f;
            ItemPickup[] all = null;
            try { all = Object.FindObjectsOfType<ItemPickup>(true); } catch { }
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    var p = all[i];
                    if (p == null || IsDropped(p) || p.gameObject == go) continue;
                    float d = Vector3.Distance(p.transform.position, pos);
                    if (d < best)
                    {
                        best = d;
                        parent = p.transform.parent;
                    }
                }
            }
            if (parent == null) return;
            try
            {
                var ls = parent.lossyScale;
                if (Mathf.Abs(ls.x - 1f) > 0.15f || Mathf.Abs(ls.y - 1f) > 0.15f)
                    return;
            }
            catch { }
            try { go.transform.SetParent(parent, true); } catch { }
        }

        static void StripUniqueId(GameObject go, ItemPickup src)
        {
            UniqueId uid = null;
            try { uid = go.GetComponent<UniqueId>(); } catch { }
            if (uid == null) return;
            string stolen = null;
            try { stolen = uid.id; } catch { }
            try { Object.DestroyImmediate(uid); } catch { try { Object.Destroy(uid); } catch { } }
            if (string.IsNullOrEmpty(stolen) || src == null) return;
            try
            {
                var original = src.GetComponent<UniqueId>();
                var all = UniqueId.allGuids;
                if (original != null && all != null)
                    all[stolen] = original;
            }
            catch { }
        }

        static void SetLayer(GameObject go, int layer)
        {
            if (go == null || layer < 0) return;
            try
            {
                go.layer = layer;
                var tfs = go.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < tfs.Length; i++)
                {
                    if (tfs[i] != null) tfs[i].gameObject.layer = layer;
                }
            }
            catch { }
        }
    }
}
