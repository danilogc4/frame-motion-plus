using System.Diagnostics;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Vulkan;
using FrameMotionPlus.Vulkan.Diagnostics;
using FrameMotionPlus.Vulkan.Windowing;

namespace FrameMotionPlus.App.Pattern;

/// <summary>Configuration of <c>fmp hdr-pattern</c> on screen.</summary>
internal sealed record HdrShowOptions
{
    public HdrPatternKind Pattern { get; init; } = HdrPatternKind.Ramp;

    public double DurationS { get; init; } = 10;

    public int WindowWidth { get; init; } = 1280;

    public int WindowHeight { get; init; } = 720;

    public bool Fullscreen { get; init; }

    /// <summary>--output: the display fullscreen goes to.</summary>
    public string? Output { get; init; }

    public int PeakNits { get; init; } = ColorMetadata.DefaultPeakNits;

    public int? MaxCll { get; init; }

    public int MaxFall { get; init; }
}

/// <summary>
/// <c>fmp hdr-pattern [pattern]</c>: an HDR test pattern on an HDR10 swapchain, exactly as captured HDR10 frames are shown
/// (the same presenter, the same HDR metadata), drawn at the window's own pixel size so it is shown pixel for pixel. The
/// pattern is written into the presentation ring from the CPU (no CUDA needed) and presented again every refresh until the
/// duration ends, Escape or the window is closed.
/// </summary>
internal sealed class HdrShow : IDisposable
{
    private readonly HdrShowOptions _o;
    private SdlWindow? _window;
    private VulkanInstance? _instance;
    private VulkanDevice? _device;
    private SwapchainTarget? _target;
    private VulkanPresenter? _presenter;
    private DiagnosticRingProducer? _producer;

    public HdrShow(HdrShowOptions options)
    {
        _o = options;
    }

    public string Output { get; private set; } = "";

