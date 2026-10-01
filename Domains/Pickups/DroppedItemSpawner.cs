// Dropped-item spawn / clone / floor placement (Domains peel from DroppedItemManager).
using System.Collections.Generic;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace SyncRADation.ItemSystem
{
    public static class DroppedItemSpawner
    {
        public static void InvalidateCaches() => DroppedItemTemplateCache.Invalidate();

        static ItemPickup[] ScenePickups() => DroppedItemTemplateCache.ScenePickups();

        public static GameObject SpawnLocalItem(Items.itemlist item, int count, int netID, Vector3 pos)
        {
            if (DroppedItemRegistry.TryGetExisting(netID, out var existing) && existing.Go != null)
                return existing.Go;
            // A new drop under this key supersedes any earlier claim record of it (ids/indices recycle).
            SyncRADation.Patches.ItemPickupPatches.ForgetDropClaim(netID);

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
            try { if (go != null) stored = go.transform.position; } catch (System.Exception e) { Guard.Swallow(e); }
            string scene = "";
            try
            {
                scene = SceneManager.GetActiveScene().name ?? "";
                if (SceneFollowService.IsTransient(scene)) scene = "";
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            DroppedItemRegistry.Put(netID, new DroppedItemRegistry.Drop
            {
                Go = go,
                Item = item,
                Count = DroppedItemRegistry.SanitizeStack(count, PartyKeyRing.IsKeyOrObject(item)),
                Key = netID,
                Scene = scene,
                Pos = stored
            });
            return go;
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
                        try { if (rs[i] != null && rs[i].enabled) rends++; } catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            string parent = "null";
            try { if (go.transform.parent != null) parent = go.transform.parent.name; } catch (System.Exception e) { Guard.Swallow(e); }
            bool active = false;
            try { active = go.activeInHierarchy; } catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
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
            ItemPickup src = DroppedItemTemplateCache.FindTemplate(item);
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

            go.name = DroppedItemRegistry.NamePrefix + netID;
            StripUniqueId(go, src);
            StripInspectJunk(go);
            try { go.transform.SetParent(null, true); } catch (System.Exception e) { Guard.Swallow(e); }
            Object.Destroy(holder);
            try { go.transform.localScale = Vector3.one; } catch (System.Exception e) { Guard.Swallow(e); }

            bool same = ResolveItem(src) == item;
            ApplyPickupFields(go, item, count, same);
            if (!same)
                ApplyCatalogMesh(go, item);
            PlaceInWorld(go, pos);
            try { go.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
            if (!same)
            {
                var m3 = go.transform.Find("Model3D");
                if (m3 != null) FitMeshToNative(m3.gameObject);
            }
            RestOnFloor(go);
            try { FinishInteractable(go); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] finish: " + ex.Message); }
            try { Physics2D.SyncTransforms(); } catch (System.Exception e) { Guard.Swallow(e); }
            return go;
        }

        static void ApplyPickupFields(GameObject go, Items.itemlist item, int count, bool sameVisual)
        {
            var p = go.GetComponent<ItemPickup>();
            if (p == null) return;

            AnItem catalog = null;
            try { catalog = InventoryManager.getItem(item); } catch (System.Exception e) { Guard.Swallow(e); }

            try { p.triggered = false; } catch (System.Exception e) { Guard.Swallow(e); }
            try { p.slave = false; } catch (System.Exception e) { Guard.Swallow(e); }
            try { p.count = DroppedItemRegistry.SanitizeStack(count, PartyKeyRing.IsKeyOrObject(item)); } catch (System.Exception e) { Guard.Swallow(e); }
            // Native pickUp re-rolls count through DynamicDifficulty.calculateCount on the first look
            // (firstObserved false): a dropped 1-round stack became 2 for a low-ammo taker.
            try { p.firstObserved = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { p.focusCamera = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { p.pauseGame = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { p.showItemView = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { p.fadeOnPickup = false; } catch (System.Exception e) { Guard.Swallow(e); }
            try { p.playPickupAnimation = false; } catch (System.Exception e) { Guard.Swallow(e); }
            try { p.dontDestroyOnPickup = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { p.message = ""; } catch (System.Exception e) { Guard.Swallow(e); }
            try { p.onPickup = new UnityEvent(); } catch (System.Exception e) { Guard.Swallow(e); }
            if (catalog != null)
            {
                try { p._item = catalog; } catch (System.Exception e) { Guard.Swallow(e); }
                try { p._itemEnum = catalog._item; } catch (System.Exception e) { Guard.Swallow(e); }
            }

            var inter = go.GetComponent<Interaction>();
            if (inter != null)
            {
                try { inter.triggered = false; } catch (System.Exception e) { Guard.Swallow(e); }
                try { inter.inRange = false; } catch (System.Exception e) { Guard.Swallow(e); }
                try { inter.enabled = true; } catch (System.Exception e) { Guard.Swallow(e); }
                try { inter.anyAngle = true; } catch (System.Exception e) { Guard.Swallow(e); }
                try { inter.type = Interaction.interType.take; } catch (System.Exception e) { Guard.Swallow(e); }
                try { p.inter = inter; } catch (System.Exception e) { Guard.Swallow(e); }
            }

            var col = go.GetComponent<BoxCollider2D>();
            if (col == null) col = go.AddComponent<BoxCollider2D>();
            try { col.enabled = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { col.isTrigger = false; } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (col.size.x < 4f || col.size.y < 4f)
                    col.size = new Vector2(6.4f, 6.4f);
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            int layer = LayerMask.NameToLayer("Interactables");
            if (layer < 0) layer = 19;
            SetLayer(go, layer);
        }

        static void FinishInteractable(GameObject go)
        {
            if (go == null) return;
            try { go.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
            var p = go.GetComponent<ItemPickup>();
            if (p != null)
            {
                try { p.enabled = true; } catch (System.Exception e) { Guard.Swallow(e); }
                try { p.triggered = false; } catch (System.Exception e) { Guard.Swallow(e); }
                try { p.slave = false; } catch (System.Exception e) { Guard.Swallow(e); }
            }
            var inter = go.GetComponent<Interaction>();
            if (inter != null)
            {
                try { inter.enabled = true; } catch (System.Exception e) { Guard.Swallow(e); }
                try { inter.triggered = false; } catch (System.Exception e) { Guard.Swallow(e); }
                try { inter.inRange = false; } catch (System.Exception e) { Guard.Swallow(e); }
                try { inter.anyAngle = true; } catch (System.Exception e) { Guard.Swallow(e); }
                try { inter.type = Interaction.interType.take; } catch (System.Exception e) { Guard.Swallow(e); }
                if (p != null)
                {
                    try { p.inter = inter; } catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            var col = go.GetComponent<BoxCollider2D>();
            if (col != null)
            {
                try { col.enabled = true; } catch (System.Exception e) { Guard.Swallow(e); }
                try { col.isTrigger = false; } catch (System.Exception e) { Guard.Swallow(e); }
            }
            RebuildCollider(go);
            int layer = LayerMask.NameToLayer("Interactables");
            if (layer < 0) layer = 19;
            SetLayer(go, layer);
            EnsureOutlines(go);
            _restNextFrame.Add(go);
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
                catch (System.Exception e) { Guard.Swallow(e); }
            }
        }

        static void ApplyCatalogMesh(GameObject go, Items.itemlist item)
        {
            AnItem catalog = null;
            try { catalog = InventoryManager.getItem(item); } catch (System.Exception e) { Guard.Swallow(e); }
            if (catalog == null || go == null) return;

            GameObject prefab = null;
            try { prefab = catalog.Image3D; } catch (System.Exception e) { Guard.Swallow(e); }
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
                try { slot.gameObject.SetActive(false); } catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
            if (m > 8f || m > target * 1.4f || m < target * 0.4f)
                vis.transform.localScale *= target / m;
        }

        /// <summary>
        /// SIGNALIS walks the XY plane and "up" is -Z (Elster's model up maps to -Z; feet sit at the floor z).
        /// The drop root is placed at the dropper's feet, so the visual's lowest point (bounds.max.z) must
        /// meet the root z: a template cloned from a table/shelf pickup otherwise keeps its raised model.
        /// </summary>
        public static void RestOnFloor(GameObject go)
        {
            if (go == null) return;
            try { if (!go.activeInHierarchy) return; } catch { return; }
            Transform vis = null;
            try
            {
                vis = go.transform.Find("Model3D");
                if (vis == null || !vis.gameObject.activeSelf) vis = FindModel(go.transform);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            Bounds b;
            if (!VisibleBounds(vis != null ? vis.gameObject : go, out b)) return;
            if (Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) > 4f) return;
            float dz = go.transform.position.z - b.max.z;
            if (Mathf.Abs(dz) <= 0.01f) return;
            dz = Mathf.Clamp(dz, -2.5f, 2.5f);
            try
            {
                if (vis != null) vis.position += new Vector3(0f, 0f, dz);
                else go.transform.position += new Vector3(0f, 0f, dz);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static bool VisibleBounds(GameObject root, out Bounds b)
        {
            b = default(Bounds);
            if (root == null) return false;
            Renderer[] rs;
            try { rs = root.GetComponentsInChildren<Renderer>(false); } catch { return false; }
            if (rs == null) return false;
            bool any = false;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null) continue;
                try
                {
                    if (!r.enabled) continue;
                    if (!any) { b = r.bounds; any = true; }
                    else b.Encapsulate(r.bounds);
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            return any;
        }

        static readonly List<GameObject> _restNextFrame = new List<GameObject>(4);

        /// <summary>Second floor pass one frame after spawn (skinned / late-bound renderer bounds).</summary>
        public static void TickRest()
        {
            if (_restNextFrame.Count == 0) return;
            for (int i = 0; i < _restNextFrame.Count; i++)
            {
                try { RestOnFloor(_restNextFrame[i]); } catch (System.Exception e) { Guard.Swallow(e); }
            }
            _restNextFrame.Clear();
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

        static Transform NearestNativeModel(Vector3 pos)
        {
            var all = ScenePickups();
            if (all == null || all.Length == 0) return null;
            Transform bestT = null;
            float best = 20f;
            for (int i = 0; i < all.Length; i++)
            {
                var p = all[i];
                if (p == null || DroppedItemRegistry.IsDropped(p)) continue;
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

        static GameObject SpawnFallbackPickup(Items.itemlist item, int count, int netID, Vector3 pos)
        {
            var go = new GameObject(DroppedItemRegistry.NamePrefix + netID);
            try { go.SetActive(false); } catch (System.Exception e) { Guard.Swallow(e); }
            int layer = LayerMask.NameToLayer("Interactables");
            if (layer >= 0) go.layer = layer;

            var col = go.AddComponent<BoxCollider2D>();
            col.isTrigger = false;
            col.size = new Vector2(6.4f, 6.4f);

            var inter = go.AddComponent<Interaction>();
            try { inter.type = Interaction.interType.take; } catch (System.Exception e) { Guard.Swallow(e); }
            try { inter.anyAngle = true; } catch (System.Exception e) { Guard.Swallow(e); }

            var p = go.AddComponent<ItemPickup>();
            ApplyPickupFields(go, item, count, false);
            try { go.AddComponent<ItemPickupName>(); } catch (System.Exception e) { Guard.Swallow(e); }
            ApplyCatalogMesh(go, item);
            PlaceInWorld(go, pos);
            try { go.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
            var m3 = go.transform.Find("Model3D");
            if (m3 != null) FitMeshToNative(m3.gameObject);
            RestOnFloor(go);
            try { FinishInteractable(go); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] finish: " + ex.Message); }
            try { Physics2D.SyncTransforms(); } catch (System.Exception e) { Guard.Swallow(e); }
            return go;
        }

        static void PlaceInWorld(GameObject go, Vector3 pos)
        {
            if (go == null) return;
            // pos is the dropper's feet (floor z). Native pickups sit on tables/shelves, so their z is not the floor.
            try { go.transform.SetParent(null, true); } catch (System.Exception e) { Guard.Swallow(e); }
            try { go.transform.rotation = Quaternion.identity; } catch (System.Exception e) { Guard.Swallow(e); }
            Transform room = RoomAt(pos);
            if (room != null)
            {
                try { go.transform.SetParent(room, true); } catch (System.Exception e) { Guard.Swallow(e); }
            }
            try { go.transform.position = pos; } catch (System.Exception e) { Guard.Swallow(e); }
        }

        static Transform RoomAt(Vector3 pos)
        {
            var rooms = WorldLookup.All<Room>();
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
            catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
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
                        try { t.gameObject.SetActive(false); } catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static Items.itemlist ResolveItem(ItemPickup p)
        {
            if (p == null) return Items.itemlist.None;
            try
            {
                if (p._item != null && p._item._item != Items.itemlist.None)
                    return p._item._item;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try { return p._itemEnum; } catch { return Items.itemlist.None; }
        }

        static void StripUniqueId(GameObject go, ItemPickup src)
        {
            UniqueId uid = null;
            try { uid = go.GetComponent<UniqueId>(); } catch (System.Exception e) { Guard.Swallow(e); }
            if (uid == null) return;
            string stolen = null;
            try { stolen = uid.id; } catch (System.Exception e) { Guard.Swallow(e); }
            try { Object.DestroyImmediate(uid); } catch { try { Object.Destroy(uid); } catch (System.Exception e) { Guard.Swallow(e); } }
            if (string.IsNullOrEmpty(stolen) || src == null) return;
            try
            {
                var original = src.GetComponent<UniqueId>();
                var all = UniqueId.allGuids;
                if (original != null && all != null)
                    all[stolen] = original;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
