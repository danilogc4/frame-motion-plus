using FrameMotionPlus.App.Play;
using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// The slots at the output's ceiling under variable refresh (<see cref="CeilingPacingState"/>): a display that has fallen
/// a whole refresh behind there cannot catch up by itself, the recovery gives it one free refresh by leaving one generated
/// slot out, and running behind is reported. Deterministic: a model of the display at its ceiling (a frame is shown at its
/// time or a shortest refresh period after the previous flip, whichever is later; or, for a loss of variable refresh, on
/// the next point of a fixed grid), feedback delivered once a show has happened, and the present thread's rule of asking
/// only at a generated slot's hand-over.
/// </summary>
public class CeilingPacingTests
{
    private static readonly long P120 = (long)Math.Round(1e9 / 119.88);
    private const long T0 = 10_000_000_000;
    private const long Delay = 900_000; // hand-over to first pixel when on time (the lead)

    private sealed record Outcome(List<(long Slot, long LateNs)> Shows, List<long> Skipped);

    /// <summary>
    /// 4x a 29.97 fps source on a 119.88 Hz output: slot k due at T0 + k·<paramref name="slotNs"/>, every fourth real. The
    /// display shows a present at max(its time plus <paramref name="extraLagAt"/>'s delay for that slot, the previous flip
    /// plus the period): at the ceiling a one-off delay is carried by every show after it; <paramref name="grid"/>: on the
    /// next point of a fixed grid instead.
    /// </summary>
    private static Outcome Run(CeilingPacingState state, int slots, long slotNs, long initialLagNs, Func<long, long>? extraLagAt = null, bool grid = false, long? periodNs = null)
    {
        var period = periodNs ?? P120;
        var shows = new List<(long Slot, long LateNs)>();
        var skipped = new List<long>();
        var pending = new Queue<(long Slot, long LateNs, long ShownNs)>();
        var lastFlip = T0 - period + initialLagNs;
        for (long k = 0; k < slots; k++)
        {
            var target = T0 + (k * slotNs);
            var handover = target - Delay;
            state.SetCondition(true, slotNs, period);
            while (pending.Count > 0 && pending.Peek().ShownNs <= handover)
            {
                var f = pending.Dequeue();
                state.OnShown(f.Slot, f.LateNs, period, f.ShownNs);
            }

            var generated = k % 4 != 0;
            if (generated && state.TakeRetime(k, handover))
            {
                skipped.Add(k);
                continue;
            }

            var earliest = target + (extraLagAt?.Invoke(k) ?? 0);
            var shown = grid
                ? T0 + initialLagNs + (long)Math.Ceiling((earliest - T0 - initialLagNs) / (double)period) * period
                : Math.Max(earliest, lastFlip + period);
            lastFlip = shown;
            shows.Add((k, shown - target));
            pending.Enqueue((k, shown - target, shown));
        }

        return new Outcome(shows, skipped);
    }

    [Fact]
    public void A_display_a_refresh_behind_at_its_ceiling_catches_up_with_exactly_one_left_out_generated_frame()
    {
        var state = new CeilingPacingState(recover: true);
        var r = Run(state, 1200, P120, initialLagNs: P120 + 1_000_000);
        Log.WriteLine($"skipped {string.Join(",", r.Skipped)}; retimes {state.Retimes}, regained {state.Regained}; behind {state.BehindShowCount} in {state.BehindEpisodes}");

        var skip = Assert.Single(r.Skipped);
        Assert.NotEqual(0, skip % 4); // a generated slot, never a real frame
        Assert.Equal(1, state.Retimes);
        Assert.Equal(1, state.Regained);
        Assert.Equal(0, state.NotRegained);

        // Behind (a refresh and 1 ms late) until the left-out slot, which came after 30 behind shows had been reported back.
        Assert.All(r.Shows.Where(s => s.Slot < skip), s => Assert.InRange(s.LateNs, P120, P120 + 1_000_000));
        Assert.InRange(skip, CeilingPacingState.BehindShows, CeilingPacingState.BehindShows + 6);

        // From the next show on, at its time plus the millisecond the display carried (under half a refresh: on time).
        Assert.All(r.Shows.Where(s => s.Slot > skip), s => Assert.Equal(1_000_000, s.LateNs));
        Assert.Equal(1, state.BehindEpisodes);
        Assert.Equal(1, state.BehindMaxRefreshes);
        Assert.Equal(0, state.BehindNow);
    }

