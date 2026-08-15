// Native HurtElster is the only HP path. Client downed vs host save-reload.
using FMODUnity;
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Players
{
    public static class NetworkDamageSystem
    {
        private static float _respawnTimer = -1f;
        private static bool _isDead;
        private static float _wipeSentAt = -99f;

        public static bool IsDead => _isDead;

        public static bool HostDying()
        {
            if (_isDead) return true;
            try { if (PlayerState.charState == PlayerState.charStates.dead) return true; } catch { }
            try { if (PlayerState.hp <= 0) return true; } catch { }
            return false;
        }

        public static bool TrySendHostWipe()
        {
            if (Time.unscaledTime - _wipeSentAt < 2f) return false;
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected) return false;
            _wipeSentAt = Time.unscaledTime;
            net.SendDeathPolicy(DeathKind.HostWipeReload);
            return true;
        }

        public static float PlayerHP
        {
            get
            {
                try { return PlayerState.hp; } catch { return 0f; }
            }
        }

        public static float MaxHP => 100f;

        public static void ApplyDamage(float damage, Vector3 hitPoint, Vector3 hitDir)
        {
            if (_isDead) return;
            try
            {
                if (PlayerState.hp <= 0 || PlayerState.charState == PlayerState.charStates.dead)
                    return;
            }
            catch { }

            try
            {
                PlayerState.HurtElster((int)damage, new Vector2(hitDir.x, hitDir.z));
            }
            catch
            {
                try
                {
                    var hurtSound = PlayerState.player?.GetComponent<ElsterHurtSound>();
                    if (hurtSound != null && !string.IsNullOrEmpty(hurtSound.HurtSound))
                        RuntimeManager.PlayOneShot(hurtSound.HurtSound, hitPoint);
                }
                catch { }

                try { PlayerState.hp = Mathf.Max(0, PlayerState.hp - (int)damage); } catch { }
                try { PlayerState.charState = PlayerState.charStates.grabbed; } catch { }
                try
                {
                    var anim = PlayerState.player?.GetComponentInChildren<Animator>(true);
                    if (anim != null)
                    {
                        anim.SetFloat("HurtTime", 1f);
                        anim.SetBool("Injured", true);
                    }
                }
                catch { }
            }

            int hp = 0;
            try { hp = PlayerState.hp; } catch { }
            ModRuntime.Log?.Msg("[Damage] -" + damage.ToString("F0") + " HP, remaining: " + hp);

            bool dead = hp <= 0;
            try { dead = dead || PlayerState.charState == PlayerState.charStates.dead; } catch { }
            if (dead)
                Die();
        }

        private static void Die()
        {
            if (_isDead) return;
            _isDead = true;
            try { PlayerState.charState = PlayerState.charStates.dead; } catch { }

            DropInventoryOnDeath();

            try
            {
                var hurtSound = PlayerState.player?.GetComponent<ElsterHurtSound>();
                if (hurtSound != null && !string.IsNullOrEmpty(hurtSound.DeathSound))
                    RuntimeManager.PlayOneShot(hurtSound.DeathSound);
            }
            catch { }

            var net = ModRuntime.Network;
            if (net != null && net.IsConnected)
            {
                if (net.Role == NetworkRole.Host)
                {
                    ModRuntime.Log?.Msg("[Damage] Host died — wipe reload");
                    TrySendHostWipe();
                    ReloadHostSave();
                    return;
                }

                net.SendDeathPolicy(DeathKind.ClientDowned);
                try { PlayerState.suspendInput = true; } catch { }
                _respawnTimer = -1f;
                ModRuntime.Log?.Msg("[Damage] Client downed — world continues");
                return;
            }

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

            try
            {
                var dict = InventoryManager.elsterItems;
                if (dict == null) return;

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

                int n = 0;
                foreach (var entry in itemsToDrop)
                {
                    ushort idx = net.AllocateItemIndex();
                    int key = (net.LocalPlayerId << 16) | idx;
                    Vector3 dropPos = pos + new Vector3(
                        UnityEngine.Random.Range(-0.12f, 0.12f),
                        0f,
                        UnityEngine.Random.Range(-0.12f, 0.12f));
                    n++;
                    DroppedItemManager.SpawnLocalItem(entry.enumVal, entry.count, key, dropPos);

                    net.SendDropItem(new Networking.DropItemSpawnMessage
                    {
                        SenderID = (byte)net.LocalPlayerId,
                        LocalIndex = idx,
                        ItemEnum = (ushort)entry.enumVal,
                        Count = entry.count,
                        PosX = dropPos.x,
                        PosY = dropPos.y,
                        PosZ = dropPos.z
                    });

                    try { InventoryManager.RemoveItem(entry.item, entry.count); } catch { }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[DeathDrop] Failed: " + ex.Message);
            }
        }

        public static void HandleDeathPolicy(DeathPolicyMessage msg)
        {
            var net = ModRuntime.Network;
            if (net == null) return;
            if (msg.SenderPlayerId == net.LocalPlayerId) return;

            if (msg.Kind == DeathKind.HostWipeReload)
            {
                if (net.Role == NetworkRole.Host) return;
                ModRuntime.Log?.Msg("[Damage] Host died — reloading last save");
                ReloadHostSave();
                return;
            }

            if (msg.Kind == DeathKind.ClientDowned)
                ModRuntime.Log?.Msg("[Damage] Peer " + msg.SenderPlayerId + " downed");
        }

        private static void ReloadHostSave()
        {
            Sync.NetGate.BeginApply();
            try
            {
                SaveManager.Load();
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Damage] SaveManager.Load failed: " + ex.Message);
            }
            finally
            {
                Sync.NetGate.EndApply();
                Reset();
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
            _isDead = false;
            try { PlayerState.charState = PlayerState.charStates.idle; } catch { }
            ModRuntime.Log?.Msg("[Damage] Respawned");
        }

        public static void Reset()
        {
            _isDead = false;
            _respawnTimer = -1f;
            _wipeSentAt = -99f;
            try { PlayerState.suspendInput = false; } catch { }
        }
    }
}
