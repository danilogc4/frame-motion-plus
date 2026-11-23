using System.Globalization;
using System.Runtime.InteropServices;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Time;
using FrameMotionPlus.Vulkan;
using FrameMotionPlus.Vulkan.Diagnostics;
using FrameMotionPlus.Vulkan.Windowing;

namespace FrameMotionPlus.App.Bench;

/// <summary>What <c>fmp bench present</c> measures.</summary>
internal enum PresentProbeTest
{
    /// <summary>Presents into an empty queue at chosen times before a refresh: where the compositor's latch is.</summary>
    Latch,

    /// <summary>Presents at off-grid intervals: whether first pixels follow the presents (variable refresh) or the refresh grid.</summary>
    Vrr,

    /// <summary>Presents as fast as the in-flight cap allows: how regular the output's refreshes are.</summary>
    Cadence,
}

internal sealed record PresentProbeOptions
{
    public PresentProbeTest Test { get; init; } = PresentProbeTest.Latch;

    public SwapchainPresentMode Mode { get; init; } = SwapchainPresentMode.Fifo;

    public bool Fullscreen { get; init; }

    /// <summary>--output: the display fullscreen goes to.</summary>
    public string? Output { get; init; }

    public double DurationSeconds { get; init; } = 10;

    /// <summary>The longest run (a fullscreen probe covers its output for as long as it runs).</summary>
    public const double MaxDurationSeconds = 120;

    /// <summary>Vrr: the intervals presented in turn, ms (empty: 1.37 and 1.71 refreshes).</summary>
    public IReadOnlyList<double> IntervalsMs { get; init; } = [];

    public int WindowWidth { get; init; } = 640;

    public int WindowHeight { get; init; } = 360;

    public string? CsvPath { get; init; }

    public static PresentProbeOptions Parse(string[] args)
    {
        var o = new PresentProbeOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new Cli.UsageException($"'{args[i]}' needs a value.");
            switch (args[i])
            {
                case "--test":
                    o = o with
                    {
                        Test = Value() switch
                        {
                            "latch" => PresentProbeTest.Latch,
                            "vrr" => PresentProbeTest.Vrr,
                            "cadence" => PresentProbeTest.Cadence,
                            var v => throw new Cli.UsageException($"Unknown --test '{v}' (latch, vrr, cadence)."),
                        },
                    };
                    break;
                case "--mode":
                    o = o with
                    {
                        Mode = Value() switch
                        {
                            "fifo" => SwapchainPresentMode.Fifo,
                            "fifo-latest-ready" => SwapchainPresentMode.FifoLatestReady,
                            "mailbox" => SwapchainPresentMode.Mailbox,
                            "immediate" => SwapchainPresentMode.Immediate,
                            var v => throw new Cli.UsageException($"Unknown --mode '{v}' (fifo, fifo-latest-ready, mailbox, immediate)."),
                        },
                    };
                    break;
                case "--fullscreen":
                    o = o with { Fullscreen = true };
                    break;
                case "--output":
                    o = o with { Output = Value() };
                    break;
                case "--duration-s":
                    o = o with { DurationSeconds = double.Parse(Value(), CultureInfo.InvariantCulture) };
                    if (!(o.DurationSeconds > 0 && o.DurationSeconds <= MaxDurationSeconds))
                    {
                        throw new Cli.UsageException($"'--duration-s' for 'fmp bench present' must be more than 0 and at most {MaxDurationSeconds:0} s.");
                    }

                    break;
                case "--interval-ms":
                    o = o with { IntervalsMs = [.. Value().Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture))] };
                    break;
                case "--window":
                    {
                        var parts = Value().Split('x');
                        o = o with { WindowWidth = int.Parse(parts[0], CultureInfo.InvariantCulture), WindowHeight = int.Parse(parts[1], CultureInfo.InvariantCulture) };
                        break;
                    }

                case "--csv":
                    o = o with { CsvPath = Value() };
                    break;
                default:
                    throw new Cli.UsageException($"Unknown option '{args[i]}' for 'fmp bench present'.");
            }
        }

        return o;
    }
}

