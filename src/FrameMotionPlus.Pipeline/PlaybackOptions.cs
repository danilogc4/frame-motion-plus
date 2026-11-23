using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Settings;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Threading;
using FrameMotionPlus.Sources.V4l2;

namespace FrameMotionPlus.Pipeline;

/// <summary>Which frame generator writes the generated slots.</summary>
public enum GeneratorKind
{
    /// <summary>NVIDIA Maxine VFG (needs the VFX SDK), on the generator thread: its Enqueue blocks.</summary>
    Vfg = 0,

    /// <summary>Linear blend (CUDA), the debug baseline.</summary>
    Blend = 1,

    /// <summary>The nearest real frame copied (CUDA), the A/B baseline.</summary>
    Repeat = 2,

    /// <summary>No generator: real frames only (the scheduler's multiplier is 1).</summary>
    None = 3,
}

/// <summary>Where frames are shown.</summary>
public enum DisplayKind
{
    /// <summary>An SDL3 window on Wayland: FIFO swapchain, pacing locked to present feedback.</summary>
    Window = 0,

    /// <summary>An offscreen target (no display server); presents complete with their GPU work.</summary>
    Offscreen = 1,
}

/// <summary>How output slots are timed against the display (the design doc's two clock modes).</summary>
public enum RefreshMode
{
    /// <summary>Variable refresh when the window is fullscreen, the display follows the presents (measured before
    /// playback) and the output's highest rate can show the source's rate times the multiplier; fixed refresh otherwise.</summary>
    Auto = 0,

    /// <summary>Slots are the output's refreshes; each present is handed over before the compositor's latch for its refresh.</summary>
    Fixed = 1,

    /// <summary>Slots follow the source (V = ts + D + kT/M); each present is handed over at its slot's time and the display
    /// refreshes for it.</summary>
    Vrr = 2,
}

/// <summary>The clock the pacing runs on.</summary>
public enum PlaybackClock
{
    /// <summary><c>CLOCK_MONOTONIC</c>: real time, real waits.</summary>
    Real = 0,

    /// <summary>
    /// A virtual clock the pacing thread advances to each decision time (offscreen only): every slot is decided exactly
    /// on time, the threads wait for each other instead of skipping, measured generation times are not fed back, and
    /// frames count as available a fixed lead before their content time. Given the same file and options the scheduler
    /// makes the same decisions every run, so every presented frame can be checked against what it must show.
    /// </summary>
    Virtual = 1,
}

/// <summary>Configuration of a <see cref="PlaybackSession"/> (what <c>fmp play</c> resolves its options into).</summary>
public sealed record PlaybackOptions
{
    /// <summary>The video file, or with <see cref="Capture"/> the V4L2 capture device (/dev/videoN).</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Capture live from the V4L2 device at <see cref="Path"/> instead of playing a file (Phase 5). Its device is
    /// <see cref="Path"/>; the session supplies the streams and kernels and sizes its history. Real clock only.
    /// </summary>
    public CaptureSourceOptions? Capture { get; init; }

    public GeneratorKind Generator { get; init; } = GeneratorKind.Vfg;

    public FrameGenQuality Quality { get; init; } = FrameGenQuality.Medium;

    /// <summary>
    /// VFG only: load the other quality's instance too (High, or Medium when <see cref="Quality"/> is High) at start, so Q
    /// in the window (<see cref="PlaybackSession.RequestQualityToggle"/>) switches between the two during playback without
    /// a load. Costs that instance's start-up time and GPU memory. A failed load keeps one instance (said once).
    /// </summary>
    public bool QualitySwitch { get; init; }

    /// <summary>
    /// VFG only: its automatic shot-change detection (on by default). VFG has no setting for how readily it calls a cut,
    /// and it takes large frame-to-frame change (fast motion, an object close to the camera, foliage rushing past) for
    /// one: the generated frame is then a copy of a real frame, so the picture falls back to the source's rate until the
    /// motion calms down. Off, every pair is interpolated: fast motion keeps its generated frames, and a real cut shows
    /// one generated frame that is a morph of the two shots (the app has no cut detector of its own; a source's own
    /// discontinuities, a seek or a loop, are still never interpolated across). Off, VFG's call only enqueues instead of
    /// blocking the generator thread for the generation. Both of Q's instances are loaded the same way.
    /// </summary>
    public bool ShotChangeDetection { get; init; } = true;

    /// <summary>
    /// The cap on output frames per source frame (the scheduler's maximum multiplier). The scheduler picks the largest
    /// ratio up to this the display allows: a 60 fps file at 2 on a 120 Hz panel shows a new frame every refresh, on a
    /// 240 Hz panel every other refresh; at 4 on the 240 Hz panel every refresh. 1 shows real frames only.
    /// </summary>
    public int Multiplier { get; init; } = 2;

