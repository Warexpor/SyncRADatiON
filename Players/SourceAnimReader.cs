using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Players
{
    public static class SourceAnimReader
    {
        private static AnimBools _lastBools;
        private static AnimTriggers _accumulatedTriggers;
        private static bool _hasLast;
        private static float _prevNormTime;

        public static void ReadFromPlayer(GameObject player, ref PlayerStateMessage msg)
        {
            Animator anim = null;
            var anims = player.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < anims.Length; i++)
            {
                if (anims[i] != null && anims[i].runtimeAnimatorController != null)
                {
                    anim = anims[i];
                    break;
                }
            }
            if (anim == null) return;

            // Read floats
            msg.Forward = SafeGetFloat(anim, "Forward");
            msg.Turn = SafeGetFloat(anim, "Turn");
            msg.AimingTime = SafeGetFloat(anim, "AimingTime");
            msg.Stamina = SafeGetFloat(anim, "Stamina");
            msg.Blend = SafeGetFloat(anim, "Blend");
            msg.IKwalk = SafeGetFloat(anim, "IKwalk");
            msg.InputX = SafeGetFloat(anim, "X");
            msg.InputY = SafeGetFloat(anim, "Y");
            msg.HurtTime = SafeGetFloat(anim, "HurtTime");

            // Detect footsteps from animation normalizedTime (base layer)
            try
            {
                var stateInfo = anim.GetCurrentAnimatorStateInfo(0);
                float nt = stateInfo.normalizedTime - Mathf.Floor(stateInfo.normalizedTime);
                msg.StepHappened = false;
                if (msg.Forward > 0.05f)
                {
                    // Two steps per cycle: at 0.0 (wrap) and 0.5 (mid)
                    if ((_prevNormTime < 0.5f && nt >= 0.5f) || (nt < _prevNormTime && nt < 0.3f))
                        msg.StepHappened = true;
                }
                _prevNormTime = nt;
            }
            catch { }

            // Detect ladder climbing from game state
            msg.Climbing = false;
            try
            {
                if (PlayerState.gameState == PlayerState.gameStates.traversing
                    && PlayerState.charState == PlayerState.charStates.animation)
                {
                    msg.Climbing = true;
                }
            }
            catch { }

            // Read bools
            AnimBools b = 0;
            if (SafeGetBool(anim, "Aiming")) b |= AnimBools.Aiming;
            if (SafeGetBool(anim, "Shooting")) b |= AnimBools.Shooting;
            if (SafeGetBool(anim, "Running")) b |= AnimBools.Running;
            if (SafeGetBool(anim, "Grounded")) b |= AnimBools.Grounded;
            if (SafeGetBool(anim, "Crouch")) b |= AnimBools.Crouch;
            if (SafeGetBool(anim, "Blocked")) b |= AnimBools.Blocked;
            if (SafeGetBool(anim, "Dead")) b |= AnimBools.Dead;
            if (SafeGetBool(anim, "Inventory")) b |= AnimBools.Inventory;
            if (SafeGetBool(anim, "Attack")) b |= AnimBools.Attack;
            if (SafeGetBool(anim, "Injured")) b |= AnimBools.Injured;
            if (SafeGetBool(anim, "Stomp")) b |= AnimBools.Stomp;
            if (SafeGetBool(anim, "Push")) b |= AnimBools.Push;
            if (SafeGetBool(anim, "Melee")) b |= AnimBools.Melee;
            if (SafeGetBool(anim, "Snap")) b |= AnimBools.Snap;
            if (SafeGetBool(anim, "Reload")) b |= AnimBools.Reload;
            if (SafeGetBool(anim, "Swap")) b |= AnimBools.Swap;
            if (SafeGetBool(anim, "Burst")) b |= AnimBools.Burst;
            if (SafeGetBool(anim, "Taser")) b |= AnimBools.Taser;
            if (SafeGetBool(anim, "Random")) b |= AnimBools.Random;
            if (SafeGetBool(anim, "Hugged")) b |= AnimBools.Hugged;
            if (SafeGetBool(anim, "ReloadRounds")) b |= AnimBools.ReloadRounds;
            if (SafeGetBool(anim, "ReloadChamber")) b |= AnimBools.ReloadChamber;
            msg.AnimBools = b;

            // Detect triggers: if a bool changed from false→true, fire the trigger
            AnimTriggers triggers = 0;
            if (_hasLast)
            {
                if (!_lastBools.HasFlag(AnimBools.Reload) && b.HasFlag(AnimBools.Reload))
                    triggers |= AnimTriggers.ReloadTrigger;
                if (!_lastBools.HasFlag(AnimBools.Attack) && b.HasFlag(AnimBools.Attack))
                    triggers |= AnimTriggers.AttackTrigger;
                if (!_lastBools.HasFlag(AnimBools.Swap) && b.HasFlag(AnimBools.Swap))
                    triggers |= AnimTriggers.SwapTrigger;
                if (!_lastBools.HasFlag(AnimBools.Burst) && b.HasFlag(AnimBools.Burst))
                    triggers |= AnimTriggers.BurstTrigger;
                if (!_lastBools.HasFlag(AnimBools.Stomp) && b.HasFlag(AnimBools.Stomp))
                    triggers |= AnimTriggers.StompTrigger;
                if (!_lastBools.HasFlag(AnimBools.Push) && b.HasFlag(AnimBools.Push))
                    triggers |= AnimTriggers.PushTrigger;
                if (!_lastBools.HasFlag(AnimBools.Snap) && b.HasFlag(AnimBools.Snap))
                    triggers |= AnimTriggers.SnapTrigger;
                if (!_lastBools.HasFlag(AnimBools.Injured) && b.HasFlag(AnimBools.Injured))
                    triggers |= AnimTriggers.Hurt;
                if (!_lastBools.HasFlag(AnimBools.Dead) && b.HasFlag(AnimBools.Dead))
                    triggers |= AnimTriggers.Die;
            }
            triggers |= _accumulatedTriggers;
            _accumulatedTriggers = 0;
            msg.AnimTriggers = triggers;
            _lastBools = b;
            _hasLast = true;
        }

        public static void AccumulateTrigger(AnimTriggers trigger)
        {
            _accumulatedTriggers |= trigger;
        }

        public static void Reset()
        {
            _hasLast = false;
            _lastBools = 0;
            _accumulatedTriggers = 0;
            _prevNormTime = 0f;
        }

        private static float SafeGetFloat(Animator a, string name)
        {
            try { return a.GetFloat(name); }
            catch { return 0f; }
        }

        private static bool SafeGetBool(Animator a, string name)
        {
            try { return a.GetBool(name); }
            catch { return false; }
        }
    }
}