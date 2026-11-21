using System.Globalization;
using FrameMotionPlus.App.Capture;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Pipeline;

namespace FrameMotionPlus.App.Play;

/// <summary>What <c>fmp play</c> prints: what it resolved at the start, and the summary and verdict at the end.</summary>
internal static class PlayPrinter
{
    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    public static void PrintSetup(TextWriter o, PlayOptions options, PlaybackOptions playback, PlaybackSetup s, string why, CudaContext cuda)
    {
        var info = s.Stream ?? throw new InvalidOperationException("fmp play prints a file's setup.");
        o.WriteLine($"fmp play: {info.Path}{(options.Gate ? "  [gate: milestone 1, 10 minutes VFG Medium 2x, zero missed slots, zero gen0 collections]" : "")}");
        o.WriteLine(string.Create(Ic,
            $"  file        {info.Describe()}; {(info.DurationNs > 0 ? $"{info.DurationNs / 1e9:0.###} s" : "length unknown")}" +
            $"{(info.DeclaredFrames > 0 ? $", {info.DeclaredFrames} frames" : "")}; colour {info.Color.Transfer}/{info.Color.Primaries} → {info.OutputFormat}" +
            $"{(playback.Loop ? "; looping" : "")}"));
        o.WriteLine($"  generator   {s.GeneratorName}: {why}{(s.GeneratorMayBlock ? "; its Enqueue blocks for the generation, so it runs on the generator thread" : "")}");
        if (s.VfgLoad is { } load)
        {
            o.WriteLine(string.Create(Ic,
                $"              {s.VfxSdk}; loaded in {load.LoadNs / 1e6:0.0} ms, warmed up in {load.WarmupNs / 1e6:0.0} ms, {load.DeviceBytes / 1048576.0:0} MiB; " +
                $"{DescribeShotChangeDetection(playback.ShotChangeDetection, capture: false)}; output written straight into the ring slots"));
        }

        PrintQualitySetup(o, s, playback);
        PrintKeys(o, playback, capture: false);
        o.WriteLine($"  device      {s.DeviceName}, Vulkan driver {s.VulkanDriver}; CUDA context scheduling {options.CudaSync}");
        o.WriteLine($"  display     {s.Display}; transform {s.Transform}");
        o.WriteLine($"  signal      {DescribeFileSignal(info)}");
        o.WriteLine($"  output      {s.Output}");
        o.WriteLine($"  scaling     {s.Scaling}");
        o.WriteLine(string.Create(Ic,
            $"  feedback    {s.FeedbackMode}; presents in flight capped at {(s.PresentsInFlightCap == 0 ? "nothing (cannot cap)" : s.PresentsInFlightCap.ToString(Ic))}"));
        o.WriteLine(string.Create(Ic,
            $"  ring        {s.RingSlots} slots, pitch {s.RingPitchBytes} B; bridge budget {s.OperationsPerWrite} CUDA operations per write, lead at most {s.MaxLead}"));
        var cap = options.OutputHz is { } hz ? string.Create(Ic, $"--output-hz {hz:0.###} → cap {options.MultiplierFor(info.NominalRate)}") : $"cap {playback.Multiplier}";
        o.WriteLine(string.Create(Ic,
            $"  rates       source {(info.NominalRate.IsValid ? info.NominalRate.Hz.ToString("0.###", Ic) + " fps" : "unknown")}, multiplier {cap}; " +
            $"warm-up {playback.WarmupNs / 1e9:0.#} s{(playback.DurationNs > 0 ? $", duration {playback.DurationNs / 1e9:0.###} s" : "")}" +
            $"{(playback.MaxSlots > 0 ? $", {playback.MaxSlots} slots" : "")}"));
    }

    /// <summary>
    /// The start-up line's account of VFG's automatic shot-change detection (<c>--scd</c>) as the run has it, naming the
    /// command's default (capture off, play on) when it is that.
    /// </summary>
    internal static string DescribeShotChangeDetection(bool on, bool capture) => on
        ? $"automatic shot-change detection on (--scd on{(capture ? "" : ", play's default")}: what it takes for a cut, fast motion too, " +
          "shows a real frame instead of a generated one; --scd off keeps generating)"
        : $"automatic shot-change detection off (--scd off{(capture ? ", a capture's default" : "")}: every pair is interpolated, " +
          "fast motion too; a real cut shows one morphed frame)";

    /// <summary>"PQ (SMPTE ST 2084), BT.2020 primaries, limited range, 10-bit, BT.2020 matrix; mastering ...": the file's signal.</summary>
    private static string DescribeFileSignal(Sources.File.FileStreamInfo info)
    {
        var c = info.Color;
        var transfer = c.Transfer switch
        {
            Core.Frames.TransferFunction.Pq => "PQ (SMPTE ST 2084)",
            Core.Frames.TransferFunction.Hlg => "HLG (shown on an SDR output, HLG's backward-compatible picture: converting it to PQ is not built)",
            Core.Frames.TransferFunction.Unspecified => "transfer unstated (SDR gamma assumed)",
            _ => "SDR gamma (" + c.Transfer + ")",
        };
        var metadata = c.IsPq
            ? c.Mastering.IsSpecified
                ? string.Create(Ic, $"; stream metadata: mastering {c.Mastering.MinLuminanceNits:0.####} to {c.Mastering.MaxLuminanceNits:0} nits, MaxCLL {c.LightLevel.MaxCll}, MaxFALL {c.LightLevel.MaxFall} (frames' side data, if any, replaces it)")
                : "; no mastering metadata in the stream (frames' side data used if any, else BT.2020/D65 1000-nit defaults)"
            : "";
        return string.Create(Ic, $"{transfer}, {c.Primaries} primaries, {info.BitDepth}-bit, decoded to {info.OutputFormat} with the stream's own matrix and range{metadata}");
    }

    /// <summary>Prints the summary; returns the verdict (true: pass).</summary>
    public static bool PrintSummary(TextWriter o, PlayOptions options, PlaybackReport r) => PrintSummary(o, options.Gate, r);

    /// <summary>
    /// Prints the summary of a file or capture run (with <paramref name="gate"/>, the Phase 4 gate's verdict too);
    /// <paramref name="more"/> adds lines (the capture's sound) before the warnings, and warnings of its own.
    /// </summary>
    public static bool PrintSummary(TextWriter o, bool gate, PlaybackReport r, Action<TextWriter, List<string>>? more = null)
    {
        var s = r.Statistics;
        var d = r.Diagnostics;
        var period = r.DisplayRate.IsValid ? r.DisplayRate.PeriodNs : 0;
        o.WriteLine(string.Create(Ic,
            $"  timing      display {(r.DisplayRate.IsValid ? r.DisplayRate.Hz.ToString("0.###", Ic) : "?")} Hz{(r.DisplayRateKnown ? "" : " (never confirmed by the output: a guess)")}" +
            $"{(r.DisplayRateChanges > 1 ? $", rate changed {r.DisplayRateChanges - 1}x during the run" : "")}; multiplier cap {r.Multiplier}, " +
            $"effective {d.Multiplier} (stride {d.Stride}: a new image every {d.Stride} refresh{(d.Stride == 1 ? "" : "es")}); " +
            $"source interval T {Ms(d.SourceIntervalNs)} ms; present lead {Ms(r.PresentLeadNs)} ms at the end{LeadHistory(r)}; scheduler margin {Ms(r.MarginNs)} ms; " +
            $"{(d.PhaseCoherent ? "phase-locked to the refreshes" : "not phase-locked")}"));
        o.WriteLine(r.RefreshModeUsed == RefreshMode.Vrr
            ? string.Create(Ic,
                $"  clock       VARIABLE REFRESH: slots follow the source (V = ts + D + kT/M), each handed over at its time; the output's shortest refresh period " +
                $"{Ms(period)} ms; expected delay from hand-over to first pixel (the lead) {Ms(r.PresentLeadNs)} ms{VrrProbe(r)}")
            : string.Create(Ic, $"  clock       fixed refresh: slots are the output's refreshes{VrrProbe(r)}" +
                $"{(r.VrrTooFast > 0 ? $"; not variable refresh: the source at its multiplier needs {r.VrrTooFast:0.###} fps, more than this output's {r.DisplayRate.Hz:0.###} Hz (--clock vrr forces it)" : "")}"));
        if (r.Resample != 1.0)
        {
            o.WriteLine(string.Create(Ic,
                $"  resample    the file plays {Math.Abs(r.Resample - 1) * 100:0.000}% {(r.Resample > 1 ? "slower" : "faster")} (content times x{r.Resample:0.000000}) to lock to the {r.DisplayRate.Hz:0.###} Hz output"));
        }

        if (r.LatchSweep is { } sweep)
        {
            o.WriteLine(sweep.Bracketed
                ? string.Create(Ic,
                    $"  latch       measured before playback ({sweep.Probes} probes of the last pre-roll image: {sweep.Made} made their refresh, {sweep.Late} a refresh late, " +
                    $"{sweep.Early} early): the latest hand-over that missed its refresh came {Ms(sweep.LateMaxNs)} ms before it (the shortest that made one: " +
                    $"{Ms(sweep.MadeMinNs)} ms); the present lead started at {Ms(sweep.LeadNs)} ms (that plus {Ms(sweep.MarginNs)})")
                : string.Create(Ic, $"  latch       not bracketed by {sweep.Probes} probes before playback ({sweep.Made} made, {sweep.Late} late, {sweep.Early} early): the lead started at {Ms(sweep.LeadNs)} ms"));
        }

        if (r.LeadChanges.Count > 1)
        {
            var shown = r.LeadChanges.Take(12).Select(c => string.Create(Ic, $"{c.Why} {Ms(c.LeadNs)}"));
            o.WriteLine($"  lead        changes: {string.Join(" → ", shown)}{(r.LeadChanges.Count > 12 ? $" ... ({r.LeadChanges.Count})" : "")}");
        }

        o.WriteLine($"  threads     pacing: {r.PacingThread}; generator: {r.WriterThread}; present: {r.PresentThread}");
        o.WriteLine(string.Create(Ic,
            $"  duration    {r.PlaybackNs / 1e9:0.00} s played, {s.MeasuredSeconds:0.00} s measured after {r.WarmupNs / 1e9:0.#} s of warm-up; stopped: {StopName(r.StopReason)}"));
        o.WriteLine(string.Create(Ic,
            $"  slots       {s.Slots} measured: {s.Real} real, {s.Generated} generated, {s.Repeated} repeated, {s.Empty} empty; " +
            $"late (real frame instead of a generated one) {s.LateDecisions}, starved {s.StarvedSlots}, hold {s.HoldSlots}, " +
            $"discontinuity {s.DiscontinuitySlots}, fallback/stride {s.FallbackSlots}"));
        var rate = s.MeasuredSeconds > 0 ? s.Presents / s.MeasuredSeconds : 0;
        o.WriteLine(string.Create(Ic,
            $"  presents    {s.Presents} shown ({rate:0.0}/s); fillers {r.Fillers}, dropped late {r.DroppedLate}, dropped refused {r.DroppedRefused} " +
            $"(whole run); max in flight {r.MaxPresentsInFlight}; presenter refusals {r.PresentRefusals}, rejected {r.PresenterStats.RejectedRequests}; " +
            $"{r.TimedShows} of {r.Shown} shows timed by their first pixel, {s.UntimedShows} untimed in the window (kept out of the grid and the intervals; " +
            $"whole run: the compositor's report came without a time for {r.PresenterStats.ReportsWithoutTime}, " +
            $"{r.PresenterStats.UntimedPresents - r.PresenterStats.ReportsWithoutTime} report(s) never came)"));
        var iv = s.OutputIntervals;
        var offGrid = s.OffGridIntervals * 50 > s.TimedIntervals && s.TimedIntervals > 0
            ? " (the output is not refreshing on a fixed grid: VRR, or a compositor missing its own deadlines; no slot can be placed on a refresh there)"
            : "";
        o.WriteLine(iv.Count == 0
            ? "  intervals   none (no present feedback in the measured window)"
            : string.Create(Ic,
                $"  intervals   from present feedback: mean {Ms(iv.Mean)} ms, sd {Ms(iv.StdDev)} ms, p99 {Ms(iv.Percentile(0.99))} ms, max {Ms(iv.Max)} ms, " +
                $"min {Ms(iv.Min)} ms over {iv.Count}{(period > 0 ? $" (refresh period {Ms(period)} ms)" : "")}; " +
                $"{s.OffGridIntervals} of {s.TimedIntervals} timed intervals more than a quarter refresh off the grid{offGrid}"));
        var total = s.TotalMisses;
        var against = r.Verification is not { } vv ? " (the verifier did not run)"
            : r.VerifierRowsDropped > 0
                ? string.Create(Ic, $" (over the first {vv.Judged} slots, all its buffer held: the verifier {vv.Misses}, the live counters {vv.LiveMisses}; {(vv.Disagreements == 0 ? "agrees" : $"DISAGREES on {vv.Disagreements}")}; the {r.VerifierBeyondSlots} after them are the live counters' alone)")
                : string.Create(Ic, $" (the verifier, from the telemetry alone: {vv.Misses}; {(vv.Disagreements == 0 ? "agrees" : $"DISAGREES on {vv.Disagreements}")})");
        o.WriteLine(string.Create(Ic,
            $"  missed      {total} slot(s){against}; the pipeline's {s.PipelineMisses}: late present {s.MissCount(MissKind.LatePresent)}, early {s.MissCount(MissKind.Early)}, " +
            $"dropped {s.MissCount(MissKind.Dropped)}, untimed and its refresh taken {s.MissCount(MissKind.NotShown)}, refresh without a new present {s.MissCount(MissKind.MissedRefresh)}, " +
            $"refused {s.MissCount(MissKind.Refused)}, skipped vblank {s.MissCount(MissKind.SkippedVblank)}, back-pressure {s.MissCount(MissKind.BackPressure)}, " +
            $"presenter backlog {s.MissCount(MissKind.PresenterBacklog)}; the display's {s.EnvironmentMisses}: shown late although handed over in time " +
            $"{s.MissCount(MissKind.DisplayLate)}, given back after those {s.MissCount(MissKind.CatchUp)}, handed over in time and discarded by the compositor " +
            $"{s.MissCount(MissKind.Discarded)}; during a refresh-rate change {s.RateChangeMisses}; unjudged {s.UnjudgedSlots}; refreshes without a new present " +
            $"{s.RefreshesWithoutPresent}; late shows {r.LateShows}: the refresh given back by the compositor {r.CompositorCatchUps}x, by the present thread {r.OwnCatchUps}x" +
            $"{(r.RefreshModeUsed == RefreshMode.Vrr ? VrrSkipText(s, r.LateGeneratedNs) : "")}"));
        if (r.VrrFollow is { } follow)
        {
            o.WriteLine(r.BehindShows > 0
                ? VrrFollowLine(follow, r.PlaybackStartNs) + string.Create(Ic, $"; {r.BehindShows} of the late shows were the display running evenly behind at the output's ceiling, which looks the same and is not a loss (the ceiling line)")
                : VrrFollowLine(follow, r.PlaybackStartNs));
        }

        if (r.RefreshModeUsed == RefreshMode.Vrr)
        {
            o.WriteLine(CeilingLine(r));
        }

        if (r.Verification is { } v)
        {
            o.WriteLine(string.Create(Ic,
                $"  verifier    from the telemetry alone: {v.Judged} slots judged, {v.Misses} missed (early {v.EarlySlots}, late {v.Count(VerifierFinding.Late)}, " +
                $"not shown {v.Count(VerifierFinding.NotShown)}, untimed with its refresh taken {v.Count(VerifierFinding.RefreshTaken)}, wrong image {v.Count(VerifierFinding.WrongImage)}, " +
                $"out of order {v.Count(VerifierFinding.OutOfOrder)}), {v.Unjudged} unjudged, {v.Gaps.Sum(g => g.Refreshes)} refresh(es) without a new present " +
                $"({v.UnexplainedRefreshes} unexplained); {(v.Disagreements == 0 ? "agrees with the live counters" : $"DISAGREES with the live counters on {v.Disagreements}")}" +
                $"{(r.VerifierRowsDropped > 0 ? $"; its buffer held the first {r.VerifierJudgedNs / 1e9:0} s of the measured window: the {r.VerifierBeyondSlots} slot(s) after that were judged by the live counters alone (not compared, not a disagreement)" : "")}"));
            if (v.Slots.Count > 0)
            {
                o.WriteLine($"              {v.Describe(10)}");
            }
        }
        else if (r.VerificationSkipped is { } why)
        {
            o.WriteLine($"  verifier    not run: {why}");
        }
        if (s.HandoverLeads.Count > 0)
        {
            o.WriteLine(string.Create(Ic,
                $"  hand-over   each present handed over (submitted and written) before its refresh: p1 {Ms(s.HandoverLeads.Percentile(0.01))} ms, " +
                $"p50 {Ms(s.HandoverLeads.Percentile(0.5))} ms (a late show is the display's only above the measured deadline: what has made its refresh in the " +
                $"last 10 s, plus 0.25 ms); fillers {s.Fillers}"));
        }
        if (s.MissEventCount > 0)
        {
            var shown = Math.Min(s.MissEventCount, 12);
            var list = new List<string>(shown);
            foreach (var m in s.MissEvents[..shown])
            {
                list.Add(string.Create(Ic, $"{m.AtNs / 1e9:0.000} s {MissName(m.Kind)}{(m.Refreshes > 1 ? $" x{m.Refreshes}" : "")}"));
            }

            o.WriteLine($"              at {string.Join(", ", list)}{(s.MissEventCount > shown ? $" ... ({s.MissEventCount} events)" : "")}");
        }

        var g = s.GenerationTimes;
        o.WriteLine(g.Count == 0
            ? "  d           no generated frames timed"
            : string.Create(Ic, $"  d           generation GPU time p50 {Ms(g.Percentile(0.5))} ms, p99 {Ms(g.Percentile(0.99))} ms, max {Ms(g.Max)} ms over {g.Count}; " +
                $"generator thread per write p50 {Ms(s.WriteHostTimes.Percentile(0.5))} ms, p99 {Ms(s.WriteHostTimes.Percentile(0.99))} ms"));
        if (s.CopyTimes.Count > 0)
        {
            o.WriteLine(string.Create(Ic, $"              real-frame copy into the ring p50 {Ms(s.CopyTimes.Percentile(0.5))} ms, p99 {Ms(s.CopyTimes.Percentile(0.99))} ms over {s.CopyTimes.Count}"));
        }

        PrintQualitySwitches(o, r);

        var dd = s.PipelineDelays;
        var stride = s.StrideDelay;
        o.WriteLine(string.Create(Ic,
            $"  D           the scheduler's D {Ms(d.PipelineDelayNs)} ms at the end (target {Ms(d.DelayTargetNs)}: hold {Ms(d.HoldNs)} + lead {Ms(d.LeadNs)} " +
            $"[p99 d {Ms(d.GenerationTimeNs)} + wake {Ms(d.WakeLatenessNs)} + margin {Ms(r.MarginNs)}] + arrival {Ms(d.ArrivalLagNs)} + alignment {Ms(d.AlignmentNs)}); " +
            $"over the window min {Ms(dd.Min)}, p50 {Ms(dd.Percentile(0.5))}, max {Ms(dd.Max)} ms"));
        if (stride.Count > 0)
        {
            o.WriteLine(string.Create(Ic,
                $"  to light    a real frame is shown present lead {Ms(s.PresentLeads.Percentile(0.5))} + D {Ms(dd.Percentile(0.5))} + the scheduler's rounding " +
                $"{Ms(stride.Percentile(0.5))} ms (p50, measured) after its content time: at stride s a new image lands on the next refresh of its stride " +
                $"(up to s − 1 refreshes after τ); not phase-locked, a real frame within the snap of τ is shown up to half an interval early"));
        }
        var model = LatencyModel.Evaluate(d.SourceIntervalNs, Math.Max(1, d.Multiplier), g.Count > 0 ? g.Percentile(0.99) : d.GenerationTimeNs);
        var lat = s.RealFrameLatency;
        if (r.Capture is null)
        {
            o.WriteLine(lat.Count == 0
                ? "  latency     no real frame shown in the measured window"
                : string.Create(Ic,
                    $"  latency     real frames shown {Ms(lat.Mean)} ms after their content time (p99 {Ms(lat.Percentile(0.99))}); latency model ΔL = d + (M−1)/M·T = " +
                    $"{Ms(model.GenerationTimeNs)} + {Ms(model.HoldNs)} = {Ms(model.AddedLatencyNs)} ms (M {model.Multiplier}, T {Ms(model.SourceIntervalNs)}); " +
                    $"the rest is the present lead, the scheduler's margin and alignment, and the stride's rounding (a file is not latency-bound)"));
        }
        else
        {
            CapturePrinter.PrintLatency(o, r, model);
        }

        o.WriteLine(string.Create(Ic,
            $"  pipeline    bridge lead at most {r.MaxOutstanding} (limit {r.Setup.MaxLead}); slots skipped for back-pressure {r.SkippedBackPressure}, for presenter backlog " +
            $"{r.SkippedPresenterBacklog}; refresh-grid updates {r.DisplayUpdates}; ring writes: {r.Copies} copies, {r.GeneratedWrites} generated, " +
            $"{r.Fallbacks} fallbacks; writes out of order {r.OutOfOrderSignals}"));
        o.WriteLine(r.GeneratorFaults == 0
            ? "  faults      generator: none"
            : $"  faults      generator: {r.GeneratorFaults} (faulted: {r.GeneratorFaulted}; playback carried on with real frames); first: {r.FirstGeneratorFault}");
        var src = r.SourceStatistics;
        if (r.Capture is { } capture)
        {
            CapturePrinter.PrintCapture(o, r, capture);
        }
        else
        {
            o.WriteLine(string.Create(Ic,
                $"  source      {src.FramesDecoded} decoded, {src.FramesDelivered} delivered, {r.FramesRead} read; loops {src.Loops}, discontinuities {r.Discontinuities} " +
                $"(scheduler {r.Counters.Discontinuities}); frame-pool growth {src.PoolGrowth}; decode thread waited for the pipeline {src.BackPressureWaits}x"));
            if (r.OutputAtEnd is { } output)
            {
                o.WriteLine($"  output      at the end: {output}{(r.PresenterStats.MetadataUpdates > 0 ? string.Create(Ic, $" (metadata updated {r.PresenterStats.MetadataUpdates}x from the frames' side data)") : "")}");
            }

            if (r.ScalingAtEnd is { } scaling)
            {
                o.WriteLine($"  scaling     at the end: {scaling}");
            }
        }
        foreach (var (name, cost) in r.Threads)
        {
            o.WriteLine(cost.AllocatedBytes < 0
                ? $"  {name,-11} not measured"
                : string.Create(Ic,
                    $"  {"cpu/" + name,-11} {cost.AllocatedBytes} B allocated, {(s.Slots > 0 && cost.CpuNs >= 0 ? cost.CpuNs / 1e3 / s.Slots : 0):0.0} µs CPU per slot " +
                    $"({cost.CpuNs / 1e9:0.000} s over {s.Slots} slots, {cost.Turns} loop turns)"));
        }

        o.WriteLine(string.Create(Ic,
            $"  gc          measured window gen0/1/2 {r.MeasuredGc.Gen0Collections}/{r.MeasuredGc.Gen1Collections}/{r.MeasuredGc.Gen2Collections}; whole run " +
            $"{r.RunGc.Gen0Collections}/{r.RunGc.Gen1Collections}/{r.RunGc.Gen2Collections}; " +
            $"{(r.Capture is { } cap ? $"capture thread {cap.Counters.DequeueThreadAllocatedBytes} B" : $"decode thread {src.DecodeThreadAllocatedBytes} B")}; {r.GcMode}"));
        o.WriteLine(r.CsvPath is null
            ? $"  telemetry   {r.TelemetryRecords} records (--csv writes them)"
            : $"  telemetry   {r.TelemetryRecords} records written to {r.CsvPath}{(r.TelemetryDropped > 0 ? $" ({r.TelemetryDropped} dropped)" : "")}");
        o.WriteLine(r.GpuAbandoned
            ? $"  teardown    ABANDONED: {r.Abandoned}"
            : r.AbandonedImports == 0 ? "  teardown    clean: ring drained, imports freed" : $"  teardown    {r.AbandonedImports} import(s) went to the reaper");
        o.WriteLine(r.ValidationEnabled
            ? string.Create(Ic, $"  validation  VK_LAYER_KHRONOS_validation with synchronization checks: {r.ValidationProblems} message(s)")
            : "  validation  off (--validate turns it on)");
        if (r.ValidationReport is { } report)
        {
            foreach (var line in report.Split('\n'))
            {
                o.WriteLine($"              {line.TrimEnd()}");
            }
        }

        var failures = r.Failures();
        var warnings = new List<string>();
        more?.Invoke(o, warnings);
        if (r.MeasuredGc.Gen0Collections > 0)
        {
            warnings.Add($"{r.MeasuredGc.Gen0Collections} gen0 collection(s) in the measured window");
        }

        if (total > 0)
        {
            warnings.Add($"{total} missed slot(s): {s.PipelineMisses} the pipeline's, {s.EnvironmentMisses} the display's, {s.RateChangeMisses} during a rate change");
        }

        if (r.GeneratorFaults > 0)
        {
            warnings.Add($"the generator failed {r.GeneratorFaults}x and was switched off");
        }

        var pass = failures.Count == 0;
        if (gate && r.Setup.Stream is { } stream)
        {
            var gateFailures = new List<string>();
            if (r.StopReason != StopReason.Duration)
            {
                gateFailures.Add($"the run did not reach its duration (stopped: {StopName(r.StopReason)})");
            }

            if (!r.Measured)
            {
                gateFailures.Add("nothing was measured");
            }

            if (s.UnjudgedSlots > 0 || r.Verification is { Unjudged: > 0 })
            {
                gateFailures.Add($"{Math.Max(s.UnjudgedSlots, r.Verification?.Unjudged ?? 0)} slot(s) without a first-pixel time that nothing can judge");
            }

            if (r.Verification is null)
            {
                gateFailures.Add($"the telemetry verifier did not run ({r.VerificationSkipped ?? "no telemetry"})");
            }

            // The Phase 4 gate (relaxed by the owner, 2026-11-18): zero missed slots caused by the pipeline; the display's
            // and the rate change's are reported apart.
            if (s.PipelineMisses > 0)
            {
                gateFailures.Add($"{s.PipelineMisses} missed slot(s) caused by the pipeline");
            }

            if (r.MeasuredGc.Gen0Collections > 0)
            {
                gateFailures.Add($"{r.MeasuredGc.Gen0Collections} gen0 collection(s) in the measured window");
            }

            if (stream.Width != 2560 || stream.Height != 1440 || !stream.NominalRate.IsValid || Math.Abs(stream.NominalRate.Hz - 60) > 0.01)
            {
                warnings.Add($"the gate is defined for a 1440p60 file; this is {stream.Width}x{stream.Height} at {stream.NominalRate}");
            }

            if (Math.Abs((r.DisplayRate.Hz / Math.Max(1, d.Stride)) - 120) > 1)
            {
                warnings.Add(string.Create(Ic, $"the gate is defined for 120 Hz output; this run's output was {r.DisplayRate.Hz / Math.Max(1, d.Stride):0.#} Hz"));
            }

            o.WriteLine(gateFailures.Count == 0 && pass
                ? string.Create(Ic,
                    $"  gate        PASS: {s.Slots} slots over {s.MeasuredSeconds:0.0} s, zero missed slots caused by the pipeline, zero gen0 collections " +
                    $"(the display's: {s.EnvironmentMisses}; during a rate change: {s.RateChangeMisses})")
                : "  gate        FAIL");
            foreach (var g2 in gateFailures)
            {
                o.WriteLine($"              - {g2}");
            }

            pass &= gateFailures.Count == 0;
        }

        foreach (var w in warnings)
        {
            o.WriteLine($"  WARNING     {w}");
        }

        if (failures.Count == 0 && r.StopReason == StopReason.User && !r.Measured && !gate)
        {
            o.WriteLine("  verdict     STOPPED by the user before anything was measured (no verdict)");
        }
        else if (failures.Count == 0)
        {
            o.WriteLine(pass ? "  verdict     PASS" : "  verdict     FAIL (the gate)");
        }
        else
        {
            o.WriteLine("  verdict     FAIL");
            foreach (var f in failures)
            {
                o.WriteLine($"              - {f}");
            }
        }

        return pass;
    }

    public static string StopName(StopReason reason) => reason switch
    {
        StopReason.Duration => "duration reached",
        StopReason.SlotLimit => "slot count reached",
        StopReason.EndOfFile => "end of file",
        StopReason.User => "by the user",
        StopReason.Failure => "failure",
        _ => "not stopped",
    };

    private static string MissName(MissKind kind) => kind switch
    {
        MissKind.Early => "early",
        MissKind.NotShown => "untimed, refresh taken",
        MissKind.MissedRefresh => "refresh without a new present",
        MissKind.DisplayLate => "display late",
        MissKind.CatchUp => "display catch-up",
        MissKind.Discarded => "discarded by the compositor",
        MissKind.RateChange => "rate change",
        MissKind.LatePresent => "late present",
        MissKind.Dropped => "dropped",
        MissKind.Refused => "refused",
        MissKind.SkippedVblank => "skipped vblank",
        MissKind.BackPressure => "back-pressure",
        _ => "presenter backlog",
    };

    private static string VrrProbe(PlaybackReport r) => r.VrrProbe is not { } p
        ? ""
        : string.Create(Ic,
            $" (probe before playback{(r.VrrProbeAttempts > 1 ? $", the {Ordinal(r.VrrProbeAttempts)} of up to 3 (the earlier ones did not find the display following yet)" : "")}: {p.Following} of {p.Pairs} pairs of shows as far apart as their presents, {p.OnGrid} on the refresh grid; " +
            $"first pixel {Ms(p.DelayP50Ns)} ms after the present, at most {Ms(p.DelayMaxNs)}: {(p.Active ? "the display follows the presents" : "fixed refresh")})");

    private static string Ordinal(int n) => n switch { 1 => "first", 2 => "second", 3 => "third", _ => n.ToString(Ic) + "th" };

    private static string LeadHistory(PlaybackReport r) => r.LeadRaises == 0
        ? " (it did not need to adapt)"
        : string.Create(Ic, $" (raised {r.LeadRaises}x when a frame's content missed the compositor's latch; peak {Ms(r.PeakLeadNs)} ms)");

    private static string Ms(double ns) => (ns / 1e6).ToString("0.000", Ic);

    /// <summary>The start-up line on Q: both VFG qualities loaded (and what the second cost), or why Q does nothing.</summary>
    public static void PrintQualitySetup(TextWriter o, PlaybackSetup s, PlaybackOptions playback)
    {
        if (s.QualitySwitch is { } pair)
        {
            var second = s.SecondVfgLoad is { } l
                ? string.Create(Ic, $"{Pipeline.PlaybackSession.QualityName(pair.Other)} loaded too in {l.LoadNs / 1e6:0.0} ms, warmed up in {l.WarmupNs / 1e6:0.0} ms, {l.DeviceBytes / 1048576.0:0} MiB more GPU memory")
                : $"{Pipeline.PlaybackSession.QualityName(pair.Other)} loaded too";
            o.WriteLine(string.Create(Ic,
                $"  quality     Q switches {Pipeline.PlaybackSession.QualityName(pair.Start)} <-> {Pipeline.PlaybackSession.QualityName(pair.Other)} while playing: {second} " +
                $"(the generator's instances together {s.GeneratorDeviceBytes / 1048576.0:0} MiB); the switch is made between two frames, with no load " +
                $"(--no-quality-switch keeps one instance)"));
        }
        else if (playback.Generator == Pipeline.GeneratorKind.Vfg)
        {
            o.WriteLine($"  quality     Q does nothing: {s.QualitySwitchOff}");
        }
    }

    /// <summary>The window's keys, once at start (a window only).</summary>
    public static void PrintKeys(TextWriter o, PlaybackOptions playback, bool capture)
    {
        if (playback.Display != Pipeline.DisplayKind.Window)
        {
            return;
        }

        o.WriteLine(capture
            ? "  keys        in the window: Esc quits; H switches SDR/HDR10 by hand, A follows the signal again; Q switches the generator's quality (Medium/High)"
            : "  keys        in the window: Esc quits; Q switches the generator's quality (Medium/High)");
    }

    /// <summary>The live line printed when the generator has switched (preformatted: the control thread allocates nothing).</summary>
    public static Action<Pipeline.QualitySwitchNotice> QualityNotices(TextWriter o)
    {
        string[] lines =
        [
            // Unmistakable in a scrolling terminal (there is nothing on the screen to show it in fullscreen): capitals, and
            // what it costs. The latency figure is the 4K one measured for Phase 9c (D rises by High's extra generation time).
            "  quality     >>> NOW LOW (Q pressed) <<<  the generator switched between two frames, no load; Q switches back",
            "  quality     >>> NOW MEDIUM (Q pressed) <<<  the generator switched between two frames, no load; Q switches back",
            "  quality     >>> NOW HIGH (Q pressed) <<<  the generator switched between two frames, no load; the picture now runs about 5 ms later at 4K (High's longer generation time); Q switches back",
            "  quality     >>> Q: NOT SWITCHED <<<  the generator's other instance is unusable; it stays where it was",
        ];
        return n => o.WriteLine(lines[!n.Switched ? 3 : n.Commit.To switch { FrameGenQuality.Low => 0, FrameGenQuality.High => 2, _ => 1 }]);
    }

    /// <summary>
    /// The ceiling line (variable refresh): whether the slots were at the output's ceiling, the display running a whole
    /// refresh or more behind there (evenly: not a loss of variable refresh, and late shows not the pipeline's), and the
    /// recovery's left-out slots.
    /// </summary>
    internal static string CeilingLine(PlaybackReport r)
    {
        if (r.CeilingDecisions == 0)
        {
            return r.CeilingPacing
                ? "  ceiling     the slots were never at the output's ceiling (--ceiling-pacing on: inert)"
                : "  ceiling     the slots were never at the output's ceiling (--ceiling-pacing off, or no generator)";
        }

        var behind = r.BehindShows == 0
            ? "the display never ran a whole refresh behind there"
            : string.Create(Ic,
                $"the display ran about a whole refresh or more behind there (shows at least three quarters of a refresh late, 30 or more in a row; up to {r.BehindMaxRefreshes}; evenly, at its highest rate it cannot catch up by itself: not a loss of variable refresh) " +
                $"for {r.BehindShows} shows in {r.BehindEpisodes} run(s), the longest {r.BehindLongestNs / 1e9:0.0} s; those shows are among the display's late shows above");
        var recovery = !r.CeilingPacing
            ? "recovery off (--ceiling-pacing off)"
            : string.Create(Ic,
                $"recovery on: {r.CeilingRetimes} generated frame(s) left out after 30 late shows in a row so the display could catch up " +
                $"({r.CeilingRetimesRegained} brought the next show half a refresh earlier or on time, {r.CeilingRetimesNotRegained} did not; " +
                $"each counted above as a dropped slot)");
        return string.Create(Ic, $"  ceiling     the slots at the output's ceiling for {r.CeilingDecisions} decisions (whole run); {behind}; {recovery}");
    }

    /// <summary>
    /// The summary's line on the display following the presents under variable refresh: the episodes where it did not
    /// (first pixels on the refresh grid, sustained), the late shows inside and outside them, and the first few episodes.
    /// </summary>
    public static string VrrFollowLine(Pipeline.VrrFollowSummary f, long startNs)
    {
        var episodes = f.Log.Count == 0 ? "" : "; " + string.Join(", ", f.Log.Take(6).Select(e => string.Create(Ic,
            $"at {(startNs > 0 ? (e.StartNs - startNs) / 1e9 : e.StartNs / 1e9):0.0} s for {e.DurationNs / 1e9:0.0} s"))) + (f.Log.Count > 6 ? ", ..." : "");
        return string.Create(Ic,
            $"  follow      variable refresh {f.Describe()} (the display showing on its refresh grid instead of following the presents, " +
            $"over {VrrFollowDetectorWindow} shows); late shows inside those {f.LateInside}, outside {f.LateOutside} (whole run){episodes}");
    }

    private const int VrrFollowDetectorWindow = Pipeline.VrrFollowDetector.Window;

    /// <summary>
    /// The end of the <c>missed</c> line under variable refresh: the generated slots skipped, split into those the source's
    /// own long intervals made (it repeated or dropped a frame: the next frame cannot come sooner) and the rest (the next
    /// frame arrived late), and the ones shown late instead of skipped (<c>--late-generated-ms</c>).
    /// </summary>
    public static string VrrSkipText(PlaybackStatistics s, long lateGeneratedNs)
    {
        var shortSkips = s.VrrSkippedSlots - s.VrrSkippedInLongIntervals;
        var delayed = lateGeneratedNs <= 0
            ? "; generated slots shown late instead of skipped: off (--late-generated-ms 0)"
            : s.VrrDelayedSlots == 0
                ? string.Create(Ic, $"; generated slots shown late instead of skipped (up to {lateGeneratedNs / 1e6:0.###} ms) 0")
                : string.Create(Ic, $"; generated slots shown late instead of skipped (up to {lateGeneratedNs / 1e6:0.###} ms) {s.VrrDelayedSlots}, " +
                    $"by mean {s.VrrDelayedTotalNs / (double)s.VrrDelayedSlots / 1e6:0.000} ms, at most {s.VrrDelayedMaxNs / 1e6:0.000} (content moved to match)");
        return string.Create(Ic,
            $"; generated slots skipped (variable refresh: the next real frame came too late for them) {s.VrrSkippedSlots}: " +
            $"{s.VrrSkippedInLongIntervals} in intervals the source made longer (a repeated or dropped frame: the next one cannot come sooner), " +
            $"{shortSkips} in its normal intervals (the next frame arrived late){delayed}");
    }

    /// <summary>
    /// Under variable refresh, one stderr line when the display stops following the presents and one when it follows again,
    /// from preformatted text and a reused buffer (the control thread allocates nothing).
    /// </summary>
    public static Action<Pipeline.VrrFollowNotice> VrrFollowNotices(TextWriter error)
    {
        const string Lost = "fmp: variable refresh lost: the display shows on its refresh grid, not when the presents come (an overlay or another window on that output?); slots may come late until it follows again";
        const string Unfocused = "fmp: variable refresh lost: the window is not the active one (another window took the focus, on either output; KWin gives variable refresh only to the active fullscreen window): click the picture or Alt+Tab back to it; slots may come late until then";
        const string Behind1 = "fmp: the display is running a whole refresh behind at its ceiling (every frame shown evenly one refresh late: the slots come at its highest rate, so it cannot catch up by itself; not an overlay); --ceiling-pacing on leaves one generated frame out so it can";
        const string BehindMore = "fmp: the display is running two or more refreshes behind at its ceiling (every frame shown evenly that late: the slots come at its highest rate, so it cannot catch up by itself; not an overlay); --ceiling-pacing on leaves one generated frame out so it can";
        const string Back = "fmp: variable refresh back after ";
        var buffer = new char[160];
        return n =>
        {
            if (n.Lost)
            {
                error.WriteLine(n.Unfocused ? Unfocused : n.BehindAtCeiling == 1 ? Behind1 : n.BehindAtCeiling > 1 ? BehindMore : Lost);
                return;
            }

            Back.AsSpan().CopyTo(buffer);
            var at = Back.Length;
            _ = (n.DurationNs / 1e9).TryFormat(buffer.AsSpan(at), out var written, "0.0", Ic);
            at += written;
            " s (episode ".AsSpan().CopyTo(buffer.AsSpan(at));
            at += 12;
            _ = n.Episodes.TryFormat(buffer.AsSpan(at), out written, default, Ic);
            at += written;
            buffer[at++] = ')';
            error.WriteLine(buffer.AsSpan(0, at));
        };
    }

    /// <summary>One "Q ignored" line (the session's text is made once at start).</summary>
    public static Action<string> QualityIgnored(TextWriter o) => text =>
    {
        o.Write("  quality     ");
        o.WriteLine(text);
    };

    /// <summary>The summary's quality lines: every switch with what it cost and the slots around it, and each quality's figures.</summary>
    public static void PrintQualitySwitches(TextWriter o, PlaybackReport r)
    {
        var setup = r.Setup;
        if (setup.QualitySwitch is not { } pair)
        {
            return;
        }

        var s = r.Statistics;
        o.WriteLine(string.Create(Ic,
            $"  quality     {r.QualitySwitches.Count} switch(es) (Q pressed {r.QualityRequests}x; called off before they were made {r.QualityCancels}; " +
            $"made before D was ready {r.QualityGaveUp}); at the end {Pipeline.PlaybackSession.QualityName(r.QualityAtEnd)}; " +
            $"generation times of the quality before a switch dropped from the estimate {r.QualityStaleSamples}"));
        var windows = s.QualitySwitches;
        foreach (var n in r.QualitySwitches)
        {
            var c = n.Commit;
            var line = string.Create(Ic,
                $"              {n.SinceStartNs / 1e9:0.000} s {Pipeline.PlaybackSession.QualityName(c.From)} -> {Pipeline.PlaybackSession.QualityName(c.To)}: " +
                $"{(n.Switched ? "switched" : "REFUSED")} on the generator thread in {n.CallNs / 1e3:0.0} µs (no load); " +
                $"prepared {(c.CommittedNs - c.RequestedNs) / 1e6:0.0} ms (D {Ms(c.DelayAtRequestNs)} -> {Ms(c.DelayAtCommitNs)} ms{(c.GaveUpWaiting ? ", not ready: gave up waiting" : "")}); " +
                $"d estimate restarted at {Ms(c.EstimateNs)} ms");
            var window = -1;
            for (var i = 0; i < windows.Length; i++)
            {
                if (windows[i].Epoch == c.Epoch)
                {
                    window = i;
                }
            }

            if (window >= 0 && windows[window].BoundaryNs != long.MinValue)
            {
                var w = windows[window];
                line += string.Create(Ic,
                    $"; around it ({(w.BoundaryNs - w.PreparedAtNs) / 1e6:0} ms before + {Pipeline.PlaybackStatistics.SwitchWindowNs / 1e9:0} s after its first slot): " +
                    $"{w.Slots} slots ({w.Real} real, {w.Generated} generated), missed {w.Missed}, late {w.LateDecisions}, " +
                    $"generated slots skipped {w.VrrSkipped}, fallbacks {w.Fallbacks}");
            }
            else
            {
                line += window >= 0 ? "; no slot decided at the new quality before the end" : "; outside the measured window";
            }

            o.WriteLine(line);
        }

        foreach (var q in new[] { pair.Start, pair.Other })
        {
            var lat = s.RealFrameLatencyAt(q);
            var gen = s.GenerationTimesAt(q);
            var delay = s.PipelineDelaysAt(q);
            if (lat.Count == 0 && gen.Count == 0)
            {
                continue;
            }

            o.WriteLine(string.Create(Ic,
                $"  at {Pipeline.PlaybackSession.QualityName(q),-8} real frames shown {(lat.Count == 0 ? "-" : Ms(lat.Mean))} ms after their content time (p99 {(lat.Count == 0 ? "-" : Ms(lat.Percentile(0.99)))}, {lat.Count} frames, outside the switches); " +
                $"d p50 {Ms(gen.Percentile(0.5))} ms, p99 {Ms(gen.Percentile(0.99))} ms over {gen.Count}; D p50 {(delay.Count == 0 ? "-" : Ms(delay.Percentile(0.5)))} ms"));
        }
    }
}
