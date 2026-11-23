using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// Whose a missed slot is (pure; no hardware): the display's only where host-clock facts show the present was handed over
/// at least the measured deadline before its refresh; the slots given up to catch up carry the late show's attribution,
/// and that ends when a present is on time again; a rate change counts on its own and forgets the old output's evidence.
/// </summary>
public class MissAccountingTests
{
    private const long Ms = Nanoseconds.PerMillisecond;
    private const long Us = Nanoseconds.PerMicrosecond;

    private static MissAccounting WithHistory(long now, params long[] onTimeLeads)
    {
        var a = new MissAccounting();
        for (var i = 0; i < onTimeLeads.Length; i++)
        {
            a.RecordOnTime(now - ((onTimeLeads.Length - i) * 4 * Ms), onTimeLeads[i]);
        }

        return a;
    }

    [Fact]
    public void Without_evidence_of_what_makes_a_refresh_every_late_show_is_the_pipelines()
    {
        var a = new MissAccounting();
        Assert.Equal(long.MaxValue, a.RequiredLeadNs(10 * Nanoseconds.PerSecond));
        Assert.Equal(MissKind.LatePresent, a.OnLateShow(10 * Nanoseconds.PerSecond, 50 * Ms, 1));
    }

    /// <summary>
    /// Presents handed over 6.8 to 8.2 ms before their refresh have been making it: one handed over 8.0 ms before and shown
    /// late is the display's; one handed over 6.85 ms before (no earlier than the earliest that made it, plus the margin)
    /// is the pipeline's.
    /// </summary>
    [Fact]
    public void A_late_show_is_the_displays_only_if_handed_over_earlier_than_presents_that_made_their_refresh()
    {
        var now = 100 * Nanoseconds.PerSecond;
        var a = WithHistory(now, 8_200 * Us, 6_800 * Us, 8_000 * Us, 7_900 * Us);
        Assert.Equal((6_800 * Us) + MissAccounting.DefaultMarginNs, a.RequiredLeadNs(now));
        Assert.Equal(MissKind.LatePresent, a.OnLateShow(now, 6_850 * Us, 1));

        var b = WithHistory(now, 8_200 * Us, 6_800 * Us, 8_000 * Us, 7_900 * Us);
        Assert.Equal(MissKind.DisplayLate, b.OnLateShow(now, 8_000 * Us, 1));
    }

    /// <summary>
    /// Conservative: a late show raises the deadline to its own lead, so the next present late at the same lead (a latch
    /// that stays early while the pipeline does not adapt) is the pipeline's, not the display's again.
    /// </summary>
    [Fact]
    public void After_a_late_show_the_next_one_at_the_same_lead_is_the_pipelines()
    {
        var now = 100 * Nanoseconds.PerSecond;
        var a = WithHistory(now, 7_000 * Us, 8_000 * Us);
        Assert.Equal(MissKind.DisplayLate, a.OnLateShow(now, 7_600 * Us, 1));
        a.OnOnTime();
        Assert.Equal(MissKind.LatePresent, a.OnLateShow(now + (4 * Ms), 7_600 * Us, 1));
        Assert.Equal(MissKind.DisplayLate, a.OnLateShow(now + (8 * Ms), 8_000 * Us, 1));
    }