    [Fact]
    public void Without_the_recovery_it_stays_a_refresh_behind_and_is_reported_as_running_behind()
    {
        var state = new CeilingPacingState(recover: false);
        var r = Run(state, 1200, P120, initialLagNs: P120 + 1_000_000);
        Assert.Empty(r.Skipped);
        Assert.All(r.Shows, s => Assert.Equal(P120 + 1_000_000, s.LateNs));
        Assert.Equal(0, state.Retimes);
        Assert.False(state.Active);
        Assert.Equal(1, state.BehindEpisodes);
        Assert.Equal(1, state.BehindMaxRefreshes);
        Assert.Equal(1, state.BehindNow);

        // Every show whose feedback came is counted (the last ones were still in flight at the end).
        Assert.InRange(state.BehindShowCount, r.Shows.Count - 2, r.Shows.Count);
        Assert.InRange(state.BehindLongestNs, (r.Shows.Count - 3) * P120, r.Shows.Count * P120);
    }

    [Fact]
    public void Two_refreshes_behind_takes_two_left_out_frames_at_least_five_seconds_apart()
    {
        var state = new CeilingPacingState(recover: true);
        var r = Run(state, 2400, P120, initialLagNs: (2 * P120) + 500_000); // 20 s
        Assert.Equal(2, r.Skipped.Count);
        Assert.True((r.Skipped[1] - r.Skipped[0]) * P120 >= CeilingPacingState.RetimeIntervalNs);
        Assert.Equal(2, state.Regained);
        Assert.All(r.Shows.Where(s => s.Slot < r.Skipped[0]), s => Assert.True(s.LateNs > 2 * P120));
        Assert.All(r.Shows.Where(s => s.Slot > r.Skipped[0] && s.Slot < r.Skipped[1]), s => Assert.InRange(s.LateNs, P120, P120 + 500_000));
        Assert.All(r.Shows.Where(s => s.Slot > r.Skipped[1]), s => Assert.Equal(500_000, s.LateNs));
        Assert.Equal(2, state.BehindMaxRefreshes);
    }

    /// <summary>
    /// As on the TV at 4x (there about 15 ppm, 1 ms a minute; 40 ppm here to fit 10 minutes): the source faster than
    /// the display's highest rate, so each show is a little later than the one before, with the GPU's 2.5 ms from the start. Each time the
    /// shows have been late (over half a refresh) for 30 in a row, one generated frame is left out and the next show is on
    /// time; no more than those 30 are late each time, and nothing is left out before the first is late (10 minutes).
    /// </summary>
    [Fact]
    public void A_source_a_little_faster_than_the_ceiling_loses_one_generated_frame_each_time_it_falls_half_a_refresh_behind()
    {
        var state = new CeilingPacingState(recover: true);
        var slot = (long)Math.Round(P120 * (1 - 40e-6));
        var r = Run(state, 72_000, slot, initialLagNs: 2_500_000);
        var late = r.Shows.Count(s => s.LateNs * 2 > P120);
        Log.WriteLine($"left out {r.Skipped.Count} at {string.Join(", ", r.Skipped.Select(k => $"{k * slot / 1e9:0} s"))}; late shows {late}; regained {state.Regained}");
        Assert.InRange(r.Skipped.Count, 5, 8); // 0.33 µs a slot: the first after about 42 s, then every 104 s
        Assert.Equal(r.Skipped.Count, state.Regained);
        Assert.Equal(0, state.NotRegained);
        Assert.InRange(late, r.Skipped.Count * CeilingPacingState.BehindShows, r.Skipped.Count * (CeilingPacingState.BehindShows + 3));
        Assert.All(r.Skipped, k => Assert.NotEqual(0, k % 4));
        Assert.Equal(0, state.BehindShowCount); // never three quarters of a refresh late
    }

