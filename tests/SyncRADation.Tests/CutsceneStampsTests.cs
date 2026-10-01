using SyncRADation.Networking;
using Xunit;

namespace SyncRADation.Tests
{
    /// <summary>Cutscene dedupe model: one start stamp + one skip stamp per cutscene (CutsceneStamps.cs).</summary>
    public class CutsceneStampsTests
    {
        const ulong Id = 0xABCDEF;
        const float Start = CutsceneStamps.StartWindow;
        const float Skip = CutsceneStamps.SkipWindow;

        [Fact]
        public void Duplicate_starts_inside_the_window_drop_and_a_repeatable_cutscene_starts_again_after_it()
        {
            var s = new CutsceneStamps();
            Assert.True(s.TryStart(Id, 10f));
            Assert.False(s.TryStart(Id, 10f + Start - 0.01f));
            Assert.True(s.TryStart(Id, 10f + Start));
        }

        [Fact]
        public void A_dropped_duplicate_does_not_extend_the_window()
        {
            var s = new CutsceneStamps();
            Assert.True(s.TryStart(Id, 0f));
            Assert.False(s.TryStart(Id, Start - 1f));
            Assert.True(s.TryStart(Id, Start + 0.01f));
        }

        [Fact]
        public void Requester_asks_once_and_still_plays_the_hosts_answer()
        {
            var s = new CutsceneStamps();
            Assert.True(s.TryRequest(Id, 0f));
            Assert.False(s.TryRequest(Id, 1f));          // repeat trigger: no second request
            Assert.True(s.TryStart(Id, 1.5f));           // host's CutsceneStart completes the request
            Assert.False(s.TryStart(Id, 2f));            // relay echo of the same start
            Assert.False(s.TryRequest(Id, 2f));          // running / just started: nothing to ask
        }

        [Fact]
        public void An_unanswered_request_can_be_repeated_after_the_window()
        {
            var s = new CutsceneStamps();
            Assert.True(s.TryRequest(Id, 0f));
            Assert.True(s.TryRequest(Id, Start));
        }

        [Fact]
        public void A_pending_request_is_not_an_accepted_start()
        {
            var s = new CutsceneStamps();
            s.TryRequest(Id, 0f);
            Assert.False(s.StartedWithin(Id, 1f, Skip));
            s.TryStart(Id, 1f);
            Assert.True(s.StartedWithin(Id, 1f + Skip - 0.01f, Skip));
            Assert.False(s.StartedWithin(Id, 1f + Skip, Skip));
        }

        [Fact]
        public void Skip_dedupes_inside_its_window_and_blocks_every_start_path()
        {
            var s = new CutsceneStamps();
            Assert.True(s.TryStart(Id, 0f));
            Assert.True(s.TrySkip(Id, 2f));
            Assert.False(s.TrySkip(Id, 2.5f));           // N players / host echo
            Assert.True(s.Skipped(Id, 2f + Skip - 0.01f));
            Assert.False(s.TryStart(Id, 10f));
            Assert.False(s.TryRequest(Id, 10f));
            Assert.False(s.Skipped(Id, 2f + Skip));
            Assert.True(s.TryStart(Id, 2f + Skip));
            Assert.True(s.TrySkip(Id, 2f + Skip));
        }

        [Fact]
        public void Skip_does_not_need_a_start_and_id_zero_is_never_stamped()
        {
            var s = new CutsceneStamps();
            Assert.True(s.TrySkip(Id, 0f));
            Assert.True(s.TryStart(0, 0f));
            Assert.True(s.TryStart(0, 0f));
            Assert.True(s.TryRequest(0, 0f));
            Assert.True(s.TrySkip(0, 0f));
            Assert.True(s.TrySkip(0, 0f));
            Assert.False(s.Skipped(0, 0f));
            Assert.False(s.StartedWithin(0, 0f, Skip));
        }

        [Fact]
        public void Clear_forgets_every_stamp()
        {
            var s = new CutsceneStamps();
            s.TryStart(Id, 0f);
            s.TrySkip(Id + 1, 0f);
            s.Clear();
            Assert.True(s.TryStart(Id, 0f));
            Assert.True(s.TryStart(Id + 1, 0f));
        }

        [Fact]
        public void Stamps_are_per_cutscene()
        {
            var s = new CutsceneStamps();
            Assert.True(s.TryStart(1, 0f));
            Assert.True(s.TryStart(2, 0f));
            Assert.True(s.TrySkip(1, 0f));
            Assert.False(s.Skipped(2, 0f));
        }
    }
}
