using FrameMotionPlus.Pipeline.Tests.Support;

namespace FrameMotionPlus.Pipeline.Tests;

/// <summary>
/// The offline verifier (pure; no hardware): it recomputes a run's verdict from the telemetry alone. Fixtures are
/// excerpts of recorded runs (Fixtures/*.csv: the columns it reads, around the events): both "0 missed" gate runs of the
/// previous revision, which it must find full of slots shown a refresh early and one refresh without a new present; the
/// review's disturbed run (slots 5945 to 5951); and a clean stretch, in which it must find nothing. Synthetic sequences
/// cover each definition on its own.
/// </summary>
public class VerifierTests
{
    private static readonly long P120 = (long)Math.Round(1e9 / 119.88);
    private static readonly long P240 = (long)Math.Round(1e9 / 240.016);

    private static List<VerifierRow> Fixture(string name)
    {
        using var reader = new StreamReader(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        return TelemetryVerifier.ParseCsv(reader);
    }

    /// <summary>Judged from the sixth slot on (the first ones lack the context an excerpt cuts off).</summary>
    private static long From(List<VerifierRow> rows) => rows.Where(r => r.Slot >= 0).OrderBy(r => r.Slot).Skip(5).First().TargetNs;

    [Theory]
    [InlineData("review-gate-snap.csv", 612L)]
    [InlineData("gate2-snap.csv", 410L)]
    public void Both_zero_miss_gate_runs_had_early_slots_and_a_refresh_without_a_new_present(string fixture, long snapSlot)
    {
        var rows = Fixture(fixture);
        var result = TelemetryVerifier.Verify(rows, P120, From(rows));
        Log.WriteLine($"{fixture}: {result.Misses} missed, {result.EarlySlots} early, gaps {result.Gaps.Count}; {result.Describe()}");
        var judgedEarly = rows.Where(r => r.Slot >= 0 && r.Slot < snapSlot && r.TargetNs >= From(rows)).Select(r => r.Slot).ToHashSet();
        foreach (var slot in judgedEarly)
        {
            Assert.Contains(result.Slots, s => s.Slot == slot && (s.Finding & VerifierFinding.Early) != 0);
        }

        Assert.Single(result.Gaps);
        Assert.Equal(1, result.Gaps[0].Refreshes);
        Assert.True(result.Gaps[0].Explained, "the refresh without a new present comes right after the last early slot");
        Assert.DoesNotContain(result.Slots, s => s.Slot > snapSlot + 1 && (s.Finding & ~VerifierFinding.Disagrees) != 0);

        // The live accounting of that revision called every one of them fine.
        Assert.Equal(judgedEarly.Count, result.Disagreements);
        Assert.False(result.Clean);
    }

    [Fact]
    public void The_reviews_disturbed_run_is_flagged_from_slot_5945_to_5951()
    {
        var rows = Fixture("review-dist.csv");
        var result = TelemetryVerifier.Verify(rows, P120, From(rows));
        Log.WriteLine(result.Describe(40));
        for (var slot = 5945L; slot <= 5951; slot++)
        {
            Assert.Contains(result.Slots, s => s.Slot == slot && (s.Finding & ~VerifierFinding.Disagrees) != 0);
        }

        Assert.True((result.Slots.First(s => s.Slot == 5951).Finding & VerifierFinding.Early) != 0);
        Assert.True((result.Slots.First(s => s.Slot == 5950).Finding & VerifierFinding.WrongImage) != 0, "5950's refresh showed 5951");
        Assert.DoesNotContain(result.Slots, s => s.Slot < 5945);
    }

    [Fact]
    public void A_clean_run_has_no_finding()
    {
        var rows = Fixture("clean-240hz.csv");
        var result = TelemetryVerifier.Verify(rows, P240, From(rows));
        Assert.True(result.Clean, result.Describe());
        Assert.Empty(result.Slots);
        Assert.Empty(result.Gaps);
        Assert.True(result.Judged > 80);
    }

    private const long T0 = 1_000_000_000;

    /// <summary>A stride-1 run at 120 Hz: slot k scheduled for T0 + kP, shown on time, submitted 10 ms ahead.</summary>
    private static List<VerifierRow> Steady(int n, long p = 8_333_333)
    {
        var rows = new List<VerifierRow>();
        for (var k = 0; k < n; k++)
        {
            var t = T0 + (k * p);
            rows.Add(new VerifierRow { Slot = k, Kind = VerifierKind.Generated, TargetNs = t, ActualNs = t + 100_000, SubmitNs = t - 10_000_000 });
        }

        return rows;
    }

    /// <summary>
    /// A display running late by more than half a refresh (a whole refresh behind at its ceiling, every slot late) when the
    /// run stops with the last decided slot not yet handed over: the slot after the last show falls inside "last show plus
    /// half a refresh", and was judged "not shown" where the live accounting did not count it (the owner's 4x High run on
    /// the TV: late 23,237 + not shown 1 against 23,238 live, a disagreement and a FAIL). A slot never handed over before
    /// the last one handed over is still not shown.
    /// </summary>
    [Fact]
    public void A_slot_not_yet_handed_over_when_the_run_stopped_is_not_judged_even_behind_at_the_ceiling()
    {
        const long P = 8_341_675;
        var rows = new List<VerifierRow>();
        for (var k = 0; k < 40; k++)
        {
            var t = T0 + (k * P);
            rows.Add(new VerifierRow { Slot = k, Kind = VerifierKind.Generated, TargetNs = t, ActualNs = t + P + 1_000_000, SubmitNs = t - 900_000, LiveMiss = true });
        }

        rows[39] = rows[39] with { ActualNs = 0, SubmitNs = 0, LiveMiss = false };
        var result = TelemetryVerifier.Verify(rows, P, vrr: true);
        Log.WriteLine(result.Describe());
        Assert.Equal(39, result.Judged);
        Assert.Equal(39, result.Count(VerifierFinding.Late));
        Assert.Equal(0, result.Count(VerifierFinding.NotShown));
        Assert.Equal(0, result.Disagreements);

        // One never handed over in the middle (a dropped slot) is still not shown.
        rows[20] = rows[20] with { ActualNs = 0, SubmitNs = 0 };
        var dropped = TelemetryVerifier.Verify(rows, P, vrr: true);
        Assert.Equal(1, dropped.Count(VerifierFinding.NotShown));
        Assert.Contains(dropped.Slots, s => s.Slot == 20 && (s.Finding & VerifierFinding.NotShown) != 0);
    }

    /// <summary>
    /// Under variable refresh with the display 1.6 refreshes behind, a present the compositor discarded (no time)
    /// has the shows of presents handed over two slots before it near its own time: those say nothing about where it went.
    /// Only its neighbours in present order judge it, as the live accounting does (a 4x High run on the TV, slot 8911).
    /// </summary>
    [Fact]
    public void Behind_under_variable_refresh_an_untimed_present_is_judged_by_its_neighbours_only()
    {
        const long P = 8_341_675;
        var rows = new List<VerifierRow>();
        for (var k = 0; k < 40; k++)
        {
            var t = T0 + (k * P);
            rows.Add(new VerifierRow { Slot = k, Kind = VerifierKind.Generated, TargetNs = t, ActualNs = t + (8 * P / 5), SubmitNs = t - 900_000, LiveMiss = true });
        }

        // Slot 20 discarded; its neighbours shown 0.6 and 2.6 refreshes after its time, slot 18 0.4 before it.
        rows[20] = rows[20] with { ActualNs = 0, LiveMiss = false, LiveUnjudged = true };
        var result = TelemetryVerifier.Verify(rows, P, vrr: true);
        Log.WriteLine(result.Describe());
        Assert.Contains(result.Slots, s => s.Slot == 20 && (s.Finding & VerifierFinding.Unjudged) != 0);
        Assert.Equal(0, result.Disagreements);

        // Fixed refresh keeps its rule (any timed show of another present at its refresh).
        var fixedRows = rows.Select(r => r with { ActualNs = r.ActualNs == 0 ? 0 : r.TargetNs + 100_000, LiveMiss = false }).ToList();
        fixedRows[21] = fixedRows[21] with { ActualNs = fixedRows[20].TargetNs + 100_000 };
        var grid = TelemetryVerifier.Verify(fixedRows, P);
        Assert.Contains(grid.Slots, s => s.Slot == 20 && (s.Finding & VerifierFinding.RefreshTaken) != 0);
    }

    [Fact]
    public void Each_definition_on_its_own()
    {
        const long P = 8_333_333;
        Assert.True(TelemetryVerifier.Verify(Steady(40), P).Clean);

        // A new image never presented: not shown, and its refresh shows the image before it.
        var dropped = Steady(40);
        dropped[20] = dropped[20] with { ActualNs = 0, SubmitNs = 0 };
        var r1 = TelemetryVerifier.Verify(dropped, P);
        Assert.Equal(VerifierFinding.NotShown | VerifierFinding.WrongImage | VerifierFinding.Disagrees, r1.Slots.Single().Finding);
        Assert.Single(r1.Gaps);

        // The same, counted by the live accounting: no disagreement.
        dropped[20] = dropped[20] with { LiveMiss = true };
        Assert.Equal(0, TelemetryVerifier.Verify(dropped, P).Disagreements);

        // Shown a refresh early from slot 20 on: every one of them early, and the refresh before shows the wrong image.
        var early = Steady(40);
        for (var k = 20; k < 30; k++)
        {
            early[k] = early[k] with { ActualNs = early[k].ActualNs - P };
        }

        var r2 = TelemetryVerifier.Verify(early, P);
        Assert.Equal(10, r2.EarlySlots);
        Assert.Contains(r2.Slots, s => s.Slot == 19 && s.Finding.HasFlag(VerifierFinding.WrongImage));
        Assert.Single(r2.Gaps); // the refresh after the last early one shows no new present

        // An untimed present whose refresh went to the next one (taken), and one with nothing to tell (unjudged).
        var taken = Steady(40);
        taken[20] = taken[20] with { ActualNs = 0 };
        taken[21] = taken[21] with { ActualNs = taken[20].TargetNs + 50_000 };
        var r3 = TelemetryVerifier.Verify(taken, P);
        Assert.True(r3.Slots.Single(s => s.Slot == 20).Finding.HasFlag(VerifierFinding.RefreshTaken));
        var unknown = Steady(40);
        unknown[20] = unknown[20] with { ActualNs = 0 };
        var r4 = TelemetryVerifier.Verify(unknown, P);
        Assert.Equal(1, r4.Unjudged);
        Assert.Equal(0, r4.Misses);

        // A repeat of an image that was never presented shows an older one: wrong image.
        var stale = Steady(40);
        stale[20] = stale[20] with { ActualNs = 0, SubmitNs = 0, LiveMiss = true };
        stale[21] = stale[21] with { Kind = VerifierKind.Repeat };
        var r5 = TelemetryVerifier.Verify(stale, P);
        Assert.True(r5.Slots.Single(s => s.Slot == 21).Finding.HasFlag(VerifierFinding.WrongImage));

        // New images out of order.
        var order = Steady(40);
        (order[20], order[21]) = (order[20] with { ActualNs = order[21].ActualNs }, order[21] with { ActualNs = order[20].ActualNs });
        Assert.True(TelemetryVerifier.Verify(order, P).Count(VerifierFinding.OutOfOrder) >= 1);

        // A refresh no slot was scheduled for and no present shown at: unexplained.
        var hole = Steady(40);
        hole.RemoveAt(20);
        var r6 = TelemetryVerifier.Verify(hole, P);
        Assert.Equal(1, r6.UnexplainedRefreshes);
    }
}
