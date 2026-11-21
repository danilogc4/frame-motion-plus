using System.Globalization;

namespace FrameMotionPlus.Pipeline;

/// <summary>A slot as the telemetry records it (one CSV row): what the verifier judges from.</summary>
public readonly record struct VerifierRow
{
    /// <summary>The slot (-1: a filler the present thread put in).</summary>
    public long Slot { get; init; }

    /// <summary>The refresh the slot was scheduled for, ns.</summary>
    public long TargetNs { get; init; }

    /// <summary>Its first pixel, ns; 0 when it was not shown or its time is unknown.</summary>
    public long ActualNs { get; init; }

    /// <summary>When its present was submitted, ns; 0 when it was never presented.</summary>
    public long SubmitNs { get; init; }

    // The one-byte fields last: 40 B a row (a long run keeps millions).
    public VerifierKind Kind { get; init; }

    /// <summary>The live accounting's verdict (the CSV's <c>missed</c>): compared with the verifier's, never used by it.</summary>
    public bool LiveMiss { get; init; }

    /// <summary>The live accounting could not judge it (an untimed present with no evidence either way).</summary>
    public bool LiveUnjudged { get; init; }
}

public enum VerifierKind : byte
{
    Real = 0,
    Generated = 1,
    Repeat = 2,
    Empty = 3,
    Filler = 4,
}

/// <summary>Why the verifier flagged a slot.</summary>
[Flags]
public enum VerifierFinding : ushort
{
    None = 0,

    /// <summary>Its first pixel came more than half a refresh before its refresh.</summary>
    Early = 1,

    /// <summary>Its first pixel came more than half a refresh after its refresh.</summary>
    Late = 2,

    /// <summary>A new image never presented.</summary>
    NotShown = 4,

    /// <summary>Presented without a first-pixel time, and its refresh showed another present.</summary>
    RefreshTaken = 8,

    /// <summary>Presented without a first-pixel time, with nothing to show where it went.</summary>
    Unjudged = 16,

    /// <summary>Its refresh showed another image than the one scheduled for it.</summary>
    WrongImage = 32,

    /// <summary>A new image shown before one scheduled ahead of it.</summary>
    OutOfOrder = 64,

    /// <summary>The live accounting's verdict differs from the verifier's.</summary>
    Disagrees = 128,

    /// <summary>The refresh before it had no slot scheduled at all (a skipped decision): flagged on the slot after the hole.</summary>
    Unscheduled = 256,
}

/// <summary>What the verifier found in a run's telemetry.</summary>
public sealed class VerificationResult
{
    public List<(long Slot, VerifierFinding Finding)> Slots { get; } = [];

    /// <summary>Intervals of two refreshes or more between consecutive shows (first pixel of the later one, refreshes in between).</summary>
    public List<(long AtNs, int Refreshes, bool Explained)> Gaps { get; } = [];

    public int Judged { get; set; }

    public int Misses { get; set; }

    public int Unjudged { get; set; }

    public int UnexplainedRefreshes { get; set; }

    /// <summary>Slots the verifier counts as missed and the live accounting did not (or the other way round).</summary>
    public int Disagreements { get; set; }

    /// <summary>Of the slots judged: those the live accounting counted as missed (its count over the same slots).</summary>
    public int LiveMisses { get; set; }

    public int EarlySlots => Count(VerifierFinding.Early);

    public int Count(VerifierFinding f)
    {
        var n = 0;
        foreach (var (_, finding) in Slots)
        {
            n += (finding & f) != 0 ? 1 : 0;
        }

        return n;
    }

    public bool Clean => Misses == 0 && Unjudged == 0 && UnexplainedRefreshes == 0 && Disagreements == 0;

    public string Describe(int max = 12)
    {
        var parts = new List<string>();
        foreach (var (slot, f) in Slots)
        {
            if (parts.Count == max)
            {
                parts.Add($"... ({Slots.Count} slots)");
                break;
            }

            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{slot}: {f}"));
        }

        return string.Join(", ", parts);
    }
}

