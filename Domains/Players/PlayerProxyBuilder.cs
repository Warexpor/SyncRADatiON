// Model-only remote-player clone: script-free copy of the local Elster's model, IL2CPP mesh/material fix-up, FF capsule.
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    /// <summary>The parts of a proxy clone the per-player drivers write.</summary>
    public sealed class ProxyRig
    {
        /// <summary>Clone of the Elster model (Ellie_Default): the armature root lives under it.</summary>
        public Transform Model;
        /// <summary>Humanoid hips bone (the one bone whose position the sender's Animator moves).</summary>
        public Transform Hips;
        /// <summary>
        /// Outfit objects indexed by CharacterModelType.ElsterType (0 normal, 1 armored, 2 eva, 3 crippled, 4 isa) and the
        /// hat: what CharacterModelType.ApplyType toggles by modelState / the static wearHat (Ghidra CharacterModelType.c).
        /// </summary>
        public readonly GameObject[] Outfits = new GameObject[5];
        public GameObject Hat;
    }

    public static class PlayerProxyBuilder
    {
        public static GameObject CreatePlayerClone(GameObject source, string objectName, out ProxyRig rig)
        {
            rig = null;
            // Model-only: a full clone duplicates door scripts ("single-use door" bug) and would run their Awake.
            Transform model = BoneSyncManager.FindModelRoot(source.transform);
            if (model == null)
            {
                PlaytestLog.Warn("Proxy", "no skinned model under '" + source.name + "' - proxy not created");
                return null;
            }

            // Instantiated under an inactive parent: no Awake / OnEnable runs on the clone's scripts, so the local
            // Elster's statics (PlayerState, CharacterModelType.instance / wearHat) are never touched.
            GameObject proxy = new GameObject(objectName);
            proxy.SetActive(false);
            // Keeps the source root tilt (SIGNALIS player roots carry non-zero X/Z euler): the clone's up is world -Z.
            proxy.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);

            GameObject modelClone = Object.Instantiate(model.gameObject, proxy.transform, true);
            modelClone.name = model.name;

            rig = new ProxyRig { Model = modelClone.transform, Hips = FindCloneHips(source, model, modelClone.transform) };
            ReadOutfits(modelClone, rig);

            foreach (var mb in proxy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb != null) Object.DestroyImmediate(mb);
            }
            // The pose comes from the sender's bones (ProxyPose); any Animator left in the clone would overwrite it.
            foreach (var anim in proxy.GetComponentsInChildren<Animator>(true))
            {
                if (anim != null) Object.DestroyImmediate(anim);
            }

            proxy.SetActive(true);

            foreach (var col in proxy.GetComponentsInChildren<Collider>(true))
            {
                if (col != null) col.enabled = false;
            }
            foreach (var col in proxy.GetComponentsInChildren<Collider2D>(true))
            {
                if (col != null) col.enabled = false;
            }

            // IL2CPP Instantiate drops sharedMesh / sharedMaterials on skinned renderers: copy them from the source by index.
            var sourceSmrs = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var proxySmrs = modelClone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < sourceSmrs.Length && i < proxySmrs.Length; i++)
            {
                var src = sourceSmrs[i];
                var dst = proxySmrs[i];
                if (src == null || dst == null) continue;
                if (dst.sharedMesh == null && src.sharedMesh != null)
                    dst.sharedMesh = src.sharedMesh;
                var mats = src.sharedMaterials;
                if (mats != null && mats.Length > 0)
                    dst.sharedMaterials = mats;
                else if (dst.sharedMaterial == null && src.sharedMaterial != null)
                    dst.sharedMaterial = src.sharedMaterial;
            }

            // After the index-matched SMR copy above (the source still has its weapon's renderers).
            ClearRuntimeMounts(modelClone.transform);

            var anyRenderer = proxy.GetComponentInChildren<Renderer>(true);
            if (anyRenderer != null)
                proxy.layer = anyRenderer.gameObject.layer;

            // Kinematic trigger capsule for friendly-fire raycasts; does not fight the net position interpolation.
            // Local Y of the proxy root is the sender root's up (world -Z), so the capsule stands on the feet.
            var rb = proxy.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = true;
            rb.constraints = RigidbodyConstraints.FreezeAll;
            var capsule = proxy.AddComponent<CapsuleCollider>();
            capsule.radius = 0.3f;
            capsule.height = 1.8f;
            capsule.center = new Vector3(0, 0.9f, 0);
            capsule.isTrigger = true;

            return proxy;
        }

        /// <summary>The source Animator's humanoid hips, found in the clone by the same path below the model.</summary>
        private static Transform FindCloneHips(GameObject source, Transform model, Transform modelClone)
        {
            Transform hips = null;
            try
            {
                var anim = source.GetComponentInChildren<Animator>(true);
                if (anim != null && anim.isHuman) hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (hips == null) return null;
            string path = hips.name;
            for (var t = hips.parent; t != null && t != model; t = t.parent)
                path = t.name + "/" + path;
            return modelClone.Find(path);
        }

        private static void ReadOutfits(GameObject modelClone, ProxyRig rig)
        {
            // Instantiate remaps the clone's serialized references onto the clone's own children.
            var cmt = modelClone.GetComponentInChildren<CharacterModelType>(true);
            if (cmt == null) return;
            rig.Outfits[0] = cmt.normal;
            rig.Outfits[1] = cmt.armored;
            rig.Outfits[2] = cmt.eva;
            rig.Outfits[3] = cmt.crippled;
            rig.Outfits[4] = cmt.isa;
            rig.Hat = cmt.hat;
        }

        /// <summary>
        /// The clone copies whatever the local Elster held at clone time: her equipped weapon (runtime child of
        /// hand_R/WeaponMount) and reload props (hand_L/MagazineMount). RemoteWeaponSync only toggles its own weapon
        /// clones, so the copied weapon would stay in the proxy's hand forever: remove it, hide the props.
        /// </summary>
        private static void ClearRuntimeMounts(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
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
    }
}
