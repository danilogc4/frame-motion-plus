using System.Globalization;
using FrameMotionPlus.App.Cli;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Cuda;

namespace FrameMotionPlus.App.Play;

/// <summary>How the generator was asked for: a backend, or "auto" (VFG when the SDK is there, else blend).</summary>
internal enum GeneratorChoice
{
    Auto = 0,
    Vfg = 1,
    Blend = 2,
    Repeat = 3,
    None = 4,
}

/// <summary>Configuration of <c>fmp play</c>.</summary>
internal sealed record PlayOptions
{
    /// <summary>The milestone-1 gate: 10 minutes, looped, VFG Medium at 2x, windowed.</summary>
    public const double GateSeconds = 600;

    public required string Path { get; init; }

    public GeneratorChoice Generator { get; init; } = GeneratorChoice.Auto;

    public FrameGenQuality Quality { get; init; } = FrameGenQuality.Medium;

    /// <summary>
    /// Whether VFG loads its second quality at start for Q when neither <c>--quality-switch</c> nor
    /// <c>--no-quality-switch</c> (nor the setting <c>generator.qualitySwitch</c>) says: see docs/benchmarks/phase9-polish.md,
    /// "Live quality switching", for what it costs.
    /// </summary>
    public const bool QualitySwitchDefault = true;

    /// <summary>VFG: load the other quality too at start, so Q switches live (<c>--quality-switch</c> / <c>--no-quality-switch</c>).</summary>
    public bool QualitySwitch { get; init; } = QualitySwitchDefault;

    /// <summary>
    /// File playback's default for <c>--scd</c>: on. Films cut often, and VFG's detection shows each cut as a real frame;
    /// a capture's default is off (<see cref="Capture.CaptureOptions.ShotChangeDetectionDefault"/>).
    /// </summary>
    public const bool ShotChangeDetectionDefault = true;

    /// <summary>VFG's automatic shot-change detection (<c>--scd on|off</c>): off keeps generated frames through fast motion.</summary>
    public bool ShotChangeDetection { get; init; } = ShotChangeDetectionDefault;

    public int Multiplier { get; init; } = 2;

    /// <summary>Asked for as an output rate (Hz) instead of a multiplier: the multiplier is this over the file's rate.</summary>
    public double? OutputHz { get; init; }

    public bool Loop { get; init; }

    public double DurationS { get; init; }

    public long Frames { get; init; }

    public int WindowWidth { get; init; } = 1280;

    public int WindowHeight { get; init; } = 720;

    public bool Fullscreen { get; init; }

    /// <summary>--output: the display fullscreen goes to.</summary>
    public string? Output { get; init; }

    /// <summary>--clock: fixed refresh, variable refresh, or auto (variable when fullscreen and the display follows the presents).</summary>
    public Pipeline.RefreshMode Refresh { get; init; } = Pipeline.RefreshMode.Auto;

    /// <summary>No window: an offscreen target on the virtual clock (deterministic), or the real clock with <see cref="Realtime"/>.</summary>
    public bool Offscreen { get; init; }

    public bool Realtime { get; init; }

    public FrameRate OffscreenRate { get; init; } = FrameRate.FromHz(120);

    public int OffscreenWidth { get; init; }

    public int OffscreenHeight { get; init; }

    public bool Validate { get; init; }

    public string? CsvPath { get; init; }

    public string? SettingsPath { get; init; }

    public string? VfxSdk { get; init; }

    public double WarmupS { get; init; } = 2;

    /// <summary>-1: automatic (one refresh windowed, 0 offscreen).</summary>
    public double PresentLeadMs { get; init; } = -1;

    /// <summary>Variable refresh: how late a generated slot may be shown instead of skipped, ms (0: skipped).</summary>
    public double LateGeneratedMs { get; init; } = Pipeline.PlaybackOptions.LateGeneratedDefaultNs / 1e6;

    /// <summary><c>--ceiling-pacing on|off</c>: see <see cref="Pipeline.PlaybackOptions.CeilingPacing"/>.</summary>
    public bool CeilingPacing { get; init; } = true;

    public double LatchMs { get; init; } = 1;

    /// <summary>How host threads wait in CUDA synchronisations (the primary context's scheduling flag).</summary>
    public CudaSchedulingMode CudaSync { get; init; } = CudaSchedulingMode.BlockingSync;

    public bool Gate { get; init; }

