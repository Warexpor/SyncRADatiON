// Cached ItemPickup templates + scene arrays for dropped-item spawn (FoT once per item/scene).
using System.Collections.Generic;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.ItemSystem
{
    internal static class DroppedItemTemplateCache
    {
        static readonly Dictionary<int, ItemPickup> ByItem = new Dictionary<int, ItemPickup>(64);
        static ItemPickup[] _scenePickups;
        static bool _scenePickupsReady;

        public static void Invalidate()
        {
            ByItem.Clear();
            _scenePickups = null;
            _scenePickupsReady = false;
        }

        public static ItemPickup[] ScenePickups()
        {
            if (_scenePickupsReady && _scenePickups != null)
                return _scenePickups;
            _scenePickups = WorldLookup.All<ItemPickup>() ?? System.Array.Empty<ItemPickup>();
            _scenePickupsReady = true;
            return _scenePickups;
        }

        public static ItemPickup FindTemplate(Items.itemlist item)
        {
            int key = (int)item;
            ItemPickup cached;
            if (ByItem.TryGetValue(key, out cached) && cached != null)
            {
                try
                {
                    if (cached.gameObject != null)
                        return cached;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
                ByItem.Remove(key);
            }

            ItemPickup[] all = null;
            try { all = Resources.FindObjectsOfTypeAll<ItemPickup>(); }
            catch
            {
                all = ScenePickups();
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
                if (p == null || DroppedItemRegistry.IsDropped(p)) continue;
                try { if (p.slave) continue; } catch (System.Exception e) { Guard.Swallow(e); }
                if (VisualTooBig(p)) continue;
                bool inScene = false;
                bool live = false;
                bool spent = false;
                try { inScene = p.gameObject != null && p.gameObject.scene.IsValid(); } catch (System.Exception e) { Guard.Swallow(e); }
                try { live = inScene && p.gameObject.activeInHierarchy; } catch (System.Exception e) { Guard.Swallow(e); }
                try { spent = p.triggered; } catch (System.Exception e) { Guard.Swallow(e); }
                if (live && !spent && anyLive == null) anyLive = p;
                if (inScene && !spent && anyScene == null) anyScene = p;
                if (ResolveItem(p) == item)
                {
                    if (live) { matchLive = p; break; }
                    if (inScene && matchScene == null) matchScene = p;
                    if (matchAny == null) matchAny = p;
                }
            }
            ItemPickup found = matchLive ?? matchScene ?? matchAny ?? anyLive ?? anyScene;
            if (found != null)
                ByItem[key] = found;
            return found;
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

        static Items.itemlist ResolveItem(ItemPickup p)
        {
            if (p == null) return Items.itemlist.None;
            try
            {
                if (p._item != null && p._item._item != Items.itemlist.None)
                    return p._item._item;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (p._itemEnum != Items.itemlist.None)
                    return p._itemEnum;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return Items.itemlist.None;
        }
    }
}