/// <summary>
/// Recomputes a run's verdict from its telemetry alone (the slots' scheduled refreshes, first-pixel times and submit times),
/// written from the definitions and independent of the present thread's logic, so the two can disagree:
/// <list type="bullet">
/// <item>every slot shown (a new image or a repeat) has its first pixel within half a refresh of its refresh;</item>
/// <item>every new image was presented;</item>
/// <item>every refresh shows the image scheduled for it: the image of the latest present whose first pixel is at or before
/// that refresh (a repeat's or filler's image is the new image presented last before it), against the slot's own image
/// for a new image and the last new image scheduled before it for a repeat;</item>
/// <item>a present without a first-pixel time is missed when a timed show of another present took its refresh, and
/// unjudged otherwise;</item>
/// <item>new images are shown in their order;</item>
/// <item>every refresh has a slot scheduled for it (a refresh skipped by the scheduler is flagged on the slot after it);</item>
/// <item>an interval of two refreshes or more between consecutive timed shows, less one refresh for each present without a
/// time submitted in between, is a refresh without a new present: explained when a slot scheduled for it (or the one
/// before) is missed, otherwise counted on its own.</item>
/// </list>
/// Then it compares each slot's verdict with the live accounting's. Pure; allocates (a check at the end of a run).
/// </summary>
public static class TelemetryVerifier
{
    /// <param name="vrr">Variable refresh: slots are not on a refresh grid, so a refresh without a slot or without a new
    /// present is no finding; every other definition holds, with half the shortest refresh period as the tolerance.</param>
    public static VerificationResult Verify(IReadOnlyList<VerifierRow> rows, long periodNs, long fromNs = long.MinValue, long toNs = long.MaxValue, bool vrr = false)
    {
        var result = new VerificationResult();
        var half = periodNs / 2;

        // Slots scheduled after the last show are not judged: the run stopped before they could be shown. Nor are slots never
        // handed over that come after the last one handed over (by slot): the run stopped before their hand-over. Without
        // that, a display running late by more than half a refresh (behind at its ceiling) had the slot after its last show
        // inside the window and judged "not shown" where the live accounting, rightly, did not count it.
        var lastShow = rows.Where(r => r.ActualNs > 0).Select(r => r.ActualNs).DefaultIfEmpty(long.MinValue).Max();
        toNs = Math.Min(toNs, lastShow == long.MinValue ? long.MinValue : lastShow + half);
        var lastHandedOver = rows.Where(r => r.SubmitNs > 0 && r.Slot >= 0).Select(r => r.Slot).DefaultIfEmpty(long.MinValue).Max();

        // Presents in the order they were submitted, each with the image it carries.
        var presents = rows.Where(r => r.SubmitNs > 0).OrderBy(r => r.SubmitNs).ThenBy(r => r.Slot).ToList();
        var imageOf = new Dictionary<(long Slot, long Submit), long>();
        var presentIndex = new Dictionary<(long Slot, long Submit), int>();
        var current = -1L;
        for (var i = 0; i < presents.Count; i++)
        {
            var p = presents[i];
            if (IsNewImage(p.Kind))
            {
                current = p.Slot;
            }

            imageOf[(p.Slot, p.SubmitNs)] = current;
            presentIndex[(p.Slot, p.SubmitNs)] = i;
        }

        // Shows in the order their first pixels came out.
        var shows = presents.Where(p => p.ActualNs > 0).OrderBy(p => p.ActualNs).ToList();
        var showTimes = shows.Select(s => s.ActualNs).ToArray();

        // Untimed presents, by submit time, for the ambiguity of the refreshes around them.
        var untimedSubmits = presents.Where(p => p.ActualNs == 0).Select(p => p.SubmitNs).ToArray();

        var slots = rows.Where(r => r.Slot >= 0 && r.Kind != VerifierKind.Empty).OrderBy(r => r.Slot).ToList();
        var slotTarget = new Dictionary<long, long>();
        foreach (var s in slots)
        {
            slotTarget[s.Slot] = s.TargetNs;
        }

        var verdict = new Dictionary<long, VerifierFinding>();
        var outside = new List<long>(); // targets of missed slots outside the judged window (they explain refreshes inside it)
        var lastScheduledImage = -1L;
        foreach (var s in slots)
        {
            var scheduled = IsNewImage(s.Kind) ? s.Slot : lastScheduledImage;
            if (IsNewImage(s.Kind))
            {
                lastScheduledImage = s.Slot;
            }

            if (s.TargetNs < fromNs || s.TargetNs > toNs || (s.SubmitNs == 0 && s.Slot > lastHandedOver))
            {
                if (s.TargetNs <= toNs && s.ActualNs > 0 && Math.Abs(s.ActualNs - s.TargetNs) > half)
                {
                    outside.Add(s.TargetNs);
                }

                continue;
            }

            var f = VerifierFinding.None;
            if (s.ActualNs > 0)
            {
                var deviation = s.ActualNs - s.TargetNs;
                f |= deviation < -half ? VerifierFinding.Early : deviation > half ? VerifierFinding.Late : VerifierFinding.None;
            }
            else if (s.SubmitNs == 0)
            {
                if (IsNewImage(s.Kind))
                {
                    f |= VerifierFinding.NotShown;
                }
            }
            else
            {
                // Presented, time unknown: a timed show of another present at its refresh means it was not shown there.
                // Under variable refresh only the timed presents next to it in present order count (the one shown in its
                // place, before or after it): a display running a refresh or more behind shows presents handed over well
                // before it at its time, which says nothing about where it went (the live accounting judges it the same way).
                var taken = false;
                if (vrr)
                {
                    var index = presentIndex[(s.Slot, s.SubmitNs)];
                    var before = index - 1;
                    while (before >= 0 && presents[before].ActualNs == 0)
                    {
                        before--;
                    }

                    var after = index + 1;
                    while (after < presents.Count && presents[after].ActualNs == 0)
                    {
                        after++;
                    }

                    taken = (before >= 0 && Math.Abs(presents[before].ActualNs - s.TargetNs) <= half)
                        || (after < presents.Count && Math.Abs(presents[after].ActualNs - s.TargetNs) <= half);
                }
                else
                {
                    foreach (var t in Near(showTimes, s.TargetNs, half))
                    {
                        taken |= shows[t].Slot != s.Slot || shows[t].SubmitNs != s.SubmitNs;
                    }
                }

                f |= taken ? VerifierFinding.RefreshTaken : VerifierFinding.Unjudged;
            }

            // The image on screen at its refresh.
            var at = LatestAtOrBefore(showTimes, s.TargetNs + half);
            if (at >= 0 && scheduled >= 0 && (f & VerifierFinding.Unjudged) == 0)
            {
                var shown = shows[at];
                var nextShow = at + 1 < shows.Count ? shows[at + 1].ActualNs : long.MaxValue;
                var ambiguous = nextShow > s.TargetNs + half && CountBetween(untimedSubmits, shown.SubmitNs, at + 1 < shows.Count ? shows[at + 1].SubmitNs : long.MaxValue) > 0;
                if (!ambiguous && imageOf[(shown.Slot, shown.SubmitNs)] != scheduled)
                {
                    f |= VerifierFinding.WrongImage;
                }
            }

            verdict[s.Slot] = f;
        }

        // Every refresh has a slot: a hole of k refreshes between consecutive slots is flagged on the later one.
        for (var i = 1; i < slots.Count && !vrr; i++)
        {
            if (slots[i].TargetNs - slots[i - 1].TargetNs >= periodNs + half && slots[i].TargetNs >= fromNs && slots[i].TargetNs <= toNs)
            {
                Mark(verdict, slots[i], VerifierFinding.Unscheduled, fromNs, toNs);
            }
        }

        // New images in order.
        long previousSlot = -1, previousActual = long.MinValue;
        foreach (var s in slots.Where(r => IsNewImage(r.Kind) && r.ActualNs > 0))
        {
            if (s.ActualNs <= previousActual)
            {
                Mark(verdict, s, VerifierFinding.OutOfOrder, fromNs, toNs);
                if (verdict.ContainsKey(previousSlot))
                {
                    verdict[previousSlot] |= VerifierFinding.OutOfOrder;
                }
            }

            previousSlot = s.Slot;
            previousActual = Math.Max(previousActual, s.ActualNs);
        }

        // Targets of the missed slots, for explaining the refreshes without a new present.
        var missedTargets = verdict.Where(kv => IsMiss(kv.Value)).Select(kv => slotTarget[kv.Key]).Concat(outside).Order().ToArray();

        // Refreshes without a new present: between consecutive timed shows, those the presents without a time submitted in
        // between (shown in between, if at all: one present per refresh, in order) cannot account for.
        for (var i = 1; i < shows.Count && !vrr; i++)
        {
            var a = shows[i - 1].ActualNs;
            var b = shows[i].ActualNs;
            if (b < fromNs || a > toNs)
            {
                continue;
            }

            var untimedBetween = CountBetween(untimedSubmits, shows[i - 1].SubmitNs, shows[i].SubmitNs);
            var refreshes = (int)Math.Round((b - a) / (double)periodNs) - untimedBetween;
            if (refreshes < 2)
            {
                continue;
            }

            var explained = true;
            for (var k = 1; k < refreshes; k++)
            {
                var g = a + (long)Math.Round(k * (double)periodNs);
                var from = Array.BinarySearch(missedTargets, g - periodNs - half);
                from = from >= 0 ? from : ~from;
                var covered = from < missedTargets.Length && missedTargets[from] <= g + half;
                if (!covered)
                {
                    explained = false;
                    result.UnexplainedRefreshes++;
                }
            }

            result.Gaps.Add((b, refreshes - 1, explained));
        }

        // The verdicts, and how they compare with the live accounting's.
        foreach (var s in slots)
        {
            if (!verdict.TryGetValue(s.Slot, out var f))
            {
                continue;
            }

            result.Judged++;
            result.LiveMisses += s.LiveMiss ? 1 : 0;
            var miss = IsMiss(f);
            var unjudged = (f & VerifierFinding.Unjudged) != 0;
            result.Misses += miss ? 1 : 0;
            result.Unjudged += unjudged ? 1 : 0;
            if (miss != s.LiveMiss || unjudged != s.LiveUnjudged)
            {
                f |= VerifierFinding.Disagrees;
                result.Disagreements++;
            }

            if (f != VerifierFinding.None)
            {
                result.Slots.Add((s.Slot, f));
            }
        }

        return result;
    }