    /// <summary>
    /// A compositor's missed refresh (a present handed over in time shown late) followed by presents handed over no earlier
    /// that make their refresh: the late show is contradicted, and the next compositor miss at that lead is the display's again.
    /// Before, the first one stayed evidence for ten seconds and made the next one the pipeline's (the simulated display's
    /// missed refreshes a second apart).
    /// </summary>
    [Fact]
    public void A_late_show_that_later_presents_at_the_same_lead_contradict_no_longer_raises_the_deadline()
    {
        var now = 100 * Nanoseconds.PerSecond;
        var a = WithHistory(now, 7_000 * Us, 8_000 * Us);
        Assert.Equal(MissKind.DisplayLate, a.OnLateShow(now, 7_600 * Us, 1));
        a.OnOnTime();
        for (var i = 1; i <= 20; i++)
        {
            // Handed over 7.3 ms before (7.55 at the most, with the default uncertainty): no later than the late one.
            a.RecordOnTime(now + (i * 8 * Ms), 7_300 * Us);
        }

        Assert.Equal(7_250 * Us, a.RequiredLeadNs(now + (200 * Ms)));
        Assert.Equal(MissKind.DisplayLate, a.OnLateShow(now + (200 * Ms), 7_600 * Us, 1));

        // Presents that made it at leads possibly longer than the late one's (7.5 ms, up to 7.75) do not contradict it.
        var b = WithHistory(now, 7_000 * Us, 8_000 * Us);
        Assert.Equal(MissKind.DisplayLate, b.OnLateShow(now, 7_600 * Us, 1));
        b.OnOnTime();
        for (var i = 1; i <= 20; i++)
        {
            b.RecordOnTime(now + (i * 8 * Ms), 7_500 * Us);
        }

        Assert.Equal(MissKind.LatePresent, b.OnLateShow(now + (200 * Ms), 7_600 * Us, 1));
    }

    [Fact]
    public void Evidence_older_than_the_window_does_not_count()
    {
        var a = new MissAccounting();
        a.RecordOnTime(0, 5 * Ms);
        Assert.Equal((5 * Ms) + MissAccounting.DefaultMarginNs, a.RequiredLeadNs(MissAccounting.DefaultWindowNs - 1));
        Assert.Equal(long.MaxValue, a.RequiredLeadNs(MissAccounting.DefaultWindowNs + Ms));
    }

    /// <summary>
    /// The slots given up after a late show are its consequence (the display's catch-up, or the pipeline's drops) for as
    /// many refreshes as it put the FIFO behind; a present submitted on time clears what is owed, so a later late drop is
    /// judged on its own (the review's "catch-up credit never cleared").
    /// </summary>
    [Fact]
    public void Catch_up_carries_the_late_shows_attribution_only_for_what_it_owes_and_is_cleared_by_an_on_time_present()
    {
        var now = 100 * Nanoseconds.PerSecond;
        var a = WithHistory(now, 7_000 * Us, 8_000 * Us);
        Assert.Equal(MissKind.DisplayLate, a.OnLateShow(now, 8_000 * Us, 2));
        Assert.Equal(2, a.Debt);
        Assert.Equal(MissKind.CatchUp, a.OnCatchUp(now, givesBackARefresh: true, shownLate: false));
        Assert.Equal(MissKind.CatchUp, a.OnCatchUp(now, givesBackARefresh: true, shownLate: false));
        Assert.Equal(0, a.Debt);
        Assert.Equal(MissKind.Dropped, a.OnCatchUp(now, givesBackARefresh: true, shownLate: false));

        Assert.Equal(MissKind.DisplayLate, a.OnLateShow(now + Ms, 9_000 * Us, 1));
        a.OnOnTime();
        Assert.Equal(MissKind.Dropped, a.OnCatchUp(now + Ms, givesBackARefresh: true, shownLate: false));
        Assert.Equal(MissKind.LatePresent, a.OnCatchUp(now + Ms, givesBackARefresh: false, shownLate: true));
    }

    [Fact]
    public void What_is_owed_after_a_pipeline_late_show_stays_the_pipelines()
    {
        var now = 100 * Nanoseconds.PerSecond;
        var a = WithHistory(now, 7_000 * Us, 8_000 * Us);
        Assert.Equal(MissKind.LatePresent, a.OnLateShow(now, 3 * Ms, 1));
        Assert.Equal(MissKind.DisplayLate, a.OnLateShow(now, 9 * Ms, 1));
        Assert.Equal(MissKind.Dropped, a.OnCatchUp(now, givesBackARefresh: true, shownLate: false));
        Assert.Equal(MissKind.Dropped, a.OnCatchUp(now, givesBackARefresh: true, shownLate: false));
    }

