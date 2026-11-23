using System.Reflection;
using System.Runtime.InteropServices;

namespace FrameMotionPlus.Vulkan.Windowing;

/// <summary>
/// The handful of SDL3 entry points the presenter needs, bound to the system library (libSDL3.so.0 on Linux,
/// SDL3.dll on Windows). Signatures follow SDL 3.4 headers; SDL's <c>bool</c> is one byte.
/// </summary>
internal static unsafe partial class SdlNative
{
    private const string Library = "SDL3";

    public const uint InitVideo = 0x00000020;

    public const ulong WindowVulkan = 0x0000000010000000;
    public const ulong WindowResizable = 0x0000000000000020;
    public const ulong WindowHighPixelDensity = 0x0000000000002000;
    public const ulong WindowMinimized = 0x0000000000000040;
    public const ulong WindowHidden = 0x0000000000000008;
    public const ulong WindowOccluded = 0x0000000000000004;
    public const ulong WindowFullscreen = 0x0000000000000001;
    public const ulong WindowInputFocus = 0x0000000000000200;

    public const uint EventQuit = 0x100;
    public const uint EventWindowShown = 0x202;
    public const uint EventWindowHidden = 0x203;
    public const uint EventWindowExposed = 0x204;
    public const uint EventWindowResized = 0x206;
    public const uint EventWindowPixelSizeChanged = 0x207;
    public const uint EventWindowMinimized = 0x209;
    public const uint EventWindowMaximized = 0x20A;
    public const uint EventWindowRestored = 0x20B;
    public const uint EventWindowCloseRequested = 0x210;
    public const uint EventWindowDisplayChanged = 0x213;
    public const uint EventWindowOccluded = 0x216;
    public const uint EventKeyDown = 0x300;

    public const uint KeycodeEscape = 0x1B;

    /// <summary>SDLK_H, SDLK_A and SDLK_Q (SDL3 keycodes of letter keys are their lower-case characters).</summary>
    public const uint KeycodeH = 0x68;
    public const uint KeycodeA = 0x61;
    public const uint KeycodeQ = 0x71;

    static SdlNative()
    {
        // The SONAME of the system library, so no -dev symlink is needed and nothing bundled is picked up instead.
        NativeLibrary.SetDllImportResolver(typeof(SdlNative).Assembly, Resolve);
    }