    [Theory]
    [InlineData(0L)]         // on time
    [InlineData(2_500_000L)] // the display 2.5 ms slow throughout (late shows, but well under three quarters of a refresh)
    public void Normal_play_never_leaves_a_frame_out(long lagNs)
    {
        var state = new CeilingPacingState(recover: true);
        var r = Run(state, 3600, P120, initialLagNs: 0, extraLagAt: k => lagNs + (k % 7 == 0 ? 1_500_000 : 0)); // 30 s, with a wobble
        Assert.Empty(r.Skipped);
        Assert.Equal(0, state.Retimes);
        Assert.Equal(0, state.BehindShowCount);
        Assert.Equal(0, state.BehindEpisodes);
    }

    [Fact]
    public void A_loss_of_variable_refresh_at_the_ceiling_costs_at_most_one_left_out_frame()
    {
        // The display on a fixed 119.88 Hz grid whose points fall 0.8 of a refresh after the slots' times: leaving a slot out
        // changes nothing there, so after one that did not help no more are left out (30 s).
        var state = new CeilingPacingState(recover: true);
        var r = Run(state, 3600, P120, initialLagNs: (long)(0.8 * P120), grid: true);
        Assert.Single(r.Skipped);
        Assert.Equal(1, state.Retimes);
        Assert.Equal(1, state.NotRegained);
        Assert.Equal(0, state.Regained);
        Assert.All(r.Shows, s => Assert.InRange(s.LateNs, (long)(0.8 * P120) - 1, (long)(0.8 * P120) + 1));
    }

    [Fact]
    public void A_display_that_keeps_falling_behind_is_given_a_frame_at_most_every_five_seconds()
    {
        // A whole refresh of extra lag every 2 s (a slow compositor pass recurring): never more than one left out per 5 s.
        var state = new CeilingPacingState(recover: true);
        var everyTwoSeconds = (long)(2e9 / P120);
        var r = Run(state, 3600, P120, initialLagNs: 0, extraLagAt: k => k > 0 && k % everyTwoSeconds == 0 ? P120 + 500_000 : 0);
        Log.WriteLine($"skipped {string.Join(",", r.Skipped)}");
        Assert.NotEmpty(r.Skipped);
        for (var i = 1; i < r.Skipped.Count; i++)
        {
            Assert.True((r.Skipped[i] - r.Skipped[i - 1]) * P120 >= CeilingPacingState.RetimeIntervalNs, $"left out {r.Skipped[i - 1]} and {r.Skipped[i]}");
        }

        Assert.InRange(r.Skipped.Count, 1, 7); // 30 s: at most one every 5 s (the first after the first fall at 2 s)
        Assert.Equal(r.Skipped.Count, state.Regained);
    }

    /// <summary>
    /// The condition from the numbers of each case (a 29.97 fps source): the slots against the output's shortest refresh
    /// period, whatever the generator. At the ceiling (4x on the TV, 8x on DP-3, under variable refresh) the recovery acts
    /// when it is on; below the ceiling and at fixed refresh it never does, on or off, and every present is handed over as
    /// before.
    /// </summary>
    [Theory]
    [InlineData("TV 2x", 2, 119.88, true, false)]
    [InlineData("TV 3x", 3, 119.88, true, false)]
    [InlineData("TV 4x", 4, 119.88, true, true)]
    [InlineData("DP-3 4x", 4, 240.016, true, false)]
    [InlineData("DP-3 6x", 6, 240.016, true, false)]
    [InlineData("DP-3 8x", 8, 240.016, true, true)]
    [InlineData("TV 4x, fixed refresh", 4, 119.88, false, false)]
    [InlineData("DP-3 8x, fixed refresh", 8, 240.016, false, false)]
    public void Only_slots_at_the_ceiling_under_variable_refresh_let_the_recovery_act(string name, int multiplier, double outputHz, bool vrr, bool atCeiling)
    {
        var slotNs = (long)Math.Round(1001e9 / 30000 / multiplier);
        foreach (var recover in new[] { true, false })
        {
            var state = new CeilingPacingState(recover);
            state.SetCondition(vrr, slotNs, 1e9 / outputHz);
            Assert.True(atCeiling == state.AtCeiling, name);
            Assert.True((atCeiling && recover) == state.Active, name);

            // A display a refresh behind for 60 shows: a frame is left out only where the recovery may act.
            for (var k = 0; k < 60; k++)
            {
                state.OnShown(k, (long)(1e9 / outputHz) + 1_000_000, (long)(1e9 / outputHz), T0 + (k * slotNs));
            }

            Assert.True((atCeiling && recover) == state.TakeRetime(61, T0 + (61 * slotNs)), name);
            Assert.True(atCeiling == (state.BehindEpisodes == 1), name); // reported wherever the slots are at the ceiling
        }
    }

