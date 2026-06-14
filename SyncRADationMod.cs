// SyncRADation — MelonMod entry point, input bindings (F2/F3/F6/F7), command-line auto-host/connect
using MelonLoader;

[assembly: MelonInfo(typeof(SyncRADation.SyncRADationMod), SyncRADation.PluginInfo.Name, SyncRADation.PluginInfo.Version, SyncRADation.PluginInfo.Author)]
[assembly: MelonGame("rose-engine", "SIGNALIS")]

namespace SyncRADation
{
    public class SyncRADationMod : MelonMod
    {
        private bool _autoActionDone;

        public override void OnInitializeMelon()
        {
            ModRuntime.Start(LoggerInstance, HarmonyInstance);
        }

        public override void OnUpdate()
        {
            ModRuntime.OnUpdate();

            if (!_autoActionDone)
            {
                _autoActionDone = true;
                ProcessCommandLine();
            }

            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F2))
                UI.MultiplayerMenu.Toggle();

            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F3))
                QuickConnect();

            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F6))
                Cheats.ItemGiver.ShowMenu = !Cheats.ItemGiver.ShowMenu;
            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F7))
                Cheats.EntitySpawner.ShowMenu = !Cheats.EntitySpawner.ShowMenu;
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

        private void ProcessCommandLine()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--sync-host")
                {
                    LoggerInstance.Msg("[Auto] Hosting on port " + PluginInfo.DefaultPort);
                    var net = Networking.LanNetworkManager.Instance;
                    if (net != null) net.StartHost(PluginInfo.DefaultPort);
                    return;
                }
                if (args[i] == "--sync-connect" && i + 2 < args.Length)
                {
                    string addr = args[i + 1];
                    int port = int.Parse(args[i + 2]);
                    LoggerInstance.Msg("[Auto] Connecting to " + addr + ":" + port);
                    var net = Networking.LanNetworkManager.Instance;
                    if (net != null) net.ConnectToHost(addr, port);
                    return;
                }
            }
        }

        public override void OnLateUpdate()
        {
            ModRuntime.OnLateUpdate();
        }

        public override void OnGUI()
        {
            UI.MultiplayerMenu.OnGUI();
            Cheats.ItemGiver.OnGUI();
            Cheats.EntitySpawner.OnGUI();
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
