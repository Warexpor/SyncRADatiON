// F6 IMGUI debug window: add any game item to the inventory (keys / story objects also go on the party ring).
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.Cheats
{
    public static class ItemGiver
    {
        // persistent: F6 window state
        public static bool ShowMenu;

        // persistent: F6 window state
        private static Vector2 _scrollPos;
        // persistent: F6 window state
        private static string _statusMessage = "";
        // persistent: F6 window state
        private static float _statusTimer;
        // persistent: F6 window state
        private static int _activeTab;
        private static readonly string[] Tabs = { "Useful", "Weapons", "Keys/Story" };

        // persistent: constant item table
        private static readonly List<ItemEntry> UsefulItems = new List<ItemEntry>
        {
            new ItemEntry("Pistol Ammo",   "PistolAmmo"),
            new ItemEntry("Revolver Ammo", "RevolverAmmo"),
            new ItemEntry("Shotgun Shells","ShotgunAmmo"),
            new ItemEntry("Rifle Ammo",    "RifleAmmo"),
            new ItemEntry("SMG Ammo",      "SmgAmmo"),
            new ItemEntry("Flare Ammo",    "FlareGunAmmo"),
            new ItemEntry("Flak Shells",   "FlakGunAmmo"),
            new ItemEntry("Health 25",     "Health25"),
            new ItemEntry("Health 50",     "Health50"),
            new ItemEntry("Health 100",    "Health100"),
            new ItemEntry("Health Fast",   "HealthFast"),
            new ItemEntry("Injector",      "Injector"),
            new ItemEntry("Flashlight",    "Flashlight"),
            new ItemEntry("Signal Flare",  "SignalFlare"),
            new ItemEntry("Photo Module",  "PhotoModule"),
            new ItemEntry("Medication",    "Medication"),
        };

        // persistent: constant item table
        private static readonly List<ItemEntry> WeaponItems = new List<ItemEntry>
        {
            new ItemEntry("Pistol",       "Pistol"),
            new ItemEntry("Revolver",     "Revolver"),
            new ItemEntry("Shotgun",      "Shotgun"),
            new ItemEntry("Rifle",        "Rifle"),
            new ItemEntry("SMG",          "SMG"),
            new ItemEntry("Flare Gun",    "FlareGun"),
            new ItemEntry("Flak Gun",     "FlakGun"),
            new ItemEntry("Machete",      "Machete"),
            new ItemEntry("Taser",        "Taser"),
        };

        // persistent: constant item table
        private static readonly List<ItemEntry> StoryItems = new List<ItemEntry>
        {
            new ItemEntry("Bone Key",         "BoneKey"),
            new ItemEntry("Airlock Key",      "AirlockKey"),
            new ItemEntry("Adler's Key",      "AdlersKey"),
            new ItemEntry("Classroom Key",    "ClassRoomKey"),
            new ItemEntry("Elevator Key",     "ElevatorKey"),
            new ItemEntry("East Key",         "EastKey"),
            new ItemEntry("Exam Key",         "ExamKey"),
            new ItemEntry("Elevator Fuse",    "ElevatorFuse"),
            new ItemEntry("Alina Photo",      "AlinaPhoto"),
            new ItemEntry("Ariane Photo",     "ArianePhoto"),
            new ItemEntry("Incinerator Key",  "IncineratorKey"),
            new ItemEntry("Library Key",      "LibraryKey"),
            new ItemEntry("Master Key",       "MasterKey"),
            new ItemEntry("Workshop Key",     "WorkshopKey"),
            new ItemEntry("Pump Key",         "PumpKey"),
            new ItemEntry("Kitchen Key",      "KitchenKey"),
            new ItemEntry("Kolibri Key",      "KolibriKey"),
            new ItemEntry("Owl Key",          "OwlKey"),
            new ItemEntry("Painting Key",     "PaintingKey"),
            new ItemEntry("Postbox Key",      "PostboxKey"),
            new ItemEntry("Rusted Key",       "RustedKey"),
            new ItemEntry("Store Key",        "StoreKey"),
            new ItemEntry("Bone Key A",       "BoneKeyA"),
            new ItemEntry("Bone Key B",       "BoneKeyB"),
            new ItemEntry("Key of Air",       "KeyOfAir"),
            new ItemEntry("Key of Earth",     "KeyOfEarth"),
            new ItemEntry("Key of Fire",      "KeyOfFire"),
            new ItemEntry("Key of Water",     "KeyOfWater"),
            new ItemEntry("Key of Gold",      "KeyOfGold"),
            new ItemEntry("Key of Blank",     "KeyOfBlank"),
            new ItemEntry("Key of Eternity",  "KeyOfEternity"),
            new ItemEntry("Key of Love",      "KeyOfLove"),
            new ItemEntry("Key of Sacrifice", "KeyOfSacrifice"),
            new ItemEntry("Ring Bride",       "RingBride"),
            new ItemEntry("Ring Regent",      "RingRegent"),
            new ItemEntry("Ring Serpent",     "RingSerpent"),
            new ItemEntry("Moon Ring",        "MoonRing"),
            new ItemEntry("Falke Spear",      "FalkeSpear"),
            new ItemEntry("Meat Key",         "MeatKey"),
            new ItemEntry("Shotgun Case",     "ShotgunCase"),
            new ItemEntry("Shotgun Case Key", "ShotgunCaseKey"),
            new ItemEntry("Music Cassette",   "MusicCassette"),
            new ItemEntry("Photo QR",         "PhotoQR"),
        };

        // persistent: F6 window state
        private static Rect _windowRect = new Rect(200f, 100f, 420f, 520f);

        public static void OnGUI()
        {
            if (!ShowMenu) return;

            _windowRect = GUI.Window(998, _windowRect, (GUI.WindowFunction)DrawWindow, "Item Giver (F6)");
        }

        private static void DrawWindow(int id)
        {
            var y = 20f;
            GUI.Label(new Rect(10, y, 400, 20), "Click an item to add it to your inventory");
            y += 24;

            for (int i = 0; i < Tabs.Length; i++)
            {
                var r = new Rect(10 + i * 135, y, 130, 22);
                var was = _activeTab == i;
                var now = GUI.Toggle(r, was, Tabs[i], GUI.skin.button);
                if (now && !was) _activeTab = i;
            }
            y += 28;

            var viewRect = new Rect(0, 0, 390, 40f);
            var items = GetActiveItems();
            viewRect.height += items.Count * 24f;

            _scrollPos = GUI.BeginScrollView(new Rect(10, y, 400, 380), _scrollPos, viewRect);
            var sy = 0f;

            foreach (var item in items)
            {
                if (GUI.Button(new Rect(15, sy, 370, 22), item.DisplayName))
                    GiveItem(item);
                sy += 24;
            }

            GUI.EndScrollView();

            y += 380 + 6;

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                GUI.Label(new Rect(10, y, 400, 20), _statusMessage);
                if (Time.realtimeSinceStartup > _statusTimer)
                    _statusMessage = "";
            }

            GUI.DragWindow();
        }

        private static List<ItemEntry> GetActiveItems()
        {
            if (_activeTab == 0) return UsefulItems;
            if (_activeTab == 1) return WeaponItems;
            return StoryItems;
        }

        private static void GiveItem(ItemEntry item)
        {
            try
            {
                string err = Give(item.ItemId, 1);
                if (err != null) SetStatus(err, true);
                else SetStatus("Added: " + item.DisplayName);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Error("ItemGiver: " + ex.Message);
                SetStatus("Error: " + ex.Message, true);
            }
        }

        /// <summary>The F6 grant (also the test pilot's "give"): null on success, else why it was refused.</summary>
        internal static string Give(string itemId, int count)
        {
            var target = FindAnItem(itemId);
            if (target == null) return "Can't find '" + itemId + "'";

            // Keys / story objects go onto the shared party ring and unlock the world for everyone: a client
            // needs the host's AllowClientCheats for those. Consumables stay personal.
            if (Sync.NetGate.Client && !Config.ModConfig.ClientCheatsAllowed
                && (target.type == AnItem.AnItemType.Object || target.type == AnItem.AnItemType.Key))
                return "Host does not allow client cheats (keys)";

            InventoryManager.AddItem(target, count < 1 ? 1 : count);
            try
            {
                if (Sync.NetGate.Live
                    && (target.type == AnItem.AnItemType.Object || target.type == AnItem.AnItemType.Key))
                {
                    SyncRADation.Networking.PartyKeyRing.Note(target);
                    if (Sync.NetGate.Host)
                        SyncRADation.Networking.PartyKeyRing.Broadcast();
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return null;
        }

        private static AnItem FindAnItem(string name)
        {
            try
            {
                Items.itemlist parsed;
                if (System.Enum.TryParse(name, true, out parsed))
                {
                    var fromMgr = InventoryManager.getItem(parsed);
                    if (fromMgr != null) return fromMgr;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            try
            {
                var resItems = Resources.FindObjectsOfTypeAll<AnItem>();
                foreach (var anItem in resItems)
                {
                    if (anItem != null && (anItem.name == name || anItem.name.EndsWith(name)))
                        return anItem;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return null;
        }

        private static void SetStatus(string msg, bool isError = false)
        {
            _statusMessage = (isError ? "ERROR: " : "") + msg;
            _statusTimer = Time.realtimeSinceStartup + 3f;
        }

        private class ItemEntry
        {
            public string DisplayName;
            public string ItemId;

            public ItemEntry(string display, string id)
            {
                DisplayName = display;
                ItemId = id;
            }
        }
    }
}
