# Frame Motion Plus: phased implementation plan

This turns the design in [`Live Frame Generation App Implementation Plan.md`](../Live%20Frame%20Generation%20App%20Implementation%20Plan.md)
(the "design doc") into build phases for a C# / .NET 10 solution. The design doc stays the source of
truth for *what* and *why*; this file says *in what order*, *in which project*, and *how each phase is
proven*.

## Development machine (probed 2026-11-15)

| Item | State | Consequence |
| --- | --- | --- |
| .NET SDK | 10.0.114 | Target `net10.0`, NativeAOT for the app |
| GPU / driver | RTX 5090, 620.70.06 | `libcuda.so.1`, `libnvcuvid`, `libnvidia-present` present |
| Vulkan | 1.4, `VK_EXT_present_timing`, `present_wait2`/`present_id2`, `fifo_latest_ready`, `hdr_metadata`, `swapchain_colorspace`, external memory/semaphore FD, `KHR_display`, `acquire_drm_display` | Everything the design doc's pacing and HDR sections rely on is exposed |
| Desktop | Plasma 6.8.2 Wayland; DP-3 4K 240 Hz, HDMI-A-1 4K 120 Hz; HDR on, VRR automatic | Real presentation, HDR and VRR tests can run here |
| FFmpeg | 9.0.2 (libavcodec 63) with `cuda` hwaccel | FFmpeg.AutoGen 9.x |
| SDL3 | 3.4.20 | System library, C# bindings |
| clang / LLVM | 23.1.5 with the `nvptx64` target | CUDA kernels compile to PTX without the CUDA toolkit |
| CUDA toolkit (`nvcc`, NVRTC) | **Not installed** | Use the driver API only; kernels via clang NVPTX |
| NVIDIA VFX SDK (Maxine VFG) | 1.3.0.0, unpacked under `lib/` (git-ignored, never committed) | Phase 3 is unblocked. See "VFX SDK layout" below |
| Capture card | Elgato 4K X (`0fd9:009b`) on a 10 Gbps USB link, `/dev/video0` (capture) and `/dev/video1` (metadata) through `uvcvideo`; readable by the desktop user without sudo (logind ACL) | Phase 5 can run on real hardware. Uncompressed NV12 up to 2160p60, 1440p144 and 1080p240; **10-bit P010 up to 1440p60 and 2160p30** (answers the design doc's open question); also YUYV, YU12, BGR3; MJPG only is offered for 2160p120/144. `vivid` (`sudo modprobe vivid`) remains an optional stand-in for tests without a signal |
| sudo | Needs a password | Agents cannot install system packages or load kernel modules |

### VFX SDK layout

The SDK is proprietary and is not redistributed with the repo. Locally it sits in `lib/`:

```
lib/VideoFX/                         SDK Core 1.3.0.0 (nvidia/maxine/vfx_sdk_core:1.3.0.0_linux)
  include/                           nvVideoEffects.h, nvCVImage.h, nvCVStatus.h
  lib/                               libVideoFX.so, libVideoFXLocal.so, libNVCVImage.so
  external/cuda/lib/                 bundled CUDA 12 runtime, NPP, cuDNN
  features/nvvfxvideoframegeneration -> ../../nvvfxvideoframegeneration
lib/nvvfxvideoframegeneration/       VFG feature 1.3.0.0: libnvVFXVideoFrameGeneration.so + header
```

A throwaway C smoke test (not in the repo) confirmed the SDK loads and interpolates on this machine
with `LD_LIBRARY_PATH` set to `VideoFX/lib`, `VideoFX/external/cuda/lib` and the feature's `lib`.
**Preliminary** numbers from it, on synthetic frames, wall-clock around `NvVFX_Run` + stream sync,
explicit timestep mode. **Phase 3 has since measured this properly and confirmed the picture; use
`docs/benchmarks/phase3-vfg.md`, not this table:**

| Resolution | Low p50 / p99 (ms) | Medium p50 / p99 (ms) | High p50 / p99 (ms) |
| --- | --- | --- | --- |
| 720p | 0.29 / 0.71 | 1.08 / 1.77 | 5.62 / 6.61 |
| 1080p | 0.33 / 0.98 | 1.12 / 1.80 | 5.72 / 6.99 |
| 1440p | 0.45 / 0.98 | 1.17 / 1.89 | 5.81 / 7.08 |
| 2160p | 0.62 / 1.30 | 1.36 / 2.02 | 6.15 / 7.37 |

RGB10A2 (`NVCV_RGB10A2` + `NVCV_P32`) costs the same as RGBA8. Cost is almost flat in resolution, not
linear as the design doc assumed, so 4K at Medium looks viable and High fits a 60 fps source. Also seen:
`NVVFX_OUTPUT_IMAGE` can be rebound after load without error, and load takes well under a second.

## Solution layout

Projects are created in the phase that first needs them.

```
FrameMotionPlus.slnx
Directory.Build.props        net10.0, nullable, TreatWarningsAsErrors=true, analyzers, AOT analysis
Directory.Packages.props     central package management: every package version lives here
nuget.config                 nuget.org only, sources cleared, package source mapping
global.json                  pins the .NET 10 SDK band
src/
  FrameMotionPlus.Core/          pure, no OS or GPU calls: the six interfaces, frame/colour types,
                                 scheduler, cadence tracker, SPSC rings, telemetry, settings   (P1)
  FrameMotionPlus.App/           NativeAOT executable `fmp`: CLI, pipeline assembly, threads   (P1)
  FrameMotionPlus.Platform/      real monotonic clock, precise sleep, real-time thread helpers (P2)
  FrameMotionPlus.Cuda/          CUDA driver-API bindings, wrappers, kernels,
                                 Repeat and Blend generators                                  (P2)
  FrameMotionPlus.Vulkan/        device selection, exportable presentation ring, presenter,
                                 SDL3 window, HUD; knows nothing about CUDA                   (P2)
  FrameMotionPlus.CudaVulkan/    the IGpuBridge: imports the Vulkan ring and semaphores
                                 into CUDA                                                    (P2)
  FrameMotionPlus.Vfx/           NvCVImage / NvVFX bindings, VfgGenerator                     (P3)
  FrameMotionPlus.Sources.File/  FFmpeg demux + NVDEC                                         (P4)
  FrameMotionPlus.Sources.V4l2/  capture card                                                 (P5)
  FrameMotionPlus.Audio/         capture-card audio: PipeWire streams and the delay line      (P8)
native/
  kernels/                       CUDA kernel sources and generated PTX                        (P2)
tests/
  FrameMotionPlus.Core.Tests/    always run
  FrameMotionPlus.Gpu.Tests/     hardware-gated (see Testing)
docs/
  implementation-plan.md         this file
  benchmarks/                    measured results that gates are judged against
```

## Conventions for every phase

- **Build:** `net10.0`, latest C#, nullable enabled, `TreatWarningsAsErrors=true`, .NET analyzers on,
  `IsAotCompatible=true` on libraries so trim/AOT warnings fail the build. No per-project package versions.
- **Interop:** source-generated `LibraryImport` and function pointers only. No `DllImport`, no reflection,
  nothing that breaks NativeAOT.
- **Hot path** (anything run per frame): preallocated structs and native memory; no LINQ, closures,
  boxing, `async`, `params`, string formatting or logging allocations. Hot paths get a test asserting
  zero bytes allocated.
- **Time:** one clock domain. Every timestamp is `long` nanoseconds on `CLOCK_MONOTONIC`.
- **Platform split:** Core never references a platform project. Linux-only code lives behind the six
  interfaces (`IFrameSource`, `IFrameGenerator`, `IPresenter`, `IGpuBridge`, `IAudioDelay`, `IClock`).
- **Testing:** xUnit v3. Unit tests always run. Hardware tests carry a category and *skip* (never fail)
  when their requirement is missing, so hosted CI stays green:
  `Gpu` (NVIDIA GPU + `libcuda`), `Vfx` (VFX SDK), `Display` (Wayland session), `Capture` (a V4L2 device).
- **CI:** GitHub Actions builds and tests on Linux and Windows from Phase 1; Linux also does the
  NativeAOT publish.

## Workflow for every phase

1. Implementation by Opus 5.5 sub-agents, working from this plan and the design doc.
2. Build, tests and the phase's automated gate are run and the output captured. Tests are run the way
   a developer runs them, `dotnet test FrameMotionPlus.slnx` on the whole solution, several times in
   a row: running each test project alone hid two failures in Phase 2 that only appeared when the
   hardware suites shared the GPU.
3. An independent sub-agent reviews the phase's diff with no knowledge of the implementer's reasoning.
4. Review findings are fixed and re-verified.
5. Report to Adam: what was built, test and gate results, review findings and their resolution,
   deviations, anything still open.
6. On approval: commit, push to `master`, start the next phase.

Gates are split into **automated** (verified by the agents on this machine) and **manual** (need Adam,
hardware or eyes). A phase report states which manual gates are still outstanding.

## Phases

### Phase 1: Solution foundation and pure core

*Design doc: architecture, frame pacing maths, automated tests.*

- Solution scaffold: `.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `nuget.config`,
  `global.json`, `.editorconfig`, `.gitignore`, `.gitattributes`, README, CI workflow.
- **Core abstractions:** the six interfaces; `GpuFrame`, `GpuSignal`, `PixelFormat` (RGBA8, BGRA8,
  RGB10A2), `MemoryDomain`, `ColorMetadata` (primaries, transfer, matrix, range, mastering display,
  content light level), `FrameGenCapabilities`, `FrameGenConfig`. 10-bit is representable from day one.
- **Scheduler:** the display-time rule (τ = V − D, t = (τ − tsₙ)/(tsₙ₊₁ − tsₙ)), epsilon snap to real
  frames, fixed-refresh and VRR slot clocks, unique-frame timeline, hold detection (no new frame within
  ~2× the expected interval stops generation), multiplier fallback when d > T/M, pipeline-delay tuner
  (hold + p99(d) + margin, slew-limited), latency model calculator.
- **Cadence tracker:** the CPU half of cadence detection: takes a per-frame max-tile-difference metric,
  classifies unique vs repeat with hysteresis, estimates the game frame rate, separate SDR and PQ thresholds.
- **Infrastructure:** lock-free SPSC ring, per-frame timeline telemetry ring, CSV export, allocation-free
  rolling percentile estimator, `IClock` with a simulated clock, settings model (source-generated JSON).
- **App:** `fmp` NativeAOT executable with `fmp simulate`, which drives the scheduler from a simulated
  source and clock with null generator and presenter and writes telemetry CSV.
- **Tests:** scheduler simulations for 30-in-60, 60 to 120, 40-in-120, 30 to 120, 59.94 on 120.00,
  non-integer 30 to 144, random drops, uneven frame times and long holds; latency model against the
  design doc's table; ring stress test; percentile and tuner tests; zero-allocation assertions.
- **Automated gate:** zero-warning build; all tests pass; `dotnet publish` NativeAOT for `linux-x64`
  succeeds with no trim/AOT warnings; `fmp simulate` output matches the latency model.

### Phase 2: GPU foundation (CUDA, Vulkan, the presentation ring)

*Design doc: GPU pipeline and CUDA/Vulkan interop; Phase 0 items that do not need VFG.*

- **CUDA:** driver-API bindings and wrappers: context, three streams (copy, compute, high-priority),
  device and pinned host memory, events for timing, module/kernel launch, external memory and external
  semaphore import.
- **Kernel toolchain:** kernels compiled to PTX, embedded as resources, generated PTX checked in so a
  build needs no GPU toolchain. The VFX SDK under `lib/` bundles `nvcc` 12.8 and the CUDA headers
  (`lib/VideoFX/external/cuda/`), which a regeneration script can use on a dev machine; clang's NVPTX
  backend is the alternative that needs no NVIDIA toolchain. First kernels: test patterns (SDR and
  PQ), blend, copy/pack.
- **CUDA context:** the device's primary context, so the cudart-based VFX SDK (Phase 3) and FFmpeg's
  CUDA hwaccel (Phase 4) share it without extra plumbing.
- **Vulkan:** physical device matched to the CUDA device by UUID; presentation ring of 4 to 6 exported
  `VkBuffer`s imported into CUDA; two timeline semaphores (CUDA signals written, Vulkan signals released);
  presenter pass (ring slot to image, scale, write swapchain); SDL3 window on Wayland at fixed refresh;
  offscreen target with readback for tests. RGBA8 and A2B10G10R10 ring formats.
- `RepeatGenerator` and `BlendGenerator` (CUDA domain) behind `IFrameGenerator`.
- **Decide here, with the real APIs in hand:** how a generator on its own CUDA stream waits for its
  inputs (the source frame's upload/convert and the ring slot being free). Phase 1 left
  `IFrameGenerator.Enqueue` exactly as the design doc has it and did not put a ready-signal on
  `GpuFrame`; if stream ordering alone is not enough, this phase changes Core to carry one.
- **Platform:** `IClock` on `CLOCK_MONOTONIC`, absolute-deadline sleep (`clock_nanosleep`), dedicated
  real-time threads with affinity; portable fallbacks so Windows builds.
- `fmp pattern` (CUDA test pattern through the ring to a window) and `fmp bench jitter`
  (NativeAOT hot-loop jitter on a dedicated thread).
- **Build order:** Platform, Cuda and Vulkan are independent and built in parallel; CudaVulkan and
  `fmp pattern` join them afterwards.
- **Tests (`Gpu`, `Display`):** CUDA memory round trip; kernel load and run; CUDA writes a pattern into
  an exported buffer and Vulkan reads it back bit-exact, both formats; semaphore handshake in both
  directions; validation layer reports nothing.
- **Automated gate:** interop round trip passes; `fmp pattern` runs clean under the validation layer;
  runtime jitter under 0.5 ms p99 (design doc Phase 0 gate).

### Phase 3: VFG backend and the feasibility gate

*Design doc: Phase 0 remainder, VFG backend details. This is the go/no-go on VFG cost.*
The SDK is found at run time through a configurable SDK directory (default `lib/VideoFX` in a dev
checkout); nothing from `lib/` is copied into build output or committed.

- `NvCVImage` / `NvVFX` bindings; `VfgGenerator`: explicit timestep mode, own CUDA stream, preallocated
  image wrappers, manual shot-change flag on discontinuities, warm-up at startup, background reload and
  swap for quality or resolution changes.
- `fmp bench vfg`: 1080p / 1440p / 2160p × Low / Medium / High × (t = 0.5, 4x); p50/p99 of d from CUDA
  events; first-load time, engine caching, VRAM. Results written to `docs/benchmarks/`.
- Answer the design doc's open questions: can `NVVFX_OUTPUT_IMAGE` be rebound per run to a ring slot;
  how long is first load and is the engine cached.
- PQ RGB10A2 clip through VFG with frame dumps for inspection.
- **Rules from Phase 2 this phase must respect** (measured on driver 620.70.06; see
  `docs/benchmarks/phase2-interop.md`):
  - No context-synchronising call during playback. A CUDA free or external-semaphore destroy waits
    for all queued work in the context, and while it waits, kernel launches and other enqueues on
    other threads block. That covers `NvCVImage` deallocation, effect destroy, and the background
    reload-and-swap: preallocate everything, and swap only at a drained point (stop producing, drain
    the ring, then free).
  - **Measure whether `NvVFX_Run` or `NvVFX_Load` synchronises the whole context**, and whether a
    waiting context-wide synchronise blocks other threads' enqueues the way a free does. If the run
    call does, the generator cannot share a context with a producer that runs ahead of its presents.
  - A CUDA stream holds about 1,024 queued operations; past that the enqueue call blocks. Count the
    operations VFG adds per frame against the bridge's `OperationsPerWrite` budget.
  - One pipeline per CUDA context; writes into the ring must be signalled in order, so a generator
    on its own stream must order its `done` signals with the stream that writes real frames.
  - Use `TryAcquireSlot` and skip a generated frame at the source when the bridge refuses; keep the
    lead within about N.
- **Automated gate:** 1440p Medium p99 d under 7 ms; VFG writes into the Vulkan-exported ring and the
  result presents. If 1440p fails, the report recommends 1080p or Low mode and later phases retarget.
- **Manual gate:** Adam looks at the PQ dumps for highlight, gradient and banding artefacts.
  **Passed 2026-11-17:** "The images from phase 3 look good."

### Phase 4: File playback, first light

*Design doc: milestone 1.*

This phase is built, reviewed and committed in three steps, because Phase 2 showed that a large
step reviewed in one go costs more rounds than several small ones:

- **4a, decode:** the generator and bridge backlogs below; FFmpeg demux + NVDEC into the app's CUDA
  context; the convert kernel; frame history; `fmp decode` to verify a file against a software decode.
- **4b, pipeline:** source, scheduler, generator and present threads joined by SPSC rings; `fmp play`
  with repeat, blend and VFG; telemetry CSV; the 10-minute gate run.
- **4c, around it:** HUD and the quality harness.
  **Deferred on 2026-11-18** so that capture (Phase 5) starts first: `fmp play`'s summary and
  telemetry CSV already report what the HUD would show. Listed under "Deferred".

- FFmpeg demux + NVDEC (`hwaccel cuda`) sharing the app's CUDA context; convert kernel
  (NV12/P010 to RGBA8/RGB10A2, chroma upsample, matrix and range); frame history; PTS timing.
- Full pipeline assembly: source, scheduler and present threads joined by SPSC rings; generator
  selectable (repeat, blend, VFG 2x); fixed-refresh presenter.
- HUD (frame-time graph, d, D, dropped/repeated, detected frame rate) and telemetry CSV export.
- **Smooth Motion is dropped for now** (owner's decision, 2026-11-17: focus on VFG). The design doc's
  second backend, NVIDIA's driver-level Smooth Motion as a presenter mode with an A/B against VFG,
  is not built in this phase. The presenter's layer policy can already enable the driver layer, so
  it can be picked up later without restructuring; it is listed under "Deferred" at the end.
- Quality harness: 120 fps footage, drop alternate frames, regenerate, score PSNR/SSIM against originals.
- File audio (audio as master clock) arrives with Phase 8; until then file video is timed from PTS.
- **Decided from the Phase 3 results** (`docs/benchmarks/phase3-vfg.md`):
  - **Go.** 1440p Medium p99 d is about 1.2 ms against the 7 ms gate; cost is almost flat in
    resolution (4K Medium about 1.4 / 1.6 ms p50 / p99; High about 6.1 / 6.5 to 6.8 ms). 4K60 to
    120 fits at Medium, as do the 4x modes. Default quality is Medium at the source's own resolution.
    High is an option, not an upgrade: it disturbed dark backgrounds in the PQ highlight test, and 4K
    High at 120 Hz is marginal (up to 8.2 ms seen against an 8.33 ms slot).
  - **VFG runs on a dedicated generator thread.** With automatic shot-change detection on (the
    default, as the design doc says), `NvVFX_Run` blocks its caller, spinning, for the whole
    generation and for its `Ready` dependencies. `FrameGenCapabilities.EnqueueMayBlock` says so. The
    generator thread must not be the present or pacing thread, nor a thread whose later action
    writes the inputs or releases the output slot. Decide here whether to make the CUDA context
    `BlockingSync` (measured: blocked-thread CPU 1.16 to 0.11 ms for 0.04 to 0.08 ms more wall time).
  - Detection off makes the call enqueue-only (under 0.1 ms) but then an unreported hard cut comes
    out as a morph of the two shots. Do not turn it off until the app has its own cut detector
    (the Phase 5 cadence kernel is the candidate).
  - Preload every VFG instance the session needs (RGBA8 and RGB10A2 can live in one context) and
    switch with `TryConfigurePrepared`. `NvVFX_Load` and `NvVFX_DestroyEffect` synchronise the whole
    context (other threads stalled about 280 to 300 ms): only at a drained point.
  - Configure the bridge for VFG's stream operations per run: Low 28, Medium 33, High 170 (the
    default budget is 16). High needs `OperationsPerWrite` of at least 176 and a lead of at most 5.
  - Call `NotifyDiscontinuity` immediately before enqueueing the pair across the cut: it binds to
    the next pair enqueued. A throwing `Enqueue` produced nothing and will not signal `done`; a
    failed SDK call leaves the generator faulted until it is reloaded.
  - The SDK is loaded by absolute path from a configurable directory (`--vfx-sdk`, `$FMP_VFX_SDK`,
    settings, a dev checkout's `lib/VideoFX` beside the binary, `/usr/local/VideoFX`); no
    `LD_LIBRARY_PATH`. The Windows loader path has never run.
- **Generator backlog from the Phase 3 review** (approved with these tracked; settle them first,
  before the generator thread is built on them):
  1. `VfgGenerator.TryConfigurePrepared` is documented as safe on any thread but writes the active
     instance and its configuration as two unsynchronised fields that `Enqueue` reads separately. A
     switch from a control thread can run the old instance against frames validated for the new
     configuration, which throws and faults a healthy instance. Restrict it to the thread that calls
     `Enqueue`, or swap one immutable reference.
  2. `DiscontinuityLatch` has three traps the `IFrameGenerator` contract must spell out or remove:
     a notification whose pair is never enqueued (the scheduler shows real frames across a cut)
     stays armed and makes the first pair *after* the cut output the current frame instead of
     interpolating; a notification made mid-pair from another thread binds to the old pair; and with
     fixed buffers and `ContentTimeNs` unset the pair key never changes, so every later run outputs
     the current frame. Every frame must carry a distinct content time (`fmp pattern` and the tests
     use 0 today), and the latch should be released when t does not increase.
  3. "A call that throws produced nothing" overstates: GPU-side waits may already be queued and the
     output partly written. Say so, and say what the pipeline does with that slot.
- **Carried over from Phase 2:**
  - The three CUDA streams (copy, compute, high-priority generator) are created here by the pipeline.
  - `GpuFrame.Ready` now carries a frame's "safe to use" signal, so `SourceFrame.Ready` and
    `PresentRequest.Ready` duplicate it; collapse them.
  - A source must wait on the generator's `done` before overwriting a history buffer.
  - With more than 8 CUDA streams alive, two can share a hardware queue and serialize
    (`CUDA_DEVICE_MAX_CONNECTIONS`, default 8). It must be set in the launcher's environment;
    setting it from inside the process does not reach the driver.
  - A GPU span timer started on an idle stream includes host enqueue time; start spans behind work.
  - `cuMemFree` and `cuMemFreeHost` block until **all** queued work in the context has finished (a
    free took 300 ms behind 300 ms of unrelated work). Never free CUDA buffers or imports on the
    capture, scheduler or present threads; hand them to a housekeeping thread.
  - The object behind a `GpuSignal` (CUDA event, imported semaphore) must outlive every frame or
    request that names it; on reconfigure or source switch, drain before disposing.
  - FFmpeg's CUDA frame pools and a decoder re-initialisation on a resolution change free CUDA
    memory; under the "no context-synchronising call during playback" rule they must happen at a
    drained point too.
  - `fmp pattern`'s pacing loop (sleep-poll for a free image on the present thread) is demo-only and
    must not be copied; the pipeline paces from the scheduler and present feedback.
- **Bridge backlog from the Phase 2 review** (approved with these tracked; fix them when this phase
  builds pipeline start-up, restart and teardown, since that is what exercises them):
  1. The import reaper can block freeing one abandoned import behind another abandoned import's
     stuck writes, holding its lock so a new bridge's constructor waits (seen: 5 s, then a 60 s
     teardown bound). Needs two abandons plus a failed Vulkan-side release. Do not free an entry
     while another entry on the same context is not yet done.
  2. A bridge disposed *after* its presenter, with a write never signalled, parks a drain wait that
     cannot complete, and later CUDA frees in the context block. In that branch release from CUDA
     and hand the import to the reaper with no probe.
  3. Abandoned imports are freed at the reaper's next 5 ms look with no bridge live, not at the
     moment the last bridge is disposed: an application that restarts its bridge immediately keeps
     every abandoned import until shutdown. Run a free pass when the live count reaches zero.
  4. Documented caller errors that stay as they are: a process returning from `Main` without
     disposing the presenter while a release waits on a never-signalled write hangs in the driver's
     exit path; N or more consecutive never-signalled writes wedge later bridges on that ring until
     the presenter is disposed; a second pipeline on the same CUDA context is unsupported.
- **Decided and found in step 4a** (`docs/benchmarks/phase4a-decode.md`, "What the pipeline must
  know", is the reference for 4b):
  - FFmpeg is bound with hand-written `LibraryImport` bindings checked against the system headers,
    not FFmpeg.AutoGen (it binds through runtime marshalling and lacks `AVCUDADeviceContext`).
  - FFmpeg shares the app's primary CUDA context through `AV_CUDA_USE_CURRENT_CONTEXT`; its copies
    run on the source's own copy stream, which nothing else may use. Steady decoding makes no
    allocation, free or context synchronise; opening and closing a file do, so they are drained
    points. The decode thread waits for the GPU once per frame inside NVDEC and stops whenever the
    compute stream is held.
  - `NotifyDiscontinuity(contentTimeNs)` names the first frame after the cut; generators refuse a
    pair whose content times do not increase. A frame's `ColorMetadata` describes the pixels in the
    buffer (converted frames: identity matrix, full range); the YCbCr description stays with the
    source.
  - A backward timestamp step, or a forward step over 10 s, is a discontinuity. The real gap is
    kept on continuous timelines (MP4, Matroska) and collapsed only for formats whose clocks jump
    (MPEG-TS/PS). Matroska's whole-millisecond timestamps (16/17/17 ms at 60 fps) are not a variable
    frame rate.
  - `IFrameSource.Release` should carry the GPU signal of the last reader (today only the concrete
    file source has that overload); `SourceFrame.Ready` and `Frame.Ready` are still duplicated.
- **Decode backlog from the 4a review** (approved with these tracked):
  1. Every MPEG-TS seek restarts at the first byte and decodes forward to the target, because the
     keyframe check accepts a landing only if the first packet read is itself a keyframe: correct,
     but the cost grows with position (a seek to 50 s decoded 1,500 frames). After landing, read
     forward to the next keyframe and accept it when it is at or before the target.
  2. `fmp decode` leaves the step across a discontinuity out of its interval statistics, so a file
     with a kept 12 s gap prints "constant frame rate"; `Seek`'s summary still says "nothing
     changed" on failure although a multi-probe search may have moved the demuxer.
  3. Untested: a file that grows while it plays; MPEG-PS (handled like TS); the Windows path, which
     reports file playback as unavailable.
- **Automated gate (relaxed by the owner on 2026-11-18):** a 1440p60 file at 2x for 10 minutes with
  **zero missed slots caused by the pipeline** and zero gen0 collections; quality harness produces
  scores for each generator. Slots the desktop compositor shows late or skips are counted and
  reported separately and do not fail the gate: the runs are made in a compositor window on a
  desktop that is in use ("missing the odd frame is to be expected"). The design doc's strict form,
  zero missed slots of any cause, moves to Phase 9, where direct display bypasses the compositor.
  This only works if the miss accounting is right: a miss is the pipeline's unless the timestamps
  show the present was handed over in time for its refresh and the display showed it late.
- **Manual gate:** VFG file playback looks right to Adam (smooth motion, no visible pacing hitches).

### Phase 5: Capture card (V4L2)

*Design doc: milestone 2.* **Prerequisite for the hardware gate: a capture card, PS5 HDCP off.**

- **Owner's target mode (2026-11-17): 4K HDR, 30 to 60.** The Elgato 4K X offers uncompressed 10-bit
  P010 at 2160p only at 30 / 29.97 fps (2160p60 is 8-bit NV12 or MJPG), so this mode captures
  `3840x2160 P010 @ 30` and generates to 60. A capture in that mode streams at 30.00 fps on this
  machine (tested with no input signal: black frames). VFG cost is no obstacle (4K Medium about
  1.4 ms, High about 6.5 ms, against a 16.7 ms slot). What has to be established first, with a real
  PS5 signal, because the PS5 outputs 60 Hz and the card does the 60 to 30 reduction itself:
  1. How the card reduces 60 Hz to 30: which copy of each repeated frame it keeps (up to one
     refresh, 16.7 ms, of extra latency if it keeps the second), whether that phase is stable, and
     whether it ever blends frames.
  2. What happens when the game's pacing is uneven or the game runs at 60 fps: a frame shown for a
     single refresh can be dropped by the card, and the app cannot see it. Cadence detection cannot
     help in this mode, because every captured frame is already unique.
  3. The V4L2 format reports sRGB / Rec. 709 for P010 here, so HDR is not signalled: the manual HDR
     override from the design doc is required, and metadata comes from defaults.
  The alternative for games that run at 60 fps is `2560x1440 P010 @ 60` to 120 with the presenter
  scaling to 4K. Both modes are built; the app should say which one a given signal calls for.

- V4L2 ioctl bindings with struct layouts checked against the kernel headers; `USERPTR` buffers from
  `cudaHostAlloc` with `MMAP` + `cudaHostRegister` fallback; real-time dequeue thread, newest-wins;
  upload and convert (adds YUYV); cadence kernel (max tile difference on a downsampled luma grid) feeding
  the Phase 1 tracker; hold handling; negotiated-format logging and MJPEG refusal.
- **Carried over from Phase 2:** `cuMemHostAlloc` memory is not page aligned (the driver sub-allocates
  at 512-byte granularity). V4L2 `USERPTR` buffers must be allocated with
  `CudaPinnedBuffer.Allocate(..., alignment: Environment.SystemPageSize)`.
- **Automated gate:** cadence detection correct on synthetic 30-in-60, 40-in-120 and dropped-frame
  sequences run through the real kernel; capture pipeline runs against `vivid` when loaded.
- **Manual gate:** end-to-end latency measured; 60 to 120 at 1440p within 2 ms of the latency model;
  a 30 fps game inside a 60 Hz signal is detected.
  **Partly passed 2026-11-18:** the owner ran `fmp capture` on the PS5 through the Elgato with VFG
  and confirmed "it works". Still open: end-to-end latency measured with a camera or photodiode,
  the 1440p 60 to 120 comparison with the latency model, and a 30 fps *game* (the home screen was
  detected) inside a 60 Hz signal.
- **Cadence tracker backlog from the Phase 1 review** (accepted as non-blocking there; these become
  acceptance cases once the real metric kernel and a capture card set the thresholds):
  1. The estimate is frozen while in-band frames (metric between the low and high thresholds) arrive
     at least once per 8 unique frames. 60 to 30 fps with every 8th new frame in-band froze at
     50.53 fps; a learned 60 fps followed by 30 fps with in-band repeats stays at 60 and takes every
     capture as new; a source that starts with in-band repeats never learns a rate.
  2. One long gap in every 3 to 4 frames that is at least 5× the short gap (60 fps with an 83 ms hitch
     every 4th frame) makes the estimate saw-tooth 30/48/30. After a held-share reseed, do not hold gaps
     up to the largest gap just accepted.
  3. Long gaps under 25% of intervals are reported as hitches, not cadence: gaps 1,1,1,1,3 read 60.00
     against a true mean of 42.86. Decide whether that is the wanted meaning of "game frame rate".
  4. Animated menus (three or more irregular sub-200 ms gaps) can reseed the estimate; it recovers
     within 32 unique frames after play resumes.
  5. Cadences whose cycle is longer than 16 frames (57 fps in a 60 Hz signal) ripple by about 3%.
  6. A cadence slower than the 200 ms cap (under 5 fps) reports 0 by design.

### Phase 6: Pacing engine

*Design doc: milestone 3.*

- `VK_EXT_present_timing` targets and feedback; VRR clock mode; live auto-tuned D; 3x and 4x;
  fallback path (`present_wait2`/`present_id2`, `FIFO_LATEST_READY`, `clock_nanosleep` absolute);
  at most two swapchain images in flight; multiplier fallback when VFG cannot keep up.
- **Automated gate:** 30 to 120 and 40 to 120 with output interval standard deviation under 0.5 ms from
  present feedback; 59.94 source on 120 Hz with no periodic judder signature over 10 minutes.
- **Outcome of this phase (2026-11-18, `docs/benchmarks/phase6-pacing.md`):**
  - Variable refresh works for fullscreen windows on both outputs and is chosen automatically
    (`--clock auto`); `fmp capture` opens fullscreen by default. On the 119.88 Hz output a 60 fps
    source doubled exceeds the ceiling, so that case stays on fixed refresh.
  - A real frame is shown after its capture timestamp: 4K HDR 30 to 60, 63 ms before, 54 ms
    windowed (fixed refresh), 47 ms fullscreen (variable refresh); 1440p with 30 fps content, 51 ms
    before, 39 ms windowed, 32 ms fullscreen. In 4K what remains is card-to-PC delivery (27 ms) and
    the hold and generation (19 ms).
  - 10-minute gate runs, judged by the live counters and `fmp verify` in agreement: 240 Hz output
    0 missed of 143,533; 120 Hz output 4 missed of 71,688 in one host stall. 3x and 4x run with no
    misses.
  - Not done: the Phase 1 scheduler tuning backlog below; zero-miss pacing; and the items in that
    document's "Known issues / not done".
- **Owner's tolerance and manual check (2026-11-18):** after playing a game through `fmp capture`
  ("incredible"; an earlier variable-refresh run was "fantastic - smooth 60fps and the latency was
  great"): "it's to be expected we're going to miss a handful of frames - 4 in 72,000 is acceptable
  for now, we can focus on perfecting it in a later phase ... let's not get lost in perfection yet".
  So a handful of missed slots in a 10-minute run does not hold this phase; exact pacing is to be
  perfected later (see "Deferred").
- **Found in Phase 2 on this machine's Wayland surface:** `VK_EXT_present_timing` is supported for
  feedback, but its present-at-absolute-time and present-at-relative-time capabilities are both
  false, so target times cannot be requested there. Plan on feedback plus the fallback path
  (`present_wait2`/`present_id2`, `FIFO_LATEST_READY`, absolute sleep) for windowed Wayland, and
  re-check the capability on a direct display (Phase 9) and on newer drivers. The surface offers
  Mailbox, FIFO_LATEST_READY, FIFO and Immediate.
- **Also found in Phase 2 on this desktop:**
  - While another GPU-heavy app is busy (a Steam/Proton game at 160 to 210% CPU, or Discord in a
    call), KWin skips about one refresh in three for our window even with a present queued: a
    240 Hz window is shown at about 160 Hz. Idle, cadence is exact. Present feedback distinguishes
    the two (refreshes skipped with a present queued versus with none). The scheduler must cope
    with the compositor delivering a fraction of the refresh rate, and the HUD should say so.
  - The 2-in-flight cap holds only with present feedback (`present_wait2`, or the older
    `present_wait` with later "shown" reports). With none, 5 presents were queued against 3 images
    and `PresenterCapabilities.MaxFramesInFlight` reports 0.
  - Pacing-thread wake-ups: p99 about 20 to 32 µs with no measurable runtime share (a plain C loop
    behaves the same). Under heavy competing load 1 to 4% of wakes are 1 to 9 ms late, costing a
    slot each. Real-time scheduling is refused for this user (no rtprio limit); whether it removes
    those misses is unmeasured. Needs `realtime-privileges` and the `realtime` group.
  - The NVIDIA driver takes about 1.6 ms in `vkQueueSubmit2` on every 1,024th queue submission when
    nothing is presented to a swapchain (offscreen); not seen in windowed presentation. Re-measure
    on a direct display.
- **Found in Phase 5 with the capture card** (`docs/benchmarks/phase5-capture.md`, "Known issues"):
  - Live 4K HDR 30 to 60 works. A real frame is shown about 62 ms after the card's timestamp: 26 ms
    arrival (USB delivery alone is 25.4 ms at 2160p P010, 11.3 ms at 1440p P010) + about 19 ms hold
    and generation + 12.5 ms present lead + rounding to the refresh. The present lead and the stride
    rounding are this phase's to reduce; the card's own HDMI-to-USB delay is not measurable from
    the host.
  - A 3-minute capture run ended with 4 missed slots from one present shown a refresh late, and
    `fmp verify` disagreed with the live counters on one slot.
  - Capture is not phase-locked to the 119.88 Hz output (the console sends 60.00 Hz), so most output
    frames are generated.
  - At 2160p P010 the card repeats one frame about every 33 s (its 30 fps output drifts against the
    console's 60 Hz); the app drops it, which is a one-frame hold.
- **Pacing backlog from step 4b** (`docs/benchmarks/phase4b-pipeline.md`, "Known issues carried to
  Phase 6"). The owner chose on 2026-11-18 to document these and move on: playback looks right on
  screen, and exact frame timing against the compositor is this phase's subject.
  1. **No gate-length run of the final 4b code has been verified from raw timestamps as free of
     pipeline-caused misses.** Earlier "missed 0" summaries understated: the offline verifier
     (`fmp verify <csv>`, run by `fmp play --gate`) finds 368 misses in a run the live counters
     passed. Run the gate on each output and reconcile the live counters with the verifier first.
  2. The hand-over window rests on an estimate of the compositor's latch, which moves: about 5 ms
     before the refresh on the 119.88 Hz output, 1.4 to 6.4 ms within one session at 240 Hz. Presents
     handed over too early were shown a refresh early; too late, a refresh late.
  3. The 240 Hz output is unverified: its own timing was irregular in the last session (intervals of
     3.5 to 8.5 ms on a 4.17 ms period) and KWin at times sent presentation reports without a
     first-pixel time for every fourth present.
  4. The adaptive present lead only rises and decays back to 1.5 refreshes (12.5 ms at 119.88 Hz);
     it never probes down toward the measured latch, so the delay to light carries the full lead.
  5. Showing a new image every second refresh (stride 2 on 240 Hz) costs one extra refresh of delay
     from the scheduler's stride quantisation.
  6. A 60.000 fps file on a 119.88 Hz output is not phase-locked: 96% of shown frames are generated
     and VFG runs at twice the rate. A file has no live constraint, so resampling its playback clock
     by 0.1% would lock it and halve the work. Decide.
  7. Millisecond container timestamps (Matroska, TS) are tolerated by a 3% multiplier slack in the
     scheduler rather than de-quantised in the source.
  8. A window moving between the 240 Hz and 120 Hz outputs costs about 10 misses and cannot be
     tested end to end (a Wayland client cannot move itself).
  9. `FMP_PLAY_FAULT` (fault injection) is compiled into the release binary and listed in `--help`.
- **Scheduler tuning backlog from the Phase 1 review** (accepted as non-blocking there; resolve against
  real hardware here):
  1. An animated menu (irregular 40 to 95 ms gaps) re-seeds the source interval and raises D to
     ~80 to 90 ms; it then slews down at 5 ms/s (~14 s). Jump D at hold boundaries or after a re-seed.
  2. Generation back-off after a slow-d period is long (resumes 133 s after d recovers from a 200 s
     slow period). The failed-probe test compares against 1.25× the warm-up value, not the budget.
  3. A warm-up d above the budget is never re-examined (warm-up 12 ms, real 3 ms: no generation).
  4. `fmp simulate --check` reports a failure on near-1:1 non-integer ratios (59.94 to 60, 110 to 120)
     where declining to generate is the intended behaviour.
  5. Constant wake lateness above the 5 ms clip makes every generated slot late or skipped.
  6. 110-on-120 with drops and jitter still cycles generation on and off (no misses).
  7. Wake lateness is charged against the GPU budget: 30 to 120 at d = 6 ms with 2 ms wake jitter
     drops to 2x although 6 ms fits the 8.33 ms budget.
  8. Bursts of three dropped frames re-seed each time (D bumps of ~6 ms, no misses).
  9. 1:1 passthrough with 1 ms timestamp jitter repeats ~19% of slots at D = margin.
  10. 24 to 60 and 50 to 120 on fixed refresh can show no real frames at an unlucky phase.
  11. VRR accepts source rates up to 2% over the panel maximum, so the presenter must drop rather than
      queue early presents, or feed present lag back to the scheduler.
  12. Disturbance sweep gaps: it does not assert skipped or late counts, allows 2 misses per event,
      treats "generating" as generated ≥ 0.3 × real, and has no near-1:1 non-integer ratio.

### Phase 7: HDR (pass-through, correct)

*Design doc: milestone 4.* **Scope set by the owner on 2026-11-18:** "We don't need SDR-to-HDR. Just
display what the PS5 is currently outputting, as long as HDR is correct." So this phase makes the
pass-through right and builds no conversions between SDR and HDR (those are under "Deferred").

- **Show what the console outputs:** an HDR10 signal on an HDR10 swapchain, an SDR signal on an SDR
  swapchain. No tone mapping, no SDR-in-HDR placement, no expansion.
- **HDR correct end to end:** PQ code values untouched from capture through VFG to the swapchain;
  BT.2020 primaries and limited/full range handled as the signal says (this card signals nothing,
  so the defaults and overrides from Phase 5 stand); `VK_EXT_hdr_metadata` sent, from the source
  where there is any, otherwise sensible defaults; scaling to the output done in linear light rather
  than on PQ code values; nothing in the path clips, bands or shifts colour.
- **Following the console:** the PS5 switches between SDR and HDR (menus, games). This card does not
  signal the change, so find what can be detected (a format or resolution change, picture
  statistics) and otherwise provide a manual switch that takes effect without restarting; both VFG
  instances (RGBA8 and RGB10A2) stay loaded so a switch is quick. (Not needed in the end: the
  generator's pixel format does not change on a switch, so nothing reloads.)
- **Found in Phase 2:** the Wayland surface here offers `A2B10G10R10` with `HDR10_ST2084` natively
  (also HLG and FP16 linear spaces), so `VK_hdr_layer` is not needed on this driver. The Phase 2
  presenter passes PQ through to an HDR10 swapchain, scales on PQ code values (not linear light)
  and sends no HDR metadata; this phase replaces those. Its placeholder for 10-bit on an SDR target
  stays a placeholder.
- **Automated gate:** HDR test patterns numerically correct on the offscreen target (code values
  preserved; linear-light scaling within tolerance; metadata as intended); no added d versus SDR.
- **Manual gate:** a PS5 HDR game looks right on the owner's HDR display.
- **Outcome (2026-11-18, `docs/benchmarks/phase7-hdr.md`):**
  - The capture conversion was already exact (0 of 2,073,600 pixels off a CPU reference on four
    HDR patterns) and 10 bits survive to the screen image; a VFG frame between identical HDR frames
    keeps transfer and range.
  - Fixed: the swapchain follows the content (HDR10 for PQ, SDR for SDR; before, any 10-bit picture
    got HDR10); PQ is scaled in linear light (before, a bright edge filtered on code values showed
    0.6 nits where 1,250 was right) and shown pixel for pixel when sizes match; HDR metadata is
    sent (a file's own; for the card BT.2020/D65 at 1,000 nits, settable with `--hdr-peak-nits`,
    `--hdr-max-cll`, `--hdr-max-fall`). `fmp hdr-pattern --check` proves these offscreen in 27
    numeric checks; `fmp hdr-pattern ramp|peak|bars|black` puts patterns on screen.
  - Latency unchanged: 4K HDR 54.0 ms before, 54.3 after (windowed); 46.6 ms fullscreen.
  - Following the console: the card signals nothing. `--hdr follow|on|off` and the H and A keys
    switch the output without restarting (rebuild at most 0.66 ms, no missed slots). Follow judges
    by the samples' bit depth and **has never seen a real SDR signal**: the owner needs to try it
    once with the PS5 set to SDR.
  - **The compositor shapes HDR brightness, and the metadata now sent moves its curve.** KWin anchors
    a PQ window's 203-nit reference white to the output's "SDR brightness" setting (440 nits on
    DP-3, 500 on HDMI-A-1) and then tone-maps the whole range with a curve keyed on the stated peak
    (MaxCLL, else the mastering peak, else 10,000 nits), clamping above it. With the 1,000-nit
    default now sent, the picture is brighter than before this phase (white about 297 nits against
    226 on DP-3; 1,000-nit content at 1,000 against 414) and highlights above 1,000 nits clip; the
    game on the card peaked at 2,100 to 2,400. `--hdr-peak-nits` moves the curve (10,000 gives the
    look from before this phase). Settled by running the PS5's "Adjust HDR" screens through the
    app, or by setting the peak to the console's. From KWin's source, not measured with a meter.
  - A colour switch costs no missed slot windowed, and can cost a late present in fullscreen
    variable refresh (5 late slots over 19 switches).
  - The `ffmpeg` CLI cannot capture this card's P010 and silently takes NV12, for which the card
    itself turns HDR into SDR: do not use it to judge the card's signal.
  - Not done: the items in that document's "Known issues / not done".

### Phase 8: Audio (capture card)

*Design doc: milestone 5.* **Scope set by the owner on 2026-11-18: the capture card only.** File
playback audio is left for later and Remote Play is left out entirely (both are under "Deferred").

- Capture-card audio: the card's USB audio source played to the chosen sink through a delay line
  that tracks the measured video latency, with drift correction between the two clocks and a manual
  trim (`--audio-offset-ms`). Results in `docs/benchmarks/phase8-audio.md`.
- **Output device (owner, 2026-11-18):** "It should default to the default audio device I have
  selected", and be "configurable to any device", the headset included. So: with no option the
  sound goes to the output selected in the desktop's audio settings and follows it when the owner
  changes that selection; `--audio-sink` names any other device. The sound never goes to a device
  the owner did not choose: if the device in force goes away the sound is muted until it returns,
  not moved to a fallback.
- **Gate:** measured A/V offset under 10 ms. The app can establish the delay it applies from
  timestamps; the card's own audio-to-video alignment and the TV's audio processing delay need the
  owner's ears or instruments.
- **Outcome (2026-11-18, `docs/benchmarks/phase8-audio.md`):**
  - `fmp capture` plays the card's sound by default (`--audio on|off|auto`, `--audio-source`,
    `--audio-sink`, `--audio-offset-ms`; `--list` shows sources and sinks). Project
    `FrameMotionPlus.Audio`: libpipewire bound directly with `LibraryImport`, no C shim (PipeWire
    1.6 exports the core and registry functions its headers declare inline); the one format pod is
    built by hand and checked byte for byte against PipeWire's own C builder.
  - Against the PC's timestamps the sound is on the picture: offset +0.03 ms (0.2 ms rms) over
    10 minutes at 1440p and 60 s at 4K, windowed; drift correction -36 ppm against a measured clock
    difference of -35 ppm; no underruns, overruns or resyncs; the audio callbacks allocate nothing.
    The path's least delay on the HDMI sink is 26.4 ms, below the lowest video latency (about 32 ms).
  - **The 10 ms gate is open** until the owner checks by eye and ear and sets the trim: the card's
    own audio-versus-video alignment and the TV's processing cannot be measured from the PC.
  - Not done: the items in that document's "Known issues / not done" (no reconnect after a PipeWire
    restart; fixed 256-frame cycle; the audio thread is not real-time without RTKit; older
    libpipewire leaves audio off).

### Phase 9: Latency extras and polish

*Design doc: milestone 6.*

- Direct display mode (`VK_KHR_display` + `VK_EXT_acquire_drm_display`), hitch-free quality switching,
  settings UI, two-hour soak (no drift, stable memory, zero steady-state collections).
- **Manual gate:** direct display measurably beats Wayland fullscreen end to end.
- **Automated gate carried here from Phase 4:** a 1440p60 file at 2x for 10 minutes on a direct
  display with zero missed slots of any cause (the design doc's milestone 1 gate as written).

- **Step 9a outcome (2026-11-18, `docs/benchmarks/phase9-polish.md`):**
  - **Saved settings.** `fmp capture` and `fmp play` load `$XDG_CONFIG_HOME/fmp/settings.json`
    (else `~/.config/fmp/settings.json`); `fmp settings show | path | set | unset` manage it;
    `--settings <file>` reads another and `--no-settings` ignores it. Built-in defaults, then the
    file, then the command line. Covers the capture device and mode, generator and quality, clock,
    window, audio (sink, source, offset) and HDR (mode, peak, MaxCLL, MaxFALL). A bare `fmp capture`
    uses the saved device and mode. Tests run with a config directory of their own.
  - **Soak, 10 minutes only** (the owner needed the machine): 143,532 slots, none missed, no
    collections in the measured window, audio drift correction -25.9 ppm against a measured -28.2,
    files, threads and GPU memory flat. **Process memory crept from 476.9 to 488.1 MB**, about
    35 MB an hour if it continued. The two-hour soak is not run; only it will show whether the
    creep levels off.
  - **Direct display, read only:** the driver lists both monitors through `VK_KHR_display` and has
    `VK_EXT_acquire_drm_display`; KWin offers DRM leasing but no monitors to lease, so it has to
    run from a text console. Nothing is built.
  - **Not done:** live quality switching (loading the High model mid-run stalls the whole GPU
    context; the generator can switch between two models loaded at start, at a cost in start-up
    time and GPU memory not yet measured); memory, drift and handle figures in every run's summary;
    the two-hour soak; direct display mode and its gates; a graphical settings window.
  - Test robustness: `PipeWireLoopbackTests.Clicks_through_the_delay_line…` failed twice in a
    whole-solution run (it runs beside the other assemblies with an audio thread that is not
    real-time) and passed three times alone.

- **After the owner's first long session (2026-11-19, 2 h 16 min, 4K HDR 30 to 60 on the 119.88 Hz
  output with variable refresh, streamed on Discord):**
  - **The two-hour soak is done** (`docs/benchmarks/phase9-polish.md`, 1c): process memory level
    (481 to 493 MB at 118 minutes; the creep seen in the 10-minute run was warm-up), threads and open
    files unchanged, no collection during play, nothing allocated on the hot threads, 244,488 frames
    captured with none dropped by the driver, audio error 0.01 ms mean with no underruns, clean
    teardown.
  - **A screen share's sound works:** the audio guard cut off Discord's recording stream and muted
    the sound in a loop. It now objects only to another output device; a recorder taking a copy is
    left alone (`docs/benchmarks/phase8-audio.md`, "Screen shares and recorders"). Not detected: a
    loopback that takes the sound as a recorder and replays it to another device.
  - **A long run no longer fails for running long:** the verifier's buffer held 77 minutes and every
    later slot counted as a disagreement. The 7.6% "late" in that run was real late shows (first
    pixel more than half the shortest refresh period after its time), in runs at an even cadence,
    wrongly labelled the pipeline's; the two accountings agreed on every slot both judged. File
    runs on the same output afterwards had none late in 23,014 shows, so the cause in the session
    (the capture source or the screen share) is not established: it needs two 120 s runs with
    `--csv` from the owner, with and without the share (`docs/benchmarks/phase6-pacing.md`, 3b).
  - 767 generated slots skipped in 8,146 s under variable refresh, one every 10.6 s (the known
    late-capture-frame item).

- **Live quality switching (2026-11-19, `docs/benchmarks/phase9-polish.md` sections 4 and 5):** Q in
  the window swaps VFG between the start quality and High with no load: both instances are loaded
  at start (about 90 ms more start-up and 1,540 MiB more GPU memory at 4K), the scheduler raises its
  delay and lead before an up-switch, and the generator swaps between frames. On by default
  (`--no-quality-switch`, setting `generator.qualitySwitch`), because the idle second instance
  changes neither Medium's timing nor latency. High costs about 4.8 ms more latency. No slot missed
  around 16 switches, nor in the reviewer's several hundred. Not exercised on the capture card (the
  console was off).
- **Whole-solution test runs** now take every test assembly through the machine lock one at a time
  (three assemblies used to run beside the GPU suites, which is what made timing tests fail only
  in full runs).
- **Still open in Phase 9:** direct display mode and its two gates (needs the owner at a text
  console), a graphical settings window, and the items listed as not done in the results document.

- **Capture at the console's rate (2026-11-19, `docs/benchmarks/phase6-pacing.md`, last sections):**
  the app asked the card for exactly 30 or 60 fps while a PS5 sends 59.94 Hz, so the card repeated a
  frame every 33 s (17 s at 60): a 58 ms gap between new images, a late show and two skipped
  generated slots each time, and about two thirds of the skips in the two-hour session. The 30 and
  60 presets now ask for 30000/1001 and 60000/1001 (falling back to the whole rate where a device
  has none); `--capture-fps` and `capture.fps` override, and a detector reports a periodic repeat
  with the rate to use. Measured on the TV: no repeats, none late, largest interval 34 ms, latency
  unchanged. The card does not lock to its input (its clock runs about 15 ppm fast), so a repeat
  about every 35 minutes should remain (predicted).
- **Variable refresh lost mid-run is detected and reported** (summary, stderr, `fmp verify --vrr`).
  The owner's two 120 s runs with and without a Discord share were the same, so the share is not
  what made 7.6% of the two-hour session's slots late. One loss was reproduced by accident (a file
  fullscreen on DP-3: lost 2.6 s in, never back, 2,242 of 2,278 slots late), which is that session's
  signature; what triggers it is not established. Falling back to fixed-refresh timing during a
  loss is designed, not built.

- **After the owner's 3 h 10 min session on the capture-rate build (2026-11-19):** PASS, 0.1% of
  slots late (7.6% in the first long session), audio error 0.00 ms mean with no underruns.
  - **Why variable refresh is lost: KWin grants it only to the active fullscreen window.**
    Activating any other window, on either output, loses it until the app's window is active
    again (every time, both outputs); a notification or the pointer does not. 593 of that session's
    723 late shows fell inside two such episodes. The stderr line now says so. Falling back to
    fixed-refresh timing during a loss is still designed, not built.
  - **A generated slot is shown late instead of skipped** (`--late-generated-ms`, default 4, at most
    8; 0 is the old behaviour): 1,517 generated slots were skipped in that session, most because
    the next real frame arrived a little after the scheduler's allowance. On the TV over 600 s:
    9 such skips before, none after, latency unchanged. Skips where the game itself repeated a
    frame remain and are counted separately.
  - The start-up probe retries up to three times before settling for fixed refresh; the pointer is
    hidden over the fullscreen picture.

- **VFG's shot-change detection is off for captures (2026-11-20, `docs/benchmarks/shot-change.md`):**
  the owner found that fast scenes, objects close to the camera and foliage "seem to disable the
  frame generation until they go away". VFG's automatic shot-change detection takes large, uneven
  motion for a cut and outputs copies of real frames (measured at 4K: 2, 6 and 9 of 9 generated
  frames are copies at 48, 64 and 96 px per frame of foliage-like motion; a uniform pan of 384 px is
  still interpolated); the SDK has no sensitivity setting. `--scd on|off` (setting
  `generator.shotChangeDetection`) now exists on `fmp capture`, where it defaults to off, and
  `fmp play`, where it stays on. With it off a real cut shows one morphed frame, and `NvVFX_Run`
  only enqueues. The owner's verdict after 46 minutes at 3x: "it now looks perfect", with nothing
  noticed at cuts, and no app-side cut detector wanted. This departs from the design doc's "leave
  automatic shot-change detection on".

- **Running at a display's ceiling (2026-11-21, `docs/benchmarks/phase6-pacing.md`, last section):**
  at 4x on the TV the slots are exactly the output's shortest refresh period, and a display at its
  ceiling cannot catch up: every flip comes at least one period after the last. The console's
  frames come about 15 ppm faster than the TV's top rate, so first pixels creep later (0.7 ms a
  minute with Medium, 1 ms with High) until a pause in the game resets them; without one they pass
  half a refresh, then a whole one (the owner's 4x High run: 70 ms for the whole run), and some runs
  start most of a refresh behind. Now, at a ceiling under variable refresh, after 30 shows in a row
  more than half a refresh late one generated frame is left out (never a real one, at most one every
  5 s), which brings the next frame back on time; `--ceiling-pacing off` turns it off. Measured on
  the TV: 4x Medium 58.3 ms over 15 minutes with three frames left out; 4x High 63.7 ms; 2x and 3x
  untouched. Holding each generation until the previous frame was on screen was tried first and
  made things worse. Not removed: about 2.5 ms of extra display delay with High at the ceiling.
- **KWin 6.8.3 regression (same day):** after a system update, any Vulkan FIFO surface covering the
  whole HDMI output was throttled to about 28 presents a second with no presentation timestamps
  (not the app, the driver or the kernel: returning only KWin to 6.8.2 fixed it). The machine table
  above describes Plasma 6.8.2; KWin is held there until a fixed release.
- **FFmpeg 9.1 (same update):** two local tests fail on the committed tree
  (`OffscreenVerificationTests.Nothing_is_interpolated_across_a_seek` and the FFmpeg layout check);
  file playback needs a look against the new version.

### Phase 10: Windows portability

*Design doc: milestone 7.* **Left for now by the owner on 2026-11-18** (listed under "Deferred"). The
solution still compiles for Windows in CI; nothing runs there.

## Deviation from the design doc's ordering

The design doc starts with a feasibility spike (its Phase 0). Here that spike is split: the parts that
need no VFG (interop proof, runtime jitter) are Phase 2, and the VFG measurements are Phase 3. The pure
core comes first because it needs no hardware or SDK and everything else builds on it. The go/no-go
still lands before any pipeline code depends on VFG, and the preliminary numbers above already point
to "go".

## Deferred

Things the design doc includes that are deliberately not being built yet.

- **NVIDIA Smooth Motion as a second backend** (design doc: "NVIDIA Smooth Motion as a second
  backend"). Dropped for now on 2026-11-17 to focus on VFG. When wanted: a presenter mode that sets
  `NVPRESENT_ENABLE_SMOOTH_MOTION=1` before the Vulkan instance is created, presents only unique real
  frames at their content cadence, and is flagged in capabilities as a mode with no generator. The
  layer policy in `FrameMotionPlus.Vulkan` already has an NVIDIA-present-layer setting. Open
  questions from the design doc remain open: whether it respects present timing, and how it behaves
  with HDR.
- **Step 4c: the HUD and the quality harness** (design doc: HUD with frame-time graph, d, D, dropped
  and repeated frames, detected frame rate; quality harness scoring generated frames against dropped
  originals with PSNR/SSIM). Deferred on 2026-11-18 to start the capture card first. `fmp play`
  prints these figures at the end of a run and writes per-frame telemetry to CSV; `fmp bench
  vfg-quality` already scores VFG against known intermediates on synthetic and graded content.
- **Perfecting frame pacing.** Zero missed slots over 10 minutes on every output and in every mode
  (the design doc's gate as written), and whatever `docs/benchmarks/phase6-pacing.md` lists as not
  done. Accepted for now by the owner at "a handful" per run.
  - The windowed hardware tests (`WindowedTests` in the Pipeline tests) demand zero missed slots in
    a 300-slot run and so fail when the desktop disturbs the compositor: on 2026-11-18 three of
    them failed intermittently with bursts of 3 to 4 late slots about 100 ms apart, on the Phase 6
    commit as well as on the working tree, with the GPU otherwise idle; they had passed in three
    whole-solution runs earlier the same day. They should allow for, or skip on, misses the display
    owns, as `A_short_starting_lead_adapts_and_the_misses_stop` now does.
- **Audio for file playback** (design doc: audio is the master clock for files, video scheduled
  against it). Left for later on 2026-11-18: the focus is the capture card.
- **PS Remote Play, entirely** (design doc: chiaki-ng window capture through the ScreenCast portal
  for SDR, then libchiaki with NVDEC for HDR). Left out on 2026-11-18 at the owner's instruction, so
  the libchiaki question (linking it would make the app AGPL) does not arise. The PipeWire window
  capture source that would also have made the app work on any window goes with it.
- **Conversions between SDR and HDR** (design doc's source/output matrix): HDR to SDR tone mapping
  for a non-HDR display, SDR placed inside an HDR output at a set paper white, SDR-to-HDR expansion
  (TrueHDR). Not needed, 2026-11-18: the app shows what the console outputs.
- **Windows support** (design doc milestone 7: Media Foundation capture, NT-handle CUDA/Vulkan
  bridge, Windows presenter, VFG on Windows; gate: file playback passes on Windows). Left for now,
  2026-11-18. It needs a Windows machine to verify.

