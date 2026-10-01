using System.Collections.Generic;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Durable world objects that gate progress: ROT_DiskManager (red/blue disks), DET_WallCreature (Hitbox.HP),
    /// MapRevealInteraction (map revealed, prompt consumed), MEM_ChecklistLogic (checked items).
    /// </summary>
    public sealed class WorldObjectPuzzleSyncService
    {
        // MEM_ChecklistLogic.Start() resets the book on every scene load; the checked set only grows, so the
        // read is OR-ed with what this session already saw (a reload must not look like "progress lost").
        static readonly Dictionary<long, int> _checklistSeen = new Dictionary<long, int>();

        // Highest HP seen per wall creature this session (its undamaged HP). The progressed rule holds a creature
        // below it, so partial damage survives a room remount, not only the dead state.
        static readonly Dictionary<long, int> _wallMaxHp = new Dictionary<long, int>();

        public static void Reset()
        {
            _checklistSeen.Clear();
            _wallMaxHp.Clear();
        }

        internal static bool WallDamaged(long wid, int hp)
        {
            int max;
            return _wallMaxHp.TryGetValue(wid, out max) && hp < max;
        }

        static void NoteWallHp(long wid, int hp)
        {
            int max;
            if (!_wallMaxHp.TryGetValue(wid, out max) || hp > max) _wallMaxHp[wid] = hp;
        }

        // Decompile MEM_ChecklistLogic: CheckItem(i) writes "x" into checklistBook.variables[i]; the list is
        // complete when every index 1..len-2 is "x" (then ArianeBusy off / ArianeReady on).
        const string Checked = "x";

        /// <summary>Decompile ROT_DiskManager.InsertDiskRed/Blue: red/blue + SProgress bool.</summary>
        internal static PuzzleStateEntry ReadDiskManager(ROT_DiskManager x, long wid)
            => Mk(PuzzleType.ROT_DiskManager, wid, x.red, x.blue, false, 0, 0, 0, 0, 0);

        /// <summary>Decompile DET_WallCreature.Update: colliders / animator are pure functions of hitb.HP.</summary>
        internal static bool TryReadWallCreature(DET_WallCreature x, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            if (x.hitb == null) return false;
            int hp = x.hitb.HP;
            NoteWallHp(wid, hp);
            entry = Mk(PuzzleType.DET_WallCreature, wid, hp < 1, false, false, hp, 0, 0, 0, 0);
            return true;
        }

        /// <summary>Decompile MapRevealInteraction.Interact: map.SetActive(true), self SetActive(false).</summary>
        internal static PuzzleStateEntry ReadMapReveal(MapRevealInteraction x, long wid)
            => Mk(PuzzleType.MapReveal, wid, x.map != null && x.map.activeSelf, false, false, 0, 0, 0, 0, 0);

        /// <summary>Int0 = checked items (OR-ed with this session's), Bool0 = complete (forceComplete only flips the Ariane objects).</summary>
        internal static PuzzleStateEntry ReadChecklist(MEM_ChecklistLogic x, long wid)
        {
            bool complete = false;
            int mask = 0;
            var vars = x.checklistBook != null ? x.checklistBook.variables : null;
            if (vars != null)
            {
                int n = vars.Length;
                complete = n > 2;
                for (int i = 1; i < n - 1 && i < 31; i++)
                {
                    if (vars[i] == Checked) mask |= 1 << i;
                    else complete = false;
                }
            }
            int seen;
            _checklistSeen.TryGetValue(wid, out seen);
            mask |= seen;
            _checklistSeen[wid] = mask;
            bool ready = complete || (x.ArianeReady != null && x.ArianeReady.activeSelf);
            return Mk(PuzzleType.MEM_ChecklistLogic, wid, ready, false, false, mask, 0, 0, 0, 0);
        }

        /// <summary>
        /// Only ever adds: CheckItem is the native path (writes the item, flips Ariane busy/ready once the list is
        /// full), so a live tick and a held re-snap after the scene reset the book are the same call.
        /// </summary>
        internal static void ApplyChecklist(MEM_ChecklistLogic x, PuzzleStateEntry e)
        {
            if (x == null) return;
            int seen;
            _checklistSeen.TryGetValue(e.WorldId, out seen);
            _checklistSeen[e.WorldId] = seen | e.Int0;
            var vars = x.checklistBook != null ? x.checklistBook.variables : null;
            if (vars != null)
            {
                int n = vars.Length;
                for (int i = 1; i < n - 1 && i < 31; i++)
                {
                    if ((e.Int0 & (1 << i)) == 0 || vars[i] == Checked) continue;
                    int item = i;
                    Native("checklist-item", () => x.CheckItem(item));
                }
            }
            if (!e.Bool0) return;
            if (x.ArianeBusy != null) x.ArianeBusy.SetActive(false);
            if (x.ArianeReady != null) x.ArianeReady.SetActive(true);
        }

        /// <summary>
        /// Native Insert* sets SProgress + the flag and starts the slide-in coroutine; the flag guard makes repeat
        /// applies (join dump, held re-snap) no-ops. OnEnable restores the pose from SProgress.
        /// </summary>
        internal static void ApplyDiskManager(ROT_DiskManager x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (e.Bool0 && !x.red)
                Native("disk-red", x.InsertDiskRed);
            if (e.Bool1 && !x.blue)
                Native("disk-blue", x.InsertDiskBlue);
        }

        /// <summary>
        /// Live: damage only moves HP down, so concurrent hits from several players merge as min(). A dump / held
        /// re-snap is the host's settled state: set exactly, so a heal / re-arm reaches late joiners and remounts.
        /// The undamaged HP is the component's own value before the first apply (a late joiner's dump already carries
        /// the damaged HP, so the dump alone would make partial damage look like the maximum).
        /// </summary>
        internal static void ApplyWallCreature(DET_WallCreature x, PuzzleStateEntry e)
        {
            if (x == null || x.hitb == null) return;
            NoteWallHp(e.WorldId, x.hitb.HP);
            NoteWallHp(e.WorldId, e.Int0);
            if (e.Int0 < x.hitb.HP || (e.Int0 != x.hitb.HP && !PuzzleSyncService.LiveEdge))
                x.hitb.HP = e.Int0;
        }

        internal static void ApplyMapReveal(MapRevealInteraction x, PuzzleStateEntry e)
        {
            if (x == null || !e.Bool0) return;
            if (x.map != null) x.map.SetActive(true);
            PuzzleSyncService.DisableInteractions(x);
            x.gameObject.SetActive(false);
        }
    }
}
