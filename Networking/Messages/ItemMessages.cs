// SyncRADation wire: World pickups, floor drops, storage box, key ring.
using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace SyncRADation.Networking
{
    public struct WorldPickupEntry
    {
        public long WorldId;
        public bool Triggered;
        public bool Active;
        /// <summary>
        /// Untriggered only: units a partial take left on the prop (0 = count unchanged); -1 = the host's DynamicSupply
        /// withheld it (prop off, WorldPickupSyncService.WithheldCount).
        /// </summary>
        public int Count;

        public void Serialize(NetDataWriter w)
        {
            w.Put(WorldId);
            w.Put(Triggered);
            w.Put(Active);
            w.Put(Count);
        }

        public static WorldPickupEntry Deserialize(NetDataReader r) =>
            new WorldPickupEntry
            {
                WorldId = r.GetLong(),
                Triggered = r.GetBool(),
                Active = r.GetBool(),
                Count = r.GetInt()
            };
    }

    public struct WorldPickupStateMessage
    {
        public int SenderPlayerId;
        public bool FullRefresh;
        public WorldPickupEntry[] Entries;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(FullRefresh);
            int n = Entries != null ? Entries.Length : 0;
            n = NetWire.ClampCount(n, NetWire.MaxPickupEntries, "WorldPickupState");
            w.Put(n);
            for (int i = 0; i < n; i++)
                Entries[i].Serialize(w);
        }

        public static WorldPickupStateMessage Deserialize(NetDataReader r)
        {
            var msg = new WorldPickupStateMessage
            {
                SenderPlayerId = r.GetInt(),
                FullRefresh = r.GetBool()
            };
            int n = NetWire.ReadCount(r, NetWire.MaxPickupEntries, "WorldPickupState");
            if (n > 0)
            {
                msg.Entries = new WorldPickupEntry[n];
                for (int i = 0; i < n; i++)
                    msg.Entries[i] = WorldPickupEntry.Deserialize(r);
            }
            return msg;
        }
    }

    public struct WorldPickupClaimMessage
    {
        public int ClaimerPlayerId;
        public long WorldId;
        public ushort ItemEnum;
        public int Count;
        /// <summary>0 = a claim. &gt;0 = the claimer's native take left this many on the prop: release its claim.</summary>
        public int Remaining;

        public void Serialize(NetDataWriter w)
        {
            w.Put(ClaimerPlayerId);
            w.Put(WorldId);
            w.Put(ItemEnum);
            w.Put(Count);
            w.Put(Remaining);
        }

        public static WorldPickupClaimMessage Deserialize(NetDataReader r) =>
            new WorldPickupClaimMessage
            {
                ClaimerPlayerId = r.GetInt(),
                WorldId = r.GetLong(),
                ItemEnum = r.GetUShort(),
                Count = r.GetInt(),
                Remaining = r.GetInt()
            };
    }

    public struct WorldPickupGrantMessage
    {
        public int TargetPlayerId;
        public long WorldId;
        public ushort ItemEnum;
        public int Count;

        public void Serialize(NetDataWriter w)
        {
            w.Put(TargetPlayerId);
            w.Put(WorldId);
            w.Put(ItemEnum);
            w.Put(Count);
        }

        public static WorldPickupGrantMessage Deserialize(NetDataReader r) =>
            new WorldPickupGrantMessage
            {
                TargetPlayerId = r.GetInt(),
                WorldId = r.GetLong(),
                ItemEnum = r.GetUShort(),
                Count = r.GetInt()
            };
    }

    /// <summary>Host → claimer: pickup was already claimed — roll back any native bag add.</summary>
    public struct WorldPickupDenyMessage
    {
        public int TargetPlayerId;
        public long WorldId;
        public ushort ItemEnum;
        public int Count;

        public void Serialize(NetDataWriter w)
        {
            w.Put(TargetPlayerId);
            w.Put(WorldId);
            w.Put(ItemEnum);
            w.Put(Count);
        }

        public static WorldPickupDenyMessage Deserialize(NetDataReader r) =>
            new WorldPickupDenyMessage
            {
                TargetPlayerId = r.GetInt(),
                WorldId = r.GetLong(),
                ItemEnum = r.GetUShort(),
                Count = r.GetInt()
            };
    }

    public struct DropItemSpawnMessage
    {
        public byte SenderID;
        public ushort LocalIndex;
        public ushort ItemEnum;
        public int Count;
        public float PosX;
        public float PosY;
        public float PosZ;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderID);
            w.Put(LocalIndex);
            w.Put(ItemEnum);
            w.Put(Count);
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
        }

        public static DropItemSpawnMessage Deserialize(NetDataReader r)
        {
            return new DropItemSpawnMessage
            {
                SenderID = r.GetByte(),
                LocalIndex = r.GetUShort(),
                ItemEnum = r.GetUShort(),
                Count = r.GetInt(),
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat()
            };
        }
    }

    public struct ItemPickedUpMessage
    {
        public byte SenderID;
        public ushort LocalIndex;
        public ushort ItemEnum;
        public int Count;
        public bool GrantToReceiver;
        /// <summary>Player who took the drop (host-stamped for client-originated copies).</summary>
        public byte ClaimerPlayerId;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderID);
            w.Put(LocalIndex);
            w.Put(ItemEnum);
            w.Put(Count);
            w.Put(GrantToReceiver);
            w.Put(ClaimerPlayerId);
        }

        public static ItemPickedUpMessage Deserialize(NetDataReader r)
        {
            return new ItemPickedUpMessage
            {
                SenderID = r.GetByte(),
                LocalIndex = r.GetUShort(),
                ItemEnum = r.GetUShort(),
                Count = r.GetInt(),
                GrantToReceiver = r.GetBool(),
                ClaimerPlayerId = r.GetByte()
            };
        }
    }

    /// <summary>Host → all: drop key (OldOwner,OldIndex) is now (NewOwner,NewIndex).</summary>
    public struct DropRekeyMessage
    {
        public byte OldOwner;
        public ushort OldIndex;
        public byte NewOwner;
        public ushort NewIndex;

        public void Serialize(NetDataWriter w)
        {
            w.Put(OldOwner);
            w.Put(OldIndex);
            w.Put(NewOwner);
            w.Put(NewIndex);
        }

        public static DropRekeyMessage Deserialize(NetDataReader r) =>
            new DropRekeyMessage
            {
                OldOwner = r.GetByte(),
                OldIndex = r.GetUShort(),
                NewOwner = r.GetByte(),
                NewIndex = r.GetUShort()
            };
    }

    public struct StorageBoxItem
    {
        public ushort ItemEnum;
        public int Count;

        public void Serialize(NetDataWriter w)
        {
            w.Put(ItemEnum);
            w.Put(Count);
        }

        public static StorageBoxItem Deserialize(NetDataReader r) =>
            new StorageBoxItem { ItemEnum = r.GetUShort(), Count = r.GetInt() };
    }

    public struct StorageBoxBlobMessage
    {
        public StorageBoxItem[] Items;

        public void Serialize(NetDataWriter w)
        {
            int n = Items != null ? Items.Length : 0;
            n = NetWire.ClampCount(n, NetWire.MaxStorageItems, "StorageBoxBlob");
            w.Put(n);
            for (int i = 0; i < n; i++)
                Items[i].Serialize(w);
        }

        public static StorageBoxBlobMessage Deserialize(NetDataReader r)
        {
            int n = NetWire.ReadCount(r, NetWire.MaxStorageItems, "StorageBoxBlob");
            var items = n > 0 ? new StorageBoxItem[n] : new StorageBoxItem[0];
            for (int i = 0; i < items.Length; i++)
                items[i] = StorageBoxItem.Deserialize(r);
            return new StorageBoxBlobMessage { Items = items };
        }
    }

    public struct PartyKeyRingMessage
    {
        public ushort[] ItemEnums;

        public void Serialize(NetDataWriter w)
        {
            int n = ItemEnums != null ? ItemEnums.Length : 0;
            n = NetWire.ClampCount(n, NetWire.MaxKeyRing, "PartyKeyRing");
            w.Put(n);
            for (int i = 0; i < n; i++)
                w.Put(ItemEnums[i]);
        }

        public static PartyKeyRingMessage Deserialize(NetDataReader r)
        {
            int n = NetWire.ReadCount(r, NetWire.MaxKeyRing, "PartyKeyRing");
            var arr = n > 0 ? new ushort[n] : new ushort[0];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = r.GetUShort();
            return new PartyKeyRingMessage { ItemEnums = arr };
        }
    }
}
