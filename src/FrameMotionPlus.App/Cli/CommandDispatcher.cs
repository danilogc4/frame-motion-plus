using System.Reflection;

namespace FrameMotionPlus.App.Cli;

internal static class CommandDispatcher
{
    public const int ExitOk = 0;
    public const int ExitCheckFailed = 1;
    public const int ExitUsage = 2;

    /// <summary>A bug: an exception nothing else handled. EX_SOFTWARE in sysexits.h.</summary>
    public const int ExitInternalError = 70;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.Write(Usage);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        if (args[0] is "--version" or "version")
        {
            output.WriteLine($"fmp {Version}");
            return ExitOk;
        }

        var rest = args.AsSpan(1).ToArray();
        try
        {
            return args[0] switch
            {
                "simulate" => SimulateCommand.Run(rest, output),
                "settings" => SettingsCommand.Run(rest, output),
                "bench" => BenchCommand.Run(rest, output, error),
                "pattern" => PatternCommand.Run(rest, output, error),
                "hdr-pattern" => HdrPatternCommand.Run(rest, output, error),
                "decode" => DecodeCommand.Run(rest, output, error),
                "play" => PlayCommand.Run(rest, output, error),
                "capture" => CaptureCommand.Run(rest, output, error),
                "verify" => VerifyCommand.Run(rest, output, error),
                _ => throw new UsageException($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException e)
        {
            error.WriteLine($"fmp: {e.Message}");
            error.WriteLine("Run 'fmp --help' for usage.");
            return ExitUsage;
        }
        catch (Exception e)
        {
            // Last-chance handler: one line and an exit code instead of an unhandled-exception crash (exit 134).
            error.WriteLine($"fmp: internal error: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ").Trim()}");
            return ExitInternalError;
        }
    }

