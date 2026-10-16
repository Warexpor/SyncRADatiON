// Sync/SessionReset.cs and Sync/DumpFlush.cs are pure (BCL + the shimmed ModRuntime.Log / Guard), so they are compiled
// into the tests directly. Both registries are process-global: every test uses unique tags and only asserts on its own
// counters.
using System;
using System.Collections.Generic;
using SyncRADation.Sync;
using Xunit;

namespace SyncRADation.Tests
{
    public class SessionResetTests
    {
        [Fact]
        public void Session_scope_clears_on_stop_and_wipe_not_on_scene()
        {
            int n = 0;
            SessionReset.Register("t.session", ResetScope.Session, () => n++);
            SessionReset.RunScene("S");
            Assert.Equal(0, n);
            SessionReset.RunAll(SessionReset.ReasonStop);
            SessionReset.RunAll(SessionReset.ReasonWipe);
            Assert.Equal(2, n);
        }

        [Fact]
        public void Connection_scope_skips_the_wipe_and_the_scene()
        {
            int n = 0;
            SessionReset.Register("t.conn", ResetScope.Connection, () => n++);
            SessionReset.RunAll(SessionReset.ReasonWipe);
            SessionReset.RunScene("S");
            Assert.Equal(0, n);
            SessionReset.RunAll(SessionReset.ReasonStop);
            Assert.Equal(1, n);
        }

        [Fact]
        public void Scene_scope_runs_only_on_scene_and_combines_with_other_scopes()
        {
            int scene = 0, both = 0;
            SessionReset.Register("t.scene", ResetScope.Scene, () => scene++);
            SessionReset.Register("t.scene+session", ResetScope.Scene | ResetScope.Session, () => both++);
            SessionReset.RunAll(SessionReset.ReasonStop);
            SessionReset.RunAll(SessionReset.ReasonWipe);
            Assert.Equal(0, scene);
            Assert.Equal(2, both);
            SessionReset.RunScene("S");
            Assert.Equal(1, scene);
            Assert.Equal(3, both);
        }

        [Fact]
        public void Scene_steps_get_the_scene_name_and_run_in_registration_order()
        {
            var order = new List<string>();
            SessionReset.Register("t.order.a", ResetScope.Scene, () => order.Add("a"));
            SessionReset.RegisterScene("t.order.b", s => order.Add("b:" + s));
            SessionReset.Register("t.order.c", ResetScope.Scene, () => order.Add("c"));
            SessionReset.RunScene("DET_Detention");
            int a = order.IndexOf("a"), b = order.IndexOf("b:DET_Detention"), c = order.IndexOf("c");
            Assert.True(a >= 0 && b > a && c > b, string.Join(",", order));
            // Scene-name steps never run on stop / wipe.
            order.Clear();
            SessionReset.RunAll(SessionReset.ReasonStop);
            Assert.DoesNotContain(order, s => s.StartsWith("b:", StringComparison.Ordinal));
        }

        [Fact]
        public void One_throwing_step_does_not_skip_the_others()
        {
            int before = Guard.Swallowed;
            int after = 0, sceneAfter = 0;
            SessionReset.Register("t.throws", ResetScope.Session | ResetScope.Scene, () => throw new InvalidOperationException("boom"));
            SessionReset.Register("t.after", ResetScope.Session, () => after++);
            SessionReset.Register("t.scene.after", ResetScope.Scene, () => sceneAfter++);
            SessionReset.RunAll(SessionReset.ReasonStop);
            SessionReset.RunScene("S");
            Assert.Equal(1, after);
            Assert.Equal(1, sceneAfter);
            Assert.True(Guard.Swallowed >= before + 2);
        }

        [Fact]
        public void Duplicate_tags_null_actions_and_no_scope_are_ignored()
        {
            int a = 0, b = 0;
            int count = SessionReset.Count;
            SessionReset.Register("t.dup", ResetScope.Session, () => a++);
            SessionReset.Register("t.dup", ResetScope.Session, () => b++);
            SessionReset.RegisterScene("t.dup", s => b++);
            SessionReset.Register("t.null", ResetScope.Session, null);
            SessionReset.RegisterScene("t.nullscene", null);
            SessionReset.Register("t.none", ResetScope.None, () => b++);
            SessionReset.Register("", ResetScope.Session, () => a++);
            Assert.Equal(count + 1, SessionReset.Count);
            SessionReset.RunAll(SessionReset.ReasonStop);
            Assert.Equal(1, a);
            Assert.Equal(0, b);
        }

        [Fact]
        public void Runs_are_not_reentrant()
        {
            int inner = 0, sceneInner = 0;
            SessionReset.Register("t.reenter", ResetScope.Session, () => { inner++; SessionReset.RunAll(SessionReset.ReasonStop); });
            SessionReset.Register("t.reenter.scene", ResetScope.Scene, () => { sceneInner++; SessionReset.RunScene("again"); });
            SessionReset.RunAll(SessionReset.ReasonStop);
            SessionReset.RunScene("S");
            Assert.Equal(1, inner);
            Assert.Equal(1, sceneInner);
        }

        [Fact]
        public void CountOf_counts_steps_per_scope()
        {
            int scene = SessionReset.CountOf(ResetScope.Scene);
            int conn = SessionReset.CountOf(ResetScope.Connection);
            SessionReset.Register("t.count", ResetScope.Scene | ResetScope.Connection, () => { });
            Assert.Equal(scene + 1, SessionReset.CountOf(ResetScope.Scene));
            Assert.Equal(conn + 1, SessionReset.CountOf(ResetScope.Connection));
        }
    }

    public class DumpFlushTests
    {
        [Fact]
        public void Hooks_run_in_order_isolated_and_deduped()
        {
            var order = new List<string>();
            int before = Guard.Swallowed;
            int count = DumpFlush.Count;
            DumpFlush.Register("t.flush.a", () => order.Add("a"));
            DumpFlush.Register("t.flush.throws", () => throw new InvalidOperationException("boom"));
            DumpFlush.Register("t.flush.b", () => order.Add("b"));
            DumpFlush.Register("t.flush.a", () => order.Add("dup"));
            DumpFlush.Register("t.flush.null", null);
            DumpFlush.Register("", () => order.Add("empty"));
            Assert.Equal(count + 3, DumpFlush.Count);
            DumpFlush.RunAll();
            int a = order.IndexOf("a"), b = order.IndexOf("b");
            Assert.True(a >= 0 && b > a, string.Join(",", order));
            Assert.DoesNotContain("dup", order);
            Assert.DoesNotContain("empty", order);
            Assert.True(Guard.Swallowed > before);
        }

        [Fact]
        public void RunAll_is_not_reentrant()
        {
            int n = 0;
            DumpFlush.Register("t.flush.reenter", () => { n++; DumpFlush.RunAll(); });
            DumpFlush.RunAll();
            Assert.Equal(1, n);
        }
    }
}
