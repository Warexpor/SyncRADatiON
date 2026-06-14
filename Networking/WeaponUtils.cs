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
    }
}