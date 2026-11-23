using System.Globalization;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Vulkan.Interop;

namespace FrameMotionPlus.Vulkan.Windowing;

public sealed record SdlWindowOptions
{
    public string Title { get; init; } = "Frame Motion Plus";

    /// <summary>Requested size in window coordinates (the pixel size is larger on a scaled display).</summary>
    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 720;

    public bool Resizable { get; init; } = true;

    /// <summary>SDL video driver to force (SDL_VIDEO_DRIVER). The design uses Wayland; null lets SDL choose.</summary>
    public string? VideoDriver { get; init; } = "wayland";

    /// <summary>
    /// The display fullscreen goes to (<c>--output</c>): a name (exact, else a unique part of it, any case), a 1-based
    /// number in SDL's order, or <c>@X,Y</c> for the display at that desktop position. Null: the display the window is
    /// on (on Wayland the compositor picks it, usually where the pointer is). A window cannot be placed on Wayland:
    /// this only steers fullscreen.
    /// </summary>
    public string? Output { get; init; }
}

[Flags]
public enum WindowEvents
{
    None = 0,
    CloseRequested = 1 << 0,
    Resized = 1 << 1,
    Minimized = 1 << 2,
    Restored = 1 << 3,
    EscapePressed = 1 << 4,
    DisplayChanged = 1 << 5,

    /// <summary>The window became occluded (e.g. the compositor suspended it) or visible again.</summary>
    OcclusionChanged = 1 << 6,

    /// <summary>H: switch between SDR and HDR10 by hand.</summary>
    HdrTogglePressed = 1 << 7,

    /// <summary>A: follow the signal's SDR/HDR10 state again.</summary>
    FollowPressed = 1 << 8,

    /// <summary>Q: switch the frame generator's quality (Medium and High).</summary>
    QualityTogglePressed = 1 << 9,
}

/// <summary>
/// An SDL3 window that can carry a Vulkan surface. Create it, pump its events and dispose it on one thread (the
/// window thread). The presenter thread only reads <see cref="PixelWidth"/>/<see cref="PixelHeight"/>,
/// <see cref="IsMinimized"/>, <see cref="IsOccluded"/> and <see cref="RefreshRate"/>, which the pump keeps current.
/// </summary>
public sealed unsafe class SdlWindow : IDisposable
{
    private nint _window;
    private readonly uint _windowId;
    private long _pixelSize;
    private int _minimized;
    private int _occluded;
    private long _refresh;
    private int _refreshKnown;
    private int _presentShown;
    private uint _targetDisplay;

    private SdlWindow(nint window)
    {
        _window = window;
        _windowId = SdlNative.GetWindowId(window);
        VideoDriver = Utf8.FromPointer(SdlNative.GetCurrentVideoDriver()) ?? string.Empty;
        RequiredInstanceExtensions = QueryInstanceExtensions();
        UpdatePixelSize();
        UpdateRefreshRate();
    }

    /// <summary>The SDL video driver in use ("wayland" when forced).</summary>
    public string VideoDriver { get; }

    /// <summary>Instance extensions SDL needs for a surface on this window (pass to <see cref="VulkanInstanceOptions.RequiredExtensions"/>).</summary>
    public IReadOnlyList<string> RequiredInstanceExtensions { get; }

    public int PixelWidth => (int)(Volatile.Read(ref _pixelSize) >> 32);

    public int PixelHeight => (int)(Volatile.Read(ref _pixelSize) & 0xFFFFFFFF);

    public bool IsMinimized => Volatile.Read(ref _minimized) != 0;

    /// <summary>SDL reports the window occluded (on Wayland: the compositor suspended it, e.g. after a user minimise).</summary>
    public bool IsOccluded => Volatile.Read(ref _occluded) != 0;

    public bool CloseRequested { get; private set; }

    /// <summary>
    /// False while <see cref="RefreshRate"/> is a guess. A new window is on no output until it has shown a frame (SDL
    /// assumes the primary display meanwhile); the rate becomes known when SDL reports the window's display or, after
    /// a frame has been shown, at the next <see cref="PumpEvents"/>.
    /// </summary>
    public bool RefreshRateKnown => Volatile.Read(ref _refreshKnown) != 0;

    /// <summary>Refresh rate of the display the window is on (60 Hz if SDL cannot tell); see <see cref="RefreshRateKnown"/>.</summary>
    public FrameRate RefreshRate
    {
        get
        {
            var packed = Volatile.Read(ref _refresh);
            return packed == 0 ? FrameRate.FromHz(60) : new FrameRate(packed >> 32, packed & 0xFFFFFFFF);
        }
    }

