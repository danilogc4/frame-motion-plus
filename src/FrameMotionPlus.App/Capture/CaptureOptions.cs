using System.Globalization;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.App.Play;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;
using FrameMotionPlus.Sources.V4l2;

namespace FrameMotionPlus.App.Capture;

/// <summary>A named capture mode: what to ask the device for and the HDR decision that goes with it.</summary>
/// <remarks>
/// The 30 and 60 fps presets ask for the console's own rates, 29.97 and 59.94 (30000/1001 and 60000/1001): a PS5 sends
/// 59.94 Hz, and a card set to 30.000 has no new frame once every 33.4 s (60.000: every 16.7 s) and sends a repeat, which
/// the app drops and the picture holds for a frame (measured, docs/benchmarks/phase6-pacing.md, 2026-11-19).
/// <c>--capture-fps 30</c> (or 60) asks for the whole rate, for a source that really sends 60.000 Hz (a PC). The 120 fps
/// presets stay at 120: the card offers no 119.88.
/// </remarks>
internal sealed record CapturePreset(string Name, int Width, int Height, FrameRate Rate, CapturePixelFormat Format, HdrMode Hdr, string Note)
{
    /// <summary>The NTSC rates a console sends.</summary>
    public static readonly FrameRate Ntsc30 = new(30000, 1001);

    public static readonly FrameRate Ntsc60 = new(60000, 1001);

    /// <summary>The presets, the owner's target first (4K HDR, 30 to 60).</summary>
    public static readonly IReadOnlyList<CapturePreset> All =
    [
        new("4k30-hdr", 3840, 2160, Ntsc30, CapturePixelFormat.P010, HdrMode.Follow, "3840x2160 P010 at 29.97 fps, HDR10 or SDR as the console sends (the Elgato 4K X's 10-bit 4K mode; generate to 59.94)"),
        new("1440p60-hdr", 2560, 1440, Ntsc60, CapturePixelFormat.P010, HdrMode.Follow, "2560x1440 P010 at 59.94 fps, HDR10 or SDR as the console sends (for 60 fps games; generate to 119.88)"),
        new("4k60-sdr", 3840, 2160, Ntsc60, CapturePixelFormat.Nv12, HdrMode.Off, "3840x2160 NV12 at 59.94 fps, SDR (8-bit)"),
        new("1440p60-sdr", 2560, 1440, Ntsc60, CapturePixelFormat.Nv12, HdrMode.Off, "2560x1440 NV12 at 59.94 fps, SDR"),
        new("1440p120-sdr", 2560, 1440, FrameRate.FromHz(120), CapturePixelFormat.Nv12, HdrMode.Off, "2560x1440 NV12 at 120 fps, SDR (the card offers no 119.88: a console's 119.88 Hz repeats a frame every 8.3 s)"),
        new("1080p60-sdr", 1920, 1080, Ntsc60, CapturePixelFormat.Nv12, HdrMode.Off, "1920x1080 NV12 at 59.94 fps, SDR"),
        new("1080p120-sdr", 1920, 1080, FrameRate.FromHz(120), CapturePixelFormat.Nv12, HdrMode.Off, "1920x1080 NV12 at 120 fps, SDR (no 119.88 offered, as 1440p120)"),
    ];

    public double Fps => Rate.Hz;

    public static CapturePreset Parse(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new UsageException($"Option '--mode' expects one of {string.Join(", ", All.Select(p => p.Name))}, got '{name}'.");
}

/// <summary>Whether <c>fmp capture</c> plays the card's sound (--audio).</summary>
internal enum AudioChoice
{
    /// <summary>On when the capture device has an audio source (the default); otherwise silently off.</summary>
    Auto = 0,

    /// <summary>On; if it cannot start, the video runs and the reason is printed (and warned about in the summary).</summary>
    On = 1,

    Off = 2,
}

/// <summary>Configuration of <c>fmp capture</c>.</summary>
internal sealed record CaptureOptions
{
    /// <summary>The device node; null: the first capture device with a 10-bit mode, else the first capture device.</summary>
    public string? Device { get; init; }

    public bool List { get; init; }

    public CapturePreset? Preset { get; init; }

    public CaptureRequest Request { get; init; } = new();

    /// <summary>The rate asked for, exactly (the preset's, --capture-fps or --fps); invalid: the device's highest.</summary>
    public FrameRate CaptureRate { get; init; }