/// <summary>
/// <c>fmp bench present</c>: what the desktop compositor does with presents, measured from first-pixel times
/// (VK_EXT_present_timing) on a window or fullscreen, with a test pattern written by Vulkan alone (no CUDA). Phase 6's
/// measurements of the latch, the outputs' regularity and variable refresh come from here. A diagnostic: it paces its
/// presents itself, unlike the pipeline.
/// </summary>
internal sealed class PresentProbe
{
    private readonly PresentProbeOptions _o;
    private readonly TextWriter _out;
    private readonly List<Sample> _samples = [];
    private readonly Dictionary<long, int> _byId = [];
    private SdlWindow? _window;
    private VulkanPresenter? _presenter;
    private DiagnosticRingProducer? _producer;
    private long _nextId = 1;
    private long _inFlight;
    private string? _stopReason;

    /// <summary>Asks the run to end (a signal, the window closed, Escape): every loop checks it, the results so far are printed.</summary>
    public void Stop(string why) => Interlocked.CompareExchange(ref _stopReason, why, null);

    private bool Stopped => Volatile.Read(ref _stopReason) is not null;

    public PresentProbe(PresentProbeOptions options, TextWriter output)
    {
        _o = options;
        _out = output;
    }

    private sealed class Sample
    {
        public long Id;
        public long SubmitNs;
        public long TargetNs;
        public double OffsetNs;
        public long ShownNs;
        public bool Reported;
    }

    public int Run()
    {
        VulkanLayerPolicy.Isolated.ApplyToProcess();
        if (!VulkanLoader.TryInitialize(out var loaderError))
        {
            _out.WriteLine($"fmp bench present: needs the Vulkan loader: {loaderError}");
            return Cli.PatternCommand.ExitUnavailable;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) || !SdlWindow.IsLibraryAvailable())
        {
            _out.WriteLine("fmp bench present: needs a Wayland session and SDL3");
            return Cli.PatternCommand.ExitUnavailable;
        }

