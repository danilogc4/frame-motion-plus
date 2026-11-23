using System.Runtime.InteropServices;
using FrameMotionPlus.App.Capture;
using FrameMotionPlus.App.Play;
using FrameMotionPlus.Audio;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Pipeline;
using FrameMotionPlus.Sources.V4l2;

namespace FrameMotionPlus.App.Cli;

/// <summary>
/// <c>fmp capture [device]</c>: a capture card (V4L2) captured live, the cadence detector keeping only new pictures,
/// interpolated (VFG by default) and presented, paced by the display-time scheduler: the same pipeline as <c>fmp play</c>
/// with a live source. <c>--list</c> shows the devices and what they offer. Prints what it negotiated and resolved at the
/// start and a summary with capture statistics and a verdict at the end. Exit 0 when the run is clean; 1 on a failure; 2 on
/// a usage error or a mode the device does not offer; 69 with one line when capture, the device, the GPU or a display is
/// missing. Ctrl+C, SIGTERM, closing the window or Escape stop it cleanly; the device is released on every way out. The
/// card's sound plays too (<see cref="CaptureAudio"/>), delayed to the picture by the measured video latency; when it
/// cannot, the video runs alone and one line says why.
/// </summary>
internal static class CaptureCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        // Saved settings (the default file, or --settings): added as the options they stand for, the command line winning.
        var loaded = UserSettings.LoadFor(args, out args);
        var applied = UserSettings.Apply(SettingsTarget.Capture, args, loaded);
        CaptureOptions options;
        try
        {
            options = CaptureOptions.Parse(applied.Args);
        }
        catch (UsageException e) when (UserSettings.Blame(loaded, applied) is { } saved)
        {
            throw new UsageException($"{e.Message} ({saved})", e);
        }

        // A failure below can come from a saved value (a device or mode that does not work here): say which ones were used.
        void Blame()
        {
            if (UserSettings.Blame(loaded, applied) is { } saved)
            {
                error.WriteLine($"fmp: capture: {saved}");
            }
        }

        if (!CaptureDevices.IsAvailable(out var unavailable))
        {
            error.WriteLine($"fmp: {unavailable}");
            return PatternCommand.ExitUnavailable;
        }

        if (options.List)
        {
            return List(options, output, error);
        }

        if (options.CsvPath is { } csv)
        {
            VfgBenchCommand.CheckWritable(csv);
        }

        if (options.FramesCsvPath is { } framesCsv)
        {
            VfgBenchCommand.CheckWritable(framesCsv);
        }

        var settings = loaded.Exists ? loaded.Settings : null;
        error = TextWriter.Synchronized(error);
        var signals = 0;
        PlaybackSession? live = null;
        var stopAsked = false;
        void OnSignal(PosixSignalContext context)
        {
            if (Interlocked.Increment(ref signals) == 1)
            {
                context.Cancel = true;
                Volatile.Write(ref stopAsked, true);
                Volatile.Read(ref live)?.RequestStop(StopReason.User);
                error.WriteLine($"fmp: capture stopping on {context.Signal} (again to end at once)");
            }
        }

        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        if (!CudaDriver.TryInitialize(out var reason) || CudaDriver.DeviceCount == 0)
        {
            error.WriteLine($"fmp: capture needs an NVIDIA GPU with the CUDA driver: {reason ?? "the CUDA driver reports no device"}");
            return PatternCommand.ExitUnavailable;
        }

        var device = options.Device ?? CaptureDevices.Default(CaptureDevices.List());
        if (device is null)
        {
            error.WriteLine("fmp: capture: no V4L2 capture device found (is the capture card plugged in? fmp capture --list)");
            return PatternCommand.ExitUnavailable;
        }

        if (!File.Exists(device))
        {
            error.WriteLine($"fmp: capture: no such capture device {device} (fmp capture --list shows the devices)");
            Blame();
            return PatternCommand.ExitUnavailable;
        }

        var (generator, why) = PlayCommand.ResolveGenerator(options.AsPlayOptions(device), settings?.Generator.VfxSdkDirectory);
        if (generator is null)
        {
            error.WriteLine($"fmp: capture {why}");
            Blame();
            return PatternCommand.ExitUnavailable;
        }

        // The sound first: its streams come up while the video opens (one line and video only if it cannot start).
        using var audio = CaptureAudio.Start(options, device, error);
        var playback = new PlaybackOptions
        {
            Path = device,
            Capture = new CaptureSourceOptions
            {
                Device = device,
                Request = options.Request,
                Color = options.Color,
                BufferMode = options.BufferMode,
                Buffers = options.Buffers,
                Cadence = options.Cadence,
                FrameLogCapacity = options.FramesCsvPath is null ? 0 : FrameLogCapacity(options),
            },
            Generator = generator.Value,
            Quality = options.Quality,
            QualitySwitch = options.QualitySwitch,
            ShotChangeDetection = options.ShotChangeDetection,
            Multiplier = options.Multiplier,
            OutputHz = options.OutputHz,
            DurationNs = (long)(options.DurationS * Nanoseconds.PerSecond),
            Display = options.Offscreen ? DisplayKind.Offscreen : DisplayKind.Window,
            Clock = PlaybackClock.Real,
            OffscreenRate = options.OffscreenRate,
            WindowWidth = options.WindowWidth,
            WindowHeight = options.WindowHeight,
            Fullscreen = options.Fullscreen,
            Output = options.Output,
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
            Audio = audio.Delay,
        };

        var cuda = CudaContext.RetainPrimary(CudaDriver.GetDevice(0), options.CudaSync);
        var releaseContext = true;
        try
        {
            PlaybackReport report;
            // A colour change while capturing (followed, or H/A in the window): one line as it happens, written from
            // preformatted text so the control thread allocates nothing (the summary lists them all).
            string[] notices =
            [
                "  colour      now SDR (BT.709): switched by hand",
                "  colour      now HDR10 (PQ, BT.2020): switched by hand",
                "  colour      now SDR (BT.709): the signal's samples now carry 8 bits",
                "  colour      now HDR10 (PQ, BT.2020): the signal's samples now carry 10 bits",
            ];
            Action<RepeatBeat>? beatNotice = null;
            var hooks = new PlaybackHooks
            {
                OnColorSwitch = w => output.WriteLine(notices[(w.ToHdr ? 1 : 0) + (w.Cause == ColorSwitchCause.Manual ? 0 : 2)]),
                OnQualitySwitch = PlayPrinter.QualityNotices(output),
                OnQualityNotice = PlayPrinter.QualityIgnored(output),
                OnVrrFollow = PlayPrinter.VrrFollowNotices(error),
                OnCaptureBeat = beat => beatNotice?.Invoke(beat),
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
                    error.WriteLine($"fmp: capture{(e.InnerException is CaptureUnavailableException ? ":" : "")} {e.Message}");
                    Blame();
                    return PatternCommand.ExitUnavailable;
                }
                catch (CaptureFormatException e)
                {
                    error.WriteLine($"fmp: capture: {e.Message} (fmp capture --list shows the modes)");
                    Blame();
                    return CommandDispatcher.ExitUsage;
                }
                catch (V4l2Exception e)
                {
                    error.WriteLine($"fmp: capture: {e.Message}");
                    Blame();
                    return CommandDispatcher.ExitCheckFailed;
                }

                if (session.PlaybackSource is CapturePlaybackSource opened)
                {
                    beatNotice = CapturePrinter.BeatNotices(error, opened.Source.Format.Rate, opened.Source.OfferedRates, options.CaptureRateForced);
                }

                CapturePrinter.PrintSetup(output, options, playback, setup, why, cuda);
                output.WriteLine(UserSettings.Describe(loaded, applied));
                audio.PrintSetup(output, options);
                output.Flush();
                using var switcher = SwitchEvery(session, error);
                using var qualitySwitcher = PlayCommand.QualitySwitchEvery(session, error, "capture");
                report = session.Run();
                if (options.FramesCsvPath is { } path && session.PlaybackSource is CapturePlaybackSource capture)
                {
                    CaptureFrameLog.Write(path, capture.Source.FrameLog, capture.Source.Format.Rate);
                    output.WriteLine($"  frames      {capture.Source.FrameLog.Length} dequeued buffers written to {path}");
                }
            }

            Volatile.Write(ref live, null);
            _ = audio.Finish(); // the session stopped it with the picture; released here (bounded)
            var videoLatency = report.Statistics.RealFrameLatency.Count > 0 ? report.Statistics.RealFrameLatency.Mean : 0;
            var verdict = PlayPrinter.PrintSummary(output, gate: false, report, (o, warnings) => audio.PrintSummary(o, warnings, videoLatency));
            var exit = verdict ? CommandDispatcher.ExitOk : CommandDispatcher.ExitCheckFailed;
            if (report.GpuAbandoned)
            {
                // As fmp play: GPU work that never finished is still queued; the device has been released already.
                releaseContext = false;
                output.Flush();
                error.Flush();
                PlayCommand.ExitNow?.Invoke(exit);
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
    /// Diagnostics: FMP_CAPTURE_SWITCH_EVERY_S=<s> asks the capture to switch between SDR and HDR10 every s seconds, as
    /// pressing H in the window does (to exercise the switch with no one at the keyboard). Null when not set.
    /// </summary>
    private static Timer? SwitchEvery(PlaybackSession session, TextWriter error)
    {
        var text = Environment.GetEnvironmentVariable("FMP_CAPTURE_SWITCH_EVERY_S");
        if (text is null)
        {
            return null;
        }

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds < 0.2 || seconds > 3600)
        {
            error.WriteLine($"fmp: capture: FMP_CAPTURE_SWITCH_EVERY_S expects seconds (0.2 to 3600), got '{text}'; ignored");
            return null;
        }

        var period = TimeSpan.FromSeconds(seconds);
        return new Timer(_ => session.RequestCaptureColor(CaptureColorRequest.Toggle), null, period, period);
    }

    /// <summary>Room for the whole run at up to 240 fps (an hour when it has no duration), plus the start-up.</summary>
    private static int FrameLogCapacity(CaptureOptions options) =>
        (int)Math.Min(10_000_000, ((options.DurationS > 0 ? options.DurationS + options.WarmupS + 10 : 3600) * 240) + 1000);

    private static int List(CaptureOptions options, TextWriter output, TextWriter error)
    {
        if (!OperatingSystem.IsLinux())
        {
            return PatternCommand.ExitUnavailable;
        }

        List<CaptureDeviceDescription> devices;
        if (options.Device is { } one)
        {
            try
            {
                devices = [CaptureDevices.Describe(one)];
            }
            catch (CaptureUnavailableException e)
            {
                error.WriteLine($"fmp: capture: {e.Message}");
                return PatternCommand.ExitUnavailable;
            }
        }
        else
        {
            devices = CaptureDevices.List();
        }

        if (devices.Count == 0)
        {
            error.WriteLine("fmp: capture: no V4L2 capture device found (is the capture card plugged in?)");
            return PatternCommand.ExitUnavailable;
        }

        var fallback = CaptureDevices.Default(devices);
        AudioGraphSnapshot? graph = null;
        string? noAudio = null;
        if (!AudioDevices.TrySnapshot(out graph, out noAudio))
        {
            graph = null;
        }

        foreach (var d in devices)
        {
            CapturePrinter.PrintDevice(output, d, d.Info.Path == fallback);
            CapturePrinter.PrintDeviceAudio(output, d.Info.Path, graph, noAudio);
        }

        CapturePrinter.PrintSinks(output, graph, noAudio);
        return CommandDispatcher.ExitOk;
    }
}