    /// <summary>Rows from a telemetry CSV (<c>fmp play --csv</c>): the columns the verifier uses, by name.</summary>
    public static List<VerifierRow> ParseCsv(TextReader reader)
    {
        var header = reader.ReadLine()?.Split(',') ?? throw new FormatException("An empty telemetry file.");
        int Column(string name) => Array.IndexOf(header, name) is var i and >= 0 ? i : throw new FormatException($"The telemetry has no '{name}' column.");
        var (slot, kind, target, actual, submit, missed) = (Column("slot"), Column("kind"), Column("target_display_ns"), Column("actual_display_ns"), Column("present_submit_ns"), Column("missed"));
        var unjudged = Array.IndexOf(header, "unjudged");
        var rows = new List<VerifierRow>();
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (line.Length == 0)
            {
                continue;
            }

            var c = line.Split(',');
            var s = long.Parse(c[slot], CultureInfo.InvariantCulture);
            rows.Add(new VerifierRow
            {
                Slot = s,
                Kind = s < 0 ? VerifierKind.Filler : c[kind] switch
                {
                    "real" => VerifierKind.Real,
                    "generated" => VerifierKind.Generated,
                    "repeat" => VerifierKind.Repeat,
                    _ => VerifierKind.Empty,
                },
                TargetNs = long.Parse(c[target], CultureInfo.InvariantCulture),
                ActualNs = long.Parse(c[actual], CultureInfo.InvariantCulture),
                SubmitNs = long.Parse(c[submit], CultureInfo.InvariantCulture),
                LiveMiss = c[missed] is "1" or "true" or "True",
                LiveUnjudged = unjudged >= 0 && c[unjudged] is "1" or "true" or "True",
            });
        }

