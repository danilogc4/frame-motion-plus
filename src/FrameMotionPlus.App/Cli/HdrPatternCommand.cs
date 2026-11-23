using System.Globalization;
using System.Runtime.InteropServices;
using FrameMotionPlus.App.Pattern;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;

namespace FrameMotionPlus.App.Cli;

/// <summary>
/// <c>fmp hdr-pattern [ramp|peak|bars|black] [options]</c>: an HDR test pattern on screen on an HDR10 swapchain (to check
/// the display by eye), or with <c>--check</c> the numeric proof of the HDR path offscreen (<see cref="HdrCheck"/>). Exit 0
/// when shown or every check passed; 1 when a check failed; 2 on a usage error; 69 without the GPU, display or HDR output.
/// </summary>
internal static class HdrPatternCommand
{
    public static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.Ordinal) { "check", "fullscreen", "no-vfg" };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var pattern = HdrPatternKind.Ramp;
        var rest = args;
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            pattern = HdrPatterns.Parse(args[0]);
            rest = args[1..];
        }

        var r = new ArgumentReader(rest, Flags);
        var check = r.Flag("check");
        var peak = (int)r.Int64("hdr-peak-nits", ColorMetadata.DefaultPeakNits, 100, 10_000);
        var maxCll = r.Has("hdr-max-cll") ? (int)r.Int64("hdr-max-cll", peak, 1, 10_000) : (int?)null;
        var maxFall = (int)r.Int64("hdr-max-fall", 0, 0, 10_000);
        return check ? Check(r, peak, maxCll, maxFall, output, error) : Show(r, pattern, peak, maxCll, maxFall, output, error);
    }

    private static int Check(ArgumentReader r, int peak, int? maxCll, int maxFall, TextWriter output, TextWriter error)
    {
        var size = r.String("size") ?? "1920x1080";
        var (w, h) = ParseSize(size);
        var options = new HdrCheckOptions
        {
            Width = w,
            Height = h,
            Vfg = !r.Flag("no-vfg"),
            Quality = r.String("quality") is { } q ? Vfg.VfgBenchOptions.ParseMode(q) : FrameGenQuality.Medium,
            SdkDirectory = r.String("vfx-sdk"),
            PeakNits = peak,
            MaxCll = maxCll,
            MaxFall = maxFall,
        };
        r.EnsureAllConsumed();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"fmp hdr-pattern --check: {w}x{h}, the HDR path by numbers (P010 → conversion → ring{(options.Vfg ? " / VFG" : "")} → presenter → offscreen HDR10 target, read back)"));
        output.Flush();
        try
        {
            using var check = new HdrCheck(options);
            var lines = check.Run(output);
            output.WriteLine($"  device      {check.Device}{(check.SdkDescription is { } sdk ? "; " + sdk : "")}");
            foreach (var line in lines)
            {
                output.WriteLine($"  {(line.Pass ? "pass" : "FAIL")}        {line.Name}: {line.Detail}");
            }

            var failed = lines.Count(l => !l.Pass);
            output.WriteLine(failed == 0 ? $"  verdict     PASS ({lines.Count} checks)" : $"  verdict     FAIL ({failed} of {lines.Count} checks)");
            return failed == 0 ? CommandDispatcher.ExitOk : CommandDispatcher.ExitCheckFailed;
        }
        catch (HardwareUnavailableException e)
        {
            error.WriteLine($"fmp: hdr-pattern {e.Message}");
            return PatternCommand.ExitUnavailable;
        }
    }

    private static int Show(ArgumentReader r, HdrPatternKind pattern, int peak, int? maxCll, int maxFall, TextWriter output, TextWriter error)
    {
        var (ww, wh) = ParseSize(r.String("window") ?? "1280x720");
        var options = new HdrShowOptions
        {
            Pattern = pattern,
            DurationS = r.Double("duration-s", 10, 0.5, 3600),
            WindowWidth = ww,
            WindowHeight = wh,
            Fullscreen = r.Flag("fullscreen"),
            Output = r.String("output"),
            PeakNits = peak,
            MaxCll = maxCll,
            MaxFall = maxFall,
        };
        r.EnsureAllConsumed();
        var stop = false;
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            Volatile.Write(ref stop, true);
        }

        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        try
        {
            using var show = new HdrShow(options);
            show.Open();
            output.WriteLine($"fmp hdr-pattern: {HdrPatterns.Name(pattern)} for {options.DurationS.ToString("0.#", CultureInfo.InvariantCulture)} s (Escape or closing the window ends it)");
            output.WriteLine($"  pattern     {HdrPatterns.Describe(pattern)}");
            output.WriteLine($"  output      {show.Output}");
            output.Flush();
            show.Run(() => Volatile.Read(ref stop));
            output.WriteLine($"  shown       {show.Presented} presents; at the end: {show.Output}");
            return CommandDispatcher.ExitOk;
        }
        catch (HardwareUnavailableException e)
        {
            error.WriteLine($"fmp: hdr-pattern {e.Message}");
            return PatternCommand.ExitUnavailable;
        }
    }

    private static (int Width, int Height) ParseSize(string text)
    {
        var parts = text.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h) || w < 64 || h < 64 || w > 8192 || h > 8192)
        {
            throw new UsageException($"fmp hdr-pattern: a size is WxH (64 to 8192 each), got '{text}'.");
        }

        return (w & ~1, h & ~1);
    }
}
