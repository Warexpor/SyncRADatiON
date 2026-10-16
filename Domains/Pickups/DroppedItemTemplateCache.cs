// Cached ItemPickup templates + scene arrays for dropped-item spawn (FoT once per item/scene).
using System.Collections.Generic;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.ItemSystem
{
    internal static class DroppedItemTemplateCache
    {
        static readonly Dictionary<int, ItemPickup> ByItem = new Dictionary<int, ItemPickup>(64);
        // Inactive copies of props native release destroyed (one per item per scene), under one inactive root.
        static readonly Dictionary<int, ItemPickup> Stashed = new Dictionary<int, ItemPickup>(16);
        static GameObject _stashRoot;
        static ItemPickup[] _scenePickups;
        static bool _scenePickupsReady;

        public static void Invalidate()
        {
            ByItem.Clear();
            Stashed.Clear();
            if (_stashRoot != null)
            {
                try { Object.Destroy(_stashRoot); } catch (System.Exception e) { Guard.Swallow(e); }
            }
            _stashRoot = null;
            _scenePickups = null;
            _scenePickupsReady = false;
        }

        /// <summary>
        /// Native release destroys a taken prop (dontDestroyOnPickup false, Ghidra ItemPickup.c release). When the
        /// taker was its only copy in the scene, their later drop of that item cloned an unrelated pickup (no
        /// usable collider: nobody but the peers who still had the prop could take it). Keep an inactive copy,
        /// made just before release; the root's SR_Drop_ name keeps it out of every world-pickup scan.
        /// </summary>
        public static void Stash(ItemPickup p)
        {
            if (p == null) return;
            try
            {
                if (p.dontDestroyOnPickup || p.slave || DroppedItemRegistry.IsDropped(p)) return;
                int key = (int)SyncRADation.Networking.WorldPickupSyncService.ResolveItem(p, bindCatalog: false);
                ItemPickup have;
                if (Stashed.TryGetValue(key, out have) && have != null) return;
                if (!IsWorldPickup(p) || VisualTooBig(p)) return;
                if (_stashRoot == null)
                {
                    _stashRoot = new GameObject(DroppedItemRegistry.NamePrefix + "Stash");
                    _stashRoot.SetActive(false);
                }
                var go = Object.Instantiate(p.gameObject, _stashRoot.transform, false);
                if (go == null) return;
                // Never woke (inactive root), so its guid was never registered: drop it before a clone copies it.
                var uid = go.GetComponent<UniqueId>();
                if (uid != null) Object.DestroyImmediate(uid);
                var copy = go.GetComponent<ItemPickup>();
                if (copy != null) Stashed[key] = copy;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
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
            // Before ByItem: that may hold a different item's fallback cached while this one had no live prop.
            if (Stashed.TryGetValue(key, out cached))
            {
                if (cached != null) return cached;
                Stashed.Remove(key);
            }
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
                if (!IsWorldPickup(p) || VisualTooBig(p)) continue;
                bool inScene = false;
                bool live = false;
                bool spent = false;
                try { inScene = p.gameObject != null && p.gameObject.scene.IsValid(); } catch (System.Exception e) { Guard.Swallow(e); }
                try { live = inScene && p.gameObject.activeInHierarchy; } catch (System.Exception e) { Guard.Swallow(e); }
                try { spent = p.triggered; } catch (System.Exception e) { Guard.Swallow(e); }
                if (live && !spent && anyLive == null) anyLive = p;
                if (inScene && !spent && anyScene == null) anyScene = p;
                if (SyncRADation.Networking.WorldPickupSyncService.ResolveItem(p, bindCatalog: false) == item)
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

        /// <summary>
        /// A floor pickup (AssetRipper PEN_Wreck Rooms/.../ItemPickup_BrokenKey: Interaction, ItemPickup, UniqueId,
        /// ItemPickupName, BoxCollider2D trigger, Prompt child), not an event-screen one (Events/PEN_PC/TapePickup:
        /// 3D BoxCollider, no name marker, scaled for the close-up camera). A clone of the latter floated, came out
        /// huge and never showed the interact prompt.
        /// </summary>
        static bool IsWorldPickup(ItemPickup p)
        {
            try { return p.GetComponent<BoxCollider2D>() != null && p.GetComponent<Collider>() == null; }
            catch { return false; }
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

    }
}