    /// <summary>True when <c>--capture-fps</c> chose the rate (the beat's advice then says so).</summary>
    public bool CaptureRateForced { get; init; }

    public CaptureColorOverrides Color { get; init; } = new();

    public CaptureBufferMode BufferMode { get; init; } = CaptureBufferMode.Auto;

    public int Buffers { get; init; } = 4;

    public bool Cadence { get; init; } = true;

    public GeneratorChoice Generator { get; init; } = GeneratorChoice.Auto;

    public FrameGenQuality Quality { get; init; } = FrameGenQuality.Medium;

    /// <summary>VFG: load the other quality too at start, so Q switches live (<c>--quality-switch</c> / <c>--no-quality-switch</c>).</summary>
    public bool QualitySwitch { get; init; } = Play.PlayOptions.QualitySwitchDefault;

    /// <summary>
    /// A capture's default for <c>--scd</c>: off. A game's fast motion (driving, foliage close to the camera) is what VFG's
    /// automatic shot-change detection takes for a cut, repeating real frames for as long as it lasts; a game has few
    /// real cuts, and with the detection off each shows one morphed frame (the owner's judgement on a 46-minute session,
    /// 2026-11-20: "it now looks perfect", no issue noticed at cuts). File playback keeps it on: films cut often.
    /// </summary>
    public const bool ShotChangeDetectionDefault = false;

    /// <summary>VFG's automatic shot-change detection (<c>--scd on|off</c>): off keeps generated frames through fast motion.</summary>
    public bool ShotChangeDetection { get; init; } = ShotChangeDetectionDefault;

    public int Multiplier { get; init; } = 2;

    public double? OutputHz { get; init; }

    public double DurationS { get; init; }

    public int WindowWidth { get; init; } = 1280;

    public int WindowHeight { get; init; } = 720;

    /// <summary>Borderless fullscreen (the default unless --windowed or --window is given, or --offscreen).</summary>
    public bool Fullscreen { get; init; }

    /// <summary>--clock: fixed refresh, variable refresh, or auto (variable when fullscreen and the display follows the presents).</summary>
    public Pipeline.RefreshMode Refresh { get; init; } = Pipeline.RefreshMode.Auto;

    public bool Offscreen { get; init; }

    public FrameRate OffscreenRate { get; init; } = FrameRate.FromHz(120);

    public bool Validate { get; init; }

    public string? CsvPath { get; init; }

    /// <summary>Write every dequeued buffer (sequence, timestamp, dequeue and judge times, cadence metric, decision) here.</summary>
    public string? FramesCsvPath { get; init; }

    public string? SettingsPath { get; init; }

    public string? VfxSdk { get; init; }

    public double WarmupS { get; init; } = 2;

    public double PresentLeadMs { get; init; } = -1;

    /// <summary>Variable refresh: how late a generated slot may be shown instead of skipped, ms (0: skipped).</summary>
    public double LateGeneratedMs { get; init; } = Pipeline.PlaybackOptions.LateGeneratedDefaultNs / 1e6;

    /// <summary><c>--ceiling-pacing on|off</c>: see <see cref="Pipeline.PlaybackOptions.CeilingPacing"/>.</summary>
    public bool CeilingPacing { get; init; } = true;

    public double LatchMs { get; init; } = 1;

    public CudaSchedulingMode CudaSync { get; init; } = CudaSchedulingMode.BlockingSync;

    /// <summary>--audio: the card's sound, delayed to its picture.</summary>
    public AudioChoice Audio { get; init; } = AudioChoice.Auto;

    /// <summary>--audio-source: the PipeWire source to play (node name); null: the one on the capture device's USB device.</summary>
    public string? AudioSource { get; init; }

    /// <summary>--audio-sink: the PipeWire sink to play to (node name); null: the default sink, followed when it changes.</summary>
    public string? AudioSink { get; init; }

    /// <summary>--audio-offset-ms: added to the measured video latency (positive: the sound later).</summary>
    public double AudioOffsetMs { get; init; }

    public static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.Ordinal)
    {
        "list", "fullscreen", "windowed", "offscreen", "validate", "no-cadence", "quality-switch", "no-quality-switch",
    };

    public static CaptureOptions Parse(IReadOnlyList<string> args)
    {
        string? device = null;
        var rest = args;
        if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            device = args[0];
            rest = [.. args.Skip(1)];
        }