    private static PlaybackReport Report() => new()
    {
        Options = new PlaybackOptions { Path = "x.mp4" },
        Setup = new PlaybackSetup
        {
            Stream = new Sources.File.FileStreamInfo { Path = "x.mp4", Container = "mp4", Codec = "h264", Profile = "", Width = 3840, Height = 2160, NominalRate = new Core.Time.FrameRate(30000, 1001) },
            GeneratorName = "VFG High",
            Display = "window",
            DeviceName = "test",
            VulkanDriver = "test",
            MaxLead = 5,
        },
    };

    [Fact]
    public void The_summary_says_what_happened_at_the_ceiling()
    {
        var none = Report();
        none.CeilingPacing = true;
        Assert.Contains("never at the output's ceiling (--ceiling-pacing on: inert)", PlayPrinter.CeilingLine(none), StringComparison.Ordinal);

        var behind = Report();
        behind.CeilingPacing = false;
        behind.CeilingDecisions = 23_500;
        behind.BehindShows = 23_238;
        behind.BehindEpisodes = 1;
        behind.BehindLongestNs = 195_900_000_000;
        behind.BehindMaxRefreshes = 1;
        var line = PlayPrinter.CeilingLine(behind);
        Assert.Contains("ran about a whole refresh or more behind there (shows at least three quarters of a refresh late, 30 or more in a row; up to 1; evenly", line, StringComparison.Ordinal);
        Assert.Contains("not a loss of variable refresh", line, StringComparison.Ordinal);
        Assert.Contains("for 23238 shows in 1 run(s), the longest 195.9 s", line, StringComparison.Ordinal);
        Assert.Contains("recovery off (--ceiling-pacing off)", line, StringComparison.Ordinal);

        var recovered = Report();
        recovered.CeilingPacing = true;
        recovered.CeilingDecisions = 23_500;
        recovered.CeilingRetimes = 1;
        recovered.CeilingRetimesRegained = 1;
        recovered.BehindShows = 30;
        recovered.BehindEpisodes = 1;
        recovered.BehindLongestNs = 250_000_000;
        recovered.BehindMaxRefreshes = 1;
        Assert.Contains("recovery on: 1 generated frame(s) left out after 30 late shows in a row so the display could catch up (1 brought the next show half a refresh earlier or on time, 0 did not", PlayPrinter.CeilingLine(recovered), StringComparison.Ordinal);
    }

    [Fact]
    public void The_lost_line_says_running_behind_at_the_ceiling_when_it_is()
    {
        using var error = new StringWriter();
        var notices = PlayPrinter.VrrFollowNotices(error);
        notices(new VrrFollowNotice(true, 1, 0, Unfocused: false, BehindAtCeiling: 1));
        notices(new VrrFollowNotice(true, 2, 0, Unfocused: false, BehindAtCeiling: 0));
        notices(new VrrFollowNotice(true, 3, 0, Unfocused: true, BehindAtCeiling: 1));
        var lines = error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("fmp: the display is running a whole refresh behind at its ceiling", lines[0], StringComparison.Ordinal);
        Assert.Contains("an overlay or another window", lines[1], StringComparison.Ordinal);
        Assert.Contains("the window is not the active one", lines[2], StringComparison.Ordinal);
    }
}