    [LibraryImport(Library, EntryPoint = "SDL_SetHint", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetHint(string name, string value);

    [LibraryImport(Library, EntryPoint = "SDL_Init")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Init(uint flags);

    [LibraryImport(Library, EntryPoint = "SDL_QuitSubSystem")]
    public static partial void QuitSubSystem(uint flags);

    [LibraryImport(Library, EntryPoint = "SDL_GetError")]
    public static partial byte* GetError();

    [LibraryImport(Library, EntryPoint = "SDL_GetCurrentVideoDriver")]
    public static partial byte* GetCurrentVideoDriver();

    [LibraryImport(Library, EntryPoint = "SDL_CreateWindow", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint CreateWindow(string title, int width, int height, ulong flags);

    [LibraryImport(Library, EntryPoint = "SDL_DestroyWindow")]
    public static partial void DestroyWindow(nint window);

    [LibraryImport(Library, EntryPoint = "SDL_GetWindowID")]
    public static partial uint GetWindowId(nint window);

    [LibraryImport(Library, EntryPoint = "SDL_GetWindowFlags")]
    public static partial ulong GetWindowFlags(nint window);

    [LibraryImport(Library, EntryPoint = "SDL_GetWindowSizeInPixels")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GetWindowSizeInPixels(nint window, int* width, int* height);

    [LibraryImport(Library, EntryPoint = "SDL_SetWindowSize")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowSize(nint window, int width, int height);

    [LibraryImport(Library, EntryPoint = "SDL_SetWindowFullscreen")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowFullscreen(nint window, [MarshalAs(UnmanagedType.U1)] bool fullscreen);

    [LibraryImport(Library, EntryPoint = "SDL_MinimizeWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool MinimizeWindow(nint window);

    [LibraryImport(Library, EntryPoint = "SDL_HideWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HideWindow(nint window);

    [LibraryImport(Library, EntryPoint = "SDL_HideCursor")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HideCursor();

    [LibraryImport(Library, EntryPoint = "SDL_ShowCursor")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ShowCursor();

    [LibraryImport(Library, EntryPoint = "SDL_SyncWindow")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SyncWindow(nint window);

    [LibraryImport(Library, EntryPoint = "SDL_GetDisplayForWindow")]
    public static partial uint GetDisplayForWindow(nint window);

    [LibraryImport(Library, EntryPoint = "SDL_GetCurrentDisplayMode")]
    public static partial DisplayMode* GetCurrentDisplayMode(uint displayId);

    /// <summary>The connected displays (free the array with <see cref="Free"/>).</summary>
    [LibraryImport(Library, EntryPoint = "SDL_GetDisplays")]
    public static partial uint* GetDisplays(int* count);

    [LibraryImport(Library, EntryPoint = "SDL_GetDisplayName")]
    public static partial byte* GetDisplayName(uint displayId);

    [LibraryImport(Library, EntryPoint = "SDL_GetDisplayBounds")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GetDisplayBounds(uint displayId, Rect* rect);

    /// <summary>
    /// On Wayland a toplevel cannot place itself (this returns false), but SDL keeps the display asked for and uses
    /// it when the window then goes fullscreen-desktop (xdg_toplevel.set_fullscreen on that output).
    /// </summary>
    [LibraryImport(Library, EntryPoint = "SDL_SetWindowPosition")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetWindowPosition(nint window, int x, int y);

    [LibraryImport(Library, EntryPoint = "SDL_free")]
    public static partial void Free(void* memory);

    /// <summary>SDL_WINDOWPOS_CENTERED_DISPLAY(displayId).</summary>
    public static int WindowPosCenteredDisplay(uint displayId) => unchecked((int)(0x2FFF0000u | displayId));

    [LibraryImport(Library, EntryPoint = "SDL_PollEvent")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool PollEvent(Event* sdlEvent);

    [LibraryImport(Library, EntryPoint = "SDL_Vulkan_GetInstanceExtensions")]
    public static partial byte** VulkanGetInstanceExtensions(uint* count);

    [LibraryImport(Library, EntryPoint = "SDL_Vulkan_CreateSurface")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool VulkanCreateSurface(nint window, nint instance, nint allocator, ulong* surface);

    [LibraryImport(Library, EntryPoint = "SDL_Vulkan_DestroySurface")]
    public static partial void VulkanDestroySurface(nint instance, ulong surface, nint allocator);

    public static string LastError() => Interop.Utf8.FromPointer(GetError()) ?? "unknown SDL error";

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name == Library && OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libSDL3.so.0", out var handle))
        {
            return handle;
        }

        return 0;
    }

    /// <summary>SDL_Event: a 128-byte union; only the window and keyboard members are read.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct Event
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(16)]
        public uint WindowId;

        /// <summary>SDL_WindowEvent.data1 (e.g. width).</summary>
        [FieldOffset(20)]
        public int Data1;

        /// <summary>SDL_WindowEvent.data2 (e.g. height).</summary>
        [FieldOffset(24)]
        public int Data2;

        /// <summary>SDL_KeyboardEvent.key (SDL_Keycode).</summary>
        [FieldOffset(28)]
        public uint Key;

        [FieldOffset(37)]
        public byte Repeat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int X;
        public int Y;
        public int W;
        public int H;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayMode
    {
        public uint DisplayId;
        public uint Format;
        public int W;
        public int H;
        public float PixelDensity;
        public float RefreshRate;
        public int RefreshRateNumerator;
        public int RefreshRateDenominator;
        public nint Internal;
    }
}