        var r = new ArgumentReader(rest, Flags);
        var list = r.Flag("list");
        var preset = r.String("mode") is { } mode ? CapturePreset.Parse(mode) : null;
        var (width, height) = Size(r, "size", 0, 0, 16, 16384);
        var fps = r.Has("fps") ? ParseCaptureFps(r, "fps") : default;
        var captureFps = r.Has("capture-fps") ? ParseCaptureFps(r, "capture-fps") : default;
        var format = ParseFormat(r.String("format"));
        if (preset is not null && (width > 0 || fps.IsValid || format != CapturePixelFormat.Unknown))
        {
            throw new UsageException("Give --mode, or --size/--fps/--format, not both.");
        }

        if (fps.IsValid && captureFps.IsValid)
        {
            throw new UsageException("Give --fps or --capture-fps, not both (the same rate).");
        }

        // --capture-fps sets the rate of a preset (or, without --mode, is --fps): exact, 29.97 is 30000/1001.
        var rate = captureFps.IsValid ? captureFps : preset?.Rate ?? fps;
        var request = preset is not null
            ? new CaptureRequest
            {
                Width = preset.Width,
                Height = preset.Height,
                Fps = rate.Hz,
                // A preset's own 29.97 or 59.94 falls back to 30 or 60 on a device with whole rates only (a typed rate does not).
                FallbackFps = captureFps.IsValid ? 0 : Math.Round(rate.Hz),
                Format = preset.Format,
            }
            : new CaptureRequest { Width = width, Height = height, Fps = rate.IsValid ? rate.Hz : 0, Format = format };
        var hdrText = r.String("hdr");
        var hdr = hdrText is null ? preset?.Hdr ?? HdrMode.Follow : ParseHdr(hdrText);
        var peak = (int)r.Int64("hdr-peak-nits", ColorMetadata.DefaultPeakNits, 100, 10_000);
        var maxCll = r.Has("hdr-max-cll") ? (int)r.Int64("hdr-max-cll", peak, 1, 10_000) : (int?)null;
        var maxFall = (int)r.Int64("hdr-max-fall", 0, 0, 10_000);
        var offscreen = r.Flag("offscreen");
        var fullscreen = r.Flag("fullscreen");
        var windowed = r.Flag("windowed") || r.Has("window");
        if (fullscreen && r.Flag("windowed"))
        {
            throw new UsageException("'--fullscreen' and '--windowed' contradict each other.");
        }

        var (windowWidth, windowHeight) = Size(r, "window", 1280, 720, 64, 8192);
        var audioSource = r.String("audio-source");
        var audioSink = r.String("audio-sink");
        var audioOffset = r.Double("audio-offset-ms", 0, -200, 500);
        var audio = r.String("audio")?.ToLowerInvariant() switch
        {
            null => audioSource is not null || audioSink is not null ? AudioChoice.On : AudioChoice.Auto,
            "auto" => AudioChoice.Auto,
            "on" => AudioChoice.On,
            "off" => AudioChoice.Off,
            var v => throw new UsageException($"Option '--audio' expects on, off or auto, got '{v}'."),
        };
        if (audio == AudioChoice.Off && (audioSource is not null || audioSink is not null || r.Has("audio-offset-ms")))
        {
            throw new UsageException("'--audio off' takes no '--audio-source', '--audio-sink' or '--audio-offset-ms'.");
        }

        if (audioSource is not null && (audioSource.Length == 0 || audioSource.Any(char.IsWhiteSpace)))
        {
            throw new UsageException($"Option '--audio-source' expects a PipeWire node name (fmp capture --list shows them), got '{audioSource}'.");
        }

        if (audioSink is not null && string.IsNullOrWhiteSpace(audioSink))
        {
            throw new UsageException("Option '--audio-sink' expects a sink's node name or a part of its name or description (fmp capture --list shows them).");
        }