    public static string Version =>
        typeof(CommandDispatcher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    private const string Usage =
        """
        fmp - Frame Motion Plus: live frame generation (PS5 capture, file, Remote Play)

        Usage:
          fmp simulate [options]    Run the real scheduler against a simulated source and display
          fmp settings [options]    Print default settings JSON, or validate a settings file
          fmp settings show|path|set <key> <value>|unset <key> [--settings <file>]
                                    Saved settings for fmp capture and fmp play (see "saved settings" below)
          fmp bench jitter [options]
                                    Measure pacing-thread wake-up jitter on the real clock (design doc Phase 0)
          fmp bench vfg [options]   Measure NVIDIA VFG's cost per generated frame over sizes, modes, formats and
                                    multipliers, with the 1440p Medium gate (design doc Phase 0)
          fmp bench vfg-quality [options]
                                    VFG on SDR and PQ scenes with known motion: scores and 16-bit PNG frame dumps
          fmp bench present [options]
                                    What the desktop compositor does with presents, from first-pixel times: its latch,
                                    the output's regularity, whether variable refresh is active (Phase 6)
          fmp pattern [options]     CUDA test pattern through the presentation ring to a window or offscreen (Phase 2)
          fmp hdr-pattern [ramp|peak|bars|black] [options]
                                    An HDR test pattern on an HDR10 swapchain, to check the display by eye; with --check the
                                    HDR path (capture conversion, VFG, presenter, metadata, scaling) proved by numbers (Phase 7)
          fmp decode <file> [options]
                                    Decode a video file with NVDEC into CUDA, convert it to RGB and check it (Phase 4)
          fmp play <file> [options] Play a video file with frame generation in real time: decoded, interpolated (VFG,
                                    blend or repeat) and presented, paced by the display-time scheduler (Phase 4)
          fmp capture [device] [options]
                                    Capture a capture card (V4L2: a PS5 over HDMI) live with frame generation: captured into
                                    CUDA-pinned buffers, repeats of a picture dropped by the cadence detector, interpolated
                                    (VFG, blend or repeat) and presented, paced by the display-time scheduler (Phase 5)
          fmp capture --list        The V4L2 capture devices and the formats, sizes and rates each offers, the audio source
                                    that goes with each, and the audio sinks
          fmp verify <file.csv> [--rate <hz>] [--vrr] [--warmup-s <s>] [--slots <a>-<b>]
                                    Recompute a play run's verdict from its telemetry CSV alone (early, late, not shown,
                                    untimed, wrong image, order, refreshes without a present) and compare it with the
                                    live accounting in the CSV; exit 1 on any miss, unjudged slot or disagreement. A
                                    variable-refresh run: --vrr --rate <the output's highest rate>
          fmp --version
          fmp --help

        simulate options:
          --source-fps <rate>       Unique source frame rate: 60, 59.94, 30, 60000/1001 ...   (60)
          --display-hz <rate>       Panel refresh, or maximum refresh under VRR               (120)
          --clock <fixed|vrr>       Slot clock: panel vblanks, or follow the source           (fixed)
          --d-ms <ms>               Simulated generation time per generated frame             (2)
          --d-jitter-ms <ms>        Uniform +/- jitter on the generation time                 (0)
          --duration-s <s>          Simulated duration                                        (60)
          --warmup-s <s>            Excluded from statistics                                  (1)
          --margin-ms <ms>          Scheduler safety margin                                   (0.5)
          --max-multiplier <n>      Cap on output frames per source frame                     (8)
          --drop-rate <p>           Probability of dropping a source frame                    (0)
          --frame-jitter-ms <ms>    Uniform +/- jitter on source frame times                  (0)
          --skew-ppm <ppm>          Source clock error versus the display clock               (0)
          --phase-ms <ms>           Source content-time offset against the vblank grid        (0)
          --hold-at-s <s>           Start of a source hold (no new frames)                    (none)
          --hold-for-s <s>          Length of that hold                                       (2)
          --ingest-ms <ms>          Capture-to-GPU availability latency                       (0)
          --ingest-jitter-ms <ms>   Extra uniform [0, J] availability delay per frame         (0)
          --wake-late-us <us>       Scheduling thread wakes this late for every decision      (0)
          --wake-jitter-us <us>     Extra uniform [0, J] wake lateness                        (0)
          --stall-at-s <s>          Scheduling thread blocked once from this time             (none)
          --stall-ms <ms>           Length of that stall                                      (400)
          --rate-change-at-s <s>    Source switches rate at this content time                 (none)
          --rate-after <rate>       The rate it switches to
          --deliver-ahead-ms <ms>   Hand frames to the scheduler before they are available    (0)
          --seed <n>                Random seed                                               (1)
          --settings <file>         Base scheduler/display settings from a JSON file
          --csv <path>              Write per-slot telemetry CSV
          --check                   Exit 1 unless the run meets the design doc: no missed slot, regression,
                                    causality or timestep error; on ideal inputs also the {k/M} pattern,
                                    the real:generated ratio and the latency band from the latency model

        settings options:
          --validate <file>         Validate a settings file instead of printing defaults

        saved settings (fmp capture and fmp play read them when the file exists; nothing else does):
          The file is $XDG_CONFIG_HOME/fmp/settings.json, else ~/.config/fmp/settings.json (%APPDATA%\fmp\settings.json on
          Windows); --settings <file> reads another, --no-settings none. Precedence: built-in defaults < the file < options on
          the command line. Each saved value stands for an option and is checked exactly as that option is; an option given on
          the command line (or one that conflicts with it, such as --size against a saved mode) wins. fmp capture and fmp play
          print one line saying which file was used and every value taken from it. A malformed file is an error naming it and
          the position; a key this fmp does not know is reported and skipped.
          fmp settings show         Every key, its value and where it comes from (the file, or the default)
          fmp settings path         The file's path
          fmp settings set <key> <value>   Check and save one value (creates the file; other keys are kept; atomic)
          fmp settings unset <key>  Remove one value (the default applies again)
          keys: capture.device (/dev/videoN), capture.mode (--mode), capture.fps (--capture-fps; not with a typed --mode),
          generator.backend (--generator), generator.quality (--quality), generator.multiplier (--multiplier), generator.qualitySwitch (true: --quality-switch, false:
          --no-quality-switch), generator.shotChangeDetection (--scd: on or off), display.clock (--clock), display.fullscreen (true: --fullscreen,
          false: a window), display.window (WxH: the window's size when windowed; unlike --window it does not choose a window),
          audio.mode (--audio), audio.sink (--audio-sink), audio.source (--audio-source), audio.offsetMs (--audio-offset-ms),
          hdr.mode (--hdr), hdr.peakNits (--hdr-peak-nits), hdr.maxCll (--hdr-max-cll), hdr.maxFall (--hdr-max-fall),
          generator.vfxSdkDirectory (after --vfx-sdk and $FMP_VFX_SDK). capture.*, audio.* and hdr.* apply to fmp capture only.
          Example: fmp settings set capture.mode 4k30-hdr; fmp settings set audio.sink inzone; then just: fmp capture

        bench jitter options:
          --duration-s <s>          Measured time per loop, after the warm-up                 (10)
          --warmup-s <s>            Excluded from statistics at the start of each loop        (1)
          --block-s <s>             The full and timer-only loops alternate in blocks this long (5)
          --rate <hz>               Output slot rate (panel refresh): 120, 240 ...            (120)
          --source-fps <rate>       Synthetic source frame rate                               (60)
          --wait <sleep|hybrid>     clock_nanosleep to the deadline, or sleep then spin       (sleep)
          --spin-us <us>            Spin window before the deadline, with --wait hybrid       (200)
          --rt                      Ask for SCHED_FIFO (falls back to a nice value; reports what it got)
          --rt-priority <n>         SCHED_FIFO priority 1-99                                  (10)
          --nice <n>                Nice value to ask for, or the fallback when --rt is refused (-10)
          --pin-core <n>            Pin the pacing thread to this CPU
          --source-core <n>         Pin the source thread to this CPU
          --timer-slack-ns <ns>     Per-thread timer slack, set explicitly; 50000 is the usual kernel default (1)
          --d-ms <ms>               Generation time reported for each generated slot          (1.2)
          --gate-ms <ms>            Exit 1 if p99 wake lateness of the full loop exceeds this (0.5)
          --max-missed <n>          Exit 1 if the full loop misses more slots than this       (0)
          --no-baseline             Skip the timer-only loop (no OS-versus-ours attribution)
          --csv <path>              Write every sample of both loops
                                    Skipped slots count in the percentiles. Also exits 1 on any GC collection or
                                    hot-thread allocation in the measured time.

        bench vfg options:
          --size <list>             1080p, 1440p, 2160p or WxH, comma-separated              (1080p,1440p,2160p)
          --mode <list>             low, medium, high                                         (low,medium,high)
          --format <list>           8 (RGBA8) and/or 10 (RGB10A2)                             (8,10)
          --multiplier <list>       2 (t = 0.5) and/or 4 (t = 0.25, 0.5, 0.75 per pair)       (2,4)
          --frames <n>              Generated frames measured per cell                        (600)
          --warmup-frames <n>       Generated frames per cell before measuring                (30)
          --content <kind>          pattern (the test pattern's moving box), pan (a textured pan), video (pattern)
          --video <input>           With --content video: a file, or lavfi:SPEC, decoded by the ffmpeg CLI
          --rate <hz>               Output rate the runs are paced at (2x: rate/2 runs/s)     (120)
          --unpaced                 Back to back instead
          --scd <on|off>            VFG's automatic shot-change detection                     (on)
          --verify-every <n>        Read every n-th output back and check it interpolates     (20)
          --gate-ms <ms>            Exit 1 if 1440p Medium p99 d exceeds this                 (7)
          --csv <path>              Write one row per cell
          --vfx-sdk <dir>           VFX SDK directory (else $FMP_VFX_SDK, --settings, lib/VideoFX of a checkout,
                                    /usr/local/VideoFX)
          --settings <file>         Settings file whose generator.vfxSdkDirectory to use
                                    Exits 1 when the gate fails or a checked output is a copy of a neighbour (or, with
                                    known motion, not closer to the true frame than a blend); 69 without a GPU or SDK.

        bench vfg-quality options:
          --size <WxH|1080p|...>    Frame size                                                (1920x1080)
          --mode <list>             Quality modes                                             (low,medium,high)
          --out <dir>               Where the PNGs and index.md go             (out/vfg-quality in the checkout)
          --no-dumps                Scores only
          --photo <image>           A picture to pan across for the photo scenes (default: a desktop wallpaper, if any)
          --no-photo                Skip the photo scenes
          --vfx-sdk <dir>, --settings <file>   As for bench vfg

        bench present options:
          --test <latch|vrr|cadence>  latch: presents into an empty queue at times before a refresh (a staircase) to find
                                    the compositor's latch; vrr: presents at off-grid intervals, first pixels following
                                    them means variable refresh; cadence: as fast as two in flight allow (regularity)
                                                                                              (latch)
          --mode <fifo|fifo-latest-ready|mailbox|immediate>  The swapchain's present mode            (fifo)
          --fullscreen              Borderless fullscreen on the window's output (KWin's VRR "automatic" needs it)
          --output <display>        The display fullscreen goes to, as for play
          --interval-ms <list>      With --test vrr: the intervals presented in turn          (1.37 and 1.71 refreshes)
          --duration-s <s>          Measured time, at most 120 s; Escape, closing the window, Ctrl+C or SIGTERM end it
                                    early with the results so far                             (10)
          --window <WxH>            Window size                                               (640x360)
          --csv <path>              Every present: submitted, aimed at, first pixel

        pattern options:
          --size <WxH>              Pattern and ring size                                     (2560x1440)
          --format <8|10>           8-bit RGBA8 (SDR) or 10-bit RGB10A2 (PQ)                  (8)
          --duration-s <s>          Run length                                                (5)
          --frames <n>              Run this many refresh slots instead
          --offscreen               Headless: offscreen target, every frame read back and verified bit-exact
          --no-verify               With --offscreen: no readback (to measure costs alone)
          --unpaced                 With --offscreen: no pacing, no wait for a free image; as fast as back-pressure
                                    allows (a stress test: drops and held-back slots allowed, shown frames verified)
          --blend                   2x generator in the loop: real frames alternate with BlendGenerator at t = 0.5
          --vfg                     2x with NVIDIA VFG instead, written straight into the ring slots; offscreen each
                                    generated frame is scored against the pattern's true middle frame
          --quality <mode>          With --vfg: low, medium or high                           (medium)
          --vfx-sdk <dir>           With --vfg: the VFX SDK directory
          --settings <file>         With --vfg: a settings file whose generator.vfxSdkDirectory to use
          --validate                Vulkan validation layer with synchronization checks; any message fails the run
          --window <WxH>            Window size                                               (960x540)
          --rate <hz>               Refresh rate to pace at (offscreen 120; windowed: the display's)
          --slots <n>               Presentation ring depth, 4 to 6                           (4)
          --warmup-frames <n>       Excluded from timing and allocation statistics            (30)
                                    Exits 1 on any failure, a dropped frame offscreen, a validation message or an
                                    allocation on the present thread; 69 when no usable GPU, driver or display is found.
                                    Ctrl+C (SIGINT) or SIGTERM stops the run cleanly with its verdict; a second one
                                    ends the process at once. With --validate each validation message is also written
                                    to stderr as it arrives.
                                    Fault injection, for testing teardown: FMP_PATTERN_FAIL_AT=<slot> in the environment
                                    makes the loop fail between acquiring that slot's write and signalling it (the run
                                    then fails with that cause and must still tear down cleanly).

        hdr-pattern [ramp|peak|bars|black] options (an HDR test pattern on an HDR10 swapchain, as captured HDR10 is shown):
          ramp                      Every PQ code 0..1023 left to right, ticks labelled in nits; R, G, B ramps   (default)
          peak                      Patches at 100, 203, 400, 600, 1000, 2000, 4000 and 10000 nits, each with an inner square
                                    25% brighter: where the inner square vanishes the display (or KWin) has reached its limit
          bars                      BT.2020 colour bars at 203 nits (HDR reference white) and at 1000 nits
          black                     Near-black steps, PQ codes 0 to 128 on code 0, labelled in nits
          --duration-s <s>          How long                                                  (10)
          --window <WxH>            Window size; the pattern is drawn at its pixel size                     (1280x720)
          --fullscreen              Borderless fullscreen (keep it short: a 10,000-nit patch is unpleasant)
          --output <display>        The display fullscreen goes to, as for play
          --hdr-peak-nits, --hdr-max-cll, --hdr-max-fall   The HDR metadata sent, as for capture
          --check                   No window: the HDR path proved by numbers, offscreen: each pattern encoded as a console
                                    sends it (P010, limited range, BT.2020, 4:2:0), converted by the capture's kernel, written
                                    into the ring as a real frame and as a VFG frame between two identical frames, presented
                                    and read back; also scaling against a linear-light reference, the HDR metadata handed to
                                    Vulkan, SDR staying SDR (10- and 8-bit) and the switch back. Exits 1 if any check fails
          --size <WxH>              With --check: the frame size                              (1920x1080)
          --no-vfg                  With --check: without the VFX SDK
          --quality <mode>, --vfx-sdk <dir>   With --check: VFG's quality and SDK directory

        decode options:
          --frames <n>              Frames to read                          (the whole file; with --loop three laps)
          --duration-s <s>          Read for this long instead
          --loop                    Start again at the end of the file (each new lap is a discontinuity)
          --verify                  Check every frame: the decoded NV12/P010 bit-exact against the ffmpeg CLI's software
                                    decode, the converted RGB bit-exact against the CPU reference
          --ffmpeg <path>           The ffmpeg CLI for --verify (ffprobe is looked for beside it)       (ffmpeg)
          --decode-ahead <n>        Frames decoded ahead of the consumer                      (4)
          --no-stall-probe          Do not time another thread's CUDA calls meanwhile
          --csv <path>              Write per-frame content times, decode and convert costs and queue depth
                                    Prints the stream (codec, size, bit depth, rate, colour and HDR metadata), frames and
                                    PTS statistics, per-frame GPU and decode-thread costs, queue depth, the worst stall of
                                    another thread's CUDA calls, allocations and GC counts. Exits 1 on a mismatch,
                                    non-increasing content times, FFmpeg frame-pool growth, a consumer-path allocation or a
                                    file that cannot be played; 69 without a GPU, the FFmpeg 9 libraries or (for --verify)
                                    the ffmpeg CLI.

        play options:
          --generator <name>        auto (VFG when the NVIDIA VFX SDK is found, else blend, saying why), vfg, blend,
                                    repeat, or none (real frames only)                        (auto)
          --quality <mode>          VFG quality: low, medium or high                          (medium)
          --quality-switch, --no-quality-switch   VFG: load the other quality too at start (High, or Medium when
                                    --quality high), so Q in the window switches between the two while playing, between
                                    two frames and with no load (D rises first for High, then the lead; nothing late).
                                    Costs that instance's load at start (about 90 ms) and its GPU memory (1.5 GB at
                                    4K, 1.3 GB at 1440p). --no-quality-switch keeps one instance; a load that fails
                                    keeps one too, said once. Q with blend, repeat or none says so  (on)
          --multiplier <n>          Cap on output frames per source frame, 1 to 8: a 60 fps file at 2 shows a new
                                    image every refresh of a 120 Hz display and every other refresh of a 240 Hz one;
                                    at 4 every refresh of the 240 Hz one                      (2)
          --output-hz <hz>          The cap as an output rate instead: the cap is this over the file's frame rate
          --loop                    Start the file again at its end (each lap is a discontinuity: never interpolated)
          --duration-s <s>          Play this long (after the start-up), else to the end of the file
          --frames <n>              Play this many output slots instead
          --window <WxH>            Window size                                               (1280x720)
          --fullscreen              Borderless fullscreen on the output the window is on (off by default)
          --output <display>        The display fullscreen goes to: its name or a unique part of it (any case), its
                            number, or @X,Y (its desktop position); a list is printed when nothing matches.
                            Without it: the display the window opens on (GNOME: where the pointer is)
          --clock <auto|fixed|vrr>  Output slots on the display's refresh grid (fixed), or following the source with
                                    variable refresh (vrr: each present at its own time, the display refreshing for it).
                                    auto: vrr when fullscreen, a probe before playback shows the display following the
                                    presents (KWin's "automatic" VRR does so for a fullscreen window) and the output's
                                    highest rate can show the source's rate times the multiplier; else fixed   (auto)
          --offscreen               No window: an offscreen target the file's size on a virtual clock (deterministic:
                                    every slot decided exactly on time, the threads waiting for each other); for tests
          --realtime                With --offscreen: the real clock instead (slots paced at --rate)
          --rate <hz>               With --offscreen: the refresh rate the target stands for  (120)
          --size <WxH>              With --offscreen: the target's size                       (the file's)
          --validate                Vulkan validation layer with synchronization checks; any message fails the run
          --csv <path>              Write the per-slot timeline (content time, decode available, decision, generation
                                    start and end on the GPU, write signalled, present submitted, shown) as CSV
          --settings <file>         The saved settings file to use instead of the default one (see "saved settings"); its
                                    scheduler, display and generator sections apply too
          --no-settings             Ignore the saved settings file
          --vfx-sdk <dir>           The VFX SDK directory (else $FMP_VFX_SDK, --settings, lib/VideoFX of a checkout,
                                    /usr/local/VideoFX)
          --warmup-s <s>            Excluded from the statistics, allocation and GC counts    (2)
          --present-lead-ms <ms>    The present lead to start from (how long before its refresh a slot's content must be
                                    ready; windowed: the compositor's latch, measured before playback, plus 2 ms, else
                                    1.5 refreshes; offscreen: 0). It adapts: up a millisecond at once when a frame decided
                                    too late misses the compositor's latch, slowly back down to where it started
          --latch-ms <ms>           The least a present is assumed to need before its refresh (offscreen: how early a
                                    slot is submitted; windowed: a floor under the adaptive lead)     (1)
          --scd <on|off>            VFG's automatic shot-change detection. On, VFG takes large, uneven change between
                                    frames (fast motion, objects close to the camera, foliage rushing past) for a cut
                                    and shows a real frame instead of generating one, so the picture drops to the
                                    source's rate until it calms down. Off, every pair is interpolated; a real cut then
                                    shows one generated frame that is a morph of the two shots (a seek or a loop is
                                    never interpolated across)                            (play: on; capture: off)
          --late-generated-ms <ms>  Variable refresh: when the real frame after a generated slot comes too late for it,
                                    show the generated frame up to this much after its time (its content moved to match)
                                    instead of skipping it (the image before then stays a whole slot longer). Costs no
                                    latency; 0 skips it as before                                       (4)
          --ceiling-pacing <on|off> Variable refresh with the slots at the output's ceiling (4x 29.97 on 119.88 Hz, 8x
                                    on 240 Hz): a display that has fallen behind cannot catch up by itself, so after 30
                                    late shows in a row one generated frame is left out (at most one every 5 s). Inert
                                    below the ceiling and at fixed refresh; running behind is reported either way (on)
          --cuda-sync <mode>        How threads wait in CUDA synchronisations: blocking (sleep; VFG's blocking call
                                    then costs about 0.06 instead of 0.35 ms of CPU per slot), spin, yield, auto (blocking)
          --gate                    The Phase 4 gate: exactly VFG Medium at 2x, looped, 10 minutes (--duration-s may
                                    shorten it), windowed; PASS with zero missed slots caused by the pipeline and zero gen0
                                    collections in the measured window. A miss is the pipeline's unless the timestamps show
                                    the present was handed over in time for its refresh (at least the measured deadline
                                    before it) and the display showed it late; those, and misses while the output's rate
                                    changed (the window moved), are counted apart
                                    Prints what it resolved (file, generator and why, display, rates) and a summary: slots,
                                    presents, missed slots and whose, output intervals from present feedback (first-pixel
                                    times where the display reports them), generation time d, the delay to light (present
                                    lead + D + the stride's rounding), per-thread CPU and allocations, GC counts, a verdict.
                                    Exits 1 on a failure, a validation message, a hot-thread allocation, a run that measured
                                    nothing or a failed gate; 69 without a GPU, FFmpeg 9, a display (windowed) or, for
                                    --generator vfg, the VFX SDK. Ctrl+C, SIGTERM, closing the window or Escape stop it
                                    cleanly with its summary, at any point of the start-up too. If GPU work never finishes,
                                    it stops, reports what teardown left and ends the process.
                                    Keys in the window act when pressed (not while held): Esc stops, Q switches the
                                    generator's quality (Medium and High) and prints one line; the summary lists every
                                    switch, what it cost, the slots around it and each quality's latency and d.
                                    Diagnostics: FMP_PLAY_WATCHDOG=1 prints where each pipeline thread is once a second;
                                    FMP_QUALITY_SWITCH_EVERY_S=<s> asks for the other quality every s seconds, as Q does;
                                    FMP_PLAY_FAULT=gpu-wait@N (a GPU wait that never completes at the N-th generated frame)
                                    or gpu-busy@N:S (S seconds of GPU work there) checks that the run still ends.

        capture options (device: /dev/videoN; default: the saved capture.device, else the first capture device with a 10-bit
        mode, else the first one):
          --list                    List the capture devices: formats, sizes, rates, the presets each one can do
          --mode <preset>           4k30-hdr (3840x2160 P010 at 29.97: the Elgato 4K X's 10-bit 4K mode; generate to 59.94),
                                    1440p60-hdr (2560x1440 P010 at 59.94: for 60 fps games; to 119.88), both HDR10 or SDR
                                    as the console sends (--hdr follow); 4k60-sdr, 1440p60-sdr, 1080p60-sdr (NV12, SDR, at
                                    59.94); 1440p120-sdr, 1080p120-sdr (NV12, SDR, at 120: the card offers no 119.88)
                                    Without --mode or the three below: the largest 10-bit mode, at its highest rate
          --capture-fps <rate>      The rate asked of the card, exactly: 29.97, 30, 59.94, 60 or a fraction (30000/1001)
                                    (the preset's: the console's 29.97 or 59.94, as a PS5 sends 59.94 Hz; a card at 30.000
                                    repeats a frame every 33 s, which shows as a hitch). 30 or 60 for a source that sends
                                    60.000 Hz (a PC). The summary says when the repeats show a periodic beat and which rate
                                    the source looks like (stderr only when that is advice to change the rate; a repeat
                                    every half hour at the console's rate is the card's own clock: nothing to change)
          --size <WxH>              Capture size (must be one the device offers)
          --fps <rate>              Capture rate: 30, 60, 59.94 ... (59.94 and 60 differ)        (the highest offered)
          --format <nv12|p010|yuyv> Pixel format (MJPEG is never used: its decode adds latency)  (P010 first)
          --hdr <mode>              HDR10 (PQ, BT.2020, limited range, BT.2020 matrix) or SDR (BT.709): follow (a P010
                                    capture follows the console: HDR10 while the samples carry 10 bits, SDR while they
                                    carry 8, as a PS5 is assumed to send SDR (not yet seen); the Elgato 4K X reports Rec. 709 whatever it gets), on,
                                    off, auto (as the device reports it), 10bit (on for any 10-bit capture)  (follow)
                                    In the window: H switches between SDR and HDR10 by hand (stops following; the output
                                    follows within a few frames; P010 captures only), A follows the signal again
                                    Keys in the window act when pressed (not while held), with or without modifiers:
                                    Esc stops, H switches SDR/HDR10 by hand, A follows the signal again, Q switches the
                                    generator's quality (Medium and High, as for play)
          --hdr-peak-nits <n>       HDR10 metadata sent with the picture (the card carries none): mastering peak, and
                                    MaxCLL unless given. Best set to the peak the PS5's HDR calibration was done for
                                    (the display's). KWin shows a PQ window's 203 nits at the output's SDR brightness
                                    and compresses above its peak (docs/benchmarks/phase7-hdr.md)        (1000)
          --hdr-max-cll <n>         MaxCLL sent                                                  (the peak)
          --hdr-max-fall <n>        MaxFALL sent (0: not stated)                                 (0)
          --range <limited|full|auto>  YCbCr range override (a PS5 sends limited)                (auto: the device's)
          --matrix <bt709|bt2020|bt601|auto>  YCbCr matrix override               (auto: the device's; BT.2020 with HDR)
          --no-cadence              Every captured frame is new (no repeat detection)
          --buffer-mode <mode>      auto (USERPTR into CUDA pinned memory, else USERPTR into registered memory, else
                                    MMAP registered with CUDA), userptr, userptr-registered, mmap                (auto)
          --buffers <n>             Capture buffers, 2 to 16                                     (4)
          --windowed                A window instead of borderless fullscreen (also implied by --window <WxH>). By
                                    default capture is fullscreen on the window's output: there KWin gives it variable
                                    refresh, which --clock auto uses (the lowest latency); a window is fixed refresh
          --generator, --quality, --quality-switch, --no-quality-switch, --multiplier, --output-hz, --duration-s, --window,
          --fullscreen, --output, --clock, --validate, --csv, --settings, --no-settings, --vfx-sdk, --warmup-s,
          --present-lead-ms, --latch-ms, --scd, --late-generated-ms, --ceiling-pacing, --cuda-sync   As for play (the
                                    duration is unlimited by default: until Ctrl+C, closing the window or Escape)
                                    Without a device or --mode, the saved capture.device and capture.mode are used if set
          --offscreen               No window: an offscreen target on the real clock at --display-hz
          --display-hz <hz>         With --offscreen: the refresh rate the target stands for     (120)
          --audio <on|off|auto>     The card's sound, delayed to its picture by the measured video latency (a real frame's
                                    first pixel less its capture timestamp, smoothed), through PipeWire. auto: when the
                                    capture device has an audio source (the sound card on the same USB device; vivid has
                                    none); on: also says why when it cannot; off. The video always runs     (auto)
          --audio-source <name>     The PipeWire source to play instead (fmp capture --list shows them; implies on)
          --audio-sink <name>       Where the sound goes: any sink, by its node name or a part of its name or description
                                    ("inzone"; several matches are refused and listed). Without it: the default output
                                    you selected in the desktop's sound settings, followed when you select another.
                                    Either way it plays to no other output device: muted (and said) while that device
                                    is gone, never moved to another by PipeWire (a link to another output is muted and
                                    cut; a recorder, such as a screen share's sound, may take a copy); implies on
          --audio-offset-ms <n>     Trim on top of the measured latency, -200 to 500 ms: positive plays the sound later,
                                    for what the timestamps cannot see (the TV's processing, the card itself)     (0)
                                    Diagnostics: FMP_AUDIO_LOG=<s> prints the delay, its target, the ratio and the learned
                                    clock drift every s seconds on stderr; FMP_CAPTURE_SWITCH_EVERY_S=<s> switches between
                                    SDR and HDR10 every s seconds as H does (to exercise the switch); FMP_QUALITY_SWITCH_EVERY_S
                                    as for play.
                                    Prints what it negotiated (and what the driver changed), the colour it resolved and
                                    why, the buffer mode, then the play summary plus capture statistics: frames dequeued,
                                    skipped as stale (newest wins), dropped by the driver, unique, repeats dropped, dropped
                                    while the pipeline was behind, the detected game rate, holds, black frames, timestamp
                                    jitter, USB delivery (dequeue − timestamp), capture to available, upload, cadence and
                                    convert GPU time, and the latency added by generation against the latency model;
                                    with sound, its source, sink and their latencies at the start, and at the end the
                                    seconds played, the delay target and what was achieved, the A/V offset from the
                                    timestamps, the drift correction (ppm), underruns, overruns and resyncs.
                                    Exits 1 on a failure (the device unplugged, a format change, a stream error), 2 for a
                                    mode the device does not offer, 69 when capture (Linux), the device, the GPU or a
                                    display is missing. The device is released on every way out.

        Exit codes: 0 success, 1 check or validation failed, 2 usage error, 70 internal error; 69 when a GPU or display is required but not there.

        """;
}
