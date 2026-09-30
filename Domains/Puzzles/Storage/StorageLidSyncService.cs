using System.Reflection;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// StorageBox lid open flag only — box item blobs stay on StorageBoxSyncService.
    /// </summary>
    public sealed class StorageLidSyncService
    {
        static FieldInfo _openField;

        public static void EnsureOpenField()
        {
            if (_openField == null)
                _openField = typeof(StorageBox).GetField("open", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        public static bool TryRead(StorageBox x, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            if (x == null) return false;
            EnsureOpenField();
            bool open = _openField != null && (bool)_openField.GetValue(x);
            entry = PuzzleDomainUtil.Mk(PuzzleType.StorageBox, wid, open, false, false, 0, 0, 0, 0, 0);
            return true;
        }

        public static void Apply(StorageBox x, PuzzleStateEntry e, bool cinematic)
        {
            if (x != null)
                SnapLid(x, e.Bool0, cinematic);
        }

        public static void SnapLid(StorageBox x, bool open, bool cinematic)
        {
            if (x == null) return;
            EnsureOpenField();
            if (_openField != null)
                _openField.SetValue(x, open);
            if (!open) return;
            if (cinematic)
            {
                try { x.StartCoroutine("Open"); return; }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            try
            {
                if (x.lid != null)
                    x.lid.localEulerAngles = new Vector3(-90f, 0f, 0f);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