    /// <summary>Asked for as an output rate instead: the multiplier cap is this over the file's frame rate (rounded).</summary>
    public double? OutputHz { get; init; }

    /// <summary>Start the file again at its end (each lap is a discontinuity: nothing is interpolated across it).</summary>
    public bool Loop { get; init; }

    /// <summary>Stop after this much playback (after the start-up); 0: until the end of the file or a stop.</summary>
    public long DurationNs { get; init; }

    /// <summary>Stop after this many output slots (after the start-up); 0: no limit.</summary>
    public long MaxSlots { get; init; }

    public DisplayKind Display { get; init; } = DisplayKind.Window;

    public PlaybackClock Clock { get; init; } = PlaybackClock.Real;

    /// <summary>The refresh rate an offscreen target stands for (nothing paces it otherwise).</summary>
    public FrameRate OffscreenRate { get; init; } = FrameRate.FromHz(120);

    /// <summary>Offscreen target size; 0: the source's size (a 1:1 present, so frames can be checked bit for bit).</summary>
    public int OffscreenWidth { get; init; }

    public int OffscreenHeight { get; init; }

    /// <summary>Window size in window coordinates.</summary>
    public int WindowWidth { get; init; } = 1280;

    public int WindowHeight { get; init; } = 720;

    /// <summary>Borderless fullscreen on the output the window is on, once it has shown a frame.</summary>
    public bool Fullscreen { get; init; }

    /// <summary>--output: the display fullscreen goes to (a name, a part of it, a number or @X,Y); null: the window's.</summary>
    public string? Output { get; init; }

    /// <summary>Fixed refresh, variable refresh, or variable when the display does it (windowed: fixed).</summary>
    public RefreshMode Refresh { get; init; } = RefreshMode.Auto;

    /// <summary>Default of <see cref="LateGeneratedNs"/>: a quarter of the 16.7 ms between slots at 2x of a 29.97 source.</summary>
    public const long LateGeneratedDefaultNs = 4 * Nanoseconds.PerMillisecond;

    /// <summary>
    /// Variable refresh: a generated slot whose next real frame comes too late for it is shown up to this much after its
    /// planned time (its content moved to match) instead of being skipped (<see cref="FrameMotionPlus.Core.Scheduling.SchedulerOptions.VrrLateGeneratedNs"/>).
    /// 0 skips it, the behaviour before 2026-11-19. No effect under fixed refresh.
    /// </summary>
    public long LateGeneratedNs { get; init; } = LateGeneratedDefaultNs;

    /// <summary>
    /// Variable refresh with the slots at the output's ceiling (no more than 1% longer than its shortest refresh period: 4x
    /// a 29.97 fps source on a 119.88 Hz output, 8x on 240.016 Hz), whatever the generator: when the display has shown 30
    /// presents in a row late (more than half a refresh after their slot's time), the next generated slot is not presented
    /// (at most one every 5 s; none again until a show is on time if one did not help), so the display, which at its
    /// ceiling cannot catch up by itself, gets a free refresh. Inert in every other case. <c>--ceiling-pacing off</c> turns it off
    /// (running behind at the ceiling is reported either way).
    /// </summary>
    public bool CeilingPacing { get; init; } = true;

    /// <summary>
    /// A file within 0.5% of a whole number of refreshes per frame plays that much slower or faster so it phase-locks to
    /// fixed refresh (60.000 fps on 119.88 Hz: 0.1% slower). Never a live source.
    /// </summary>
    public bool ResampleToDisplay { get; init; } = true;

    /// <summary>Vulkan validation layer with synchronization checks.</summary>
    public bool Validate { get; init; }

    /// <summary>Excluded from the statistics and the allocation and GC counts at the start of playback.</summary>
    public long WarmupNs { get; init; } = 2 * Nanoseconds.PerSecond;

    /// <summary>
    /// The present lead to start from (and the lowest it adapts down to): how long before its refresh a slot's content must
    /// be ready, on top of the generation time and the scheduler's margin, so its present is handed over before the
    /// compositor latches it. -1: 1.5 refresh periods for a window; 0 offscreen.
    /// </summary>
    public long PresentLeadNs { get; init; } = -1;

    /// <summary>
    /// The least a present is assumed to need before its refresh (offscreen: how early a slot is submitted; windowed: a floor
    /// under the measured, adaptive present lead used for fillers and for an empty queue's prediction).
    /// </summary>
    public long LatchLeadNs { get; init; } = Nanoseconds.PerMillisecond;

