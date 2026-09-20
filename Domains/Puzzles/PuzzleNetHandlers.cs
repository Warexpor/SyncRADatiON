using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SyncRADation.Networking
{
    /// <summary>PuzzleState send + apply — keeps LanNetworkManager thin.</summary>
    internal sealed class PuzzleNetHandlers
    {
        private readonly LanNetworkManager _net;
        private readonly PuzzleSyncService _puzzle;

        internal PuzzleNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
            _puzzle = net.PuzzleSync ?? throw new System.ArgumentNullException(nameof(net.PuzzleSync));
        }

        internal void SendPuzzleState(IList<PuzzleStateEntry> entries, bool fullRefresh, int exceptPlayerId = -1)
        {
            if (entries == null || entries.Count == 0) return;
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.PuzzleState);
            writer.Put(_net.LocalPlayerId);
            writer.Put(fullRefresh);
            writer.Put(entries.Count);
            for (int i = 0; i < entries.Count; i++)
                entries[i].Serialize(writer);
            if (exceptPlayerId >= 0)
                _net.BroadcastRawExcept(writer, DeliveryMethod.ReliableOrdered, exceptPlayerId);
            else
                _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandlePuzzleState(PuzzleStateMessage msg)
        {
            _puzzle.ApplyPuzzleState(msg);
        }
    }
}