    /// <summary>
    /// The window moved to another output (the rate changed): for a while every miss is the change's, and what was learnt
    /// about the old output's deadline is forgotten (afterwards a late show without new evidence is the pipeline's).
    /// </summary>
    [Fact]
    public void Misses_during_a_rate_change_are_its_own_and_the_old_outputs_evidence_is_forgotten()
    {
        var now = 100 * Nanoseconds.PerSecond;
        var a = WithHistory(now, 7_000 * Us, 8_000 * Us);
        a.BeginRateChange(now, 2 * Nanoseconds.PerSecond);
        Assert.True(a.InRateChange(now + Nanoseconds.PerSecond));
        Assert.Equal(MissKind.RateChange, a.OnLateShow(now + Ms, 20 * Ms, 1));
        Assert.Equal(MissKind.RateChange, a.OnCatchUp(now + Ms, givesBackARefresh: true, shownLate: false));
        a.RecordOnTime(now + Ms, 12 * Ms); // evidence during the change does not count either
        Assert.False(a.InRateChange(now + (2 * Nanoseconds.PerSecond)));
        Assert.Equal(long.MaxValue, a.RequiredLeadNs(now + (2 * Nanoseconds.PerSecond)));
        Assert.Equal(MissKind.LatePresent, a.OnLateShow(now + (2 * Nanoseconds.PerSecond), 20 * Ms, 1));
    }
}

/// <summary>The adaptive present lead (pure).</summary>
public class LeadControllerTests
{
    private const long Ms = Nanoseconds.PerMillisecond;
    private const long S = Nanoseconds.PerSecond;

    /// <summary>
    /// A frame whose content missed the latch raises the lead by one step at once (one step: the next frames' content is
    /// ready a millisecond earlier), and the new level is held for ten minutes without coming back down into the same miss
    /// (no oscillation).
    /// </summary>
    [Fact]
    public void A_late_frame_raises_the_lead_once_and_the_new_level_holds()
    {
        var c = new LeadController(6_250_000, 8_000_000, adaptive: true);
        Assert.True(c.OnLateContent(10 * S));
        Assert.Equal(7_250_000, c.LeadNs);
        Assert.Equal(1, c.Raises);
        for (var t = 11 * S; t < (10 * S) + LeadController.FloorHoldNs; t += S)
        {
            Assert.False(c.Tick(t));
        }

        Assert.Equal(7_250_000, c.LeadNs);
    }

    [Fact]
    public void The_lead_never_exceeds_what_the_in_flight_cap_allows()
    {
        var c = new LeadController(6_250_000, 8_000_000, adaptive: true);
        Assert.True(c.OnLateContent(1 * S));
        Assert.True(c.OnLateContent(2 * S));
        Assert.Equal(8_000_000, c.LeadNs);
        Assert.False(c.OnLateContent(3 * S));
        Assert.Equal(8_000_000, c.PeakLeadNs);
    }

    [Fact]
    public void After_the_hold_it_comes_back_down_slowly_to_the_configured_lead_and_no_further()
    {
        var c = new LeadController(6_250_000, 8_000_000, adaptive: true);
        _ = c.OnLateContent(0);
        var t = LeadController.FloorHoldNs + S;
        var steps = 0;
        for (; t < LeadController.FloorHoldNs + (600 * S); t += S)
        {
            steps += c.Tick(t) ? 1 : 0;
        }

        Assert.Equal(6_250_000, c.LeadNs);
        Assert.Equal(4, steps); // 1 ms in 0.25 ms steps, ten seconds apart
    }

    [Fact]
    public void A_fixed_lead_never_moves_and_a_new_output_starts_from_its_own_lead()
    {
        var fixedLead = new LeadController(3 * Ms, 8 * Ms, adaptive: false);
        Assert.False(fixedLead.OnLateContent(S));
        Assert.Equal(3 * Ms, fixedLead.LeadNs);

        var c = new LeadController(6_250_000, 8_000_000, adaptive: true);
        _ = c.OnLateContent(S);
        c.Reset(12_500_000, 16_000_000, 2 * S);
        Assert.Equal(12_500_000, c.LeadNs);
    }

