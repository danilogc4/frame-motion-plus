using System.Globalization;
using FrameMotionPlus.App.Play;
using FrameMotionPlus.Audio;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Cuda.Kernels;
using FrameMotionPlus.Pipeline;
using FrameMotionPlus.Sources.V4l2;

namespace FrameMotionPlus.App.Capture;

/// <summary>What <c>fmp capture</c> prints: devices (--list), what it negotiated at the start, the capture's part of the summary.</summary>
internal static class CapturePrinter
{
    private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    public static void PrintDevice(TextWriter o, CaptureDeviceDescription d, bool isDefault)
    {
        o.WriteLine($"{d.Info.Path}: {d.Info.Card} ({d.Info.Driver}, {d.Info.BusInfo}){(isDefault ? "  [the default device]" : "")}");
        if (d.Current is { } current)
        {
            o.WriteLine($"  current     {current}");
        }

        if (d.Input is { } input)
        {
            o.WriteLine($"  input       {input}");
        }

        foreach (var (format, sizes) in d.Formats)
        {
            var kind = CaptureFormats.FromFourCc(format.FourCc);
            var note = format.Compressed
                ? "compressed: never used, a decode step adds latency"
                : kind == CapturePixelFormat.Unknown ? "not supported by this source" : kind == CapturePixelFormat.P010 ? "10-bit: HDR" : "8-bit";
            o.WriteLine($"  {format.Name,-4}        {format.Description} ({note})");
            foreach (var size in sizes)
            {
                var rates = size.Intervals.Count == 0 ? "no rates reported" : string.Join(", ", size.Intervals.Select(i => i.ToString()));
                o.WriteLine(string.Create(Ic, $"              {size.Width}x{size.Height}{(size.Stepwise ? " (range end)" : "")}: {rates} fps"));
            }
        }

        o.WriteLine("  presets     " + string.Join(", ", CapturePreset.All.Where(p => d.Modes.Any(m => m.Format == p.Format && m.Width == p.Width && m.Height == p.Height && Math.Abs(m.Fps - p.Fps) < 0.01)).Select(p => p.Name)) switch
        {
            "" => "none of fmp capture's presets",
            var list => list,
        });
    }

    /// <summary>The audio source that goes with a capture device (--list).</summary>
    public static void PrintDeviceAudio(TextWriter o, string device, AudioGraphSnapshot? graph, string? noAudio)
    {
        if (graph is null)
        {
            o.WriteLine($"  audio       unknown: {noAudio}");
            return;
        }

        o.WriteLine(AudioDevices.SourceForVideoDevice(graph, device) is { } source
            ? $"  audio       {source.Description ?? source.Name} ({source.Name}): fmp capture plays it, delayed to the picture"
            : "  audio       none (no sound card on the same USB device): fmp capture runs without sound unless --audio-source names one");
    }