    public static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.Ordinal)
    {
        "loop", "fullscreen", "offscreen", "realtime", "validate", "gate", "quality-switch", "no-quality-switch",
    };

    public static PlayOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new UsageException("play needs a file: fmp play <file> [options].");
        }

        var r = new ArgumentReader([.. args.Skip(1)], Flags);
        var gate = r.Flag("gate");
        var offscreen = r.Flag("offscreen");
        var (windowWidth, windowHeight) = Size(r, "window", 1280, 720, 64, 8192);
        var (offscreenWidth, offscreenHeight) = Size(r, "size", 0, 0, 16, 16384);
        var o = new PlayOptions
        {
            Path = args[0],
            Gate = gate,
            Generator = ParseGenerator(r.String("generator")),
            Quality = r.String("quality") is { } quality ? Vfg.VfgBenchOptions.ParseMode(quality) : FrameGenQuality.Medium,
            QualitySwitch = ParseQualitySwitch(r),
            ShotChangeDetection = r.String("scd") is { } scd ? Vfg.VfgBenchOptions.ParseOnOff(scd, "scd") : ShotChangeDetectionDefault,
            Multiplier = (int)r.Int64("multiplier", 2, 1, 8),
            OutputHz = r.Has("output-hz") ? r.Double("output-hz", 0, 1, 1000) : null,
            Loop = r.Flag("loop"),
            DurationS = r.Double("duration-s", 0, 0.05, 86_400),
            Frames = r.Int64("frames", 0, 1, 100_000_000),
            WindowWidth = windowWidth,
            WindowHeight = windowHeight,
            Fullscreen = r.Flag("fullscreen"),
            Output = r.String("output"),
            Refresh = r.String("clock")?.ToLowerInvariant() switch
            {
                null or "auto" => Pipeline.RefreshMode.Auto,
                "fixed" => Pipeline.RefreshMode.Fixed,
                "vrr" => Pipeline.RefreshMode.Vrr,
                var v => throw new UsageException($"Option '--clock' expects auto, fixed or vrr, got '{v}'."),
            },
            Offscreen = offscreen,
            Realtime = r.Flag("realtime"),
            OffscreenRate = r.Rate("rate", FrameRate.FromHz(120)),
            OffscreenWidth = offscreenWidth,
            OffscreenHeight = offscreenHeight,
            Validate = r.Flag("validate"),
            CsvPath = r.String("csv"),
            SettingsPath = r.String("settings"),
            VfxSdk = r.String("vfx-sdk"),
            WarmupS = r.Double("warmup-s", 2, 0, 600),
            PresentLeadMs = r.Double("present-lead-ms", -1, -1, 100),
            LateGeneratedMs = r.Double("late-generated-ms", Pipeline.PlaybackOptions.LateGeneratedDefaultNs / 1e6, 0, 8),
            CeilingPacing = r.String("ceiling-pacing") is not { } ceiling || Vfg.VfgBenchOptions.ParseOnOff(ceiling, "ceiling-pacing"),
            LatchMs = r.Double("latch-ms", 1, 0, 50),
            CudaSync = ParseSync(r.String("cuda-sync")),
        };
        r.EnsureAllConsumed();
        if (o.Frames > 0 && o.DurationS > 0)
        {
            throw new UsageException("Give --frames or --duration-s, not both.");
        }

        if (o.OutputHz is not null && args.Any(static a => a.StartsWith("--multiplier", StringComparison.Ordinal)))
        {
            throw new UsageException("Give --multiplier or --output-hz, not both.");
        }

        if (!offscreen && (o.Realtime || args.Any(static a => a.StartsWith("--rate", StringComparison.Ordinal) || a.StartsWith("--size", StringComparison.Ordinal))))
        {
            throw new UsageException("'--realtime', '--rate' and '--size' only apply with '--offscreen'.");
        }

        if (offscreen && (o.Fullscreen || args.Any(static a => a.StartsWith("--window", StringComparison.Ordinal))))
        {
            throw new UsageException("'--window' and '--fullscreen' do not apply with '--offscreen'.");
        }

        if (gate)
        {
            if (offscreen)
            {
                throw new UsageException("'--gate' is the windowed milestone-1 run; it does not apply with '--offscreen'.");
            }

            if (o.Generator is not (GeneratorChoice.Auto or GeneratorChoice.Vfg)
                || args.Any(static a => a.StartsWith("--quality", StringComparison.Ordinal) || a.StartsWith("--multiplier", StringComparison.Ordinal)
                    || a.StartsWith("--output-hz", StringComparison.Ordinal) || a.StartsWith("--frames", StringComparison.Ordinal)
                    || a.StartsWith("--scd", StringComparison.Ordinal)))
            {
                throw new UsageException("'--gate' runs exactly VFG Medium at 2x, looped, for 10 minutes: no --generator, --quality, --multiplier, --output-hz, --scd or --frames.");
            }

            o = o with
            {
                Generator = GeneratorChoice.Vfg,
                Quality = FrameGenQuality.Medium,
                Multiplier = 2,
                Loop = true,
                DurationS = o.DurationS > 0 ? o.DurationS : GateSeconds,
            };
        }

        return o;
    }

    /// <summary><c>--quality-switch</c> / <c>--no-quality-switch</c> (both: a usage error); neither: the default.</summary>
    internal static bool ParseQualitySwitch(ArgumentReader r)
    {
        var on = r.Flag("quality-switch");
        var off = r.Flag("no-quality-switch");
        if (on && off)
        {
            throw new UsageException("'--quality-switch' and '--no-quality-switch' contradict each other.");
        }

        return on || (!off && QualitySwitchDefault);
    }

    /// <summary>The multiplier cap: <see cref="Multiplier"/>, or <see cref="OutputHz"/> over the file's rate.</summary>
    public int MultiplierFor(FrameRate sourceRate)
    {
        if (OutputHz is not { } hz || !sourceRate.IsValid)
        {
            return Multiplier;
        }

        return Math.Clamp((int)Math.Round(hz / sourceRate.Hz), 1, 8);
    }

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