    /// <summary>
    /// --lead-max-ms: at 119.88 Hz the in-flight limit is about 16.2 ms (the 5.5 → 16.2 ms climb of the GNOME baseline of
    /// 23/11/2026); a cap of 8 ms stops the climb there, a cap under the latch floor stays at the floor, and no cap (-1)
    /// or one above the limit leaves the limit.
    /// </summary>
    [Fact]
    public void A_lead_cap_stops_the_climb_but_never_goes_under_the_latch_floor()
    {
        const long limit = 16_183_000;
        Assert.Equal(8 * Ms, PresentLoop.CappedMaxLead(limit, 8 * Ms, 1 * Ms));
        Assert.Equal(1 * Ms, PresentLoop.CappedMaxLead(limit, 0, 1 * Ms));
        Assert.Equal(limit, PresentLoop.CappedMaxLead(limit, -1, 1 * Ms));
        Assert.Equal(limit, PresentLoop.CappedMaxLead(limit, 40 * Ms, 1 * Ms));

        var c = new LeadController(5_500_000, PresentLoop.CappedMaxLead(limit, 8 * Ms, 1 * Ms), adaptive: true);
        for (var t = 1L; t <= 6; t++)
        {
            _ = c.OnLateContent(t * S);
        }

        Assert.Equal(8 * Ms, c.LeadNs);
        Assert.Equal(8 * Ms, c.PeakLeadNs);
    }
}

/// <summary>Catching up after a late show without losing a new real frame where something else can be given up (pure).</summary>
/// <summary>The lead's raise from a late present's hand-over (pure).</summary>
public class LeadRaiseTests
{
    private const long S = 1_000_000_000;

    /// <summary>
    /// A present handed over earlier than the lead asked that still missed takes the lead to its hand-over lead plus two
    /// steps: from 1.5 ms, one handed over 2.7 ms ahead puts it at 4.7 ms, not 2.5 (one step at a time it settled 0.06 ms
    /// past a 4.44 ms latch and kept missing); one handed over at the lead, or without a hand-over, one step as before.
    /// </summary>
    [Fact]
    public void A_raise_clears_the_missed_hand_over_by_two_steps()
    {
        var c = new LeadController(1_500_000, 8_000_000, adaptive: true);
        Assert.True(c.OnLateContent(1 * S, 2_700_000));
        Assert.Equal(4_700_000, c.LeadNs);

        var b = new LeadController(3_500_000, 8_000_000, adaptive: true);
        Assert.True(b.OnLateContent(1 * S, 3_400_000));
        Assert.Equal(4_500_000, b.LeadNs);

        var d = new LeadController(3_500_000, 8_000_000, adaptive: true);
        Assert.True(d.OnLateContent(1 * S));
        Assert.Equal(4_500_000, d.LeadNs);

        var e = new LeadController(3_500_000, 8_000_000, adaptive: true);
        Assert.True(e.OnLateContent(1 * S, 7_000_000));
        Assert.Equal(8_000_000, e.LeadNs);
    }
}

public class CatchUpPolicyTests
{
    [Fact]
    public void On_time_everything_is_submitted()
    {
        Assert.Equal(CatchUpAction.Submit, CatchUpPolicy.Decide(PresentKind.Write, SlotKind.Real, 0, false, false));
        Assert.Equal(CatchUpAction.Submit, CatchUpPolicy.Decide(PresentKind.Repeat, SlotKind.Repeat, 0, false, false));
    }

