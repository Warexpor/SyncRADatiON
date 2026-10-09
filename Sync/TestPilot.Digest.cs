// Test pilot "digest <tag>": this peer's view of the shared world, one sorted "section key value" line per object,
// written to <pilot dir>/digest-<tag>.txt. The scripts take one on every peer at the same moment ("at") and diff them
// (scripts/pilot/digest-diff.py): every line outside the "self" section must match the host's.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Players;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static partial class TestPilot
    {
        private static void WriteDigest(string tag)
        {
            var net = LanNetworkManager.Instance;
            var lines = new List<string>(512);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            lines.Add("scene name " + ActiveScene());

            // Story: the shared SProgress table (per-player keys never ride a commit, so they are not compared).
            var flags = new Dictionary<string, StoryFlagEntry>();
            if (SyncRADation.Networking.ProgressSlot.ReadShared(flags))
                foreach (var kv in flags)
                    lines.Add("story " + kv.Key.Replace(' ', '_') + " " + FlagText(kv.Value));

            lines.Add("ring keys [" + RingText() + "]");
            lines.Add("box items [" + BoxText() + "]");

            foreach (var kv in WorldRegistry.AllEnemies())
            {
                var e = kv.Value;
                if (e == null) continue;
                bool dead = e.state == EnemyController.enemystate.dead;
                lines.Add("enemy " + kv.Key.ToString("X16") + " " + (dead ? "dead" : "alive hp=" + (e.hitbox != null ? e.hitbox.HP : -1))
                    + " " + e.gameObject.name.Replace(' ', '_'));
            }
            // Doorway_Double.locked is a per-frame mirror of its lock's ConnectedDoors (InteractiveLockSingle.Update),
            // stale in a sleeping room chunk: compared only while awake. The link itself is the doorC line.
            foreach (var kv in WorldRegistry.AllDoubleDoors())
                if (kv.Value != null)
                    lines.Add("door2 " + kv.Key.ToString("X16") + " open=" + kv.Value.open + " locked="
                        + (kv.Value.gameObject.activeInHierarchy ? kv.Value.locked.ToString() : "asleep"));
            foreach (var kv in WorldRegistry.AllSlidingDoors())
                if (kv.Value != null)
                    lines.Add("doorS " + kv.Key.ToString("X16") + " open=" + kv.Value.opened);
            foreach (var kv in WorldRegistry.AllConnectedDoors())
                if (kv.Value != null)
                    lines.Add("doorC " + kv.Key.ToString("X16") + " locked=" + kv.Value.locked);

            net?.PickupSync?.PilotDigest(lines);
            net?.PuzzleSync?.PilotDigest(lines);

            try
            {
                foreach (var c in Resources.FindObjectsOfTypeAll<CutsceneManager>())
                {
                    if (c == null || !c.gameObject.scene.IsValid()) continue;
                    lines.Add("cutscene " + WorldId.FromGameObject(c.gameObject).ToString("X16") + " completed=" + c.completed
                        + " " + Owner(c));
                }
            }
            catch (Exception e) { Guard.Swallow(e); }

            // Per peer (not compared): who this is and what it carries.
            GameObject me = PlayerState.player;
            lines.Add("self role " + (net != null ? net.Role.ToString() : "-") + " p" + (net != null ? net.LocalPlayerId : -1));
            lines.Add("self where room=" + RoomName().Replace(' ', '_') + " pos=" + (me != null ? Pos(me.transform.position) : "-"));
            lines.Add("self hp " + PlayerState.hp + " dead=" + NetworkDamageSystem.IsDead);
            lines.Add("self bag [" + BagText() + "]");

            lines.Sort(StringComparer.Ordinal);
            string file = Path.Combine(Dir, "digest-" + tag + ".txt");
            try { File.WriteAllLines(file, lines.ToArray()); }
            catch (Exception ex) { Out("  digest write failed: " + ex.Message); return; }
            int enemies = 0, story = 0, puzzles = 0, pickups = 0;
            foreach (string l in lines)
            {
                if (l.StartsWith("enemy ", StringComparison.Ordinal)) enemies++;
                else if (l.StartsWith("story ", StringComparison.Ordinal)) story++;
                else if (l.StartsWith("puzzle ", StringComparison.Ordinal)) puzzles++;
                else if (l.StartsWith("pickup ", StringComparison.Ordinal)) pickups++;
            }
            Out("  digest " + tag + ": " + lines.Count + " lines (story " + story + ", enemies " + enemies + ", puzzles " + puzzles
                + ", pickups " + pickups + ") in " + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
        }
    }
}
