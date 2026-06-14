// SyncRADation — MonoBehaviour: billboard sprite, pickup trigger, DontDestroyOnLoad
using UnityEngine;

namespace SyncRADation.ItemSystem
{
    public sealed class WorldItem : MonoBehaviour
    {
        public int ItemKey;
        public Items.itemlist ItemEnum;
        public int Count;

        public static int NearbyID = -1;

        public void Setup(Items.itemlist item, int count, int itemKey)
        {
            ItemEnum = item;
            Count = count;
            ItemKey = itemKey;

            var itemData = InventoryManager.getItem(item);
            if (itemData != null && itemData.worldSprite != null)
            {
                var sr = gameObject.GetComponent<SpriteRenderer>();
                if (sr == null) sr = gameObject.AddComponent<SpriteRenderer>();
                sr.sprite = itemData.worldSprite;
            }

            var col = gameObject.GetComponent<BoxCollider>();
            if (col == null)
            {
                col = gameObject.AddComponent<BoxCollider>();
                col.isTrigger = true;
                col.size = new Vector3(0.6f, 1.2f, 0.6f);
            }

            var rb = gameObject.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
            }
        }

        void Start()
        {
            Object.DontDestroyOnLoad(gameObject);
        }

        void Update()
        {
            if (Camera.main != null)
                transform.rotation = Camera.main.transform.rotation;
        }

        void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag("Player") && other.gameObject == PlayerState.player)
                NearbyID = ItemKey;
        }

        void OnTriggerExit(Collider other)
        {
            if (other.CompareTag("Player") && other.gameObject == PlayerState.player && NearbyID == ItemKey)
                NearbyID = -1;
        }

        void OnDestroy()
        {
            if (NearbyID == ItemKey)
                NearbyID = -1;
        }
    }
}