    /// <summary>
    /// Stride 2 (each image twice): after a late show the next new real frame is shown a refresh late, and the repeat after
    /// it is the one given up: its refresh shows that real frame anyway, so catching up costs no image. (Before, the real
    /// frame was dropped, the generated frame before it shown twice: two misses per source frame for as long as the
    /// compositor's latch stayed early.)
    /// </summary>
    [Fact]
    public void A_real_frame_a_refresh_late_is_shown_and_the_repeat_after_it_gives_back_the_refresh()
    {
        Assert.Equal(CatchUpAction.SubmitLate, CatchUpPolicy.Decide(PresentKind.Write, SlotKind.Real, 1, afterLateReal: false, lateRealOutstanding: false));
        Assert.Equal(CatchUpAction.DropAbsorbed, CatchUpPolicy.Decide(PresentKind.Repeat, SlotKind.Repeat, 1, afterLateReal: true, lateRealOutstanding: true));
    }

    [Fact]
    public void Without_a_repeat_a_generated_frame_is_given_up_rather_than_a_real_one()
    {
        Assert.Equal(CatchUpAction.Drop, CatchUpPolicy.Decide(PresentKind.Write, SlotKind.Generated, 1, afterLateReal: false, lateRealOutstanding: false));
        Assert.Equal(CatchUpAction.Drop, CatchUpPolicy.Decide(PresentKind.Write, SlotKind.Generated, 1, afterLateReal: true, lateRealOutstanding: true));
    }

    [Fact]
    public void Real_frames_go_only_when_nothing_else_can_two_refreshes_late_or_again_before_the_fifo_caught_up()
    {
        Assert.Equal(CatchUpAction.Drop, CatchUpPolicy.Decide(PresentKind.Write, SlotKind.Real, 2, false, false));
        Assert.Equal(CatchUpAction.Drop, CatchUpPolicy.Decide(PresentKind.Write, SlotKind.Real, 1, afterLateReal: false, lateRealOutstanding: true));
        Assert.Equal(CatchUpAction.Drop, CatchUpPolicy.Decide(PresentKind.Repeat, SlotKind.Repeat, 1, afterLateReal: false, lateRealOutstanding: false));
    }
}

/// <summary>The start offset (pure): files are shifted so their first frame is shown first; live sources never are.</summary>
public class StartOffsetTests
{
    [Fact]
    public void A_file_is_shifted_so_its_first_frame_is_due_a_start_lead_from_now_and_a_live_source_is_not()
    {
        Assert.Equal(1_000 + 16 - 400, PacingLoop.StartOffset(live: false, nowNs: 1_000, startLeadNs: 16, firstContentNs: 400));
        Assert.Equal(0, PacingLoop.StartOffset(live: true, nowNs: 1_000, startLeadNs: 16, firstContentNs: 400));
    }
}

/// <summary>The compositor's latch bounded from timed shows (pure).</summary>
public class LatchEstimatorTests
{
    private const long P = 8_341_675;
    private const long FirstPixel = 160_000;

    /// <summary>
    /// Presents handed over 12.5 ms before their refresh make it; then one at the same lead (a little less: hand-overs
    /// scatter) is shown a refresh late because the compositor skipped that refresh. That is no evidence of the latch:
    /// taken as one, it put the latch's lower bound at 12.4 ms, the next presents at that lead looked unreachable and one
    /// was dropped while the compositor gave the refresh back itself. A present handed over clearly later than any that
    /// made it, and missed, still is evidence.
    /// </summary>
    [Fact]
    public void A_miss_at_a_lead_that_made_its_refresh_is_no_evidence_of_the_latch()
    {
        var latch = new LatchEstimator();
        long now = 1_000_000_000;
        for (var i = 0; i < 100; i++, now += P)
        {
            latch.Add(now, now + FirstPixel, now - 12_500_000 + ((i % 5) * 50_000), P, couldHaveMadeEarlier: false);
        }

        Assert.Equal(12_460_000, latch.HighNs);
        latch.AddMissed(now, now, now - 12_400_000);
        Assert.Equal(0, latch.LowNs);

        latch.AddMissed(now + P, now + P, now + P - 2_000_000);
        Assert.Equal(2_000_000, latch.LowNs);
    }
}