        // Ctrl+C (SIGINT) or SIGTERM ends the run after the current probe, with what it measured; a second one ends it at
        // once. (SDL would otherwise turn them into quit events, which nobody here reads.)
        void OnSignal(PosixSignalContext context)
        {
            if (!Stopped)
            {
                context.Cancel = true;
                Stop($"by {context.Signal}");
            }
        }

        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using var window = SdlWindow.Create(new SdlWindowOptions { Title = "fmp bench present", Width = _o.WindowWidth, Height = _o.WindowHeight, Output = _o.Output });
        _window = window;
        using var instance = VulkanInstance.Create(new VulkanInstanceOptions
        {
            ApplicationName = "fmp bench present",
            Layers = VulkanLayerPolicy.Isolated,
            RequiredExtensions = window.RequiredInstanceExtensions,
        });
        using var device = VulkanDevice.Create(instance, DeviceSelector.FirstDiscreteGpu);
        using var target = new SwapchainTarget(device, window, new SwapchainTargetOptions { PresentMode = _o.Mode });
        var presenter = new VulkanPresenter(device, target);
        _presenter = presenter;
        try
        {
            presenter.Configure(new PresenterConfig { Width = 320, Height = 180, Format = PixelFormat.Rgba8 });
            using (var export = presenter.Ring!.Export())
            {
                _producer = DiagnosticRingProducer.Create(instance, export);
            }

            _producer.FillAllStaging(TestPattern.MovingBar, 0, frameStep: 1);
            if (!presenter.ReportsPresentTimes)
            {
                _out.WriteLine($"fmp bench present: no first-pixel times on this surface ({target.PresentTimingUnavailableReason})");
                return Cli.CommandDispatcher.ExitCheckFailed;
            }

            // On an output (and fullscreen if asked), its rate known, the grid from a second of presents.
            Asap(0.4);
            if (_o.Fullscreen)
            {
                _ = window.SetFullscreen(true);
                Asap(1.5);
            }

            _samples.Clear();
            _byId.Clear();
            Asap(1.0);
            var rate = presenter.Timing.RefreshRate;
            var period = rate.PeriodNs;
            var phase = Phase(period);
            _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"fmp bench present: {_o.Test}, {target.PresentModeName}, {(_o.Fullscreen ? "fullscreen" : "windowed")} {window.PixelWidth}x{window.PixelHeight} px on '{window.CurrentDisplayName ?? "?"}', " +
                $"output {rate.Hz:0.###} Hz (period {period / 1e6:0.0000} ms); swapchain {target.SwapchainFormatName}; present timing {target.PresentTimingStage}"));
            _samples.Clear();
            _byId.Clear();
            var result = Stopped ? Cli.CommandDispatcher.ExitOk : _o.Test switch
            {
                PresentProbeTest.Latch => Latch(period, phase),
                PresentProbeTest.Vrr => Vrr(period, phase),
                _ => Cadence(period, phase),
            };
            WriteCsv(period, phase);
            if (Volatile.Read(ref _stopReason) is { } why)
            {
                _out.WriteLine($"  stopped     {why}: the results above cover the run up to then");
            }

            return result;
        }
        finally
        {
            if (_o.Fullscreen)
            {
                _ = window.SetFullscreen(false); // the desktop back before the teardown
            }

            presenter.Dispose();
            _producer?.Dispose();
        }
    }

    /// <summary>As fast as the presenter takes them (two in flight), for <paramref name="seconds"/>.</summary>
    private void Asap(double seconds)
    {
        var until = Monotonic.NowNs + (long)(seconds * 1e9);
        while (Monotonic.NowNs < until && !Stopped)
        {
            Pump();
            if (_presenter!.IsReadyForPresent())
            {
                Present(Monotonic.NowNs, 0);
            }
            else
            {
                Thread.Sleep(0);
            }
        }

        Drain(200 * Nanoseconds.PerMillisecond);
    }

    private void Pump()
    {
        if ((_window!.PumpEvents() & (WindowEvents.CloseRequested | WindowEvents.EscapePressed)) != 0)
        {
            Stop("by the user (window closed or Escape)");
        }

        while (_presenter!.TryGetFeedback(out var f))
        {
            if (_byId.TryGetValue(f.SlotIndex, out var i))
            {
                _samples[i].ShownNs = f.ActualTimeNs;
                _samples[i].Reported = true;
            }

            _inFlight--;
        }
    }

    private void Drain(long timeoutNs)
    {
        var until = Monotonic.NowNs + timeoutNs;
        while (_inFlight > 0 && Monotonic.NowNs < until)
        {
            Pump();
            Thread.Sleep(0);
        }
    }

    private bool Present(long targetNs, double offsetNs)
    {
        var n = _producer!.WriteStaged();
        var id = _nextId;
        var request = new PresentRequest { SlotIndex = id, Frame = _presenter!.Ring!.ConsumerFrame(n) };
        var submit = Monotonic.NowNs;
        if (!_presenter.TryPresent(request))
        {
            return false;
        }

        _nextId++;
        _inFlight++;
        _byId[id] = _samples.Count;
        _samples.Add(new Sample { Id = id, SubmitNs = submit, TargetNs = targetNs, OffsetNs = offsetNs });
        return true;
    }

    /// <summary>The refresh grid's phase from the shows so far (median residual of first pixels, mod the period).</summary>
    private double Phase(double period)
    {
        var shown = _samples.Where(s => s.ShownNs > 0).Select(s => s.ShownNs).ToList();
        if (shown.Count == 0)
        {
            return 0;
        }

        var reference = shown[^1];
        var residuals = shown.Select(t => PositiveMod(t - reference + (period / 2), period) - (period / 2)).Order().ToList();
        return reference + residuals[residuals.Count / 2];
    }

    private static double PositiveMod(double x, double m) => ((x % m) + m) % m;

    private static long NextRefresh(double phase, double period, long after) =>
        (long)Math.Round(phase + (Math.Ceiling((after - phase) / period) * period));

    /// <summary>
    /// A staircase on the hand-over time: each probe goes into an empty queue at R − x for a refresh R; shown at R means
    /// the latch is at most x before R, shown a refresh later that it is more than x. x steps down 0.25 ms after a probe
    /// that made its refresh and up after one that did not, so the probes gather around the latch.
    /// </summary>
    private int Latch(double period, double phase)
    {
        var x = Math.Min(0.9 * period, 7.5e6);
        var until = Monotonic.NowNs + (long)(_o.DurationSeconds * 1e9);
        var made = new List<double>();
        var missed = new List<double>();
        var early = 0;
        var untimed = 0;
        var waiter = DeadlineWaiter.Sleep;
        while (Monotonic.NowNs < until && !Stopped)
        {
            Drain(100 * Nanoseconds.PerMillisecond);
            var now = Monotonic.NowNs;
            var refresh = NextRefresh(phase, period, now + (long)x + 2_000_000);
            var at = refresh - (long)x;
            while (Monotonic.NowNs < at - 1_000_000 && !Stopped)
            {
                Pump();
                waiter.WaitUntil(Math.Min(at - 1_000_000, Monotonic.NowNs + 1_000_000));
            }

            if (Stopped)
            {
                break;
            }

            waiter.WaitUntil(at);
            if (!Present(refresh, x))
            {
                continue;
            }

            var sample = _samples[^1];
            Drain(100 * Nanoseconds.PerMillisecond);
            if (sample.ShownNs <= 0)
            {
                untimed++;
                continue;
            }

            var handover = x - (sample.SubmitNs - at);
            var refreshes = Math.Round((sample.ShownNs - refresh) / period);
            if (refreshes == 0)
            {
                made.Add(handover);
                x = Math.Max(250_000, x - 250_000);
            }
            else if (refreshes > 0)
            {
                missed.Add(handover);
                x = Math.Min(2 * period - 500_000, x + 250_000);
            }
            else
            {
                early++;
                x = Math.Max(250_000, x - 250_000);
            }

            // The grid follows the shows (a few ppm off the nominal period add up over a run).
            phase = sample.ShownNs - (refreshes * period);
        }

        made.Sort();
        missed.Sort();
        static string Q(List<double> v, double p) => v.Count == 0 ? "-" : (v[Math.Min(v.Count - 1, (int)(p * v.Count))] / 1e6).ToString("0.000", CultureInfo.InvariantCulture);
        _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  probes      {made.Count + missed.Count} timed ({made.Count} made their refresh, {missed.Count} shown a refresh later, {early} early, {untimed} untimed)"));
        _out.WriteLine($"  made at     hand-over before the refresh: min {Q(made, 0)}, p10 {Q(made, 0.1)}, p50 {Q(made, 0.5)} ms");
        _out.WriteLine($"  missed at   hand-over before the refresh: p50 {Q(missed, 0.5)}, p90 {Q(missed, 0.9)}, max {Q(missed, 1)} ms");
        _out.WriteLine(missed.Count == 0 || made.Count == 0
            ? "  latch       not bracketed (widen the run)"
            : string.Create(CultureInfo.InvariantCulture,
                $"  latch       between {missed[^1] / 1e6:0.000} ms (the latest hand-over that missed) and {made[0] / 1e6:0.000} ms (the earliest that made it) before the first pixel; " +
                $"a lead covering every miss seen: {(missed[^1] / 1e6) + 0.25:0.000} ms"));
        return Cli.CommandDispatcher.ExitOk;
    }

    /// <summary>Presents at the given intervals (in turn), each into an empty queue: with fixed refresh the first pixels land
    /// on the refresh grid; with variable refresh they follow the presents.</summary>
    private int Vrr(double period, double phase)
    {
        var intervals = _o.IntervalsMs.Count > 0 ? _o.IntervalsMs.Select(ms => ms * 1e6).ToArray() : [1.37 * period, 1.71 * period];
        var until = Monotonic.NowNs + (long)(_o.DurationSeconds * 1e9);
        var waiter = DeadlineWaiter.Sleep;
        var next = Monotonic.NowNs + 5_000_000;
        var k = 0;
        while (Monotonic.NowNs < until && !Stopped)
        {
            while (Monotonic.NowNs < next - 500_000 && !Stopped)
            {
                Pump();
                waiter.WaitUntil(Math.Min(next - 500_000, Monotonic.NowNs + 1_000_000));
            }

            if (Stopped)
            {
                break;
            }

            waiter.WaitUntil(next);
            if (_presenter!.IsReadyForPresent())
            {
                _ = Present(next, intervals[k % intervals.Length]);
            }

            next += (long)intervals[k++ % intervals.Length];
        }

        Drain(200 * Nanoseconds.PerMillisecond);
        var timed = _samples.Where(s => s.ShownNs > 0).ToList();
        var delays = timed.Select(s => (double)(s.ShownNs - s.SubmitNs)).Order().ToList();
        var onGrid = 0;
        var following = 0;
        var pairs = 0;
        var phaseResiduals = new List<double>();
        for (var i = 1; i < timed.Count; i++)
        {
            if (timed[i].Id != timed[i - 1].Id + 1)
            {
                continue;
            }

            pairs++;
            var shownInterval = timed[i].ShownNs - timed[i - 1].ShownNs;
            var submitted = timed[i].SubmitNs - timed[i - 1].SubmitNs;
            var periods = shownInterval / period;
            onGrid += Math.Abs(periods - Math.Round(periods)) * period < 250_000 ? 1 : 0;
            following += Math.Abs(shownInterval - submitted) < 250_000 ? 1 : 0;
            phaseResiduals.Add(Math.Abs(PositiveMod(timed[i].ShownNs - phase + (period / 2), period) - (period / 2)));
        }

        static string Q(List<double> v, double p) => v.Count == 0 ? "-" : (v[Math.Min(v.Count - 1, (int)(p * v.Count))] / 1e6).ToString("0.000", CultureInfo.InvariantCulture);
        _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  presents    {_samples.Count} at intervals {string.Join("/", intervals.Select(v => (v / 1e6).ToString("0.000", CultureInfo.InvariantCulture)))} ms; " +
            $"{timed.Count} timed, {_samples.Count - timed.Count} untimed"));
        _out.WriteLine($"  delay       first pixel − submit: p1 {Q(delays, 0.01)}, p50 {Q(delays, 0.5)}, p99 {Q(delays, 0.99)} ms");
        _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  intervals   of {pairs} consecutive shows: {onGrid} a whole number of refreshes apart (fixed grid, ±0.25 ms), {following} as far apart as their presents (±0.25 ms)"));
        _out.WriteLine(pairs == 0
            ? "  verdict     nothing to judge"
            : following > pairs * 0.8 && following > onGrid
                ? "  verdict     first pixels follow the presents: variable refresh is active"
                : onGrid > pairs * 0.8
                    ? "  verdict     first pixels stay on the refresh grid: fixed refresh"
                    : "  verdict     neither: mixed or irregular");
        return Cli.CommandDispatcher.ExitOk;
    }

    /// <summary>As fast as the in-flight cap allows: the output's own regularity.</summary>
    private int Cadence(double period, double phase)
    {
        Asap(_o.DurationSeconds);
        var timed = _samples.Where(s => s.ShownNs > 0).ToList();
        var intervals = new List<double>();
        var offGrid = 0;
        for (var i = 1; i < timed.Count; i++)
        {
            var d = timed[i].ShownNs - timed[i - 1].ShownNs;
            intervals.Add(d);
            var periods = d / period;
            offGrid += Math.Abs(periods - Math.Round(periods)) > 0.25 ? 1 : 0;
        }

        intervals.Sort();
        var multi = intervals.Count(d => d > 1.5 * period);
        static string Q(List<double> v, double p) => v.Count == 0 ? "-" : (v[Math.Min(v.Count - 1, (int)(p * v.Count))] / 1e6).ToString("0.000", CultureInfo.InvariantCulture);
        _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  presents    {_samples.Count}, {timed.Count} timed, {_samples.Count - timed.Count} untimed; {_samples.Count / _o.DurationSeconds:0.0}/s on a {1e9 / period:0.###} Hz output"));
        _out.WriteLine($"  intervals   p1 {Q(intervals, 0.01)}, p50 {Q(intervals, 0.5)}, p99 {Q(intervals, 0.99)}, max {Q(intervals, 1)} ms; {offGrid} of {intervals.Count} more than a quarter refresh off the grid; {multi} of two refreshes or more");
        _ = phase;
        return Cli.CommandDispatcher.ExitOk;
    }

    private void WriteCsv(double period, double phase)
    {
        if (_o.CsvPath is not { } path)
        {
            return;
        }

        using var w = new StreamWriter(path);
        w.WriteLine("id,submit_ns,target_ns,offset_ns,shown_ns,reported,period_ns,phase_ns");
        foreach (var s in _samples)
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{s.Id},{s.SubmitNs},{s.TargetNs},{s.OffsetNs:0},{s.ShownNs},{(s.Reported ? 1 : 0)},{period:0.###},{phase:0}"));
        }

        _out.WriteLine($"  csv         {_samples.Count} presents written to {path}");
    }
}