    /// <summary>The sinks the sound can go to (--list).</summary>
    public static void PrintSinks(TextWriter o, AudioGraphSnapshot? graph, string? noAudio)
    {
        if (graph is null)
        {
            o.WriteLine($"audio sinks: unknown ({noAudio})");
            return;
        }

        o.WriteLine($"audio sinks (PipeWire {graph.LibraryVersion}): the sound goes to your selected default output, or to --audio-sink <name or part of one>, and to no other output device (a recorder may take a copy):");
        foreach (var sink in graph.Sinks)
        {
            o.WriteLine($"  {sink.Name}{(sink.IsDefault ? "  [your selected default output]" : "")}" +
                $"{(sink.Name == graph.DefaultSink && !sink.IsDefault ? "  [WirePlumber's fallback while the selected one is unavailable]" : "")}");
            if (sink.Description is { } description)
            {
                o.WriteLine($"              {description}");
            }
        }

        if (graph.Sinks.Count == 0)
        {
            o.WriteLine("  none");
        }
    }

    public static void PrintSetup(TextWriter o, CaptureOptions options, PlaybackOptions playback, PlaybackSetup s, string why, CudaContext cuda)
    {
        var c = s.Capture ?? throw new InvalidOperationException("Not a capture session.");
        var f = c.Format;
        o.WriteLine($"fmp capture: {c.Device.Path} - {c.Device.Card} ({c.Device.Driver}, {c.Device.BusInfo})");
        o.WriteLine($"  asked       {(options.Preset is { } preset ? $"--mode {preset.Name}: {preset.Note}" : options.Request.ToString())}");
        o.WriteLine($"  negotiated  {f.Describe()}{(f.Changes.Count == 0 ? " (as asked)" : "; the driver changed: " + string.Join("; ", f.Changes))}");
        o.WriteLine($"  colour      the device reports {c.Color.Reported} → {c.Color.Origin}");
        var bits = f.Format == CapturePixelFormat.P010 ? 10 : 8;
        o.WriteLine($"  signal      taken as {CaptureColorMapping.SignalDescription(c.Color, bits, f.Format != CapturePixelFormat.Yuyv)}, chroma {c.Color.Siting}-sited; " +
            $"frames {s.Source.Format} {(s.Source.Color.IsPq ? "PQ / BT.2020" : "SDR / BT.709")} (full range, the code values passed through: " +
            $"{(bits == 10 ? "10 bits kept from capture to the swapchain" : "8 bits")})");
        if (options.Color.Hdr == HdrMode.Follow && f.Format == CapturePixelFormat.P010)
        {
            o.WriteLine("              following the console: samples with 10 significant bits are HDR10, with 8 (how a PS5 is assumed to send SDR; not yet seen) SDR " +
                "(BT.709, the same range); in the window H switches by hand, A follows again");
        }
        else
        {
            o.WriteLine($"              fixed for the run (--hdr {options.Color.Hdr.ToString().ToLowerInvariant()}); in the window H switches between SDR and HDR10 by hand" +
                $"{(f.Format == CapturePixelFormat.P010 ? ", A follows the signal's depth" : "")}");
        }
        o.WriteLine(string.Create(Ic,
            $"  buffers     {CaptureFormatsText(c)}; source-change events {(c.SourceChangeEvents ? "subscribed" : "not offered by the driver")}" +
            $"{(c.Input is { } input ? $"; input {input}" : "")}"));
        if (c.RefusedBufferModes.Count > 0)
        {
            o.WriteLine($"              refused first: {string.Join("; ", c.RefusedBufferModes)}");
        }

        o.WriteLine(c.Cadence && c.CadenceOptions is { } cadence
            ? string.Create(Ic,
                $"  cadence     on: {CadenceGrid.CellSize}x{CadenceGrid.CellSize}-sample cells, {CadenceGrid.CellSize * CadenceGrid.TileCells}x{CadenceGrid.CellSize * CadenceGrid.TileCells}-pixel tiles; " +
                $"repeat at or below / new at or above: SDR {cadence.Sdr.Low:0.0000} / {cadence.Sdr.High:0.0000}, PQ {cadence.Pq.Low:0.0000} / {cadence.Pq.High:0.0000} of full scale " +
                $"({(s.Source.Color.IsHdr ? "PQ" : "SDR")} thresholds in use)")
            : "  cadence     off (--no-cadence): every captured frame counts as new");
        o.WriteLine($"  generator   {s.GeneratorName}: {why}{(s.GeneratorMayBlock ? "; its Enqueue blocks for the generation, so it runs on the generator thread" : "")}");
        if (s.VfgLoad is { } load)
        {
            o.WriteLine(string.Create(Ic,
                $"              {s.VfxSdk}; loaded in {load.LoadNs / 1e6:0.0} ms, warmed up in {load.WarmupNs / 1e6:0.0} ms, {load.DeviceBytes / 1048576.0:0} MiB; " +
                $"{Play.PlayPrinter.DescribeShotChangeDetection(playback.ShotChangeDetection, capture: true)}; output written straight into the ring slots"));
        }

        Play.PlayPrinter.PrintQualitySetup(o, s, playback);
        Play.PlayPrinter.PrintKeys(o, playback, capture: true);
        o.WriteLine($"  device      {s.DeviceName}, Vulkan driver {s.VulkanDriver}; CUDA context scheduling {options.CudaSync}");
        o.WriteLine($"  display     {s.Display}; transform {s.Transform}");
        o.WriteLine($"  output      {s.Output}");
        o.WriteLine($"  scaling     {s.Scaling}");
        o.WriteLine(string.Create(Ic,
            $"  feedback    {s.FeedbackMode}; presents in flight capped at {(s.PresentsInFlightCap == 0 ? "nothing (cannot cap)" : s.PresentsInFlightCap.ToString(Ic))}"));
        o.WriteLine(string.Create(Ic,
            $"  ring        {s.RingSlots} slots, pitch {s.RingPitchBytes} B; bridge budget {s.OperationsPerWrite} CUDA operations per write, lead at most {s.MaxLead}"));
        var rate = s.Source.NominalRate;
        var cap = options.OutputHz is { } hz && rate.IsValid
            ? string.Create(Ic, $"--output-hz {hz:0.###} → cap {Math.Clamp((int)Math.Round(hz / rate.Hz), 1, 8)}")
            : $"cap {playback.Multiplier}";
        o.WriteLine(string.Create(Ic,
            $"  rates       signal {(rate.IsValid ? rate.Hz.ToString("0.###", Ic) + " fps" : "unknown")} (the game's own rate is what the cadence detector finds), multiplier {cap}; " +
            $"warm-up {playback.WarmupNs / 1e9:0.#} s{(playback.DurationNs > 0 ? $", duration {playback.DurationNs / 1e9:0.###} s" : ", until stopped")}"));
        if (f.Format == CapturePixelFormat.P010 && f.Height >= 2160 && f.Interval.Fps < 31)
        {
            o.WriteLine("  note        2160p P010 at 29.97 or 30: the card itself takes every other frame of the console's 59.94 Hz; a game running at 60 loses every other frame " +
                "before the app sees it. For 60 fps games use --mode 1440p60-hdr.");
        }

        _ = cuda;
    }

    /// <summary>The capture's lines of the summary.</summary>
    public static void PrintCapture(TextWriter o, PlaybackReport r, CaptureSummary c)
    {
        var k = c.Counters;
        o.WriteLine(string.Create(Ic,
            $"  capture     {k.Dequeued} dequeued (stale, skipped as newest won: {k.SkippedStale}; dropped by the driver: {k.DriverDropped}; error-flagged {k.ErrorFrames}, " +
            $"short {k.ShortFrames}); {k.Captured} captured → {k.Unique} unique, {k.Repeats} repeats dropped, {k.DroppedBehind} dropped as the pipeline was behind, " +
            $"{k.Published} published ({k.Primed} of them repeats published to start on a still picture), {r.FramesRead} read by the pacing thread"));
        o.WriteLine(string.Create(Ic,
            $"              detected game rate {(k.DetectedFps > 0 ? k.DetectedFps.ToString("0.00", Ic) + " fps" : "not yet known")} (signal interval {Ms(c.SignalIntervalNs)} ms); " +
            $"holds {k.Holds} (gaps over twice the game's interval); black frames {k.BlackFrames}; no-signal episodes {k.NoSignalEpisodes}; " +
            $"state at the end {c.State}; timestamps repaired {k.TimestampRepairs}, not CLOCK_MONOTONIC {k.NonMonotonicTimestamps}"));
        o.WriteLine(string.Create(Ic,
            $"  capture t   timestamp intervals mean {Ms(c.Intervals.Mean)} ms, sd {Us(c.Intervals.StdDev)} µs, min {Ms(c.Intervals.Min)}, max {Ms(c.Intervals.Max)} ms over {c.Intervals.Count}; " +
            $"{c.DequeueLag.Ms("dequeue − timestamp (USB delivery)")}; {c.CaptureToAvailable.Ms("timestamp → frame available")}; {c.UploadWait.Ms("dequeue → upload judged")}"));
        o.WriteLine(string.Create(Ic,
            $"  capture gpu {c.UploadGpu.Ms("upload")}; {c.CadenceGpu.Ms("cadence metric")}; {c.ConvertGpu.Ms("convert")}"));
        o.WriteLine(string.Create(Ic,
            $"  cadence     metric (millionths of full scale) of new pictures: min {c.UniqueMetric.Min}, p50 {c.UniqueMetric.P50} over {c.UniqueMetric.Count}; " +
            $"of repeats: p50 {c.RepeatMetric.P50}, p99 {c.RepeatMetric.P99}, max {c.RepeatMetric.Max} over {c.RepeatMetric.Count}; " +
            $"unique-frame intervals p50 {Ms(c.UniqueIntervals.P50)}, p99 {Ms(c.UniqueIntervals.P99)}, max {Ms(c.UniqueIntervals.Max)} ms"));
        if (c.ThreadReport is { } thread)
        {
            o.WriteLine($"  capture th  {thread}");
        }

        o.WriteLine(RateLine(c));
        PrintColor(o, r, c);
    }

    /// <summary>
    /// The summary's capture-rate line: the rate asked for and set (exact), the repeats one longer than the content's steady
    /// cadence and the frames left out, their period when periodic, and what that says about the source.
    /// </summary>
    public static string RateLine(CaptureSummary c)
    {
        var b = c.Beat;
        var rates = string.Create(Ic, $"  capture fps asked {Exact(c.AskedRate)}, set {Exact(c.NegotiatedRate)}; ");
        var periodic = Math.Min(b.PeriodicEvents, b.ExtraRepeats + b.Skips);
        var events = string.Create(Ic, $"extra repeats (one more than the content's steady cadence) {b.ExtraRepeats}, frames left out {b.Skips}") +
            (b.ExtraRepeats + b.Skips == 0 ? ""
                : string.Create(Ic, $" ({periodic} of them in a periodic beat, the card's or the clocks'; {b.ExtraRepeats + b.Skips - periodic} not: the content's own, a game's repeated frame or the start of a still)"));
        if (!b.IsPeriodic)
        {
            return rates + events + "; no periodic beat at the end (the card's rate and the source's agree, or the content hides it)";
        }

        var what = b.Kind == BeatKind.Repeats ? "a repeat" : "a frame left out";
        var every = b.PeriodNs >= 120 * Nanoseconds.PerSecond
            ? string.Create(Ic, $"{b.PeriodNs / 60e9:0.0} min")
            : string.Create(Ic, $"{b.PeriodNs / 1e9:0.00} s");
        var line = rates + events + string.Create(Ic, $"; periodic: {what} every {every} ({b.AgreeingEvents} in a row agree), the source about {b.ImpliedSourceHz:0.0000} Hz");
        return line + "; " + Advice(b, c.OfferedRates);
    }

    /// <summary>What to do about a periodic beat: the --capture-fps that matches the source, if the device offers it.</summary>
    public static string Advice(RepeatBeat b, IReadOnlyList<FrameRate> offered)
    {
        if (b.IsCardClock)
        {
            var ppm = Nanoseconds.PerSecond / (double)b.PeriodNs / b.CaptureRate.Hz * 1e6;
            var way = b.Kind == BeatKind.Repeats ? "faster" : "slower";
            return string.Create(Ic,
                $"the card's own clock, about {ppm:0} ppm {way} than the source's (it free-runs on its crystal, not locked to its input): expected at the source's own rate, nothing to change");
        }

        if (!b.Suggested.IsValid)
        {
            return "it matches no whole or NTSC rate next to the capture's (something in the content, not the clocks?)";
        }

        var text = RepeatBeat.RateText(b.Suggested);
        return offered.Contains(b.Suggested)
            ? $"the source looks like {text} Hz: use --capture-fps {text}"
            : $"the source looks like {text} Hz, which the card does not offer at this size and format: the beat stays";
    }

    private static string Exact(FrameRate rate) => !rate.IsValid ? "the highest offered"
        : rate.Denominator == 1 ? string.Create(Ic, $"{rate.Numerator} fps")
        : string.Create(Ic, $"{rate.Hz:0.###} fps ({rate.Numerator}/{rate.Denominator}, {1e3 / rate.Hz:0.0000} ms)");

    /// <summary>
    /// fmp capture: one stderr line when the capture's repeats show a periodic beat, from text made at start (the control
    /// thread allocates nothing).
    /// </summary>
    public static Action<RepeatBeat> BeatNotices(TextWriter error, FrameRate asked, IReadOnlyList<FrameRate> offered, bool forced)
    {
        var sibling = asked.Denominator == 1 ? new FrameRate(asked.Numerator * 1000, 1001)
            : asked.Denominator == 1001 && asked.Numerator % 1000 == 0 ? new FrameRate(asked.Numerator / 1000, 1) : default;
        var askedText = asked.IsValid ? RepeatBeat.RateText(asked) : "?";
        var match = sibling.IsValid
            ? $"fmp: capture: the card {(sibling.Hz < asked.Hz ? "repeats a frame" : "leaves a frame out")} periodically at {askedText} fps: " +
              Advice(new RepeatBeat { Suggested = sibling }, offered) + (forced ? " (--capture-fps chose " + askedText + ")" : "")
            : "";
        // Only advice to change the rate goes to stderr: the card's own slow beat at the source's rate (its clock free-runs a
        // few tens of ppm from its input) and a beat that matches no rate (the content's) are in the summary alone.
        return b =>
        {
            if (b.Suggested.IsValid && b.Suggested == sibling && !b.IsCardClock)
            {
                error.WriteLine(match);
            }
        };
    }

    /// <summary>The colour lines of the summary: what the frames were at the end, the switches, the output.</summary>
    public static void PrintColor(TextWriter o, PlaybackReport r, CaptureSummary c)
    {
        var depths = c.IsP010
            ? string.Create(Ic, $"; sample depth of the frames: 10-bit {c.Depths.TenBit}, 8-bit {c.Depths.EightBit}, could not tell (black or flat) {c.Depths.Inconclusive}")
            : "";
        o.WriteLine(string.Create(Ic,
            $"  colour      {(c.HdrAtEnd ? "HDR10" : "SDR")} at the end ({(c.FollowingAtEnd ? "following the signal" : "fixed or chosen by hand")}); " +
            $"{c.ColorSwitches} switch{(c.ColorSwitches == 1 ? "" : "es")}{depths}"));
        if (c.SwitchLog.Count > 0)
        {
            var list = string.Join(", ", c.SwitchLog.Select(w => string.Create(Ic,
                $"{(c.FirstTimestampNs > 0 ? (w.TimestampNs - c.FirstTimestampNs) / 1e9 : 0):0.00} s → {(w.ToHdr ? "HDR10" : "SDR")} " +
                $"({(w.Cause == ColorSwitchCause.Manual ? "by hand" : $"followed: {w.Depth.LowBitSamples} of {w.Depth.Samples} samples with low bits, {w.Depth.DistinctCodes} codes")})")));
            var change = r.LastOutputColorChange;
            o.WriteLine(string.Create(Ic,
                $"              {list}{(c.ColorSwitches > c.SwitchLog.Count ? " ..." : "")}; the output rebuilt {r.PresenterStats.OutputColorChanges}x, each at its first frame in the new " +
                $"colour once the frames in flight were done: waits at most {Ms(r.PresenterStats.WorstOutputChangeWaitNs)} ms ({r.PresenterStats.OutputChangeRefusals} request(s) " +
                $"refused meanwhile in all), rebuilds at most {Ms(r.PresenterStats.WorstOutputChangeRebuildNs)} ms (the last: {Ms(change.WaitNs)} and {Ms(change.RebuildNs)} ms)"));
        }

        if (r.OutputAtEnd is { } output)
        {
            o.WriteLine($"  output      at the end: {output}");
        }

        if (r.ScalingAtEnd is { } scaling)
        {
            o.WriteLine($"  scaling     at the end: {scaling}");
        }
    }

    /// <summary>The latency line of a capture run: measured from the capture timestamp, and D against the latency model.</summary>
    public static void PrintLatency(TextWriter o, PlaybackReport r, LatencyEstimate model)
    {
        var s = r.Statistics;
        var d = r.Diagnostics;
        var lat = s.RealFrameLatency;
        var added = d.HoldNs + d.LeadNs + d.AlignmentNs;
        o.WriteLine(string.Create(Ic,
            $"  added       by frame generation (the scheduler's D less its arrival lag): hold {Ms(d.HoldNs)} + lead {Ms(d.LeadNs)} + alignment {Ms(d.AlignmentNs)} = " +
            $"{Ms(added)} ms; latency model ΔL = d + (M−1)/M·T = {Ms(model.GenerationTimeNs)} + {Ms(model.HoldNs)} = {Ms(model.AddedLatencyNs)} ms " +
            $"(M {model.Multiplier}, T {Ms(model.SourceIntervalNs)}); difference {Ms(added - model.AddedLatencyNs)} ms (the lead's wake and margin, the stride's alignment)"));
        var display = r.RefreshModeUsed == Pipeline.RefreshMode.Vrr
            ? string.Create(Ic, $"the display's delay from hand-over to first pixel (variable refresh: no present lead, no rounding to a refresh; measured {Ms(s.SubmitToShown.Count > 0 ? s.SubmitToShown.Percentile(0.5) : s.PresentLeads.Percentile(0.5))}, p50 of every present's hand-over to its first pixel)")
            : string.Create(Ic, $"present lead {Ms(s.PresentLeads.Percentile(0.5))} + the stride's rounding (p50 {Ms(s.StrideDelay.Count > 0 ? s.StrideDelay.Percentile(0.5) : 0)})");
        o.WriteLine(lat.Count == 0
            ? "  latency     no real frame shown in the measured window"
            : string.Create(Ic,
                $"  latency     a real frame is shown {Ms(lat.Mean)} ms after its capture timestamp (p99 {Ms(lat.Percentile(0.99))}): arrival lag {Ms(d.ArrivalLagNs)} " +
                $"(USB delivery, upload, conversion) + the generation's {Ms(added)} + {display}; " +
                $"before the timestamp, the card's own HDMI-to-USB latency is not measurable here (photodiode or camera)"));
    }

    private static string CaptureFormatsText(CaptureSetup c) => string.Create(Ic,
        $"{c.Buffers} x {CaptureBuffersName(c.BufferMode)}, {Environment.SystemPageSize}-byte aligned");

    private static string CaptureBuffersName(CaptureBufferMode mode) => mode switch
    {
        CaptureBufferMode.UserPtrPinned => "USERPTR into CUDA pinned memory (the driver writes where the GPU copies from)",
        CaptureBufferMode.UserPtrRegistered => "USERPTR into anonymous memory registered with CUDA",
        CaptureBufferMode.MmapRegistered => "MMAP driver buffers registered with CUDA",
        _ => mode.ToString(),
    };

    private static string Ms(double ns) => (ns / 1e6).ToString("0.000", Ic);

    private static string Us(double ns) => (ns / 1e3).ToString("0.0", Ic);
}