    /// <summary>VFX SDK directory (else <c>$FMP_VFX_SDK</c>, the settings file, a checkout's lib/VideoFX, /usr/local/VideoFX).</summary>
    public string? VfxSdkDirectory { get; init; }

    /// <summary>Scheduler, display and generator settings (a settings file); null: the defaults.</summary>
    public FmpSettings? Settings { get; init; }

    /// <summary>
    /// The live source's sound, delayed to its picture (Phase 8): the control thread gives it the measured video latency
    /// (a real frame's first pixel less its capture timestamp, smoothed: <see cref="LatencyFollower"/>) as it changes, and
    /// stops it (which returns at once) when playback ends. Its owner starts and disposes it. Nothing on a per-frame path
    /// touches it, and nothing waits for it.
    /// </summary>
    public IAudioDelay? Audio { get; init; }

    /// <summary>Write the per-slot timeline (Core's telemetry records) to this CSV file.</summary>
    public string? CsvPath { get; init; }

    /// <summary>Per-slot timeline records kept for the CSV and the summary between collector passes.</summary>
    public int TelemetryCapacity { get; init; } = 8192;

    /// <summary>Tests only: the verifier's buffer in rows (0: sized from the duration and the display's rate).</summary>
    internal int VerifierRows { get; init; }

    /// <summary>Frames decoded and converted ahead of the pacing thread.</summary>
    public int DecodeAhead { get; init; } = 4;

    /// <summary>Frames the pacing thread may hold at once: the scheduler's start-up frames plus the pair in use.</summary>
    public int HistoryDepth { get; init; } = 10;

    /// <summary>How the pacing, generator and present threads are scheduled (default: SCHED_OTHER, 1 ns timer slack).</summary>
    public ThreadSchedulingRequest Threads { get; init; } = ThreadSchedulingRequest.Default;

    /// <summary>
    /// Windowed: the present lead adapts to the output (it goes up at once when a present's content misses the compositor's
    /// latch, and comes back down slowly). False keeps it at <see cref="PresentLeadNs"/> (diagnostics, tests).
    /// </summary>
    public bool AdaptiveLead { get; init; } = true;

    /// <summary>Bounded waits of the teardown and of the virtual clock's lockstep (a hang becomes a failure).</summary>
    public long StallTimeoutNs { get; init; } = 10 * Nanoseconds.PerSecond;

    internal void Check()
    {
        ArgumentException.ThrowIfNullOrEmpty(Path);
        ArgumentOutOfRangeException.ThrowIfLessThan(Multiplier, 1, nameof(Multiplier));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Multiplier, 8, nameof(Multiplier));
        ArgumentOutOfRangeException.ThrowIfNegative(DurationNs, nameof(DurationNs));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxSlots, nameof(MaxSlots));
        ArgumentOutOfRangeException.ThrowIfNegative(WarmupNs, nameof(WarmupNs));
        ArgumentOutOfRangeException.ThrowIfNegative(LatchLeadNs, nameof(LatchLeadNs));
        ArgumentOutOfRangeException.ThrowIfLessThan(PresentLeadNs, -1, nameof(PresentLeadNs));
        if (Capture is not null && Clock == PlaybackClock.Virtual)
        {
            throw new ArgumentException("A live capture runs on the real clock: its frames come when they come.", nameof(Clock));
        }

        if (Clock == PlaybackClock.Virtual && Display != DisplayKind.Offscreen)
        {
            throw new ArgumentException("The virtual clock needs the offscreen display: a window's refreshes run on real time.", nameof(Clock));
        }

        if (!OffscreenRate.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(OffscreenRate), "The offscreen rate must be set.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(OffscreenWidth, nameof(OffscreenWidth));
        ArgumentOutOfRangeException.ThrowIfNegative(OffscreenHeight, nameof(OffscreenHeight));
        ArgumentOutOfRangeException.ThrowIfLessThan(WindowWidth, 64, nameof(WindowWidth));
        ArgumentOutOfRangeException.ThrowIfLessThan(WindowHeight, 64, nameof(WindowHeight));
        ArgumentOutOfRangeException.ThrowIfLessThan(TelemetryCapacity, 256, nameof(TelemetryCapacity));
        if ((TelemetryCapacity & (TelemetryCapacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TelemetryCapacity), TelemetryCapacity, "Must be a power of two.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(DecodeAhead, 1, nameof(DecodeAhead));
        ArgumentOutOfRangeException.ThrowIfLessThan(HistoryDepth, 4, nameof(HistoryDepth));
        ArgumentNullException.ThrowIfNull(Threads, nameof(Threads));
        ArgumentOutOfRangeException.ThrowIfLessThan(StallTimeoutNs, Nanoseconds.PerSecond, nameof(StallTimeoutNs));
    }
}
