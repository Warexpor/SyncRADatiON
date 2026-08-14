// SyncRADation — single source of truth: Items.itemlist > WeaponType mapping
namespace SyncRADation.Networking
{
    public static class WeaponUtils
    {
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
    }
}