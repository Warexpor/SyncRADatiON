// SyncRADation � model-only clone: MB removal, mesh/material fix (IL2CPP), CapsuleCollider+Rigidbody
using MelonLoader;
using UnityEngine;

namespace SyncRADation.Players
{
    /// <summary>
    /// The clone's outfit objects, read from its CharacterModelType before every MonoBehaviour is stripped
    /// (CharacterModelType.ApplyType toggles exactly these: normal / armored / eva / crippled / isa by modelState,
    /// hat by the static wearHat; Ghidra CharacterModelType.c).
    /// </summary>
    public sealed class ProxyModelVariants
    {
        /// <summary>Indexed by CharacterModelType.ElsterType (0 normal, 1 armored, 2 eva, 3 crippled, 4 isa).</summary>
        public readonly GameObject[] Models = new GameObject[5];
        public GameObject Hat;
    }

    public static class PlayerProxyBuilder
    {
        public static GameObject CreatePlayerClone(GameObject source, string objectName, Vector3 positionOffset,
            MelonLogger.Instance log, out ProxyModelVariants variants)
        {
            variants = null;
            if (source == null)
            {
                log?.Warning("Cannot spawn player clone: source is null.");
                return null;
            }

            // Model-only approach: clone only the facing-pivot child (skinned model).
            // A full clone duplicates door scripts ("single-use door" bug) and would run their Awake.
            Transform facingChild = FindFacingPivotRoot(source.transform);
            if (facingChild == null)
            {
                log?.Warning("[Proxy] no skinned facing-pivot child under '" + source.name + "' - proxy not created");
                return null;
            }

            // Instantiated under an inactive parent: no Awake / OnEnable runs on the clone's scripts, so the local
            // Elster's statics (PlayerState, CharacterModelType.instance / wearHat) are never touched.
            GameObject proxy = new GameObject(objectName);
            proxy.SetActive(false);
            proxy.transform.position = source.transform.position + positionOffset;
            proxy.transform.rotation = source.transform.rotation;

            GameObject modelClone = Object.Instantiate(facingChild.gameObject, proxy.transform, true);
            modelClone.name = facingChild.name;

            proxy.tag = "Untagged";

            variants = ReadVariants(modelClone);

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

            // Animator-primary pose: keep enabled so the controller evaluates network params.
            Animator sourceAnim = source.GetComponentInChildren<Animator>(true);
            if (sourceAnim != null)
            {
                Animator proxyAnim = proxy.AddComponent<Animator>();
                proxyAnim.runtimeAnimatorController = sourceAnim.runtimeAnimatorController;
                proxyAnim.avatar = sourceAnim.avatar;
                proxyAnim.applyRootMotion = false;
                proxyAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                proxyAnim.updateMode = AnimatorUpdateMode.UnscaledTime; // remote player is not paused by our inventory
                proxyAnim.speed = 1f;
                proxyAnim.enabled = true;
                try { proxyAnim.Rebind(); proxyAnim.Update(0f); } catch (System.Exception e) { Guard.Swallow(e); }
                if (ModRuntime.VerboseLogging)
                    log?.Msg("[Proxy] Animator ENABLED: ctrl=" + sourceAnim.runtimeAnimatorController
                    + " avatar=" + sourceAnim.avatar);
            }
            else
            {
                log?.Warning("[Proxy] No source Animator found!");
            }

            // Preserve source root tilt (SIGNALIS uses non-zero root X/Z euler)
            proxy.transform.rotation = source.transform.rotation;

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

            if (ModRuntime.VerboseLogging)
                log?.Msg("Model-only proxy created: " + proxy.name + " at " + proxy.transform.position.ToString("F1")
                + " rootEuler=" + proxy.transform.eulerAngles.ToString("F1"));

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
                if (sourceSmrs[si] != null && proxySmrs[si] != null)
                {
                    if (proxySmrs[si].sharedMaterial == null && sourceSmrs[si].sharedMaterial != null)
                        proxySmrs[si].sharedMaterial = sourceSmrs[si].sharedMaterial;
                    if (sourceSmrs[si].sharedMaterials != null && sourceSmrs[si].sharedMaterials.Length > 0)
                        proxySmrs[si].sharedMaterials = sourceSmrs[si].sharedMaterials;
                }
            }
            if (copyCount > 0 && ModRuntime.VerboseLogging)
                log?.Msg("[Proxy] Copied " + copyCount + " sharedMeshes from source to proxy");

            // After the index-matched SMR copy above (the source still has its weapon's renderers).
            ClearRuntimeMounts(modelClone.transform);

            var anyRenderer = proxy.GetComponentInChildren<Renderer>(true);
            if (anyRenderer != null)
                proxy.layer = anyRenderer.gameObject.layer;

            // Kinematic collider for FF raycasts — does not fight net position interp. Local Y of the proxy root is
            // the sender root's up (world -Z), so the capsule stands on the feet.
            var proxyRb = proxy.AddComponent<Rigidbody>();
            proxyRb.useGravity = false;
            proxyRb.isKinematic = true;
            proxyRb.constraints = RigidbodyConstraints.FreezeAll;
            var proxyCol = proxy.AddComponent<CapsuleCollider>();
            proxyCol.radius = 0.3f;
            proxyCol.height = 1.8f;
            proxyCol.center = new Vector3(0, 0.9f, 0);
            proxyCol.isTrigger = true;
            if (ModRuntime.VerboseLogging)
                log?.Msg("[Proxy] Kinematic trigger capsule for FF detection");

            return proxy;
        }

        private static ProxyModelVariants ReadVariants(GameObject modelClone)
        {
            var v = new ProxyModelVariants();
            try
            {
                // Instantiate remaps the clone's serialized references onto the clone's own children.
                var cmt = modelClone.GetComponentInChildren<CharacterModelType>(true);
                if (cmt == null) return v;
                v.Models[0] = cmt.normal;
                v.Models[1] = cmt.armored;
                v.Models[2] = cmt.eva;
                v.Models[3] = cmt.crippled;
                v.Models[4] = cmt.isa;
                v.Hat = cmt.hat;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return v;
        }

        /// <summary>
        /// The clone copies whatever the local Elster held at clone time: her equipped weapon (runtime child of
        /// hand_R/WeaponMount) and reload props (hand_L/MagazineMount). RemoteWeaponSync only toggles its own weapon
        /// clones, so the copied weapon would stay in the proxy's hand forever: remove it, hide the props.
        /// </summary>
        private static void ClearRuntimeMounts(Transform root)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                if (t.name == "WeaponMount")
                {
                    for (int c = t.childCount - 1; c >= 0; c--)
                        Object.DestroyImmediate(t.GetChild(c).gameObject);
                }
                else if (t.name == "MagazineMount")
                {
                    for (int c = 0; c < t.childCount; c++)
                        t.GetChild(c).gameObject.SetActive(false);
                }
            }
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
    }
}