        var o = new CaptureOptions
        {
            Device = device,
            List = list,
            Preset = preset,
            Request = request,
            CaptureRate = rate,
            CaptureRateForced = captureFps.IsValid,
            Color = new CaptureColorOverrides
            {
                Hdr = hdr,
                Range = ParseRange(r.String("range")),
                Matrix = ParseMatrix(r.String("matrix")),
                PeakNits = peak,
                MaxCll = maxCll,
                MaxFall = maxFall,
            },
            BufferMode = ParseBufferMode(r.String("buffer-mode")),
            Buffers = (int)r.Int64("buffers", 4, 2, 16),
            Cadence = !r.Flag("no-cadence"),
            Generator = ParseGenerator(r.String("generator")),
            Quality = r.String("quality") is { } quality ? Vfg.VfgBenchOptions.ParseMode(quality) : FrameGenQuality.Medium,
            QualitySwitch = Play.PlayOptions.ParseQualitySwitch(r),
            ShotChangeDetection = r.String("scd") is { } scd ? Vfg.VfgBenchOptions.ParseOnOff(scd, "scd") : ShotChangeDetectionDefault,
            Multiplier = (int)r.Int64("multiplier", 2, 1, 8),
            OutputHz = r.Has("output-hz") ? r.Double("output-hz", 0, 1, 1000) : null,
            DurationS = r.Double("duration-s", 0, 0.05, 86_400),
            WindowWidth = windowWidth,
            WindowHeight = windowHeight,
            // Fullscreen by default: there KWin gives the window variable refresh, which --clock auto then uses (the
            // lowest latency measured, Phase 6); --windowed or a --window size keeps a window (fixed refresh).
            Fullscreen = !offscreen && (fullscreen || !windowed),
            Refresh = r.String("clock")?.ToLowerInvariant() switch
            {
                null or "auto" => Pipeline.RefreshMode.Auto,
                "fixed" => Pipeline.RefreshMode.Fixed,
                "vrr" => Pipeline.RefreshMode.Vrr,
                var v => throw new UsageException($"Option '--clock' expects auto, fixed or vrr, got '{v}'."),
            },
            Offscreen = offscreen,
            OffscreenRate = r.Rate("display-hz", FrameRate.FromHz(120)),
            Validate = r.Flag("validate"),
            CsvPath = r.String("csv"),
            FramesCsvPath = r.String("frames-csv"),
            SettingsPath = r.String("settings"),
            VfxSdk = r.String("vfx-sdk"),
            WarmupS = r.Double("warmup-s", 2, 0, 600),
            PresentLeadMs = r.Double("present-lead-ms", -1, -1, 100),
            LateGeneratedMs = r.Double("late-generated-ms", Pipeline.PlaybackOptions.LateGeneratedDefaultNs / 1e6, 0, 8),
            CeilingPacing = r.String("ceiling-pacing") is not { } ceiling || Vfg.VfgBenchOptions.ParseOnOff(ceiling, "ceiling-pacing"),
            LatchMs = r.Double("latch-ms", 1, 0, 50),
            CudaSync = ParseSync(r.String("cuda-sync")),
            Audio = audio,
            AudioSource = audioSource,
            AudioSink = audioSink,
            AudioOffsetMs = audioOffset,
        };
        r.EnsureAllConsumed();
        if (o.OutputHz is not null && rest.Any(static a => a.StartsWith("--multiplier", StringComparison.Ordinal)))
        {
            throw new UsageException("Give --multiplier or --output-hz, not both.");
        }

        if (!offscreen && rest.Any(static a => a.StartsWith("--display-hz", StringComparison.Ordinal)))
        {
            throw new UsageException("'--display-hz' only applies with '--offscreen'.");
        }

        if (offscreen && (fullscreen || rest.Any(static a => a.StartsWith("--window", StringComparison.Ordinal))))
        {
            throw new UsageException("'--window' and '--fullscreen' do not apply with '--offscreen'.");
        }

        if (list && rest.Count > 1)
        {
            throw new UsageException("'--list' takes no other option (an optional device first: fmp capture /dev/video0 --list).");
        }

