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

            // --- Model-only approach: clone only the facing-pivot child (skinned model).
            //     This avoids duplicating PlayerState / AlternatePlayerController etc.
            //     which have static fields that Awake corrupts. ---
            Transform facingChild = FindFacingPivotRoot(source.transform);
            if (facingChild == null)
            {
                log?.Warning("Cannot find facing-pivot child — falling back to full clone (may break doors)");
                return CreateFullClone(source, objectName, positionOffset, log);
            }

            var savedStatics = SavePlayerStatics();
            var savedModelInstance = CharacterModelType.instance;
            var savedWearHat = CharacterModelType.wearHat;

            // Create bare root — no MonoBehaviours at all
            GameObject proxy = new GameObject(objectName);
            proxy.transform.position = source.transform.position + positionOffset;
            proxy.transform.rotation = source.transform.rotation;

            // Clone ONLY the model hierarchy (no game-logic scripts)
            GameObject modelClone = Object.Instantiate(facingChild.gameObject, proxy.transform, true);
            modelClone.name = facingChild.name;

            RestorePlayerStatics(savedStatics);
            CharacterModelType.instance = savedModelInstance;
            CharacterModelType.wearHat = savedWearHat;

            proxy.tag = "Untagged";

            foreach (var mb in proxy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb != null)
                    mb.enabled = false;
            }

            // Re-enable Animators so the driver can set params
            foreach (var anim in proxy.GetComponentsInChildren<Animator>(true))
            {
                if (anim != null)
                {
                    anim.enabled = true;
                    anim.applyRootMotion = false;
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    anim.speed = 1f;
                }
            }

            // Disable ALL colliders
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

            foreach (var anim in proxy.GetComponentsInChildren<Animator>(true))
            {
                if (anim != null)
                {
                    anim.applyRootMotion = false;
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    anim.speed = 1f;
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
