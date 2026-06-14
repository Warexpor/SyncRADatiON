using MelonLoader;
using UnityEngine;

namespace SyncRADation.Players
{
    public static class PlayerProxyBuilder
    {
        private static System.Collections.Generic.Dictionary<string, object> SavePlayerStatics()
        {
            var dict = new System.Collections.Generic.Dictionary<string, object>();
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            foreach (var field in typeof(PlayerState).GetFields(flags))
                dict[field.Name] = field.GetValue(null);
            return dict;
        }

        private static void RestorePlayerStatics(System.Collections.Generic.Dictionary<string, object> saved)
        {
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            foreach (var field in typeof(PlayerState).GetFields(flags))
            {
                if (saved.TryGetValue(field.Name, out var val))
                    field.SetValue(null, val);
            }
        }

        public static GameObject CreatePlayerClone(GameObject source, string objectName, Vector3 positionOffset, MelonLogger.Instance log)
        {
            if (source == null)
            {
                log?.Warning("Cannot spawn player clone: source is null.");
                return null;
            }

            // Model-only approach: clone only the facing-pivot child (skinned model).
            // Full-clone duplicates door scripts → "single-use door" bug.
            Transform facingChild = FindFacingPivotRoot(source.transform);
            if (facingChild == null)
            {
                log?.Warning("Cannot find facing-pivot child — falling back to full clone");
                return CreateFullClone(source, objectName, positionOffset, log);
            }

            var savedStatics = SavePlayerStatics();
            var savedModelInstance = CharacterModelType.instance;
            var savedWearHat = CharacterModelType.wearHat;

            GameObject proxy = new GameObject(objectName);
            proxy.SetActive(false);
            proxy.transform.position = source.transform.position + positionOffset;
            proxy.transform.rotation = source.transform.rotation;

            GameObject modelClone = Object.Instantiate(facingChild.gameObject, proxy.transform, true);
            modelClone.name = facingChild.name;

            RestorePlayerStatics(savedStatics);
            CharacterModelType.instance = savedModelInstance;
            CharacterModelType.wearHat = savedWearHat;

            proxy.tag = "Untagged";

            // Destroy ALL MonoBehaviours while proxy is still inactive (prevents Awake from ever running)
            var allMbs = proxy.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var mb in allMbs)
            {
                if (mb != null)
                    Object.DestroyImmediate(mb);
            }

            // Also destroy the source's Animator clones on modelClone (we'll add a fresh one)
            var animators = modelClone.GetComponentsInChildren<Animator>(true);
            foreach (var anim in animators)
            {
                if (anim != null && anim.gameObject != modelClone)
                    Object.DestroyImmediate(anim);
            }

            // Now safe to activate proxy — no Awake runs (no MBs left)
            proxy.SetActive(true);

            // Find source Animator and copy controller + avatar to a fresh Animator on proxy root
            Animator sourceAnim = source.GetComponentInChildren<Animator>(true);
            if (sourceAnim != null)
            {
                Animator proxyAnim = proxy.AddComponent<Animator>();
                proxyAnim.runtimeAnimatorController = sourceAnim.runtimeAnimatorController;
                proxyAnim.avatar = sourceAnim.avatar;
                proxyAnim.applyRootMotion = false;
                proxyAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                proxyAnim.updateMode = sourceAnim.updateMode;
                proxyAnim.speed = 1f;
                proxyAnim.enabled = false;
                try { proxyAnim.Rebind(); proxyAnim.Update(0f); } catch { }
                log?.Msg("[Proxy] Added Animator: ctrl=" + sourceAnim.runtimeAnimatorController
                    + " avatar=" + sourceAnim.avatar
                    + " updateMode=" + sourceAnim.updateMode);
            }
            else
            {
                log?.Warning("[Proxy] No source Animator found!");
            }

            foreach (var col in proxy.GetComponentsInChildren<Collider>(true))
            {
                if (col != null)
                    col.enabled = false;
            }
            foreach (var col in proxy.GetComponentsInChildren<Collider2D>(true))
            {
                if (col != null)
                    col.enabled = false;
            }

            var rb2 = proxy.GetComponent<Rigidbody2D>();
            if (rb2 != null) { rb2.gravityScale = 0f; rb2.isKinematic = true; rb2.Sleep(); }
            var rb3 = proxy.GetComponent<Rigidbody>();
            if (rb3 != null) { rb3.useGravity = false; rb3.isKinematic = true; rb3.Sleep(); }