        return rows;
    }

    private static bool IsNewImage(VerifierKind k) => k is VerifierKind.Real or VerifierKind.Generated;

    /// <summary>How many of the ascending <paramref name="sorted"/> lie strictly between <paramref name="low"/> and <paramref name="high"/>.</summary>
    private static int CountBetween(long[] sorted, long low, long high)
    {
        if (high <= low)
        {
            return 0;
        }

        var from = Array.BinarySearch(sorted, low);
        from = from >= 0 ? UpperBound(sorted, from, low) : ~from;
        var to = Array.BinarySearch(sorted, high);
        to = to >= 0 ? LowerBound(sorted, to, high) : ~to;
        return Math.Max(0, to - from);
    }

    private static int UpperBound(long[] sorted, int i, long value)
    {
        while (i < sorted.Length && sorted[i] == value)
        {
            i++;
        }

        return i;
    }

    private static int LowerBound(long[] sorted, int i, long value)
    {
        while (i > 0 && sorted[i - 1] == value)
        {
            i--;
        }

        return i;
    }

    private static bool IsMiss(VerifierFinding f) => (f & ~(VerifierFinding.Unjudged | VerifierFinding.Disagrees)) != 0;

    private static void Mark(Dictionary<long, VerifierFinding> verdict, in VerifierRow s, VerifierFinding f, long fromNs, long toNs)
    {
        if (s.TargetNs >= fromNs && s.TargetNs <= toNs)
        {
            verdict[s.Slot] = (verdict.TryGetValue(s.Slot, out var v) ? v : VerifierFinding.None) | f;
        }
    }

    private static int LatestAtOrBefore(long[] sorted, long t)
    {
        var i = Array.BinarySearch(sorted, t);
        if (i >= 0)
        {
            while (i + 1 < sorted.Length && sorted[i + 1] == t)
            {
                i++;
            }

            return i;
        }

        return ~i - 1;
    }

    private static IEnumerable<int> Near(long[] sorted, long t, long within)
    {
        var i = LatestAtOrBefore(sorted, t + within);
        for (; i >= 0 && sorted[i] >= t - within; i--)
        {
            yield return i;
        }
    }
}
