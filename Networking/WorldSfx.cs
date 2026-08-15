// 3D FMOD one-shots at a world transform/point: quadratic falloff + occlusion.
using FMODUnity;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class WorldSfx
    {
        public const float Range = 40f;
        public const float CombatRange = 50f;

        public static bool TryVolume(Vector3 worldPos, out float volume, float range = Range)
        {
            volume = 1f;
            try
            {
                var player = PlayerState.player;
                if (player == null) return true;
                float dist = Vector3.Distance(player.transform.position, worldPos);
                if (dist > range) return false;
                float u = dist / range;
                volume = 1f - u * u;
                if (volume < 0.05f) volume = 0.05f;
                return true;
            }
            catch { return true; }
        }

        public static void Play(string path, Transform at, float gain = 1f, float range = Range)
        {
            if (string.IsNullOrEmpty(path) || at == null) return;
            float vol;
            if (!TryVolume(at.position, out vol, range)) return;
            Start(path, RuntimeUtils.To3DAttributes(at), vol * Mathf.Clamp01(gain));
        }

        public static void Play(string path, Vector3 pos, float gain = 1f, float range = Range)
        {
            if (string.IsNullOrEmpty(path)) return;
            float vol;
            if (!TryVolume(pos, out vol, range)) return;
            Start(path, RuntimeUtils.To3DAttributes(pos), vol * Mathf.Clamp01(gain));
        }

        public static void PlayClip(AudioClip clip, Vector3 pos, float gain = 1f, float range = Range)
        {
            if (clip == null) return;
            float vol;
            if (!TryVolume(pos, out vol, range)) return;
            try { AudioSource.PlayClipAtPoint(clip, pos, vol * Mathf.Clamp01(gain)); }
            catch { }
        }

        public static void PlayFootstep(string path, Transform at, bool running, float gain = 1f)
        {
            if (string.IsNullOrEmpty(path) || at == null) return;
            float vol;
            if (!TryVolume(at.position, out vol)) return;
            try
            {
                var inst = RuntimeManager.CreateInstance(path);
                inst.setVolume(vol * Mathf.Clamp01(gain));
                inst.set3DAttributes(RuntimeUtils.To3DAttributes(at));
                float run = running ? 1f : 0f;
                inst.setParameterByName("Run", run);
                inst.setParameterByName("Running", run);
                inst.setParameterByName("Speed", running ? 1f : 0.35f);
                inst.setParameterByName("runMod", running ? 1f : 0f);
                inst.start();
                inst.release();
            }
            catch { }
        }

        static void Start(string path, FMOD.ATTRIBUTES_3D attrs, float volume)
        {
            try
            {
                var inst = RuntimeManager.CreateInstance(path);
                inst.setVolume(volume);
                inst.set3DAttributes(attrs);
                inst.start();
                inst.release();
            }
            catch { }
        }
    }
}
