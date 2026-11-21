# Frame Motion Plus

Live frame generation for a PS5 (capture card first, then video files and PS Remote Play): unique
frames are interpolated on the GPU with NVIDIA Maxine Video Frame Generation (VFG) in CUDA and
presented with Vulkan, paced by a scheduler that works from display times. Linux first (KDE Plasma
Wayland, RTX 50 series); Windows and macOS are design targets.

- Design (what and why): [`Live Frame Generation App Implementation Plan.md`](Live%20Frame%20Generation%20App%20Implementation%20Plan.md)
- Build phases (order, projects, gates): [`docs/implementation-plan.md`](docs/implementation-plan.md)

## Status

Phase 1 (solution foundation and pure core) is in place: the platform-neutral core with the six
backend interfaces, frame and colour types, the display-time scheduler, the cadence tracker, lock-free
rings, telemetry, settings, and the `fmp simulate` command.

Phase 2 (GPU foundation) is joined: CUDA driver-API bindings and kernels, the Vulkan presenter with its
exportable presentation ring, the CUDA/Vulkan bridge that imports the ring and its two timeline
semaphores into CUDA, the platform clock and threads, and the `fmp pattern` and `fmp bench jitter`
commands. Results: [`docs/benchmarks/phase2-interop.md`](docs/benchmarks/phase2-interop.md),
[`docs/benchmarks/phase2-jitter.md`](docs/benchmarks/phase2-jitter.md).

Phase 3 (the VFG backend and the feasibility gate) adds `FrameMotionPlus.Vfx`: bindings for the NVIDIA VFX
SDK, loading it from a configurable directory, and `VfgGenerator`, the real frame generator behind
`IFrameGenerator`; and the `fmp bench vfg`, `fmp bench vfg-quality` and `fmp pattern --vfg` commands.
Results and the answers that shape the Phase 4 pipeline: [`docs/benchmarks/phase3-vfg.md`](docs/benchmarks/phase3-vfg.md).

Phase 4a (decode) adds `FrameMotionPlus.Sources.File`: FFmpeg demux and NVDEC decode through FFmpeg's CUDA hwaccel
in the application's CUDA context, the NV12/P010-to-RGB convert kernel, the frame history, and `fmp decode`, which
checks a file against a software decode. Results: [`docs/benchmarks/phase4a-decode.md`](docs/benchmarks/phase4a-decode.md).

Phase 4b (pipeline) adds `FrameMotionPlus.Pipeline`, which joins everything: the file source, the scheduler, the
generator (VFG, blend or repeat) on a thread of its own, the presentation ring and the presenter, on dedicated
threads, and `fmp play`, which plays a file with frame generation in real time (first light, the design doc's
milestone 1). Results, the threading and time model, and the frame-timing issues carried to Phase 6 (the 10-minute
gate has not been verified on the current code):
[`docs/benchmarks/phase4b-pipeline.md`](docs/benchmarks/phase4b-pipeline.md). The HUD and the quality harness are 4c.

Phase 5 (capture card) adds `FrameMotionPlus.Sources.V4l2`: a live source for a V4L2 capture card (a PS5 on an Elgato
4K X), captured into CUDA-pinned buffers, uploaded, judged by the cadence detector (a GPU kernel feeding Core's tracker:
repeats of a picture are dropped, a 30 fps game is found inside a 60 Hz signal), converted on the GPU, and played through
the same pipeline with VFG: `fmp capture`. Results, what the card does, and what still needs the owner's hardware or
eyes: [`docs/benchmarks/phase5-capture.md`](docs/benchmarks/phase5-capture.md).

Phase 6 (pacing and latency) makes the miss accounting and the offline verifier agree (a compositor's own catch-up is
recognised; a repeat given up is judged by what its refresh showed), measures the compositor's latch before each run and
starts the present lead from it (2.7 to 4.5 ms instead of 12.5 at 119.88 Hz, in most runs), removes the stride's extra
refresh on phase-locked outputs, stretches a near-locked file's clock to lock it, and adds **variable refresh**:
fullscreen, KWin follows the presents, and `--clock auto` (the default) then times each slot from the source with no
present lead and no rounding to a refresh (`fmp capture` is fullscreen by default). A real 4K HDR 30 → 60 frame now
reaches the screen 46.6 ms after its capture timestamp (63.3 before). The 10-minute gate run, verified from the
telemetry: 0 missed on DP-3; 4 on HDMI-A-1, in one host stall (a handful is acceptable to the owner for now).
Results, the compositor's measured behaviour, and what is not done:
[`docs/benchmarks/phase6-pacing.md`](docs/benchmarks/phase6-pacing.md).

