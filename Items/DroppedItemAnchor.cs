using UnityEngine;

namespace SyncRADation.ItemSystem
{
    public class DroppedItemAnchor : MonoBehaviour
    {
        void LateUpdate()
        {
            DroppedItemManager.RestOnFloor(gameObject);
            enabled = false;
        }
    }
}