    internal nint Handle => _window;

    /// <summary>Probes whether SDL3 can be loaded at all (for tests that need a display).</summary>
    public static bool IsLibraryAvailable()
    {
        try
        {
            _ = SdlNative.GetError();
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static SdlWindow Create(SdlWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.VideoDriver is { } driver)
        {
            SdlNative.SetHint("SDL_VIDEO_DRIVER", driver);
        }

        if (!SdlNative.Init(SdlNative.InitVideo))
        {
            throw new InvalidOperationException($"SDL video init failed: {SdlNative.LastError()}");
        }

        uint target = 0;
        string? targetName = null;
        if (!string.IsNullOrWhiteSpace(options.Output))
        {
            try
            {
                (target, targetName) = ResolveOutput(options.Output.Trim());
            }
            catch (InvalidOperationException)
            {
                SdlNative.QuitSubSystem(SdlNative.InitVideo);
                throw;
            }
        }

        var flags = SdlNative.WindowVulkan | SdlNative.WindowHighPixelDensity | (options.Resizable ? SdlNative.WindowResizable : 0);
        var window = SdlNative.CreateWindow(options.Title, options.Width, options.Height, flags);
        if (window == 0)
        {
            var error = SdlNative.LastError();
            SdlNative.QuitSubSystem(SdlNative.InitVideo);
            throw new InvalidOperationException($"SDL_CreateWindow failed: {error}");
        }

        return new SdlWindow(window) { _targetDisplay = target, OutputName = targetName };
    }

    /// <summary>The name of the display the window is on now (SDL's), or null before it is on one.</summary>
    public string? CurrentDisplayName
    {
        get
        {
            var display = _window == 0 ? 0 : SdlNative.GetDisplayForWindow(_window);
            return display == 0 ? null : Utf8.FromPointer(SdlNative.GetDisplayName(display));
        }
    }

    /// <summary>The display <see cref="SdlWindowOptions.Output"/> chose (its SDL name), or null.</summary>
    public string? OutputName { get; private init; }

    /// <summary>The connected displays as SDL sees them: id, name and desktop bounds (SDL's video subsystem up).</summary>
    public static IReadOnlyList<(uint Id, string Name, int X, int Y, int Width, int Height)> Displays()
    {
        var list = new List<(uint, string, int, int, int, int)>();
        var count = 0;
        var ids = SdlNative.GetDisplays(&count);
        if (ids == null)
        {
            return list;
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                SdlNative.Rect r;
                if (!SdlNative.GetDisplayBounds(ids[i], &r))
                {
                    r = default;
                }

                list.Add((ids[i], Utf8.FromPointer(SdlNative.GetDisplayName(ids[i])) ?? $"display {ids[i]}", r.X, r.Y, r.W, r.H));
            }
        }
        finally
        {
            SdlNative.Free(ids);
        }

        return list;
    }

    private static (uint Id, string Name) ResolveOutput(string spec)
    {
        var displays = Displays();
        string Listing() => string.Join("; ", displays.Select((d, i) =>
            string.Create(CultureInfo.InvariantCulture, $"{i + 1} '{d.Name}' at @{d.X},{d.Y} ({d.Width}x{d.Height})")));

        if (spec.StartsWith('@'))
        {
            var parts = spec[1..].Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                foreach (var d in displays)
                {
                    if (d.X == x && d.Y == y)
                    {
                        return (d.Id, d.Name);
                    }
                }
            }

            throw new InvalidOperationException($"no display at '--output {spec}'; displays: {Listing()}");
        }

