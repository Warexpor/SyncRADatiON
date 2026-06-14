using MelonLoader;

[assembly: MelonInfo(typeof(SyncRADation.SyncRADationMod), SyncRADation.PluginInfo.Name, SyncRADation.PluginInfo.Version, SyncRADation.PluginInfo.Author)]
[assembly: MelonGame("rose-engine", "SIGNALIS")]

namespace SyncRADation
{
    public class SyncRADationMod : MelonMod
    {
        public override void OnInitializeMelon()
        {
            ModRuntime.Start(LoggerInstance, HarmonyInstance);
        }

        public override void OnUpdate()
        {
            ModRuntime.OnUpdate();

            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F2))
                UI.MultiplayerMenu.Toggle();
        }

        public override void OnLateUpdate()
        {
            ModRuntime.OnLateUpdate();
        }

        public override void OnGUI()
        {
            UI.MultiplayerMenu.OnGUI();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            ModRuntime.OnSceneChanged();
        }

        public override void OnApplicationQuit()
        {
            ModRuntime.Stop();
        }
    }
}
