using UnityEngine;

namespace SyncRADation.Sync.Proxies
{
    public sealed class EntityProxy : MonoBehaviour
    {
        public short StableId { get; set; }
        public string EntityName { get; set; }

        public static EntityProxy Spawn(short id, string entityName, Vector3 position)
        {
            var go = new GameObject("Proxy_" + entityName + "_" + id);
            go.transform.position = position;
            var proxy = go.AddComponent<EntityProxy>();
            proxy.StableId = id;
            proxy.EntityName = entityName;
            return proxy;
        }
    }
}
