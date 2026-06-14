// SyncRADation — player HP (100), damage, stagger, death, respawn 5s, inventory drop on death
using FMODUnity;
using SyncRADation.ItemSystem;
using UnityEngine;

namespace SyncRADation.Players
{
    public static class NetworkDamageSystem
    {
        public static float PlayerHP = 100f;
        public static float MaxHP = 100f;
        private static float _respawnTimer = -1f;
        private static bool _isDead;

        public static void ApplyDamage(float damage, Vector3 hitPoint, Vector3 hitDir)
        {
            if (_isDead) return;
            if (PlayerHP <= 0f) return;

            PlayerHP -= damage;

            try
            {
                var hurtSound = PlayerState.player?.GetComponent<ElsterHurtSound>();
                if (hurtSound != null && !string.IsNullOrEmpty(hurtSound.HurtSound))
                    RuntimeManager.PlayOneShot(hurtSound.HurtSound, hitPoint);
            }
            catch { }

            PlayerState.charState = PlayerState.charStates.grabbed;

            try
            {
                var anim = PlayerState.player?.GetComponent<Animator>();
                if (anim != null)
                {
                    anim.SetFloat("HurtTime", 1f);
                    anim.SetBool("Injured", true);
                }
            }
            catch { }

            ModRuntime.Log?.Msg("[Damage] -" + damage.ToString("F0") + " HP, remaining: " + PlayerHP.ToString("F0"));

            if (PlayerHP <= 0f)
                Die();
        }

        private static void Die()
        {
            _isDead = true;
            PlayerHP = 0f;
            PlayerState.charState = PlayerState.charStates.dead;

            DropInventoryOnDeath();

            try
            {
                var hurtSound = PlayerState.player?.GetComponent<ElsterHurtSound>();
                if (hurtSound != null && !string.IsNullOrEmpty(hurtSound.DeathSound))
                    RuntimeManager.PlayOneShot(hurtSound.DeathSound);
            }
            catch { }

            _respawnTimer = 5f;
            ModRuntime.Log?.Msg("[Damage] Player died. Respawn in 5s");
        }

        private static void DropInventoryOnDeath()
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected) return;
            var player = PlayerState.player;
            if (player == null) return;

            Vector3 pos = player.transform.position;
            var netPos = pos;

            try
            {
                var dict = InventoryManager.elsterItems;
                if (dict == null) return;

                // Collect items first, then remove (avoid modifying dictionary during enumeration)
                var itemsToDrop = new System.Collections.Generic.List<(AnItem item, int count, Items.itemlist enumVal)>();
                var enumerator = dict.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var kvp = enumerator.Current;
                    var item = kvp.key;
                    int count = kvp.value;
                    if (item == null || count <= 0) continue;
                    Items.itemlist itemEnum;
                    try { itemEnum = item._item; } catch { continue; }
                    if (itemEnum == Items.itemlist.None || itemEnum == Items.itemlist.Injector) continue;
                    itemsToDrop.Add((item, count, itemEnum));
                }
                enumerator.Dispose();

                foreach (var entry in itemsToDrop)
                {
                    ushort idx = net.AllocateItemIndex();
                    int key = (net.LocalPlayerId << 16) | idx;
                    DroppedItemManager.SpawnLocalItem(entry.enumVal, entry.count, key, netPos);

                    net.SendDropItem(new Networking.DropItemSpawnMessage
                    {
                        SenderID = (byte)net.LocalPlayerId,
                        LocalIndex = idx,
                        ItemEnum = (ushort)entry.enumVal,
                        Count = entry.count,
                        PosX = netPos.x,
                        PosY = netPos.y,
                        PosZ = netPos.z
                    });

                    try { InventoryManager.RemoveItem(entry.item, entry.count); } catch { }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[DeathDrop] Failed: " + ex.Message);
            }
        }

        public static void TickRespawn()
        {
            if (_respawnTimer <= 0f) return;
            _respawnTimer -= Mathf.Min(Time.deltaTime, 0.1f);
            if (_respawnTimer <= 0f)
                Respawn();
        }

        private static void Respawn()
        {
            PlayerHP = MaxHP;
            _isDead = false;
            PlayerState.charState = PlayerState.charStates.idle;
            ModRuntime.Log?.Msg("[Damage] Respawned");
        }

        public static void Reset()
        {
            PlayerHP = MaxHP;
            _isDead = false;
            _respawnTimer = -1f;
        }
    }
}
