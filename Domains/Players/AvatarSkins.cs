// Local display choice for the other players' model: Elster (the proxy clone itself) or one of two Ariane outfits.
// Nothing goes on the wire: every peer picks how it draws everyone else (F2 menu, RemotePlayerModel pref).
//
// Where the Ariane model comes from: the game has no prefab of her. She only exists as cutscene / room objects of some
// levels (`ariane_metarig_IK_sleep`, `ariane_metarig_IK_uniform`; AssetRipper scenes PEN_Wreck, MEM_Memory,
// LAB_Emptiness, BOS_Adler, ROT_Rotfront), all on the same meshes (Body, Hair_Ariane, HairHead_Ariane, plus HairLong
// and Skirt for the dress, Tasche for the uniform) with one material per outfit (ariane_body_texture_cryo /
// _uniformed). A template of each outfit is banked the first time a loaded scene has the parts (script-free clone
// under an inactive DontDestroyOnLoad holder, like the F11 enemy templates) and then lives for the whole process.
// For a process that has not seen her yet, the main menu can load LAB_Emptiness additively just long enough to bank
// both templates (Preload): its objects are switched off and destroyed in the same callback, before any Start.
using SyncRADation.Config;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Players
{
    public enum RemoteModel
    {
        Elster = 0,
        ArianeDress = 1,
        ArianeUniform = 2
    }

    public static class AvatarSkins
    {
        /// <summary>The smallest level that has both outfits (dress in "Hatch Failure", uniform in "The Crash Site").</summary>
        const string PreloadScene = "LAB_Emptiness";
        const string TemplatePrefix = "SR_Ariane_";
        /// <summary>Every object the mod makes of her starts with this (never harvested as a source).</summary>
        public const string OwnPrefix = "SR_";
        const string DressMaterial = "ariane_body_texture_cryo";
        const string UniformMaterial = "ariane_body_texture_uniformed";

        // persistent: per-process model templates (banked DontDestroyOnLoad clones, null-checked and re-harvested)
        static readonly GameObject[] _templates = new GameObject[3];
        // persistent: DontDestroyOnLoad root of the templates, recreated if it was ever destroyed
        static GameObject _holder;
        // persistent: an additive preload is in flight (ends in OnSceneLoaded)
        static bool _preloading;
        // persistent: the automatic preload runs at most once per process
        static bool _autoTried;
        // persistent: when the main menu came up (the automatic preload waits for it to settle)
        static float _menuAt = -1f;
        // persistent: the menu's time scale / cursor, put back after the preload scene's Awake
        static float _timeScale = 1f;
        // persistent: see _timeScale
        static CursorLockMode _cursorLock;
        // persistent: see _timeScale
        static bool _cursorVisible = true;

        public static RemoteModel Wanted
        {
            get
            {
                int v = ModConfig.RemotePlayerModel != null ? ModConfig.RemotePlayerModel.Value : 0;
                return v == 1 ? RemoteModel.ArianeDress : v == 2 ? RemoteModel.ArianeUniform : RemoteModel.Elster;
            }
        }

        public static bool Has(RemoteModel model) =>
            model == RemoteModel.Elster || _templates[(int)model] != null;

        public static GameObject Template(RemoteModel model) =>
            model == RemoteModel.Elster ? null : _templates[(int)model];

        public static bool Preloading => _preloading;

        /// <summary>F2 choice: saved, then the template is looked for right away (and loaded when at the main menu).</summary>
        public static void Select(RemoteModel model)
        {
            if (ModConfig.RemotePlayerModel == null) return;
            ModConfig.RemotePlayerModel.Value = (int)model;
            try { MelonLoader.MelonPreferences.Save(); } catch (System.Exception e) { Guard.Swallow(e); }
            if (!Has(model)) Harvest();
            bool load = !Has(model) && CanPreload;
            PlaytestLog.EventAlways("Skin", "selected " + model + " in " + ActiveScene() + ": "
                + (Has(model) ? "ready" : load ? "loading now" : "not loaded, waits for the main menu"));
            if (load) Preload();
        }

        /// <summary>F2 status line for the chosen model.</summary>
        public static string Status()
        {
            var w = Wanted;
            if (w == RemoteModel.Elster) return "";
            if (Has(w)) return "Ariane model: ready";
            if (_preloading) return "Ariane model: loading…";
            return CanPreload
                ? "Ariane model: not loaded"
                : "Ariane model: not loaded here (load it at the main menu)";
        }

        static string ActiveScene()
        {
            try { return SceneManager.GetActiveScene().name ?? ""; }
            catch (System.Exception e) { Guard.Swallow(e); return ""; }
        }

        // ------------------------------------------------------------------ harvest (any loaded scene)

        /// <summary>Bank the outfits whose parts are loaded right now. Cheap no-op once both are banked.</summary>
        public static void Harvest()
        {
            if (Has(RemoteModel.ArianeDress) && Has(RemoteModel.ArianeUniform)) return;
            try
            {
                var all = Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>();
                if (all == null) return;
                Material dressMat = null, uniformMat = null;
                Transform dressSrc = null, uniformSrc = null;
                for (int i = 0; i < all.Length; i++)
                {
                    var smr = all[i];
                    // Every Ariane model has this hair mesh object as a direct child.
                    if (smr == null || smr.name != "Hair_Ariane") continue;
                    var model = smr.transform.parent;
                    if (model == null || IsOurs(model)) continue;
                    for (int c = 0; c < model.childCount; c++)
                    {
                        var r = model.GetChild(c).GetComponent<SkinnedMeshRenderer>();
                        var m = r != null ? r.sharedMaterial : null;
                        if (m == null) continue;
                        string mn = m.name ?? "";
                        if (dressMat == null && mn == DressMaterial) dressMat = m;
                        else if (uniformMat == null && mn == UniformMaterial) uniformMat = m;
                    }
                    if (dressSrc == null && model.Find("Body") != null && model.Find("Skirt") != null && model.Find("HairLong") != null)
                        dressSrc = model;
                    if (uniformSrc == null && model.Find("Body") != null && model.Find("Tasche") != null)
                        uniformSrc = model;
                }
                if (!Has(RemoteModel.ArianeDress) && dressSrc != null && dressMat != null)
                    Bank(RemoteModel.ArianeDress, dressSrc, dressMat);
                if (!Has(RemoteModel.ArianeUniform) && uniformSrc != null && uniformMat != null)
                    Bank(RemoteModel.ArianeUniform, uniformSrc, uniformMat);
            }
            catch (System.Exception e) { Guard.Swallow("AvatarSkins.Harvest", e); }
        }

        static bool IsOurs(Transform model)
        {
            for (var t = model; t != null; t = t.parent)
            {
                string n = t.name;
                if (n != null && n.StartsWith(OwnPrefix, System.StringComparison.Ordinal)) return true;
            }
            return false;
        }

        static bool IsPart(RemoteModel model, string name)
        {
            if (name == "Body" || name == "Hair_Ariane" || name == "HairHead_Ariane") return true;
            return model == RemoteModel.ArianeDress ? name == "Skirt" || name == "HairLong" : name == "Tasche";
        }

        static GameObject Holder()
        {
            if (_holder != null) return _holder;
            _holder = new GameObject("SR_Skins");
            _holder.SetActive(false);
            Object.DontDestroyOnLoad(_holder);
            return _holder;
        }

        static void Bank(RemoteModel model, Transform src, Material mat)
        {
            // Cloned under the inactive holder: nothing on the clone ever wakes.
            var go = Object.Instantiate(src.gameObject, Holder().transform, false);
            if (go == null) return;
            go.name = TemplatePrefix + model;
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null) Object.DestroyImmediate(mb);
            foreach (var an in go.GetComponentsInChildren<Animator>(true))
                if (an != null) Object.DestroyImmediate(an);
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                if (col != null) Object.DestroyImmediate(col);
            CopyMeshes(src.gameObject, go);
            var t = go.transform;
            for (int c = 0; c < t.childCount; c++)
            {
                var child = t.GetChild(c);
                var r = child.GetComponent<SkinnedMeshRenderer>();
                if (r == null) continue; // the armature
                bool part = IsPart(model, child.name);
                child.gameObject.SetActive(part);
                if (!part) continue;
                r.enabled = true;
                r.sharedMaterial = mat;
            }
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
            go.SetActive(true);
            _templates[(int)model] = go;
            PlaytestLog.EventAlways("Skin", "banked " + model + " from '" + src.name + "' in " + ActiveScene());
        }

        /// <summary>IL2CPP Instantiate drops sharedMesh on skinned renderers (PlayerProxyBuilder): copy them back by index.</summary>
        public static void CopyMeshes(GameObject from, GameObject to)
        {
            var a = from.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var b = to.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                if (a[i] == null || b[i] == null) continue;
                if (b[i].sharedMesh == null && a[i].sharedMesh != null) b[i].sharedMesh = a[i].sharedMesh;
                if (b[i].sharedMaterial == null && a[i].sharedMaterial != null) b[i].sharedMaterial = a[i].sharedMaterial;
            }
        }

        // ------------------------------------------------------------------ main menu preload

        /// <summary>Only at the main menu with no session: the level's Awake runs once, over nothing that is in play.</summary>
        public static bool CanPreload =>
            !_preloading && !NetGate.Active && SceneFollowService.IsMainMenu(ActiveScene())
            && !(Has(RemoteModel.ArianeDress) && Has(RemoteModel.ArianeUniform));

        public static void Preload()
        {
            if (!CanPreload) return;
            try
            {
                _timeScale = Time.timeScale;
                _cursorLock = Cursor.lockState;
                _cursorVisible = Cursor.visible;
                if (ModConfig.SkinPreloadPending != null)
                {
                    ModConfig.SkinPreloadPending.Value = true;
                    MelonLoader.MelonPreferences.Save();
                }
                _preloading = true;
                PlaytestLog.EventAlways("Skin", "loading the Ariane model from " + PreloadScene);
                SceneManager.LoadSceneAsync(PreloadScene, LoadSceneMode.Additive);
            }
            catch (System.Exception e)
            {
                _preloading = false;
                ClearPending();
                Guard.Swallow("AvatarSkins.Preload", e);
            }
        }

        static void ClearPending()
        {
            try
            {
                if (ModConfig.SkinPreloadPending == null || !ModConfig.SkinPreloadPending.Value) return;
                ModConfig.SkinPreloadPending.Value = false;
                MelonLoader.MelonPreferences.Save();
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>Every frame (mod update): the automatic preload, once per process, when an Ariane model is chosen.</summary>
        public static void Tick()
        {
            if (_preloading)
            {
                // The scene-loaded hook normally ends it; never leave a level standing loaded if that call was missed.
                try
                {
                    var sc = SceneManager.GetSceneByName(PreloadScene);
                    if (sc.IsValid() && sc.isLoaded && ActiveScene() != PreloadScene) FinishPreload();
                }
                catch (System.Exception e) { Guard.Swallow("AvatarSkins.Tick", e); }
                return;
            }
            if (_autoTried || _menuAt < 0f) return;
            if (Wanted == RemoteModel.Elster || Has(Wanted)) return;
            if (Time.realtimeSinceStartup - _menuAt < 2f || !CanPreload) return;
            _autoTried = true;
            if (ModConfig.SkinPreloadPending != null && ModConfig.SkinPreloadPending.Value)
            {
                PlaytestLog.EventAlways("Skin", "the last Ariane model load did not finish: automatic load skipped (F2 has the button)");
                return;
            }
            Preload();
        }

        /// <summary>
        /// Scene-loaded hook, before the mod's own scene handling. True: this was the preload scene arriving, already
        /// harvested and torn down, not a scene change.
        /// </summary>
        public static bool OnSceneLoaded(string sceneName)
        {
            // Additive: the preload scene arrives without becoming the active scene, whatever was loaded meanwhile
            // (queued loads finish in order, so it lands before a chapter load issued after it).
            string active = ActiveScene();
            if (_preloading && sceneName == PreloadScene && active != PreloadScene)
            {
                FinishPreload();
                return true;
            }
            if (_preloading && active == PreloadScene)
            {
                // The party really went there: that load replaced the additive one.
                _preloading = false;
                ClearPending();
            }
            _menuAt = SceneFollowService.IsMainMenu(sceneName) ? Time.realtimeSinceStartup : -1f;
            Harvest();
            return false;
        }

        static void FinishPreload()
        {
            _preloading = false;
            Harvest();
            TearDownPreload();
            ClearPending();
            PlaytestLog.EventAlways("Skin", "Ariane model load done: dress=" + Has(RemoteModel.ArianeDress)
                + " uniform=" + Has(RemoteModel.ArianeUniform));
        }

        static void TearDownPreload()
        {
            try
            {
                // Before any Start / Update of the level: everything of it goes off, then away.
                var all = Resources.FindObjectsOfTypeAll<Transform>();
                var roots = new System.Collections.Generic.List<GameObject>();
                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null || t.parent != null) continue;
                    var go = t.gameObject;
                    if (go == null || !go.scene.IsValid() || go.scene.name != PreloadScene) continue;
                    roots.Add(go);
                }
                for (int i = 0; i < roots.Count; i++)
                {
                    try { roots[i].SetActive(false); } catch (System.Exception e) { Guard.Swallow(e); }
                }
                for (int i = 0; i < roots.Count; i++)
                {
                    try { Object.Destroy(roots[i]); } catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            catch (System.Exception e) { Guard.Swallow("AvatarSkins.TearDown", e); }
            try { SceneManager.UnloadSceneAsync(PreloadScene); }
            catch (System.Exception e) { Guard.Swallow("AvatarSkins.Unload", e); }
            try
            {
                Time.timeScale = _timeScale;
                Cursor.lockState = _cursorLock;
                Cursor.visible = _cursorVisible;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
