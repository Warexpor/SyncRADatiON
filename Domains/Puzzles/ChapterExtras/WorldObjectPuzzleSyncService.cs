using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Durable world objects that gate progress but had no sync: ROT_DiskManager (red/blue disks),
    /// DET_WallCreature (Hitbox.HP), MapRevealInteraction (map object revealed, prompt consumed).
    /// Host + client emit; late-join via full dump; ReapplyHeld re-snaps after a room remount.
    /// </summary>
    public sealed class WorldObjectPuzzleSyncService
    {
        // MEM_ChecklistLogic.Start() resets the book on every scene load; the checked set only grows, so the
        // read is OR-ed with what this session already saw (a reload must not look like "progress lost").
        static readonly System.Collections.Generic.Dictionary<long, int> _checklistSeen
            = new System.Collections.Generic.Dictionary<long, int>();

        // Highest HP seen per wall creature this session (its undamaged HP). IsProgressed holds a creature that is
        // below it, so partial damage survives a room remount, not only the dead state.
        static readonly System.Collections.Generic.Dictionary<long, int> _wallMaxHp
            = new System.Collections.Generic.Dictionary<long, int>();

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

        // Decompile MEM_ChecklistLogic: CheckItem(i) writes "x" into checklistBook.variables[i]; the list is
        // complete when every index 1..len-2 is "x" (then ArianeBusy off / ArianeReady on).
        const string Checked = "x";

        static int ReadChecklistMask(MEM_ChecklistLogic x, out bool complete)
        {
            complete = false;
            int mask = 0;
            var vars = x.checklistBook != null ? x.checklistBook.variables : null;
            if (vars == null) return 0;
            int n = vars.Length;
            bool all = n > 2;
            for (int i = 1; i < n - 1 && i < 31; i++)
            {
                if (vars[i] == Checked) mask |= 1 << i;
                else all = false;
            }
            complete = all;
            return mask;
        }

        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.ROT_DiskManager:
                {
                    // Decompile ROT_DiskManager.InsertDiskRed/Blue: red/blue + SProgress bool.
                    var x = (ROT_DiskManager)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.red, x.blue, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.DET_WallCreature:
                {
                    // Decompile DET_WallCreature.Update: colliders / animator are pure functions of hitb.HP.
                    var x = (DET_WallCreature)c;
                    if (x.hitb == null) return false;
                    int hp = x.hitb.HP;
                    int maxHp;
                    if (!_wallMaxHp.TryGetValue(wid, out maxHp) || hp > maxHp) _wallMaxHp[wid] = hp;
                    entry = PuzzleDomainUtil.Mk(type, wid, hp < 1, false, false, hp, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.MapReveal:
                {
                    // Decompile MapRevealInteraction.Interact: map.SetActive(true), self SetActive(false).
                    var x = (MapRevealInteraction)c;
                    bool revealed = x.map != null && x.map.activeSelf;
                    entry = PuzzleDomainUtil.Mk(type, wid, revealed, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.MEM_ChecklistLogic:
                {
                    var x = (MEM_ChecklistLogic)c;
                    bool complete;
                    int mask = ReadChecklistMask(x, out complete);
                    int seen;
                    _checklistSeen.TryGetValue(wid, out seen);
                    mask |= seen;
                    _checklistSeen[wid] = mask;
                    // ForceComplete state is not readable (static flag is consumed); Ariane objects mirror it.
                    bool ready = complete;
                    try { if (x.ArianeReady != null && x.ArianeReady.activeSelf) ready = true; } catch (System.Exception e) { Guard.Swallow(e); }
                    entry = PuzzleDomainUtil.Mk(type, wid, ready, false, false, mask, 0, 0, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyChecklist(MEM_ChecklistLogic x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Only ever adds: CheckItem is the native path (writes the item, flips Ariane busy/ready once the
            // list is full), so a live tick and a held re-snap after the scene reset the book are the same call.
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
                    try { x.CheckItem(i); }
                    catch (System.Exception ex) { PuzzleSyncService.WarnOnce("checklist-item", ex.Message); }
                }
            }
            if (!e.Bool0) return;
            // Complete (incl. forceComplete, which only flips these two objects): durable object state.
            try
            {
                if (x.ArianeBusy != null) x.ArianeBusy.SetActive(false);
                if (x.ArianeReady != null) x.ArianeReady.SetActive(true);
            }
            catch (System.Exception ex) { PuzzleSyncService.WarnOnce("checklist-ariane", ex.Message); }
        }

        public static void ApplyDiskManager(ROT_DiskManager x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Native Insert* sets SProgress + the flag and starts the slide-in coroutine; the flag guard
            // makes repeat applies (join dump, held re-snap) no-ops. OnEnable restores the pose from SProgress.
            if (e.Bool0 && !x.red)
            {
                try { x.InsertDiskRed(); }
                catch (System.Exception ex) { PuzzleSyncService.WarnOnce("disk-red", ex.Message); }
            }
            if (e.Bool1 && !x.blue)
            {
                try { x.InsertDiskBlue(); }
                catch (System.Exception ex) { PuzzleSyncService.WarnOnce("disk-blue", ex.Message); }
            }
        }

        public static void ApplyWallCreature(DET_WallCreature x, PuzzleStateEntry e)
        {
            if (x == null || x.hitb == null) return;
            // Live: damage only moves HP down, concurrent hits from several players merge as min().
            // Dump / held re-snap (not a live edge) is the host's settled state: set it exactly, so a
            // heal / re-arm the host already did reaches late joiners and remounts.
            // The undamaged HP is the component's own value before the first apply (a late joiner's dump already
            // carries the damaged HP, so the dump alone would make partial damage look like the maximum).
            int seenMax;
            int localHp = x.hitb.HP;
            if (!_wallMaxHp.TryGetValue(e.WorldId, out seenMax) || localHp > seenMax) _wallMaxHp[e.WorldId] = localHp;
            if (!_wallMaxHp.TryGetValue(e.WorldId, out seenMax) || e.Int0 > seenMax) _wallMaxHp[e.WorldId] = e.Int0;
            if (e.Int0 < x.hitb.HP || (e.Int0 != x.hitb.HP && !PuzzleSyncService.LiveEdge))
                x.hitb.HP = e.Int0;
        }

        public static void ApplyMapReveal(MapRevealInteraction x, PuzzleStateEntry e)
        {
            if (x == null || !e.Bool0) return;
            try { if (x.map != null) x.map.SetActive(true); }
            catch (System.Exception ex) { PuzzleSyncService.WarnOnce("map-reveal", ex.Message); }
            PuzzleSyncService.DisableInteractions(x);
            try { x.gameObject.SetActive(false); }
            catch (System.Exception ex) { PuzzleSyncService.WarnOnce("map-reveal-self", ex.Message); }
        }
    }
}
