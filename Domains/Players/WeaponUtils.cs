// SyncRADation � single source of truth: Items.itemlist > WeaponType mapping
namespace SyncRADation.Networking
{
    public static class WeaponUtils
    {
        public static WeaponType EquippedWeaponType()
        {
            try
            {
                var equipped = InventoryManager.EquippedWeapon;
                if (equipped == null || equipped.parentItem == null) return WeaponType.None;
                return ItemToWeaponType(equipped.parentItem._item);
            }
            catch { return WeaponType.None; }
        }

        public static WeaponType ItemToWeaponType(Items.itemlist item)
        {
            switch (item)
            {
                case Items.itemlist.Pistol: return WeaponType.Pistol;
                case Items.itemlist.Revolver: return WeaponType.Revolver;
                case Items.itemlist.Shotgun: return WeaponType.Shotgun;
                case Items.itemlist.Rifle: return WeaponType.Rifle;
                case Items.itemlist.SMG: return WeaponType.SMG;
                case Items.itemlist.FlareGun: return WeaponType.Flare;
                case Items.itemlist.FlakGun: return WeaponType.CAR;
                case Items.itemlist.Machete: return WeaponType.Melee;
                case Items.itemlist.Taser: return WeaponType.Handgun;
                default: return WeaponType.None;
            }
        }

        /// <summary>ElsterNewController bool names. Most guns are Weapon/X; Handgun and CAR are unprefixed.</summary>
        public static string AnimatorBoolName(WeaponType weapon)
        {
            switch (weapon)
            {
                case WeaponType.Handgun: return "Handgun";
                case WeaponType.Melee: return "Weapon/Melee";
                case WeaponType.Pistol: return "Weapon/Pistol";
                case WeaponType.Revolver: return "Weapon/Revolver";
                case WeaponType.Shotgun: return "Weapon/Shotgun";
                case WeaponType.Rifle: return "Weapon/Rifle";
                case WeaponType.SMG: return "Weapon/SMG";
                case WeaponType.Flare: return "Weapon/Flare";
                case WeaponType.CAR: return "CAR";
                default: return null;
            }
        }

        public static readonly string[] AnimatorBoolNames =
        {
            "Handgun",
            "Weapon/Melee",
            "Weapon/Pistol",
            "Weapon/Revolver",
            "Weapon/Shotgun",
            "Weapon/Rifle",
            "Weapon/SMG",
            "Weapon/Flare",
            "CAR"
        };

        /// <summary>Cached Animator.StringToHash of <see cref="AnimatorBoolNames"/> (same order).</summary>
        // persistent: constant hash table
        public static readonly int[] AnimatorBoolHashes = HashAll();

        static int[] HashAll()
        {
            var names = AnimatorBoolNames;
            var ids = new int[names.Length];
            for (int i = 0; i < ids.Length; i++)
                ids[i] = UnityEngine.Animator.StringToHash(names[i]);
            return ids;
        }

        /// <summary>Animator bool hash for the weapon, or 0 for none (matches AnimatorBoolName).</summary>
        public static int AnimatorBoolHash(WeaponType weapon)
        {
            string n = AnimatorBoolName(weapon);
            if (n == null) return 0;
            for (int i = 0; i < AnimatorBoolNames.Length; i++)
                if (AnimatorBoolNames[i] == n) return AnimatorBoolHashes[i];
            return 0;
        }
    }
}