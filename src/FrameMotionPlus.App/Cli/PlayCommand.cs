using System.Globalization;
using System.Runtime.InteropServices;
using FrameMotionPlus.App.Play;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Pipeline;
using FrameMotionPlus.Sources.File;
using FrameMotionPlus.Vfx;

namespace FrameMotionPlus.App.Cli;

/// <summary>
/// <c>fmp play &lt;file&gt;</c>: a video file decoded (FFmpeg/NVDEC), interpolated (VFG, blend, repeat or none) and
/// presented in real time, paced by the display-time scheduler (<see cref="PlaybackSession"/>). Prints what it resolved
/// at the start and a summary with a verdict at the end. Exit 0 when the run is clean (and, with <c>--gate</c>, the gate
/// passes); 1 on a failure, a validation message, a hot-thread allocation or a failed gate; 69 with one line when the
/// GPU, FFmpeg, a display or (for an explicit <c>--generator vfg</c>) the VFX SDK is missing. Ctrl+C, SIGTERM, closing
/// the window or Escape stop it cleanly with its summary; a second signal ends the process at once.
/// </summary>
internal static class PlayCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var loaded = UserSettings.LoadFor(args, out args);
        var applied = UserSettings.Apply(SettingsTarget.Play, args, loaded);
        var options = PlayOptions.Parse(applied.Args);
        if (options.CsvPath is { } csv)
        {
            VfgBenchCommand.CheckWritable(csv);
        }

        var settings = loaded.Exists ? loaded.Settings : null;
        error = TextWriter.Synchronized(error);

        // Signals first: a stop asked for at any point of the start-up (loading, opening the device, the pre-roll) is kept
        // and honoured as soon as there is a session to stop.
        var signals = 0;
        PlaybackSession? live = null;
        var stopAsked = false;
        void OnSignal(PosixSignalContext context)
        {
            if (Interlocked.Increment(ref signals) == 1)
            {
                context.Cancel = true; // the first one: stop cleanly
                Volatile.Write(ref stopAsked, true);
                Volatile.Read(ref live)?.RequestStop(StopReason.User);
                error.WriteLine($"fmp: play stopping on {context.Signal} (again to end at once)");
            }
        }

        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        if (!CudaDriver.TryInitialize(out var reason) || CudaDriver.DeviceCount == 0)
        {
            error.WriteLine($"fmp: play needs an NVIDIA GPU with the CUDA driver: {reason ?? "the CUDA driver reports no device"}");
            return PatternCommand.ExitUnavailable;
        }

        if (!FileFrameSource.IsAvailable(out var ffmpeg))
        {
            error.WriteLine($"fmp: play needs the FFmpeg 9 shared libraries: {ffmpeg}");
            return PatternCommand.ExitUnavailable;
        }

        if (!File.Exists(options.Path))
        {
            error.WriteLine($"fmp: play: no such file: {options.Path}");
            return CommandDispatcher.ExitCheckFailed;
        }

        var (generator, why) = ResolveGenerator(options, settings?.Generator.VfxSdkDirectory);
        if (generator is null)
        {
            error.WriteLine($"fmp: play {why}");
            return PatternCommand.ExitUnavailable;
        }

        var playback = new PlaybackOptions
        {
            Path = options.Path,
            Generator = generator.Value,
            Quality = options.Quality,
            QualitySwitch = options.QualitySwitch,
            ShotChangeDetection = options.ShotChangeDetection,
            Multiplier = options.Multiplier,
            OutputHz = options.OutputHz,
            Loop = options.Loop,
            DurationNs = (long)(options.DurationS * Nanoseconds.PerSecond),
            MaxSlots = options.Frames,
            Display = options.Offscreen ? DisplayKind.Offscreen : DisplayKind.Window,
            Clock = options.Offscreen && !options.Realtime ? PlaybackClock.Virtual : PlaybackClock.Real,
            OffscreenRate = options.OffscreenRate,
            OffscreenWidth = options.OffscreenWidth,
            OffscreenHeight = options.OffscreenHeight,
            WindowWidth = options.WindowWidth,
            WindowHeight = options.WindowHeight,
            Fullscreen = options.Fullscreen,
            Refresh = options.Refresh,
            Validate = options.Validate,
            WarmupNs = (long)(options.WarmupS * Nanoseconds.PerSecond),
            PresentLeadNs = options.PresentLeadMs < 0 ? -1 : (long)(options.PresentLeadMs * Nanoseconds.PerMillisecond),
            LatchLeadNs = (long)(options.LatchMs * Nanoseconds.PerMillisecond),
            LateGeneratedNs = (long)Math.Round(options.LateGeneratedMs * Nanoseconds.PerMillisecond),
            CeilingPacing = options.CeilingPacing,
            VfxSdkDirectory = options.VfxSdk,
            Settings = settings,
            CsvPath = options.CsvPath,
        };

        // The scheduling flag must be set before the primary context is first made active in this process.
        var cuda = CudaContext.RetainPrimary(CudaDriver.GetDevice(0), options.CudaSync);
        var releaseContext = true;
        try
        {
            Func<GeneratorHandle, CudaStream, Cuda.Kernels.ImageKernels, GeneratorHandle>? fault;
            try
            {
                fault = PlaybackFaults.Parse(Environment.GetEnvironmentVariable("FMP_PLAY_FAULT"));
            }
            catch (FormatException e)
            {
                throw new UsageException(e.Message);
            }

            PlaybackReport report;
            var hooks = new PlaybackHooks
            {
                WrapGenerator = fault,
                OnQualitySwitch = PlayPrinter.QualityNotices(output),
                OnQualityNotice = PlayPrinter.QualityIgnored(output),
                OnVrrFollow = PlayPrinter.VrrFollowNotices(error),
            };
            using (var session = new PlaybackSession(playback, cuda, hooks, validationEcho: options.Validate ? error : null))
            {
                Volatile.Write(ref live, session);
                if (Volatile.Read(ref stopAsked))
                {
                    session.RequestStop(StopReason.User);
                }

                PlaybackSetup setup;
                try
                {
                    setup = session.Open();
                }
                catch (PlaybackUnavailableException e)
                {
                    error.WriteLine($"fmp: play {e.Message}");
                    return PatternCommand.ExitUnavailable;
                }
                catch (FileSourceException e)
                {
                    error.WriteLine($"fmp: play: {e.Message}");
                    return CommandDispatcher.ExitCheckFailed;
                }
                catch (FFmpegUnavailableException e)
                {
                    error.WriteLine($"fmp: play needs the FFmpeg 9 shared libraries: {e.Message}");
                    return PatternCommand.ExitUnavailable;
                }

                PlayPrinter.PrintSetup(output, options, playback, setup, why, cuda);
                output.WriteLine(UserSettings.Describe(loaded, applied));
                output.Flush();
                using var qualitySwitcher = QualitySwitchEvery(session, error, "play");
                report = session.Run();
            }

            Volatile.Write(ref live, null);
            var verdict = PlayPrinter.PrintSummary(output, options, report);
            var exit = verdict ? CommandDispatcher.ExitOk : CommandDispatcher.ExitCheckFailed;
            if (report.GpuAbandoned)
            {
                // GPU work that never finished is still queued: releasing the context (and the CUDA driver's own clean-up at
                // a normal exit) would wait for it. The summary is out; end the process here, the kernel reclaims the GPU.
                releaseContext = false;
                output.Flush();
                error.Flush();
                ExitNow?.Invoke(exit);
            }

            return exit;
        }
        finally
        {
            if (releaseContext)
            {
                cuda.Dispose();
            }
        }
    }

    /// <summary>
    /// The generator to run, and why: an explicit choice as asked (VFG only with the SDK: otherwise null and a one-line
    /// reason, exit 69); auto: VFG when the VFX SDK loads, else blend with the reason.
    /// </summary>
    internal static (GeneratorKind? Kind, string Why) ResolveGenerator(PlayOptions options, string? settingsSdk)
    {
        switch (options.Generator)
        {
            case GeneratorChoice.Blend:
                return (GeneratorKind.Blend, "asked for (--generator blend)");
            case GeneratorChoice.Repeat:
                return (GeneratorKind.Repeat, "asked for (--generator repeat)");
            case GeneratorChoice.None:
                return (GeneratorKind.None, "asked for (--generator none): real frames only");
            case GeneratorChoice.Vfg:
                return VfxSdk.TryLoad(out var reason, options.VfxSdk, settingsSdk)
                    ? (GeneratorKind.Vfg, options.Gate ? "the gate's generator" : "asked for (--generator vfg)")
                    : (null, $"needs the NVIDIA VFX SDK with Video Frame Generation for --generator vfg: {reason}");
            default:
                return VfxSdk.TryLoad(out var why, options.VfxSdk, settingsSdk)
                    ? (GeneratorKind.Vfg, "the default: the VFX SDK was found")
                    : (GeneratorKind.Blend, $"VFG is the default but the NVIDIA VFX SDK is not available ({why}); falling back to blend");
        }
    }

    /// <summary>Ends the process at once with the exit code (null in-process tests: they get the code returned instead).</summary>
    internal static Action<int>? ExitNow { get; set; } = Platform.Runtime.ImmediateExit.Now;

    internal static string Ms(double ns) => (ns / 1e6).ToString("0.000", CultureInfo.InvariantCulture);
    /// <summary>
    /// Diagnostics: FMP_QUALITY_SWITCH_EVERY_S=&lt;s&gt; asks for the other generator quality every s seconds, as pressing Q in
    /// the window does (to exercise the switch with no one at the keyboard; fmp play and fmp capture). Null when not set.
    /// </summary>
    internal static Timer? QualitySwitchEvery(PlaybackSession session, TextWriter error, string command)
    {
        var text = Environment.GetEnvironmentVariable("FMP_QUALITY_SWITCH_EVERY_S");
        if (text is null)
        {
            return null;
        }

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds < 0.05 || seconds > 3600)
        {
            error.WriteLine($"fmp: {command}: FMP_QUALITY_SWITCH_EVERY_S expects seconds (0.05 to 3600), got '{text}'; ignored");
            return null;
        }

        var period = TimeSpan.FromSeconds(seconds);
        return new Timer(_ => session.RequestQualityToggle(), null, period, period);
    }
}