            log?.Msg("Model-only proxy created: " + proxy.name + " at " + proxy.transform.position.ToString("F1"));

            // --- Fix IL2CPP: copy sharedMesh from source SMRs to proxy SMRs ---
            var sourceSmrs = facingChild.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var proxySmrs = modelClone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int copyCount = 0;
            for (int si = 0; si < sourceSmrs.Length && si < proxySmrs.Length; si++)
            {
                if (sourceSmrs[si] != null && proxySmrs[si] != null
                    && proxySmrs[si].sharedMesh == null && sourceSmrs[si].sharedMesh != null)
                {
                    proxySmrs[si].sharedMesh = sourceSmrs[si].sharedMesh;
                    copyCount++;
                }
            }
            for (int si = 0; si < sourceSmrs.Length && si < proxySmrs.Length; si++)
            {
                if (sourceSmrs[si] != null && proxySmrs[si] != null
                    && proxySmrs[si].sharedMaterial == null && sourceSmrs[si].sharedMaterial != null)
                {
                    proxySmrs[si].sharedMaterial = sourceSmrs[si].sharedMaterial;
                }
            }
            if (copyCount > 0)
                log?.Msg("[Proxy] Copied " + copyCount + " sharedMeshes from source to proxy");

            return proxy;
        }

        // Returns the first direct child of root that has a SkinnedMeshRenderer in its subtree
        private static Transform FindFacingPivotRoot(Transform root)
        {
            SkinnedMeshRenderer[] smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int si = 0; si < smrs.Length; si++)
            {
                if (smrs[si] != null)
                {
                    Transform t = smrs[si].transform;
                    while (t != null && t.parent != null && t.parent != root)
                        t = t.parent;
                    if (t != null && t.parent == root)
                        return t;
                }
            }
            return null;
        }

        /// <summary>
        /// Returns the first direct child of root (or grandchild via intermediate transforms) matching name.
        /// </summary>
        private static Transform FindChildByName(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = root.GetChild(i);
                if (c.name == name) return c;
                Transform sub = FindChildByName(c, name);
                if (sub != null) return sub;
            }
            return null;
        }

        // --- Fallback: full clone for players without a facing-pivot child ---
        private static GameObject CreateFullClone(GameObject source, string objectName, Vector3 positionOffset, MelonLogger.Instance log)
        {
            var savedStatics = SavePlayerStatics();
            var savedModelInstance = CharacterModelType.instance;
            var savedWearHat = CharacterModelType.wearHat;

            GameObject dummy = new GameObject("__Dummy");
            dummy.SetActive(false);
            dummy.transform.position = source.transform.position;
            dummy.transform.rotation = source.transform.rotation;
            GameObject proxy = Object.Instantiate(source, dummy.transform, true);
            proxy.name = objectName;

            RestorePlayerStatics(savedStatics);
            CharacterModelType.instance = savedModelInstance;
            CharacterModelType.wearHat = savedWearHat;

            proxy.transform.SetParent(null, true);
            RestorePlayerStatics(savedStatics);
            CharacterModelType.instance = savedModelInstance;
            CharacterModelType.wearHat = savedWearHat;

            proxy.transform.position += positionOffset;

            proxy.tag = "Untagged";

            var proxyAnimators = proxy.GetComponentsInChildren<Animator>(true);

            foreach (var mb in proxy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb != null)
                    mb.enabled = false;
            }

            foreach (var col in proxy.GetComponentsInChildren<Collider>(true))
            {
                if (col != null)
                    col.enabled = false;
            }
            foreach (var col in proxy.GetComponentsInChildren<Collider2D>(true))
            {
                if (col != null)
                    col.enabled = false;
            }

            foreach (var anim in proxyAnimators)
            {
                if (anim != null)
                {
                    anim.applyRootMotion = false;
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    anim.updateMode = AnimatorUpdateMode.Normal;
                    anim.speed = 1f;
                    anim.enabled = true;
                }
            }

            var rb2 = proxy.GetComponent<Rigidbody2D>();
            if (rb2 != null) { rb2.gravityScale = 0f; rb2.isKinematic = true; rb2.Sleep(); }
            var rb3 = proxy.GetComponent<Rigidbody>();
            if (rb3 != null) { rb3.useGravity = false; rb3.isKinematic = true; rb3.Sleep(); }

            Object.Destroy(dummy);

            log?.Msg("Full proxy created: " + proxy.name + " at " + proxy.transform.position.ToString("F1"));
            return proxy;
        }

    }
}
