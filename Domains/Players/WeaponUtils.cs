// Items.itemlist -> WeaponType: the single mapping the pose wire, proxy weapon models and FF damage share.
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
    }
}