Phase 7 (HDR, pass-through) makes the HDR path right from capture to swapchain and shows an SDR signal as SDR (no
conversions between the two are built). The output follows the frames' colour: PQ content gets an HDR10 swapchain with
`VK_EXT_hdr_metadata` (the file's metadata, or BT.2020/D65 defaults with a settable peak for the capture card), SDR
content an SDR one; scaling to the output filters PQ in linear light (pixel for pixel when the sizes match); a P010 capture
follows the console between HDR10 and SDR by the bit depth of its samples, and `H` in the window switches by hand at
once (the output is rebuilt at the next frame, no missed slot in the runs). `fmp hdr-pattern` puts HDR test patterns on
screen and `fmp hdr-pattern --check` proves the path by numbers. Results, what KWin does with the surface as far as could
be established, and how to check HDR by eye: [`docs/benchmarks/phase7-hdr.md`](docs/benchmarks/phase7-hdr.md).

Phase 8, first part (capture-card audio) adds `FrameMotionPlus.Audio`: `fmp capture` now plays the card's sound,
delayed so it is heard when its picture is seen. The card's PipeWire audio source is captured and played to the default
sink through a delay line whose total delay (from a sample's capture to the time it is heard, both from PipeWire's
timestamps) follows the video latency the pipeline measures (a real frame's first pixel less its capture timestamp),
with drift between the two sample clocks corrected by resampling. libpipewire is bound directly (no C shim). Results,
the measured latencies and what could not be measured: [`docs/benchmarks/phase8-audio.md`](docs/benchmarks/phase8-audio.md).

## Layout

```
src/FrameMotionPlus.Core/         pure managed core: interfaces, scheduler, cadence, rings, telemetry, settings, simulation
src/FrameMotionPlus.App/          the `fmp` NativeAOT executable
src/FrameMotionPlus.Platform/     CLOCK_MONOTONIC clock, absolute-deadline waits, dedicated real-time threads, GC helpers
src/FrameMotionPlus.Cuda/         CUDA driver-API bindings, streams, events, external memory/semaphores, kernels, Repeat/Blend generators
src/FrameMotionPlus.Vulkan/       device selection, exportable presentation ring, presenter, SDL3 window, offscreen target
src/FrameMotionPlus.CudaVulkan/   the IGpuBridge: the presenter's ring and timelines imported into CUDA (Linux, opaque FDs)
src/FrameMotionPlus.Vfx/          NVIDIA VFX SDK bindings and loading, VfgGenerator (Video Frame Generation, CUDA domain)
src/FrameMotionPlus.Sources.File/ FFmpeg 9 bindings, NVDEC decode into CUDA, the file IFrameSource and its frame history
src/FrameMotionPlus.Sources.V4l2/ V4L2 bindings, format negotiation, the capture IFrameSource (pinned buffers, cadence)
src/FrameMotionPlus.Pipeline/     playback: source → scheduler → generator → ring → presenter on dedicated threads, telemetry
src/FrameMotionPlus.Audio/        the capture card's sound: libpipewire bindings, the delay line (ring, sinc resampler, controller)
native/kernels/, native/shaders/  CUDA kernel and shader sources with the generated PTX and SPIR-V checked in
tests/FrameMotionPlus.Core.Tests/ xUnit v3 tests of the core and the CLI (always run)
tests/FrameMotionPlus.Platform.Tests/ tests of the platform layer and `fmp bench jitter` (Linux-specific ones skip elsewhere)
tests/FrameMotionPlus.Cuda.Tests/   CUDA bindings, kernels against CPU references, generators (category Gpu)
tests/FrameMotionPlus.Vulkan.Tests/ ring, presenter, offscreen and windowed presents under the validation layer (Gpu, Display)
tests/FrameMotionPlus.Gpu.Tests/    CUDA/Vulkan interop end to end and `fmp pattern` (Gpu, Display)
tests/FrameMotionPlus.Vfx.Tests/    SDK loading, VfgGenerator, measured SDK behaviour, the VFG commands (Vfx)
tests/FrameMotionPlus.Sources.File.Tests/ FFmpeg layouts, the convert kernel, decode against software, `fmp decode` (Media)
tests/FrameMotionPlus.Pipeline.Tests/ the whole path checked frame by frame offscreen, faults, allocations, windowed runs, `fmp play`
tests/FrameMotionPlus.Sources.V4l2.Tests/ V4L2 layouts, negotiation, the cadence kernel and tracker, vivid, the card, `fmp capture`
tests/FrameMotionPlus.Audio.Tests/ the delay line simulated, PipeWire layouts, a loopback through null sinks, the audio options
```

Hardware tests carry the category `Gpu` (NVIDIA GPU, CUDA driver, Vulkan with the validation layer),
`Display` (a Wayland session and SDL3 as well), `Vfx` (the NVIDIA VFX SDK as well) or `Media` (FFmpeg 9's
shared libraries and the `ffmpeg` CLI, which makes the test clips at test time) and skip, never fail, when
that is missing, so hosted CI stays green. The capture tests add `Capture` (the kernel's `vivid` test driver, loaded by
hand with `sudo modprobe vivid`) and `Elgato` (the Elgato 4K X with a picture on its input). The pipeline tests need all of a GPU, FFmpeg 9 and the `ffmpeg` CLI
(category `Gpu`), and a display or the SDK for some. Vulkan tests run under the validation layer and
fail on any message. The audio tests' PipeWire loopback (two null sinks it creates and removes) and its layout check
(the PipeWire development headers and gcc) skip where those are missing.

## The NVIDIA VFX SDK

Video Frame Generation comes from NVIDIA's VFX SDK (Maxine), which is proprietary: it is never committed,
copied into build output or referenced by a project. `fmp` loads it at run time from the first of: the
`--vfx-sdk <dir>` option, `$FMP_VFX_SDK`, `generator.vfxSdkDirectory` in a settings file, `lib/VideoFX`
of the repository checkout the `fmp` binary sits in (never one found from the current directory), and
`/usr/local/VideoFX` (where NVIDIA's installer puts it).
The directory is the SDK root (`lib/`, `external/cuda/lib/`) with the VFG feature under
`features/nvvfxvideoframegeneration/` as the installer lays it out. No `LD_LIBRARY_PATH` is needed: the
SDK's libraries are loaded by absolute path, dependencies first, so the SDK's own lookups by bare name
find them (details in [`docs/benchmarks/phase3-vfg.md`](docs/benchmarks/phase3-vfg.md)). Without the SDK
everything else works; the VFG commands exit 69 with a one-line reason.

## FFmpeg

File playback uses the system's FFmpeg 9 shared libraries (`libavcodec.so.63`, `libavformat.so.63`,
`libavutil.so.61`) with its CUDA hwaccel, loaded at run time by soname (or from `$FMP_FFMPEG_DIR`), through
hand-written source-generated bindings whose structure offsets and field sizes, constants and prototypes (C types
mapped to the C# signatures, parameter by parameter) a test checks against the installed headers. Another major
version is refused by name; so is any platform but x86-64 Linux, where those layouts are checked. Without FFmpeg
everything else works and `fmp decode` exits 69 with a one-line reason.

## Build, test, publish

Requires the .NET 10 SDK (see `global.json`). NativeAOT publishing on Linux also needs `clang` and
`zlib` development headers.

```sh
dotnet build FrameMotionPlus.slnx -c Release
dotnet test FrameMotionPlus.slnx -c Release
dotnet publish src/FrameMotionPlus.App -c Release -r linux-x64
```

Build output goes to `artifacts/`; the native binary is
`artifacts/publish/FrameMotionPlus.App/release_linux-x64/fmp`. Warnings are errors, and package
versions live only in `Directory.Packages.props`.

## Simulating the scheduler

`fmp simulate` runs the real scheduler against a simulated source and display (null generator and
presenter), prints slot counts and the measured added latency, and compares it with the design doc's
latency model ΔL = d + (M − 1)/M · T.

```sh
fmp simulate --source-fps 60 --display-hz 120                  # 2x, fixed refresh
fmp simulate --source-fps 30 --display-hz 120 --clock vrr      # 4x, VRR slot clock
fmp simulate --source-fps 59.94 --display-hz 120 --csv run.csv # non-integer ratio, telemetry CSV
fmp simulate --source-fps 30 --display-hz 120 --d-ms 10        # multiplier fallback (d > T/M)
fmp simulate --drop-rate 0.05 --hold-at-s 5 --hold-for-s 3     # dropped frames and a long hold
fmp --help
```

`--check` makes the command exit non-zero unless the run meets the design doc: no missed slot, content
regression, causality or timestep error on any run, and on ideal inputs also the {k/M} timestep pattern,
the real:generated ratio and the latency band from the latency model. Wake-up lateness, stalls, ingest
jitter, rate changes and decode-ahead delivery can be injected (`fmp --help`). `fmp settings` prints the
default settings JSON; `fmp settings --validate <file>` checks a settings file and exits 1 if it has unknown,
duplicate or null properties or out-of-range values (exit codes: 0 success, 1 check failed, 2 usage error,
70 internal error).

## Measuring pacing jitter

`fmp bench jitter` is the design doc's runtime-jitter measurement. A dedicated pacing thread waits for each
output slot's decision time on `CLOCK_MONOTONIC` and then does the real per-slot work: it feeds synthetic frame
arrivals from a second dedicated thread (through the real SPSC ring) to the real scheduler, makes the decision and
writes a telemetry record. It reports wake lateness (actual wake minus requested deadline) and per-slot work time
(p50 to max), missed slots, GC collections and hot-thread allocations. Slots the loop came too late for count in
the percentiles too. The same loop with the scheduler work removed runs in alternating blocks with it, so the OS's
timer jitter can be told apart from what our managed code adds. It exits 1 if p99 wake lateness exceeds the gate
(0.5 ms by default), if any slot is missed (`--max-missed` sets a budget), or on any collection or hot-thread
allocation. Run it on the
published binary; results for this machine are in [`docs/benchmarks/phase2-jitter.md`](docs/benchmarks/phase2-jitter.md).

```sh
fmp bench jitter                                   # 120 Hz, clock_nanosleep wait, 10 s measured per loop
fmp bench jitter --rate 240 --wait hybrid --duration-s 60 --csv jitter.csv  # sleep, then spin the last 200 us
fmp bench jitter --rt --pin-core 4                 # ask for SCHED_FIFO and a core; prints what was granted and why not
```

Real-time scheduling is optional and never fatal: the threads report what they obtained, and when `SCHED_FIFO` is
refused they print what would allow it (an `rtprio` limit, `CAP_SYS_NICE`, or rtkit).

## Showing the test pattern

`fmp pattern` runs the Phase 2 pipeline end to end: CUDA draws a test pattern (colour bars, a moving
box, a ramp and a 32-bit frame counter; 8-bit SDR or 10-bit PQ) straight into a presentation-ring slot,
the slot's timelines order everything GPU-side, and the Vulkan presenter shows it at the display's fixed
refresh, from a dedicated thread paced on `CLOCK_MONOTONIC`. `--blend` puts a 2x generator in the loop:
real frames alternate with `BlendGenerator` output at t = 0.5, written straight into the ring.
`--offscreen` needs no display: the target is the pattern's size and every frame is read back and
compared bit for bit with a CPU reference, so the command doubles as an automated check. It prints
frames presented, dropped and skipped, presents in flight, the GPU time of the pattern, blend and
present work, CPU time per frame, and allocations and collections during the run.

```sh
fmp pattern                                         # 2560x1440 8-bit in a 960x540 window for 5 s (Esc closes)
fmp pattern --format 10 --blend --validate          # 10-bit (HDR10 swapchain where offered), 2x blend, validation layer
fmp pattern --offscreen --size 3840x2160 --frames 600 --validate   # headless, every frame verified
fmp pattern --offscreen --no-verify --frames 1200   # headless without readback, to measure costs alone
fmp pattern --offscreen --unpaced --no-verify --duration-s 3   # stress: no pacing, back-pressure alone
```

It also shows how the presenter learns that a present was shown (and warns when a window cannot cap
presents in flight), how many writes the producer had handed out that the GPU had not finished (the
bridge holds it back at a limit of 2 × the ring's slots), any present request the presenter rejected,
and whether the bridge drained and freed its import at teardown. `--unpaced` drops the demo's pacing
and its wait for a free image, so only the bridge's back-pressure and the presenter's own rules keep
the loop going (frames may be held back or dropped there; nothing may be rejected). It exits 1 on any
failure, a dropped or held-back frame offscreen (paced), a rejected present request, writes signalled
out of order, an import not freed after teardown, a validation message (with `--validate`) or a
managed allocation on the present thread after the warm-up, and 69 with a one-line message when no
usable NVIDIA GPU, Vulkan device, Wayland session or validation layer is found. Ctrl+C or SIGTERM stops
the run cleanly with its verdict (a second one ends it at once). The bridge needs Linux; on other
systems the command says so.

## Decoding a file

`fmp decode <file>` plays a file through the real source path as fast as it decodes: FFmpeg demuxes it, NVDEC
decodes it into CUDA memory in the application's context, the convert kernel turns each NV12/P010 picture into
RGBA8 (8-bit) or RGB10A2 (10-bit) in the frame history. It prints the stream (codec, size, bit depth, rate,
colour and HDR metadata), the content-time statistics (with any jumps in the stream's own timestamps, such as
concatenated recordings, and timestamps repaired), loops and seeks, per-frame GPU and decode-thread costs, the
queue depth, the worst stall of another thread's CUDA calls meanwhile, and the allocation and GC counts. The
verdict fails, naming each reason, on a mismatch, a frame too few or too many in the software decode, content
times that do not increase or that bunch up, corrupt or truncated input, a loop that could not be made, frame-pool
growth or an allocation on a per-frame path.

```sh
fmp decode clip.mkv                         # the whole file
fmp decode clip.mkv --verify                # every frame bit-exact against the ffmpeg CLI's software decode,
                                            #   and every RGB frame against the CPU reference of the conversion
fmp decode clip.mkv --loop --frames 3000 --csv decode.csv
fmp decode clip.h264 --loop --verify        # an elementary stream loops too (from its first byte)
```

## Playing a file

`fmp play <file>` plays a video file with frame generation: decoded ahead (FFmpeg/NVDEC), each output slot
decided by the display-time scheduler (a real frame, a generated one at timestep t, or the last image again),
generated frames made by VFG (the default when the VFX SDK is found; otherwise blend, saying why) straight into the
presentation ring, and shown one per refresh in a window (FIFO, at most two presents in flight, paced from present
feedback: first-pixel times where the display reports them). The present lead (how long before its refresh a slot's
content must be ready) starts from the compositor's latch, measured before playback, plus 2 ms, and adapts. Fullscreen,
where the display follows the presents (variable refresh), each slot is instead handed over at its own time
(`--clock auto`, the default; `--clock fixed|vrr` to choose). There, a generated slot whose next real frame arrives a
little too late for it is shown up to 4 ms after its time, its content moved to match, instead of being skipped (which
would leave the image before it on screen a whole slot longer); it costs no latency, and `--late-generated-ms <ms>` sets
the limit (0 skips them as before). When the slots come at the output's highest rate (4x a 29.97 fps source on a
119.88 Hz TV), a display that has fallen behind cannot catch up by itself (and the console's clock, a little faster
than the TV's highest rate, makes it fall behind about 1 ms a minute), so one generated frame is then left out so it can
(after 30 late shows in a row, at most one every 5 s; `--ceiling-pacing off` turns it off), and the summary's `ceiling`
line reports running behind either way.
A file a hair off the display's rate (60.000 fps on 119.88 Hz) plays 0.1% slower to lock to it. At the end it prints a summary:
slots, presents, every missed slot with its time and whose it was (the pipeline's unless the timestamps show the present
was handed over in time and the display showed it late; misses while the output's rate changes are counted apart),
output intervals from present feedback, the generation time d, the delay to light (lead + D + the stride's rounding),
per-thread CPU and allocations, GC counts and a verdict.

```sh
# a test clip: 60 s of ffmpeg's moving test picture at 1440p60
ffmpeg -f lavfi -i "testsrc2=size=2560x1440:rate=60,trim=duration=60" -c:v libx264 -preset veryfast \
  -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 clip-1440p60.mp4
fmp play clip-1440p60.mp4 --loop                       # VFG Medium, 2x (60 → 120 output), window 1280x720
fmp play clip-1440p60.mp4 --loop --multiplier 4        # 4x where the display allows (every refresh at 240 Hz)
fmp play clip-1440p60.mp4 --generator blend --csv play.csv   # blend, per-slot timeline as CSV
fmp play clip-1440p60.mp4 --fullscreen                 # borderless fullscreen on the window's output (variable refresh)
fmp play clip-1440p60.mp4 --loop --no-quality-switch   # one VFG instance: Q does nothing, 1.3-1.5 GB less GPU memory
fmp play clip-1440p60.mp4 --loop --scd off             # VFG's shot-change detection off: fast motion keeps its generated frames
fmp play clip-1440p60.mp4 --gate                       # the Phase 4 gate: 10 minutes VFG Medium 2x, no miss of the pipeline's
fmp play clip.mkv --offscreen --generator blend --duration-s 5   # no window: offscreen on a virtual clock
fmp verify play.csv                                    # recompute a run's verdict from its telemetry CSV alone
fmp verify play.csv --vrr --rate 240.016               # ... of a variable-refresh run (the output's highest rate)
fmp bench present --test latch                         # where this compositor takes a present (a window's latch)
fmp bench present --test vrr --fullscreen              # whether first pixels follow the presents (variable refresh)
```

A 60 fps file at `--multiplier 2` shows a new image every refresh of a 120 Hz display and every other refresh of a
240 Hz one (each image then shows for two refreshes); `--multiplier 4` gives every refresh at 240 Hz. Window close,
Escape, Ctrl+C and SIGTERM stop it cleanly with its summary, during the start-up too; if GPU work never finishes, it
stops, says what its teardown had to leave and ends the process.

**Q switches VFG's quality while playing** (`fmp play` and `fmp capture`, in the window; key-down only): between the
quality it started at and High (Medium and High by default; Medium and High also when started with `--quality high`; Low
and High with `--quality low`). Both instances are loaded at start, so the switch is a pointer
swap on the generator thread between two frames, with no load (loading during playback would stall the whole GPU for
50 to 200 ms). Going up, the pipeline delay D first rises by High's extra cost (about 5 ms at 4K, in about 100 ms) and the
decisions move earlier step by step, then the generator switches, so no slot is late; going down it switches at once and D
falls back over about a second. Each switch prints one line in capitals (`quality     >>> NOW HIGH (Q pressed) <<<  …`:
nothing on the screen shows it in fullscreen); the summary lists every switch (when, what it cost, the
slots around it) and each quality's latency and generation time. The cost of having it: about 90 ms more start-up and
the second instance's GPU memory (High: about 1.5 GB at 4K, 1.3 GB at 1440p; printed at start); `--no-quality-switch`
(or `fmp settings set generator.qualitySwitch false`) keeps one instance. Measured in
[`docs/benchmarks/phase9-polish.md`](docs/benchmarks/phase9-polish.md), "Live quality switching".

**`--scd on|off`: VFG's automatic shot-change detection** (`fmp play` and `fmp capture`). On, VFG takes large, uneven
change between two frames for a cut, and fast motion is that too (foliage rushing past, an object close to the camera,
fast driving): it then shows a real frame in the generated frame's place, so the picture drops to the source's rate for as
long as the motion lasts. Off, every pair is interpolated; a real cut then shows one generated frame that is a morph of
the two shots (a seek or a loop is still never interpolated across). On by default for `fmp play` (films cut often), **off
for `fmp capture`** (games have fast motion and few cuts); `fmp settings set generator.shotChangeDetection on|off` saves it.
The start-up line says which. Measured in [`docs/benchmarks/shot-change.md`](docs/benchmarks/shot-change.md).

A published binary outside a checkout does not find
the VFX SDK in `lib/VideoFX`: set `$FMP_VFX_SDK` (or `--vfx-sdk`). See `fmp --help` for every option.

## Capturing from a capture card

`fmp capture [device]` captures a V4L2 capture card live (a PS5 over HDMI on an Elgato 4K X) and plays it with frame
generation through the same pipeline as `fmp play`. Each frame is written by the driver straight into CUDA-pinned memory
(V4L2 USERPTR), uploaded and judged by the cadence detector (repeats of a picture are dropped, so a 30 fps game in a 60 Hz
signal is seen as 30 fps, with the time each picture first appeared), then converted to RGB on the GPU (RGB10A2 for 10-bit
P010, RGBA8 for NV12 and YUYV). Nothing waits for anything: when the pipeline does not take frames, the capture drops them
itself, newest first kept. At the start it prints what the device negotiated (and anything the driver changed), the colour
it resolved and why, and the buffer mode; at the end the play summary plus the capture's statistics (frames captured,
unique, repeats dropped, skipped as stale, the detected game frame rate, holds, timestamp jitter, USB delivery, the GPU
cost of upload, cadence and conversion) and the latency the generation adds against the design doc's latency model.

```sh
fmp capture --list                                   # the capture devices and what each offers
fmp capture --mode 4k30-hdr                          # the owner's target, lowest latency: 3840x2160 P010 at 29.97, HDR10 (or SDR
                                                     # as the console sends), VFG to 60, fullscreen (the default) with
                                                     # variable refresh (on DP-3, 240 Hz)
fmp capture --mode 4k30-hdr --hdr-peak-nits 1000     # the HDR10 metadata sent: set the peak the PS5's HDR calibration was for
fmp capture --mode 4k30-hdr --windowed               # the same in a window (fixed refresh, the measured present lead)
fmp capture --mode 1440p60-hdr                       # 2560x1440 P010 at 59.94, HDR10, VFG to 119.88 (60 fps games), variable refresh
fmp capture --mode 4k30-hdr --capture-fps 30         # ask the card for 30.000 (a source that sends 60.000 Hz, a PC)
fmp capture --mode 1440p60-hdr --multiplier 4        # a 30 fps game in that signal to 120
fmp capture /dev/video0 --size 1920x1080 --fps 120 --format nv12 --hdr off   # any offered mode, SDR
fmp capture --mode 4k30-hdr --duration-s 180 --csv capture.csv --frames-csv frames.csv   # timelines for fmp verify
fmp capture --mode 4k30-hdr --scd on                 # VFG's shot-change detection back on (off by default for a capture)
```

The presets ask the card for the console's own rates: 29.97 and 59.94 (exactly 30000/1001 and 60000/1001), as a PS5
sends 59.94 Hz. A card set to 30.000 runs out of new frames once every 33 s and repeats one, which shows as a 58 ms hitch
(at 60.000: every 17 s). `--capture-fps 30` or `60` (or `fmp settings set capture.fps 60`) asks for the whole rate, for
a source that really sends 60.000 Hz. The summary's `capture fps` line says what was asked and set, the repeats one more
than the content's steady cadence and the frames left out, and, when they come at a regular period, which rate the
source looks like and the `--capture-fps` to use (also said once on stderr as soon as it is seen: stderr carries only that
advice). A repeat every half hour or so at the console's own rate is the card's clock (it free-runs a few tens of ppm from
its input): the summary says so and that nothing needs changing. Extra repeats that come at no regular period are the
content's own (a game's repeated frame, the start of a still) and are counted apart. The 120 fps presets
stay at 120: the card offers no 119.88. Under variable refresh the summary's `follow` line says whether the display
stopped following the presents mid-run (first pixels on its refresh grid for a second or more: an overlay, another
window), for how long, and how many late shows fell inside those stretches; one stderr line marks each start and end, and
`fmp verify <csv> --vrr` shows the same from the telemetry.

The Elgato 4K X offers 10-bit P010 only up to 1440p60 and 2160p30 (it takes 30 of the console's 60 Hz itself in that
mode), and reports Rec. 709 whatever the console sends. A P010 capture therefore starts as HDR10 (PQ, BT.2020 primaries,
limited range, the BT.2020 matrix) and **follows the console** (`--hdr follow`, the default): samples that carry 10
significant bits are HDR10, samples that carry only 8 (how a PS5 is assumed to send SDR, padded into P010 by the card;
not yet seen) SDR (BT.709).
The HDR side of that rule is measured; the SDR side is not yet (the console stayed in HDR while this was built): if a
console in SDR is still shown as HDR10, press **H** in the window, which switches between SDR and HDR10 by hand at the
next frame (the output is rebuilt with it, within a frame or two; on 10-bit captures only); **A** follows the signal
again. Keys act when pressed, not while held: Esc stops, H and A as above, Q switches VFG's quality as in `fmp play`
(the start-up's `keys` line lists them). `--hdr on|off` fixes it
for the run. The NV12 modes (`-sdr`) are SDR: the card fits an HDR signal into SDR itself for its 8-bit formats.
`--hdr-peak-nits` (and `--hdr-max-cll`, `--hdr-max-fall`) set the HDR metadata sent with the picture, since the card
carries none: best the peak the PS5's own HDR calibration was done for. At the start `fmp capture` says what the signal is
taken to be, what the output is (format, colour space, the metadata sent) and whether the picture is scaled (PQ in linear
light) or shown pixel for pixel. `--range` and `--matrix` override the rest. MJPEG is never used. Window close, Escape,
Ctrl+C and SIGTERM stop it cleanly; the device is released on every way out. Set `$FMP_VFX_SDK` for a binary outside the
checkout, as for `fmp play`.

### Saved settings

So that `fmp capture` alone gives your setup, save the options you always type:

```sh
fmp settings set capture.mode 4k30-hdr               # checked exactly as --mode is
fmp settings set audio.sink inzone                   # --audio-sink
fmp settings set hdr.peakNits 1000                   # --hdr-peak-nits
fmp capture                                          # now the same as: fmp capture --mode 4k30-hdr --audio-sink inzone --hdr-peak-nits 1000
fmp settings show                                    # every key, its value and whether it came from the file or the default
fmp settings unset audio.sink                        # back to the default (your selected output)
fmp settings path                                    # where the file is
```

The file is `$XDG_CONFIG_HOME/fmp/settings.json`, else `~/.config/fmp/settings.json` (`%APPDATA%\fmp\settings.json` on
Windows); `fmp capture` and `fmp play` read it when it exists, `--settings <file>` reads another and `--no-settings` none.
Precedence: built-in defaults < the file < the command line: an option you type (or one that conflicts with a saved one,
such as `--size` against a saved mode, or `--audio off` against a saved sink) wins for that run. At the start both commands
print one `settings` line naming the file and every value taken from it, and what was not used and why. A malformed file
stops the command with one error naming the file and the line; a key this fmp does not know is reported and skipped. The
keys are `capture.device`, `capture.mode`, `capture.fps` (`--capture-fps`; not used when `--mode` is typed: the mode
brings its own rate), `generator.backend`, `generator.quality`, `generator.multiplier`,
`generator.qualitySwitch` (true or false: `--quality-switch` / `--no-quality-switch`), `generator.shotChangeDetection`
(on or off: `--scd`; unset, capture off and play on),
`display.clock`, `display.fullscreen` (true or false), `display.window` (the window's size when windowed), `audio.mode`,
`audio.sink`, `audio.source`, `audio.offsetMs`, `hdr.mode`, `hdr.peakNits`, `hdr.maxCll`, `hdr.maxFall` and
`generator.vfxSdkDirectory` (`fmp --help`, "saved settings"). `set` writes the file atomically and keeps the other keys
(comments in a hand-edited file are not kept: `set` and `unset` say so when the file had some). If the command then
fails on a saved value (a saved device that is not there), the error is followed by a line naming the file and the
values used from it. The defaults `fmp settings` prints, saved as a file, run exactly as no file.

### HDR test patterns

```sh
fmp hdr-pattern ramp                 # every PQ code left to right, labelled in nits (10 s, in a window; Escape ends it)
fmp hdr-pattern peak                 # patches at 100 ... 10,000 nits, each with an inner square 25% brighter
fmp hdr-pattern bars                 # BT.2020 colour bars at 203 nits (HDR reference white) and at 1,000 nits
fmp hdr-pattern black --fullscreen   # near-black steps; keep fullscreen runs of the bright patterns short
fmp hdr-pattern --check              # no window: the HDR path proved by numbers (exit 1 if any check fails)
```

The patterns go through the same presenter and HDR metadata as a capture. What a correct display shows, and how to
compare with the PS5 plugged straight into the monitor, is in [`docs/benchmarks/phase7-hdr.md`](docs/benchmarks/phase7-hdr.md).
On this desktop the NVIDIA driver describes the HDR10 window to KWin as PQ with the default 203-nit reference white
(and the metadata sent), and the Wayland colour-management protocol has the compositor anchor reference whites: so a PQ
window's 203 nits are shown at the output's "SDR brightness" (440 and 500 nits here), everything scaled with it and
compressed above the output's peak. PQ levels are shown as coded only with that setting at 203 nits (the owner's choice:
it dims the desktop's SDR content too). This follows from what KWin and the driver report; it was not measured with a meter.

### Sound

`fmp capture` plays the card's sound by default: the PipeWire source of the sound card on the capture device's USB
device (`fmp capture --list` shows which), delayed by the video latency the pipeline measures, so the sound is heard when
its picture is seen. It goes to **the default output you selected** in the desktop's sound settings, and moves with it when
you select another; or to `--audio-sink`, any output (the headset too), named by its node name or a part of its name or
description. It plays to no other output device: if that device goes away the sound is muted (and `fmp` says so) until it
is back; it is never moved to another device by PipeWire's own fallback, and a link to another output device (another sink,
a null sink, a link made by hand) is muted and cut. A recorder that takes a copy of the sound is left alone: a screen
share's sound (Discord's takes an application's sound by linking a recording stream to its playback stream, which it finds
by `application.process.binary`, here the binary's name), OBS, `parec --monitor-stream`. (The desktop's
per-application "move to" is refused for this stream: change the output with the default device or `--audio-sink`.) The delay follows that measurement
slowly (a change is resampled away at no more than 2 ms per second, a pitch change of at most 3.5 cents) and the drift
between the card's and the sink's sample clocks is corrected continuously. At the start it prints the source, the sink
and the latencies PipeWire reports; at the end, the seconds played, the delay target and what was achieved, the A/V
offset from the timestamps, the drift correction and any underruns. If the sound cannot start (no PipeWire, no source
for the device, as with `vivid`), the video runs alone and one line says why.

```sh
fmp capture --mode 4k30-hdr                          # with sound (auto: when the device has an audio source)
fmp capture --mode 4k30-hdr --audio off              # picture only
fmp capture --mode 4k30-hdr --audio-offset-ms 20     # the sound 20 ms later than the measured latency says
fmp capture --mode 4k30-hdr --audio-sink inzone       # the INZONE headset (a part of its name is enough)
```

The timestamps cannot see the card's own audio-versus-video delay or the TV's processing, so check by eye and ear
once: in a game or menu with sharp sounds tied to the picture (a button click and its highlight, a gunshot and its
muzzle flash), if the sound comes early, raise `--audio-offset-ms`; if late, lower it (it may go negative down to what
the path allows). If you also hear the console another way (the TV on the card's HDMI out, a headset on the
controller), that sound is not delayed and leads the picture by the video latency: mute it there.

## Benchmarking VFG

`fmp bench vfg` is the design doc's Phase 0 benchmark: for each of 1080p/1440p/2160p × Low/Medium/High ×
RGBA8/RGB10A2 × 2x (t = 0.5)/4x (t = 0.25, 0.5, 0.75) it loads a VFG instance, generates frames between
neighbouring frames of moving content into rotating outputs (rebound every run, as into ring slots) at the
pipeline's cadence, and reports d (the GPU time of each generated frame, from CUDA events), the host time of
each call, load and warm-up time and the instance's VRAM. Every 20th output is read back and checked to be a
genuine interpolation (not a copy of a neighbour, and closer to the true frame than a blend where the motion
is known). It exits 1 if the gate (1440p Medium p99 d ≤ 7 ms) fails or a check does, 69 without a GPU or SDK.

```sh
fmp bench vfg                                          # the full matrix (about 6 minutes)
fmp bench vfg --size 1440p --mode medium --csv vfg.csv # the gate cells only
fmp bench vfg --content pan --unpaced --scd off        # a textured pan, back to back, shot-change detection off
fmp bench vfg --video lavfi:testsrc2=size=1920x1080:rate=60   # frames decoded by the ffmpeg CLI
fmp bench vfg-quality                                  # SDR and PQ scenes: scores and 16-bit PNGs in out/vfg-quality
fmp pattern --vfg --offscreen --validate --format 10   # VFG writing straight into the presentation ring, scored
```

`fmp bench vfg-quality` runs VFG at t = 0.5 on scenes with known motion (the test pattern, a textured pan,
a photo, the PQ ramp and wedge panned, moving specular highlights, a dark gradient) in each quality mode,
scores the output against the true middle frame, and writes before/truth/after/blend/VFG/error images as
16-bit PNGs (PQ ones tagged BT.2020 + ST 2084) with an `index.md` into `out/vfg-quality` (git-ignored).
`fmp pattern --vfg` puts VFG in the Phase 2 pipeline: real pattern frames alternate with VFG output at
t = 0.5 written into the ring slots, real frames verified bit-exact and generated ones scored against the
pattern's true middle frame (`--settings <file>` applies a settings file's SDK directory). It is a check, not
a model: with automatic shot-change detection on (the default) VFG's `Enqueue` blocks until the frame is
generated (`FrameGenCapabilities.EnqueueMayBlock`), and the demo calls it on its present thread, where a
pipeline must not.