        if (int.TryParse(spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            if (number >= 1 && number <= displays.Count)
            {
                return (displays[number - 1].Id, displays[number - 1].Name);
            }

            throw new InvalidOperationException($"no display number {number} for '--output'; displays: {Listing()}");
        }

        var exact = displays.Where(d => string.Equals(d.Name, spec, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact : displays.Where(d => d.Name.Contains(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => (matches[0].Id, matches[0].Name),
            0 => throw new InvalidOperationException($"no display matches '--output {spec}'; displays: {Listing()}"),
            _ => throw new InvalidOperationException($"'--output {spec}' matches {matches.Count} displays (give its number or @X,Y); displays: {Listing()}"),
        };
    }

    /// <summary>Drains SDL's event queue (allocation-free) and reports what happened to this window.</summary>
    public WindowEvents PumpEvents()
    {
        ObjectDisposedException.ThrowIf(_window == 0, this);
        var result = WindowEvents.None;
        SdlNative.Event e;
        while (SdlNative.PollEvent(&e))
        {
            switch (e.Type)
            {
                case SdlNative.EventQuit:
                    result |= WindowEvents.CloseRequested;
                    break;
                case SdlNative.EventWindowCloseRequested when e.WindowId == _windowId:
                    result |= WindowEvents.CloseRequested;
                    break;
                case SdlNative.EventWindowResized or SdlNative.EventWindowPixelSizeChanged when e.WindowId == _windowId:
                    UpdatePixelSize();
                    result |= WindowEvents.Resized;
                    break;
                case SdlNative.EventWindowMinimized or SdlNative.EventWindowHidden when e.WindowId == _windowId:
                    Volatile.Write(ref _minimized, 1);
                    result |= WindowEvents.Minimized;
                    break;
                case SdlNative.EventWindowRestored or SdlNative.EventWindowShown or SdlNative.EventWindowMaximized when e.WindowId == _windowId:
                    Volatile.Write(ref _minimized, 0);
                    UpdatePixelSize();
                    result |= WindowEvents.Restored;
                    break;
                case SdlNative.EventWindowDisplayChanged when e.WindowId == _windowId:
                    UpdateRefreshRate();
                    Volatile.Write(ref _refreshKnown, 1);
                    result |= WindowEvents.DisplayChanged;
                    break;
                case SdlNative.EventWindowOccluded or SdlNative.EventWindowExposed when e.WindowId == _windowId:
                    result |= UpdateOcclusion() ? WindowEvents.OcclusionChanged : WindowEvents.None;
                    break;
                case SdlNative.EventKeyDown when e.Key == SdlNative.KeycodeEscape && e.Repeat == 0:
                    result |= WindowEvents.EscapePressed;
                    break;
                case SdlNative.EventKeyDown when e.Key == SdlNative.KeycodeH && e.Repeat == 0:
                    result |= WindowEvents.HdrTogglePressed;
                    break;
                case SdlNative.EventKeyDown when e.Key == SdlNative.KeycodeA && e.Repeat == 0:
                    result |= WindowEvents.FollowPressed;
                    break;
                case SdlNative.EventKeyDown when e.Key == SdlNative.KeycodeQ && e.Repeat == 0:
                    result |= WindowEvents.QualityTogglePressed;
                    break;
            }
        }

        if ((result & WindowEvents.CloseRequested) != 0)
        {
            CloseRequested = true;
        }

        // Once a frame has been shown the window is on an output and SDL's display for it is the real one.
        if (Volatile.Read(ref _refreshKnown) == 0 && Volatile.Read(ref _presentShown) != 0)
        {
            UpdateRefreshRate();
            Volatile.Write(ref _refreshKnown, 1);
        }

        return result;
    }

    /// <summary>Called (from any thread) when a present to this window has been reported complete.</summary>
    internal void NotePresentShown() => Volatile.Write(ref _presentShown, 1);

    /// <summary>Asks for a new size (window coordinates) and waits briefly for the compositor to apply it.</summary>
    public void SetSize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_window == 0, this);
        SdlNative.SetWindowSize(_window, width, height);
        SdlNative.SyncWindow(_window);
        UpdatePixelSize();
    }

    /// <summary>
    /// Borderless fullscreen on the display the window is on now (SDL's fullscreen-desktop mode: no mode change), or
    /// back to a window. Call it once the window has shown a frame, so it is on an output; a new window is on none and
    /// SDL would pick the primary display. Waits briefly for the compositor to apply it. Window thread.
    /// </summary>
    public bool SetFullscreen(bool fullscreen)
    {
        ObjectDisposedException.ThrowIf(_window == 0, this);

        // No mouse pointer over a fullscreen picture: a pointer drawn over it is one more thing the compositor must
        // composite (KWin's "automatic" variable refresh wants the fullscreen window alone on the output). SDL hides it
        // over its own windows only; elsewhere it stays.
        _ = fullscreen ? SdlNative.HideCursor() : SdlNative.ShowCursor();

        // --output: ask for the chosen display first. Wayland refuses the move itself (false is expected), but SDL
        // keeps the display and hands its wl_output to the fullscreen request, so the compositor puts the window there.
        if (fullscreen && _targetDisplay != 0)
        {
            var at = SdlNative.WindowPosCenteredDisplay(_targetDisplay);
            _ = SdlNative.SetWindowPosition(_window, at, at);
        }

        var ok = SdlNative.SetWindowFullscreen(_window, fullscreen);
        SdlNative.SyncWindow(_window);
        UpdatePixelSize();
        return ok;
    }

    /// <summary>Whether the window has the keyboard focus, i.e. is the compositor's active window (SDL's window flags).</summary>
    public bool HasInputFocus => _window != 0 && (SdlNative.GetWindowFlags(_window) & SdlNative.WindowInputFocus) != 0;

    /// <summary>Whether the window is fullscreen now (SDL's window flags).</summary>
    public bool IsFullscreen => _window != 0 && (SdlNative.GetWindowFlags(_window) & SdlNative.WindowFullscreen) != 0;

    public void Minimize()
    {
        ObjectDisposedException.ThrowIf(_window == 0, this);
        SdlNative.MinimizeWindow(_window);
    }

    /// <summary>
    /// Takes the window off the screen before the teardown, while its swapchain images still exist, and waits for the
    /// compositor to take it in. On GNOME (Mutter 51, NVIDIA) a fullscreen window whose images and surface were
    /// destroyed while it was still shown left its last frame on the output until a workspace switch (22/11/2026).
    /// Window thread.
    /// </summary>
    public void HideForTeardown(TimeSpan settle)
    {
        if (_window == 0)
        {
            return;
        }

        _ = SdlNative.ShowCursor();
        _ = SdlNative.HideWindow(_window);
        _ = SdlNative.SyncWindow(_window);
        var until = Environment.TickCount64 + (long)settle.TotalMilliseconds;
        while (Environment.TickCount64 < until)
        {
            _ = PumpEvents();
            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        if (_window == 0)
        {
            return;
        }

        SdlNative.DestroyWindow(_window);
        _window = 0;
        SdlNative.QuitSubSystem(SdlNative.InitVideo);
    }

    internal ulong CreateSurface(VulkanInstance instance)
    {
        ulong surface;
        if (!SdlNative.VulkanCreateSurface(_window, (nint)instance.Handle.Handle, 0, &surface))
        {
            throw new InvalidOperationException($"SDL_Vulkan_CreateSurface failed: {SdlNative.LastError()}");
        }

        return surface;
    }

    internal static void DestroySurface(VulkanInstance instance, ulong surface) =>
        SdlNative.VulkanDestroySurface((nint)instance.Handle.Handle, surface, 0);

    private static string[] QueryInstanceExtensions()
    {
        uint count;
        var names = SdlNative.VulkanGetInstanceExtensions(&count);
        if (names == null)
        {
            throw new InvalidOperationException($"SDL_Vulkan_GetInstanceExtensions failed: {SdlNative.LastError()}");
        }

        var list = new string[count];
        for (var i = 0; i < count; i++)
        {
            list[i] = Utf8.FromPointer(names[i]) ?? string.Empty;
        }

        return list;
    }

    private void UpdatePixelSize()
    {
        int w;
        int h;
        if (!SdlNative.GetWindowSizeInPixels(_window, &w, &h))
        {
            return;
        }

        var minimized = (SdlNative.GetWindowFlags(_window) & SdlNative.WindowMinimized) != 0;
        Volatile.Write(ref _minimized, minimized ? 1 : 0);
        Volatile.Write(ref _pixelSize, ((long)w << 32) | (uint)h);
    }

    private bool UpdateOcclusion()
    {
        var occluded = (SdlNative.GetWindowFlags(_window) & SdlNative.WindowOccluded) != 0 ? 1 : 0;
        return Interlocked.Exchange(ref _occluded, occluded) != occluded;
    }

    private void UpdateRefreshRate()
    {
        var display = SdlNative.GetDisplayForWindow(_window);
        var mode = display == 0 ? null : SdlNative.GetCurrentDisplayMode(display);
        if (mode == null)
        {
            return;
        }

        long num = mode->RefreshRateNumerator;
        long den = mode->RefreshRateDenominator;
        if (num <= 0 || den <= 0)
        {
            if (mode->RefreshRate <= 0)
            {
                return;
            }

            var rate = FrameRate.FromDouble(mode->RefreshRate);
            num = rate.Numerator;
            den = rate.Denominator;
        }

        Volatile.Write(ref _refresh, (num << 32) | den);
    }
}