        return o;
    }

    /// <summary>The generator choice as <c>fmp play</c> takes it (shared resolution rules).</summary>
    public PlayOptions AsPlayOptions(string device) => new()
    {
        Path = device,
        Generator = Generator,
        Quality = Quality,
        Multiplier = Multiplier,
        OutputHz = OutputHz,
        VfxSdk = VfxSdk,
        CudaSync = CudaSync,
        Validate = Validate,
    };

    /// <summary>A capture rate as <c>--capture-fps</c> and <c>--fps</c> take it: 30, 29.97, 60, 59.94, 30000/1001 (exact).</summary>
    private static FrameRate ParseCaptureFps(ArgumentReader r, string name)
    {
        var text = r.String(name);
        return FrameRate.TryParse(text, out var rate) && rate.Hz is >= 1 and <= 1000
            ? rate
            : throw new UsageException($"Option '--{name}' expects a rate in frames per second (1 to 1000: 30, 29.97, 60, 59.94 or a fraction such as 30000/1001), got '{text}'.");
    }

    private static CapturePixelFormat ParseFormat(string? text) => text?.ToLowerInvariant() switch
    {
        null => CapturePixelFormat.Unknown,
        "nv12" => CapturePixelFormat.Nv12,
        "p010" => CapturePixelFormat.P010,
        "yuyv" => CapturePixelFormat.Yuyv,
        "mjpg" or "mjpeg" or "jpeg" => throw new UsageException("MJPEG is never used: it needs a decode step and adds latency; give nv12, p010 or yuyv."),
        _ => throw new UsageException($"Option '--format' expects nv12, p010 or yuyv, got '{text}'."),
    };

    private static HdrMode ParseHdr(string text) => text.ToLowerInvariant() switch
    {
        "on" => HdrMode.On,
        "off" => HdrMode.Off,
        "auto" => HdrMode.Auto,
        "10bit" => HdrMode.TenBit,
        "follow" => HdrMode.Follow,
        _ => throw new UsageException($"Option '--hdr' expects follow (HDR10 or SDR as the signal's depth says), on, off, auto (as the device reports) or 10bit (on for 10-bit captures), got '{text}'."),
    };

    private static ColorRange? ParseRange(string? text) => text?.ToLowerInvariant() switch
    {
        null or "auto" => null,
        "limited" or "tv" => ColorRange.Limited,
        "full" or "pc" => ColorRange.Full,
        _ => throw new UsageException($"Option '--range' expects limited, full or auto, got '{text}'."),
    };

    private static MatrixCoefficients? ParseMatrix(string? text) => text?.ToLowerInvariant() switch
    {
        null or "auto" => null,
        "bt709" or "709" => MatrixCoefficients.Bt709,
        "bt2020" or "2020" => MatrixCoefficients.Bt2020NonConstant,
        "bt601" or "601" => MatrixCoefficients.Bt601,
        _ => throw new UsageException($"Option '--matrix' expects bt709, bt2020, bt601 or auto, got '{text}'."),
    };

    private static CaptureBufferMode ParseBufferMode(string? text) => text?.ToLowerInvariant() switch
    {
        null or "auto" => CaptureBufferMode.Auto,
        "userptr" => CaptureBufferMode.UserPtrPinned,
        "userptr-registered" => CaptureBufferMode.UserPtrRegistered,
        "mmap" => CaptureBufferMode.MmapRegistered,
        _ => throw new UsageException($"Option '--buffer-mode' expects auto, userptr, userptr-registered or mmap, got '{text}'."),
    };

    private static GeneratorChoice ParseGenerator(string? text) => text?.ToLowerInvariant() switch
    {
        null or "auto" => GeneratorChoice.Auto,
        "vfg" => GeneratorChoice.Vfg,
        "blend" => GeneratorChoice.Blend,
        "repeat" => GeneratorChoice.Repeat,
        "none" or "off" => GeneratorChoice.None,
        _ => throw new UsageException($"Option '--generator' expects auto, vfg, blend, repeat or none, got '{text}'."),
    };

    private static CudaSchedulingMode ParseSync(string? text) => text?.ToLowerInvariant() switch
    {
        null or "blocking" => CudaSchedulingMode.BlockingSync,
        "spin" => CudaSchedulingMode.Spin,
        "auto" => CudaSchedulingMode.Auto,
        "yield" => CudaSchedulingMode.Yield,
        _ => throw new UsageException($"Option '--cuda-sync' expects blocking, spin, yield or auto, got '{text}'."),
    };

    private static (int Width, int Height) Size(ArgumentReader r, string name, int width, int height, int min, int max)
    {
        var text = r.String(name);
        if (text is null)
        {
            return (width, height);
        }

        var x = text.IndexOfAny(['x', 'X']);
        if (x <= 0
            || !int.TryParse(text.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(text.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || w < min || h < min || w > max || h > max)
        {
            throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"Option '--{name}' expects WIDTHxHEIGHT ({min} to {max} each), got '{text}'."));
        }

        return (w, h);
    }
}
