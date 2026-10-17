// SyncRADation wire: message type ids. Never renumber (FrozenConstantsTests pins every value); per-version history in docs/PROTOCOL.md.
using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace SyncRADation.Networking
{
    public enum NetMessageType : byte
    {
        // Unassigned: 3-7, 16, 18, 36-40, 43-59, 64-66, 68-72 (retired ids). New ids go after _Highest.
        Handshake = 1,
        PlayerState = 2,
        DoorState = 8,
        DropItemSpawn = 9,
        ItemPickedUp = 10,
        FriendlyFire = 11,
        EnemyState = 12,
        EnemyDamage = 13,
        SceneHello = 14,
        PuzzleState = 15,
        BossState = 17,
        SnapshotRequest = 19,
        WorldPickupState = 20,
        PlayerVital = 21,
        WorldPickupClaim = 22,
        WorldPickupGrant = 23,
        SceneFollow = 24,
        InteractionRequest = 25,
        InteractionAck = 26,
        StoryCommit = 27,
        StoryPresentation = 28,
        StorageBoxBlob = 29,
        PartyKeyRing = 30,
        DeathPolicy = 31,
        FmodEmitter = 32,
        PlayerRoster = 33,
        BonePose = 34,
        EnemySpawn = 35,
        PartySave = 41,
        PartyRoom = 42,
        /// <summary>Host → client: world pickup claim refused (another player got it first).</summary>
        WorldPickupDeny = 60,
        /// <summary>Peer → all (host relays): reliable avatar one-shot triggers (Fire/Hurt/Die/...).</summary>
        AvatarOneShot = 61,
        /// <summary>Client → host: boss hitbox damage / Falke stab request.</summary>
        BossHit = 62,
        /// <summary>Client → host: authoritative enemy side effect (stomp Kill, push, burn, wake).</summary>
        EnemyAction = 63,
        /// <summary>Client → host: a world StudioEventEmitter the client triggered locally (host relays to the other clients).</summary>
        FmodEmitterRequest = 67,
        /// <summary>Host → all: a departed peer's floor drop moved into the host key namespace.</summary>
        DropRekey = 73,
        /// <summary>Host → one client: the host's WorldIds of the categories whose registry checksum differs (chunked).</summary>
        SceneDiff = 74,
        /// <summary>Host → all: the host's shared-world digest (chunked). Client → host: what it found different (log lines).</summary>
        WorldDigest = 75,
        /// <summary>Host → all: the scene every ready peer is in (scene owners follow from it, SceneOwners).</summary>
        PeerScenes = 76,
        /// <summary>World traffic of a scene the host is not in, carried through the host (WorldRelayMessage modes).</summary>
        WorldRelay = 77,
        _Highest = 77
    }
}
