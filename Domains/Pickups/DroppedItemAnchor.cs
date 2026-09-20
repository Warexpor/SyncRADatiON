using UnityEngine;

namespace SyncRADation.ItemSystem
{
    public class DroppedItemAnchor : MonoBehaviour
    {
        void LateUpdate()
        {
            // One-shot floor snap after spawn; keep cheap and self-disable.
            if (!isActiveAndEnabled) return;
            GameObject go = gameObject;
            if (go == null)
            {
                enabled = false;
                return;
            }
            try
            {
                if (!go.activeInHierarchy)
                {
                    enabled = false;
                    return;
                }
            }
            catch
            {
                enabled = false;
                return;
            }
            DroppedItemManager.RestOnFloor(go);
            enabled = false;
        }
    }
}
