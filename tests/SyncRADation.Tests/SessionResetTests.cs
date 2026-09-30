// Sync/SessionReset.cs is pure (BCL + the shimmed ModRuntime.Log / Guard), so it is compiled into the tests directly.
// The registry is process-global: every test uses unique tags and only asserts on its own counters.
using System;
using SyncRADation.Sync;
using Xunit;

namespace SyncRADation.Tests
{
    public class SessionResetTests
    {
        [Fact]
        public void Session_scope_clears_on_start_stop_and_wipe()
        {
            int n = 0;
            SessionReset.Register("t.session", () => n++);
            SessionReset.RunAll(SessionReset.ReasonStart);
            SessionReset.RunAll(SessionReset.ReasonStop);
            SessionReset.RunAll(SessionReset.ReasonWipe);
            Assert.Equal(3, n);
        }

        [Fact]
        public void Connection_scope_skips_the_wipe()
        {
            int n = 0;
            SessionReset.RegisterConnection("t.conn", () => n++);
            SessionReset.RunAll(SessionReset.ReasonWipe);
            Assert.Equal(0, n);
            SessionReset.RunAll(SessionReset.ReasonStart);
            SessionReset.RunAll(SessionReset.ReasonStop);
            Assert.Equal(2, n);
        }

        [Fact]
        public void One_throwing_clear_does_not_skip_the_others()
        {
            int before = Guard.Swallowed;
            int after = 0;
            SessionReset.Register("t.throws", () => throw new InvalidOperationException("boom"));
            SessionReset.Register("t.after", () => after++);
            SessionReset.RunAll(SessionReset.ReasonStop);
            Assert.Equal(1, after);
            Assert.True(Guard.Swallowed > before);
        }

        [Fact]
        public void Duplicate_tags_and_null_actions_are_ignored()
        {
            int a = 0, b = 0;
            int count = SessionReset.Count;
            SessionReset.Register("t.dup", () => a++);
            SessionReset.Register("t.dup", () => b++);
            SessionReset.Register("t.null", null);
            SessionReset.Register("", () => a++);
            Assert.Equal(count + 1, SessionReset.Count);
            SessionReset.RunAll(SessionReset.ReasonStop);
            Assert.Equal(1, a);
            Assert.Equal(0, b);
        }

        [Fact]
        public void RunAll_is_not_reentrant()
        {
            int inner = 0;
            SessionReset.Register("t.reenter", () => { inner++; SessionReset.RunAll(SessionReset.ReasonStop); });
            SessionReset.RunAll(SessionReset.ReasonStop);
            Assert.Equal(1, inner);
        }
    }
}
