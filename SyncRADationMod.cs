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

            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F3))
                QuickConnect();
        }

        private void QuickConnect()
        {
            var net = Networking.LanNetworkManager.Instance;
            if (net == null || net.Role != Networking.NetworkRole.Offline)
            {
                LoggerInstance.Msg("[QuickConnect] Already connected or network offline");
                return;
            }

            string addr = Config.ModConfig.ConnectAddress?.Value ?? "127.0.0.1";
            int port = Config.ModConfig.ConnectPort?.Value ?? PluginInfo.DefaultPort;
            LoggerInstance.Msg("[QuickConnect] Connecting to " + addr + ":" + port + " (F3)");
            net.ConnectToHost(addr, port);
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