    public int Presented { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Opens the window and the HDR10 output (69-style failures as <see cref="HardwareUnavailableException"/>).</summary>
    public void Open()
    {
        Require(OperatingSystem.IsLinux(), "needs Linux");
        Require(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")), "needs a Wayland session (WAYLAND_DISPLAY is not set)");
        VulkanLayerPolicy.Isolated.ApplyToProcess();
        Require(VulkanLoader.TryInitialize(out var loaderError), $"needs the Vulkan loader: {loaderError}");
        Require(SdlWindow.IsLibraryAvailable(), "needs SDL3 (libSDL3.so.0)");
        try
        {
            _window = SdlWindow.Create(new SdlWindowOptions { Title = $"fmp hdr-pattern - {HdrPatterns.Name(_o.Pattern)}", Width = _o.WindowWidth, Height = _o.WindowHeight, Output = _o.Output });
            _instance = VulkanInstance.Create(new VulkanInstanceOptions
            {
                ApplicationName = "fmp hdr-pattern",
                Layers = VulkanLayerPolicy.Isolated,
                RequiredExtensions = _window.RequiredInstanceExtensions,
            });
            _device = VulkanDevice.Create(_instance, DeviceSelector.FirstDiscreteGpu);
        }
        catch (Exception e) when (e is InvalidOperationException or VulkanException or VulkanDeviceNotFoundException)
        {
            throw new HardwareUnavailableException($"cannot open a window with Vulkan: {e.Message.Split('\n')[0]}", e);
        }

        _target = new SwapchainTarget(_device, _window);
        if (!_target.SupportsHdr10)
        {
            throw new HardwareUnavailableException("the window's surface offers no HDR10 ST 2084 format: is HDR on for this output (System Settings > Display)?");
        }

        _presenter = new VulkanPresenter(_device, _target, new VulkanPresenterOptions());
        Configure(_window.PixelWidth, _window.PixelHeight);
    }

    /// <summary>Shows the pattern until the duration ends, Escape, the window closes or <paramref name="stop"/> is set.</summary>
    public void Run(Func<bool> stop)
    {
        var window = _window!;
        var presenter = _presenter!;
        var write = Draw();
        var request = Request(write);
        var end = Stopwatch.GetTimestamp() + (long)(_o.DurationS * Stopwatch.Frequency);
        var fullscreenDone = !_o.Fullscreen;
        while (Stopwatch.GetTimestamp() < end && !stop())
        {
            var events = window.PumpEvents();
            if ((events & (WindowEvents.CloseRequested | WindowEvents.EscapePressed)) != 0)
            {
                break;
            }

            if (!fullscreenDone && presenter.IsTimingKnown)
            {
                // Fullscreen once the window is on an output; the pattern is drawn again at the new size (pixel for pixel).
                fullscreenDone = true;
                _ = window.SetFullscreen(true);
                Thread.Sleep(300);
                _ = window.PumpEvents();
                Configure(window.PixelWidth, window.PixelHeight);
                write = Draw();
                request = Request(write);
            }

            if (presenter.IsReadyForPresent() && presenter.TryPresent(request with { SlotIndex = Presented }))
            {
                Presented++;
            }

            while (presenter.TryGetFeedback(out _))
            {
            }

            Thread.Sleep(4);
        }

        // The display is read at the end: after a move to another output SDL reports it only once the compositor has
        // shown the window there (0.3 to 1.8 s after the fullscreen request, measured on GNOME 51).
        if (_o.Fullscreen)
        {
            Console.WriteLine($"  fullscreen  on '{window.CurrentDisplayName ?? "?"}'{(window.OutputName is null ? "" : " (--output)")}");
        }

        Output = Describe();
    }

    private void Configure(int width, int height)
    {
        Width = Math.Max(64, width & ~1);
        Height = Math.Max(64, height & ~1);
        var color = ColorMetadata.Hdr10WithPeak(_o.PeakNits, _o.MaxCll ?? _o.PeakNits, _o.MaxFall);
        var generation = _presenter!.RingGeneration;
        _presenter.Configure(new PresenterConfig { Width = Width, Height = Height, Format = PixelFormat.Rgb10A2, OutputColor = color });
        if (_presenter.RingGeneration != generation || _producer is null)
        {
            _producer?.Dispose();
            using var export = _presenter.Ring!.Export();
            _producer = DiagnosticRingProducer.Create(_instance!, export);
        }

        Output = Describe();
    }

    private ulong Draw()
    {
        var px = HdrPatterns.Render(_o.Pattern, Width, Height);
        var ring = _presenter!.Ring!;
        var bytes = new byte[ring.ContentSizeBytes];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var (r, g, b) = px[(y * Width) + x];
                BitConverter.TryWriteBytes(bytes.AsSpan((y * ring.PitchBytes) + (x * 4)), TestPatterns.PackRgb10A2(r, g, b, 3));
            }
        }

        return _producer!.WritePixels(bytes);
    }

    private PresentRequest Request(ulong write) => new() { SlotIndex = 0, Frame = _presenter!.Ring!.ConsumerFrame(write), TargetTimeNs = 0 };

    private string Describe()
    {
        var t = _target!;
        var metadata = t.HdrMetadataSent is { } m ? $"HDR metadata sent (vkSetHdrMetadataEXT): {m}" : $"no HDR metadata sent ({t.HdrMetadataUnavailableReason ?? "SDR"})";
        return $"{Width}x{Height} pattern on a {t.Width}x{t.Height} swapchain {t.SwapchainFormatName} / {t.SwapchainColorSpaceName} " +
            $"({(_presenter!.IsScalingExact ? "pixel for pixel" : "scaled in linear light")}); transform {_presenter.ActiveTransform}; {metadata}";
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new HardwareUnavailableException(message);
        }
    }

    public void Dispose()
    {
        if (_window is { IsFullscreen: true } w)
        {
            w.Minimize();
        }

        _presenter?.Dispose();
        _producer?.Dispose();
        _target?.Dispose();
        _device?.Dispose();
        _instance?.Dispose();
        _window?.Dispose();
    }
}
