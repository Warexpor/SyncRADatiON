// SyncRADation — MelonPreferences: IP, port bindings
using MelonLoader;

namespace SyncRADation.Config
{
    public static class ModConfig
    {
        public static MelonPreferences_Category Category;
        public static MelonPreferences_Entry<string> ConnectAddress;
        public static MelonPreferences_Entry<int> ConnectPort;

        public static void Bind()
        {
            Category = MelonPreferences.CreateCategory("SyncRADation", "SyncRADation Alpha");
            ConnectAddress = Category.CreateEntry("ConnectAddress", "127.0.0.1", "Default IP address for connecting");
            ConnectPort = Category.CreateEntry("ConnectPort", PluginInfo.DefaultPort, "Default UDP port for LAN connections");
        }
    }
}
