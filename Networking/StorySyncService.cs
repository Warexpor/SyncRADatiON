// Host commits SProgress / Dialoguer / END_Manager; peers apply presentation natives.
using System.Collections.Generic;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed class StorySyncService
    {
        private readonly Dictionary<string, StoryFlagEntry> _flags
            = new Dictionary<string, StoryFlagEntry>();
        private bool _needSend = true;
        private float _timer;
        public StoryCmd LastCmd;
        public ulong LastWorldId;

        public void Reset()
        {
            _flags.Clear();
            _needSend = true;
            _timer = 0f;
            LastCmd = StoryCmd.None;
            LastWorldId = 0;
        }

        public void RequestFullSend() => _needSend = true;

        public void NoteBool(string key, bool val) => Note(new StoryFlagEntry { Kind = 0, Key = key, BoolVal = val });
        public void NoteInt(string key, int val) => Note(new StoryFlagEntry { Kind = 1, Key = key, IntVal = val });
        public void NoteFloat(string key, float val) => Note(new StoryFlagEntry { Kind = 2, Key = key, FloatVal = val });
        public void NoteString(string key, string val) => Note(new StoryFlagEntry { Kind = 3, Key = key, StringVal = val ?? "" });
        public void NoteVector(string key, Vector3 val) =>
            Note(new StoryFlagEntry { Kind = 4, Key = key, FloatVal = val.x, VecY = val.y, VecZ = val.z });

        private void Note(StoryFlagEntry e)
        {
            if (string.IsNullOrEmpty(e.Key)) return;
            _flags[e.Key] = e;
            _needSend = true;
        }

        public void TickHost(LanNetworkManager net)
        {
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            _timer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_timer < 0.75f && !_needSend) return;
            _timer = 0f;
            if (!_needSend && _flags.Count == 0) return;
            Send(net, _needSend);
            _needSend = false;
        }

        public void Send(LanNetworkManager net, bool full, bool replayPresentation = false)
        {
            if (full)
                DumpLiveProgress();

            var arr = new StoryFlagEntry[_flags.Count];
            int i = 0;
            foreach (var kvp in _flags)
                arr[i++] = kvp.Value;

            string xml = "";
            try { xml = Dialoguer.GetGlobalVariablesState() ?? ""; } catch { }

            int circle = 0, death = 0, graves = 0, leave = 0, ending = 0;
            try
            {
                circle = END_Manager.Circle;
                death = END_Manager.Death;
                graves = END_Manager.Graves;
                leave = END_Manager.Leave;
                ending = END_Manager.Ending;
            }
            catch { }

            byte gs = 0;
            try { gs = (byte)PlayerState.gameState; } catch { }

            net.SendStoryCommit(new StoryCommitMessage
            {
                FullRefresh = full,
                DialoguerXml = xml,
                EndCircle = circle,
                EndDeath = death,
                EndGraves = graves,
                EndLeave = leave,
                EndingId = ending,
                Flags = arr,
                ActiveGameState = gs,
                ActiveWorldId = replayPresentation ? unchecked((long)LastWorldId) : 0,
                ActiveStoryCmd = replayPresentation ? (byte)LastCmd : (byte)0
            });
            if (full)
                PlaytestLog.Event("Story", "commit full flags=" + arr.Length
                    + " xml=" + (xml != null ? xml.Length : 0)
                    + " cmd=" + (replayPresentation ? LastCmd.ToString() : "-")
                    + " gs=" + gs);
        }

        private void DumpLiveProgress()
        {
            try
            {
                var p = SProgress.progress;
                if (p == null) return;
                DumpBools(p);
                DumpInts(p);
                DumpFloats(p);
                DumpStrings(p);
                DumpVectors(p);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Story] Dump progress: " + ex.Message);
            }
        }

        private void DumpBools(ProgressSlotBehaviour p)
        {
            try
            {
                var keys = p.boolKeys;
                var vals = p.bools;
                if (keys == null || vals == null) return;
                int n = Mathf.Min(keys.Count, vals.Count);
                for (int i = 0; i < n; i++)
                {
                    string k = keys[i];
                    if (!string.IsNullOrEmpty(k))
                        _flags[k] = new StoryFlagEntry { Kind = 0, Key = k, BoolVal = vals[i] };
                }
            }
            catch { }
        }

        private void DumpInts(ProgressSlotBehaviour p)
        {
            try
            {
                var keys = p.intKeys;
                var vals = p.ints;
                if (keys == null || vals == null) return;
                int n = Mathf.Min(keys.Count, vals.Count);
                for (int i = 0; i < n; i++)
                {
                    string k = keys[i];
                    if (!string.IsNullOrEmpty(k))
                        _flags[k] = new StoryFlagEntry { Kind = 1, Key = k, IntVal = vals[i] };
                }
            }
            catch { }
        }

        private void DumpFloats(ProgressSlotBehaviour p)
        {
            try
            {
                var keys = p.floatKeys;
                var vals = p.floats;
                if (keys == null || vals == null) return;
                int n = Mathf.Min(keys.Count, vals.Count);
                for (int i = 0; i < n; i++)
                {
                    string k = keys[i];
                    if (!string.IsNullOrEmpty(k))
                        _flags[k] = new StoryFlagEntry { Kind = 2, Key = k, FloatVal = vals[i] };
                }
            }
            catch { }
        }

        private void DumpStrings(ProgressSlotBehaviour p)
        {
            try
            {
                var keys = p.stringKeys;
                var vals = p.strings;
                if (keys == null || vals == null) return;
                int n = Mathf.Min(keys.Count, vals.Count);
                for (int i = 0; i < n; i++)
                {
                    string k = keys[i];
                    if (!string.IsNullOrEmpty(k))
                        _flags[k] = new StoryFlagEntry { Kind = 3, Key = k, StringVal = vals[i] ?? "" };
                }
            }
            catch { }
        }

        private void DumpVectors(ProgressSlotBehaviour p)
        {
            try
            {
                var keys = p.vectorKeys;
                var vals = p.vectors;
                if (keys == null || vals == null) return;
                int n = Mathf.Min(keys.Count, vals.Count);
                for (int i = 0; i < n; i++)
                {
                    string k = keys[i];
                    if (string.IsNullOrEmpty(k)) continue;
                    var v = vals[i];
                    _flags[k] = new StoryFlagEntry { Kind = 4, Key = k, FloatVal = v.x, VecY = v.y, VecZ = v.z };
                }
            }
            catch { }
        }

        public void ApplyCommit(StoryCommitMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.Role == NetworkRole.Host) return;

            NetGate.BeginApply();
            try
            {
                if (msg.Flags != null)
                {
                    for (int i = 0; i < msg.Flags.Length; i++)
                    {
                        var f = msg.Flags[i];
                        if (string.IsNullOrEmpty(f.Key)) continue;
                        _flags[f.Key] = f;
                        try
                        {
                            switch (f.Kind)
                            {
                                case 0: SProgress.SetBool(f.Key, f.BoolVal); break;
                                case 1: SProgress.SetInt(f.Key, f.IntVal); break;
                                case 2: SProgress.SetFloat(f.Key, f.FloatVal); break;
                                case 3: SProgress.SetString(f.Key, f.StringVal); break;
                                case 4: SProgress.SetVector(f.Key, new Vector3(f.FloatVal, f.VecY, f.VecZ)); break;
                            }
                        }
                        catch { }
                    }
                }

                if (!string.IsNullOrEmpty(msg.DialoguerXml))
                {
                    try { Dialoguer.SetGlobalVariablesState(msg.DialoguerXml); } catch { }
                }

                try
                {
                    END_Manager.Circle = msg.EndCircle;
                    END_Manager.Death = msg.EndDeath;
                    END_Manager.Graves = msg.EndGraves;
                    END_Manager.Leave = msg.EndLeave;
                    END_Manager.Ending = msg.EndingId;
                }
                catch { }
            }
            finally
            {
                NetGate.EndApply();
            }

            PlaytestLog.Event("Story", "apply commit full=" + msg.FullRefresh
                + " flags=" + (msg.Flags != null ? msg.Flags.Length : 0)
                + " xml=" + (msg.DialoguerXml != null ? msg.DialoguerXml.Length : 0)
                + " cmd=" + (StoryCmd)msg.ActiveStoryCmd);

            if (msg.FullRefresh && msg.ActiveStoryCmd != 0 && msg.ActiveWorldId != 0)
            {
                var replay = (StoryCmd)msg.ActiveStoryCmd;
                if (!IsLocalInspect(replay))
                {
                    ApplyPresentation(new StoryPresentationMessage
                    {
                        WorldId = msg.ActiveWorldId,
                        Cmd = replay,
                        Int0 = 0,
                        Text = ""
                    });
                }
            }
        }

        public void BroadcastPresentation(StoryCmd cmd, ulong worldId, int int0, string text)
        {
            if (IsLocalInspect(cmd)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            LastCmd = cmd;
            LastWorldId = worldId;
            PlaytestLog.Event("Story", "send " + cmd + " id=" + worldId.ToString("X16") + " i=" + int0
                + (string.IsNullOrEmpty(text) ? "" : " '" + text + "'"));
            net.SendStoryPresentation(new StoryPresentationMessage
            {
                WorldId = unchecked((long)worldId),
                Cmd = cmd,
                Int0 = int0,
                Text = text ?? ""
            });
        }

        public void ApplyPresentation(StoryPresentationMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.Role == NetworkRole.Host) return;

            NetGate.BeginApply();
            try
            {
                ulong id = unchecked((ulong)msg.WorldId);
                PlaytestLog.Event("Story", "apply " + msg.Cmd + " id=" + id.ToString("X16") + " i=" + msg.Int0);
                switch (msg.Cmd)
                {
                    case StoryCmd.DialogueStart:
                    {
                        var d = Find<Dialogue>(id);
                        if (d != null && !LocalInspect.Dialogue(d))
                            d.StartDialogue();
                        else if (d != null)
                            PlaytestLog.Event("Story", "skip local inspect DialogueStart");
                        break;
                    }
                    case StoryCmd.DialoguerStartId:
                        try { Dialoguer.StartDialogue(msg.Int0); } catch { }
                        break;
                    case StoryCmd.DialogueContinue:
                        try
                        {
                            if (msg.Int0 != 0) Dialoguer.ContinueDialogue(msg.Int0);
                            else Dialoguer.ContinueDialogue();
                        }
                        catch { }
                        break;
                    case StoryCmd.DialogueEnd:
                        try { Dialoguer.EndDialogue(); } catch { }
                        break;
                    case StoryCmd.CutsceneStart:
                    {
                        var c = Find<CutsceneManager>(id);
                        if (c != null && LocalInspect.Cinematic(c.gameObject))
                            PlaytestLog.Event("Story", "skip local cinematic CutsceneStart");
                        else if (c != null)
                            c.StartCutscene();
                        break;
                    }
                    case StoryCmd.CutsceneSkip:
                    {
                        var c = Find<CutsceneManager>(id);
                        if (c != null)
                        {
                            try
                            {
                                if (c.skipper != null && c.skipper.skipEvent != null)
                                    c.skipper.skipEvent.Invoke();
                            }
                            catch { }
                        }
                        break;
                    }
                    case StoryCmd.CutsceneProceed:
                    {
                        var cut = Find<CutsceneCut>(id);
                        if (cut != null) cut.Proceed();
                        break;
                    }
                    case StoryCmd.EventScreenStart:
                    case StoryCmd.EventScreenExit:
                    case StoryCmd.OpenBookMemory:
                    case StoryCmd.BookOpen:
                        PlaytestLog.Event("Story", "skip local inspect " + msg.Cmd);
                        break;
                    case StoryCmd.EventZoneFire:
                    {
                        var z = Find<EventZone>(id);
                        if (z != null)
                        {
                            z.triggered = true;
                            try { if (z.onInRange != null) z.onInRange.Invoke(); } catch { }
                        }
                        break;
                    }
                    case StoryCmd.MultiConditionFire:
                    {
                        var m = Find<MultiConditionEvent>(id);
                        if (m != null)
                        {
                            try { m.TryOnce(); } catch { }
                            try { if (m.OnTryDone != null) m.OnTryDone.Invoke(); } catch { }
                        }
                        break;
                    }
                    case StoryCmd.DetermineEnding:
                    {
                        try
                        {
                            ModRuntime.Log?.Msg("[Story] END_Manager.Ending=" + END_Manager.Ending);
                            var finales = Object.FindObjectsOfType<Finale>();
                            if (finales != null && finales.Length > 0 && finales[0] != null)
                                finales[0].determineEnding();
                        }
                        catch { }
                        break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Story] Apply presentation: " + ex.Message);
            }
            finally
            {
                NetGate.EndApply();
            }
        }

        private static bool IsLocalInspect(StoryCmd cmd)
        {
            return cmd == StoryCmd.EventScreenStart
                || cmd == StoryCmd.EventScreenExit
                || cmd == StoryCmd.OpenBookMemory
                || cmd == StoryCmd.BookOpen;
        }

        private static T Find<T>(ulong worldId) where T : Component
        {
            if (worldId == 0) return null;
            try
            {
                var all = Object.FindObjectsOfType<T>();
                if (all == null) return null;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    if (WorldId.FromGameObject(all[i].gameObject) == worldId)
                        return all[i];
                }
            }
            catch { }
            PlaytestLog.Miss("Story", typeof(T).Name, worldId);
            return null;
        }
    }
}
