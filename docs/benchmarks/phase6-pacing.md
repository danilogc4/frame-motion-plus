# Phase 6: pacing and latency

Scope (`docs/implementation-plan.md`, Phase 6; design doc milestone 3): how late and how evenly frames reach the screen.
Time-boxed by the owner: work down a priority list (trustworthy measurement; the latency the pipeline adds for live
capture; variable refresh; fixed refresh verified on both outputs; 3x and 4x; scheduler backlog if time remains), each
item to a working, tested, measured state, and say plainly what was not done.

Machine: RTX 5090, driver 620.70.06, Linux 7.3.4 (Arch), KDE Plasma 6.8.2 / KWin Wayland; DP-3 3840x2160 at 240.016 Hz
(primary) and HDMI-A-1 3840x2160 at 119.88 Hz, both HDR, VRR "automatic". Elgato 4K X on `/dev/video0`; the PS5 was asleep
when this work started (black frames and the card's "no signal" still) and on its home screen from about 13:50 (a moving
picture of 30 fps content: the runs below say which). Real-time scheduling refused for this user (every thread SCHED_OTHER,
nice 0). `fmp` is the NativeAOT binary from `dotnet publish src/FrameMotionPlus.App -c Release -r linux-x64` (the "before"
binary is the same publish of the Phase 5 commit, `2b884e9`). A window was put on a chosen output with a KWin script loaded
for the run and unloaded after (a test aid only: `workspace.sendClientToScreen` for windows whose caption starts with
"fmp"). The owner was using the desktop; each run's load and GPU state are in its log.

## Summary

| | Before (Phase 5) | Now |
| --- | --- | --- |
| Live miss counters vs `fmp verify` | disagreed on a dropped repeat; one late show cascaded to 4 to 5 missed slots | agree (every run here); the cascade's cause fixed |
| Present lead at 119.88 Hz (windowed) | 12.5 ms (1.5 refreshes), only ever rising | measured before each run: latch about 0.7 to 2.5 ms, lead 2.7 to 4.5 ms (one run in twelve: KWin took presents a refresh earlier, lead 10.5) |
| Real frame after its capture timestamp, `4k30-hdr` → 60 | 63.3 ms (p99 67.5) | fixed refresh, windowed: 53.5 ms (p99 57.5); variable refresh, fullscreen: **46.6 ms (p99 46.9)** |
| Real frame after its capture timestamp, `1440p60-hdr` (30 fps content) → 60 | 50.9 ms (p99 61.7) | fixed refresh, windowed: 38.8 ms (p99 42.9); variable refresh, fullscreen: **31.7 ms (p99 31.9)** |
| Variable refresh | never run | **works**: KWin gives a fullscreen window VRR on both outputs; `--clock auto` (the default) uses it when fullscreen and a probe shows the display following the presents; `fmp capture` is fullscreen by default |
| Stride 2 on a phase-locked output | every real frame a whole refresh late | on the refresh τ reaches it |
| 60.000 fps file on 119.88 Hz | not locked: 96% generated, VFG at twice the rate | file clock stretched 0.1%: locked, half the slots real |
| The 10-minute gate run (1440p60 file, VFG Medium 2x, windowed), verified from the telemetry | never (4b's PASS withdrawn) | DP-3: **0 missed** of 143,533 slots; HDMI-A-1: **4 missed** of 71,688, the pipeline's, in one host stall at 38 s (the owner accepts a handful in 10 minutes for now) |
| 3x and 4x output intervals | not measured | sd 0.018 to 0.088 ms on fixed refresh (30 → 120, 40 → 120, 60 → 240), 0.130 ms under variable refresh; no miss |

**Best configuration** (the owner's commands):

```sh
fmp capture --mode 4k30-hdr       # 4K HDR 30 → 60, fullscreen (now capture's default), variable refresh
fmp capture --mode 1440p60-hdr    # 1440p HDR 60 → 120 (a 60 fps game; 30 fps content is 30 → 60)
```

Both on DP-3 (the 240 Hz output, the primary, where the window usually opens). Capture is fullscreen by default now
(`--windowed` or `--window WxH` for a window), and `--clock auto` (the default) takes variable refresh when the pre-roll's
probe shows the display following the presents; otherwise fixed refresh with the measured lead. On HDMI-A-1 variable
refresh works too, but its 119.88 Hz ceiling is below 120.00 fps: 60 → 120 stays on fixed refresh there (see "Variable
refresh"). The owner has played a game this way and finds it very good; a handful of misses in a 10-minute run is
acceptable to the owner for now.

## 1. Trustworthy measurement

### The review's disagreement and cascade (r4k.csv, 160.13 s)

Read from the review's CSV (slots 19299 to 19306, 4k30-hdr at 119.88 Hz, stride 2, present lead 12.5 ms):

| Slot | Kind | What happened |
| --- | --- | --- |
| 19300 | repeat | handed over 14.3 ms before its refresh, shown a refresh late: KWin missed refresh 578.5 (nothing was shown there) |
| 19301 | generated | queued in time; its report came **without a first-pixel time** and its refresh went to 19300: KWin **discarded** it |
| 19302 | repeat | dropped by the present thread to give the lost refresh back |
| 19303, 19304 | real, repeat | shown a refresh **early** |

Two things went wrong, both now fixed:

1. **The cascade: two give-backs for one lost refresh.** KWin gives a lost refresh back by itself: with two presents
   queued, of two ready at a later latch it shows the newer and discards the older. Every late-show episode in the Phase 4b
   and 5 telemetry with two presents queued shows it (the 4b 240 Hz runs a1/a2, 26 and 20 late shows in episodes of one
   or two, each episode followed by one present reported without a time and the FIFO back on time; the review's run:
   19301). The present thread also dropped a slot
   (19302), so the FIFO went a refresh ahead and the next two slots were early (counted as the pipeline's). Now, after a
   late show, the presents already queued are taken to be behind, but new ones are submitted for their own refresh; the
   present thread gives a refresh back itself only once a present submitted after the late show is late too (the
   compositor did not), or at once when one present is queued (lead under a refresh: KWin was seen staying behind then).
   `PresentLoop.cs` (`ExpectGiveBack`, the late-show branch of `OnShown`, `DiscardCause`).
2. **The disagreement: a dropped repeat was never checked against the screen.** The live accounting counted a given-up
   repeat as no miss ("its refresh shows the image it repeats"); here that image (19301) was discarded and the refresh showed
   19303. The collector now holds such repeats until the next timed show after their refresh and judges them by the image
   then on screen, exactly as the verifier does (`TelemetryCollector.OnScreen`, `SettleRepeats`).

Found while reproducing it on a model of the compositor (below), and fixed:

3. **A compositor's missed refresh poisoned the evidence for ten seconds.** The deadline for "in time" took the largest
   lead that was ever late: one compositor miss at the normal lead made the next one at that lead the pipeline's. A late
   show now stops counting once a later present at no longer a lead made its refresh (contradicted), and each on-time
   present is recorded as an upper bound of its real lead (the presenter call it went out in, plus a present-thread turn
   when its content was seen written after the submission) instead of adding a flat 0.25 ms to every one
   (`MissAccounting.RequiredLeadNs`, `RecordOnTime`).
4. **It also poisoned the latch estimate.** The latch's lower bound took "the refresh before was free" as proof the present
   missed that refresh's latch; after a compositor miss it read 12.9 ms (at 119.88 Hz) and made the next presents look
   unreachable, which dropped them. That bound is gone (the 4b note "1.4 to 6.4 ms within one session" was most likely
   this); the latch is measured directly now (below).
5. **The lead went up on the display's misses** (a compositor miss raised it a millisecond for ten minutes) and **a raise
   lost a refresh** (the slot about to be decided found its earlier deadline already past and was skipped). The lead goes up
   only on the pipeline's late shows or a second late show within a second (a latch that moved), and a raise applies from
   the slot after the one being decided (`PacingLoop.PumpDisplay`).
6. **A discarded present handed over in time is the display's** (`MissKind.Discarded`), and so is an early show right
   after one.
7. **A compositor miss attributed to the pipeline still poisoned the latch.** When the late show of a compositor's missed
   refresh could not be proven in time (the gate's conservative rule) it was taken as a missed latch at its lead (12.4 ms
   at the review's 12.5 ms lead); the next presents at that lead looked unreachable and one was dropped while the
   compositor gave the refresh back: the double give-back again, one slot early (seen in one of two simulated runs of the
   review's case). A miss at a lead that other presents made their refresh with (within 0.5 ms) is no longer latch
   evidence (`LatchEstimator.AddMissed`). Since: 11 of 11 simulated runs of that case without a drop or an early slot.

### The verifier next to the live counters

Every windowed run already ran the verifier; the summary now prints its total on the live counters' line (`missed N slot(s)
(the verifier, from the telemetry alone: M; agrees)`), with its own line below. `fmp verify` takes `--vrr` (and `--rate`)
for variable-refresh runs. Every run in this document agrees.

### A model of the compositor for tests

`SimulatedCompositor` (Pipeline; a test seam behind `PlaybackHooks.SimulatedDisplay`): the present thread's window logic
runs on the real clock against an offscreen target whose first-pixel reports come from a model of KWin's FIFO: a latch
before each refresh, one present per refresh in order, missed refreshes on a schedule, the give-back by discard (or not),
or variable refresh; presents complete (for the in-flight cap) when reported. Tests in `SimulatedDisplayTests`.

## 2. The latency the pipeline adds

### What the compositor does (measured with `fmp bench present`)

A new diagnostic, `fmp bench present --test latch|vrr|cadence` (App, `Bench/PresentProbe.cs`): a test pattern written by
Vulkan alone, presented at chosen times, judged from first-pixel times. It runs at most 120 s, and Escape, closing the
window, Ctrl+C or SIGTERM end it early with the results so far (a "stopped" line); test
`CliTests.A_signal_stops_the_present_probe_with_what_it_measured`.

**The latch, windowed** (a staircase: each probe into an empty queue at R − x; 8 s, 640 to 770 probes):

| Output | Made its refresh | A refresh late | The latch (before the first pixel) |
| --- | --- | --- | --- |
| HDMI-A-1, 119.88 Hz | x ≥ 2.25 ms: 98.8%; 2.0 ms: 22%; 1.75 ms: 3% | | **about 1.95 ms** (one probe late at 3.19 ms in the first second) |
| DP-3, 240.016 Hz | x ≥ 4.75 ms: 100%; 4.5 ms: 53% | x ≤ 4.25 ms: 100% | **about 4.44 ms: more than a refresh** |

Stable over each 8 s run (per-second brackets 1.93 to 1.95 ms; 4.43 to 4.44 ms). Earlier phases' "about 5 ms at 119.88 Hz"
came from the lower-bound estimate of item 4 above. The pipeline's own pre-roll sweep (below; its latest hand-over that missed)
measured 0.7 to 2.5 ms at 119.88 Hz in the runs here, and 4.2 to 4.7 ms at 240 Hz: noisier than the 8 s probe (24 to 54
probes over about a second).

**The latch is not fixed: KWin sometimes takes a present a whole refresh earlier.** In one of twelve windowed runs on
HDMI-A-1 (x3-40to120, a minute after a run that measured 1.97 ms, same window size) the sweep found a sharp threshold at
**8.47 ms**: a refresh (8.34) plus 0.13 ms; 15 probes made their refresh and 9 missed it, all within 2 µs of that. The
lead started at 10.47 ms and the run had no miss. At 240.016 Hz the latch was always of that shape: 4.44 ms is a refresh
(4.17) plus 0.27. This looks like KWin's triple buffering (it starts compositing a refresh ahead when its frames are slow;
not confirmed from KWin's side). The measured start covers it; a switch *during* a run from the short latch to the long one
would make every present late until the lead has climbed (a millisecond per step: about six steps at 119.88 Hz), and the
lead is never probed lower again, so a run that started in the long state keeps the long lead.

**Fullscreen**: no latch: **variable refresh is active** on both outputs (section 3).

### The present lead: measured before each run

Before playback the present thread probes the latch with the last pre-roll image (a staircase like the probe's, at most 1.5
s, usually 0.5 to 1 s; black frames, nothing visible) and starts the lead at the latest hand-over that missed plus 2 ms
(`PresentLoop.Vrr.cs`: `SweepStep`, `FinishSweep`; `SweepMarginNs`). The margin covers a present-thread turn (0.25 ms), the latch
moving with the compositor's load (it came 0.3 to 1 ms earlier once playback was under way than over the pre-roll's black
frames, in the first runs with a 1.25 ms margin), and a thread woken a millisecond late on a busy desktop. Then, as
before, the lead goes up a millisecond when a present the pipeline handed over late misses (and now on a second late show
within a second), held for ten minutes, and comes back down to the measured start, never below. Every change is in the
summary (`lead changes: Start 12.513 → Measured 3.476`) and the lead is part of the delay to light. When the late
present had been handed over earlier than the lead asked and still missed, a raise takes the lead to its hand-over lead
plus 2 ms; otherwise one step (found at hand-over: from a short configured start, one step at a time, the lead settled
0.06 ms past DP-3's latch, 4.5 against 4.44 ms, and kept missing there without raising, since those presents were held
by the in-flight cap; `WindowedTests.A_short_starting_lead_adapts_and_the_misses_stop` failed 7 of 12 runs. With the
jump: 1 or 2 raises to 4.7 to 5.7 ms, 8 of 8 runs clean, windowed and simulated. `LeadController.OnLateContent`; the
rule is pinned by `LeadRaiseTests` (fails without it). `SimulatedDisplayTests.A_short_starting_lead_rises_past_the_
latch_and_the_misses_stop` checks the climb end to end but does not reproduce the failure reliably: without the fix it
failed 1 run in 3 before the review and passed 4 of 4 and 8 of 8 since, so it is no proof of the fix. The windowed test
without the fix, on a quieter desktop at the end: 1 failure in 8 (9 misses of the pipeline's in its second half, 6 not
cleared by their timestamps), 7 passes, no skip. An unconditional jump broke two other simulated cases (a 2 ms jump at a
12.5 ms lead skipped vblanks), hence the condition.

| Output | Lead before | Lead now (the runs here) |
| --- | --- | --- |
| HDMI-A-1, 119.88 Hz | 12.5 ms | 2.7 to 4.5 ms (10.5 in one run of twelve: the long latch above) |
| DP-3, 240.016 Hz | 6.25 ms | about 6.4 ms (latch 4.4 + 2): unchanged in effect |

At 119.88 Hz the lead is now under a refresh: one present is queued at a time (the present for R is handed over after
refresh R − P), which is also why the catch-up rule above depends on it. Tests: `SimulatedDisplayTests.The_present_lead_
starts_from_the_latch_measured_before_playback` (2 ms latch: sweep 2.25 ms, lead 4.25 ms, no miss), `When_the_latch_moves_
earlier_the_lead_follows_and_the_misses_stop` (2 → 5 ms mid-run), and the four catch-up cases.

### The stride's extra refresh

At stride 2 on a phase-locked output every real frame was shown a refresh after τ reached it (4.11 ms p50 at 240 Hz in the
4b runs; Phase 1's simulator showed the same at 240.016 Hz: 15.9 ms real-frame latency against 12.5 at exactly 240). The
alignment keeps τ within its hysteresis of each real frame, often a few microseconds before it, and the stride buckets
(measured from the frame before, 1 µs tolerance) put that slot in the previous bucket. With the snap as the tolerance when
phase-locked, the real frame is shown at the slot τ reaches (`FrameScheduler.StrideToleranceNs`); not locked, the old
tolerance (a wider bucket would only lengthen the hold). Phase 1's guarantees are unchanged: all 675 existing Core tests and the
disturbance sweep pass; the new test `FallbackSchedulingTests.At_stride_two_a_phase_locked_source_shows_each_real_frame_at_
the_slot_its_time_reaches` fails before the change. Simulator, 60 fps at 2x on 240.016 Hz: real-frame latency 15.9 → 11.8 ms.

### Other delay between a frame and its first pixel

- **The present thread saw a new job up to a millisecond late.** Its idle wait slept through any wait under a millisecond,
  and the idle wait of a millisecond always measured just under one. Waits of 2 ms or more are on the event now; shorter
  ones sleep a tenth of a millisecond at a time (`PresentLoop.WaitForJob`). Seen as a 0.5 ms jitter of real frames under
  variable refresh; in fixed refresh it ate into the hand-over margin.
- **A live source's frames were noticed up to 2 ms late** by the pacing thread (it polls the source); now every 0.25 ms for
  a live source (`PacingLoop`, `LiveIdleNs`). Under variable refresh a real frame is decided the moment it is there.
- **The rounding to a refresh** (fixed refresh, not phase-locked: 4.16 ms p50 at 119.88 Hz) and **the present lead** are
  what fixed refresh costs; variable refresh removes both.
- **D's terms** (hold, generation lead = p99 d + wake + 0.5 ms margin, arrival lag p99) are as designed; the arrival lag
  (USB delivery, 25.9 ms at 2160p P010, 11.6 ms at 1440p P010) is the card's.

### Latency, before and after (the PS5's home screen, 30 fps content; 30 s runs)

A real frame shown after its capture timestamp (mean; p99), and its parts (p50). Model: ΔL = d + (M−1)/M·T.

| Mode, clock, output | Real frame after capture | = arrival | + generation (hold + lead) | + present lead | + rounding / display delay | Model ΔL |
| --- | --- | --- | --- | --- | --- | --- |
| 4k30-hdr → 60, before: fixed, HDMI-A-1 windowed | 63.3 (67.5) | 26.9 | 18.7 | 13.5 | 3.8 | 18.3 |
| 4k30-hdr → 60, now: fixed, HDMI-A-1 windowed | 53.5 (57.5) | 27.0 | 18.8 | 3.5 | 4.2 | 18.3 |
| 4k30-hdr → 60, now: **variable**, DP-3 fullscreen | **46.6 (46.9)** | 27.0 | 18.8 | 0 | 0.8 (hand-over to first pixel) | 18.2 |
| 1440p60-hdr (30 fps) → 60, before: fixed, HDMI-A-1 | 50.9 (61.7) | 12.3 | 18.8 | 12.5 | 4.4 | 18.2 |
| 1440p60-hdr (30 fps) → 60, now: fixed, HDMI-A-1 | 38.8 (42.9) | 12.2 | 18.6 | 3.7 | 4.2 | 18.0 |
| 1440p60-hdr (30 fps) → 60, now: **variable**, DP-3 fullscreen | **31.7 (31.9)** | 12.2 | 18.5 | 0 | 0.8 | 17.9 |

The generation's part is the model plus 0.5 to 0.6 ms (the scheduler's wake allowance and margin). Spread (p1 to p99 of
real frames): fixed refresh about 8 ms either way (the rounding to a refresh, not phase-locked: 49.3 to 57.5 ms, 34.6 to
42.9 ms); variable refresh 0.4 to 0.5 ms (46.36 to 46.87 ms, 31.52 to 31.87 ms). Every run: 0 missed by
the live counters and by `fmp verify` (with `--vrr --rate 240.016` for the variable-refresh ones), GC 0, 0 B on every thread.
The first variable-refresh capture runs (before the fixes in section 3) had problems that are described there.

A 60 fps game (true 60 → 120) was not available (the console showed its home screen). In its place, the kernel's test
capture device (vivid, 1280x720 NV12 at a nominal 60 fps; it delivers 58.8) at 2x: a real frame after its capture
timestamp 31.7 ms before (fixed refresh, HDMI-A-1, lead 12.5), 23.8 ms now on the same output (lead 4.2), and **11.5 ms
under variable refresh** on DP-3 (p99 11.6; model ΔL 9.7 + the display's 0.7). On fixed refresh a source not locked to
the refreshes has D tend to T + d (the design doc's non-integer case: 19.5 ms here against the model's 9.8, 143 of 3,358
slots real); under variable refresh it is T/2 + d and every other slot is real.

## 3. Variable refresh

### Established by measurement

`fmp bench present --test vrr --fullscreen`: presents at 1.37 and 1.71 refreshes in turn, into an empty queue (6 s each):

| Output, mode | Shows as far apart as their presents | On the refresh grid | Hand-over to first pixel p1 / p50 / p99 |
| --- | --- | --- | --- |
| HDMI-A-1 fullscreen, FIFO | 455 of 467 | 0 | 0.80 / 0.87 / 1.18 ms |
| DP-3 fullscreen, FIFO | 920 of 935 | 3 | 0.62 / 0.72 / 0.92 ms |
| DP-3 fullscreen, FIFO_LATEST_READY | 769 of 779 | 1 | 0.62 / 0.72 / 0.92 ms |
| DP-3 fullscreen, MAILBOX | 767 of 779 | 3 | 0.61 / 0.73 / 0.94 ms |
| DP-3 fullscreen, IMMEDIATE | 766 of 779 | 2 | 0.63 / 0.77 / 0.94 ms |
| DP-3 windowed, FIFO | 1 of 779 | 777 | 4.3 / 6.5 / 8.5 ms (latch + up to a refresh) |

So KWin's "automatic" VRR is on for a fullscreen window and off for a window, on both outputs. Every present mode gives the
same timing under VRR; **FIFO** is the one to use (no tearing, nothing discarded; IMMEDIATE may tear and buys nothing here,
MAILBOX and FIFO_LATEST_READY would discard a present a slot was meant to show). The swapchain takes a present mode now
(`SwapchainTargetOptions.PresentMode`); the pipeline keeps FIFO. Seen in every run, probe and playback alike: once
every 128 presents (exactly: every 1.067 s at 120 presents a second) one is shown 1.5 to 1.9 ms later than usual (2.3 to
2.6 ms after its hand-over); the driver's or the compositor's, not investigated.

### Implemented end to end

- `fmp capture` is fullscreen by default (after the measurement runs, at the owner's request that the plain commands give
  the best configuration): `--windowed` or a `--window WxH` size keeps a window; `fmp play` stays windowed unless
  `--fullscreen`. `CaptureOptions.Parse`; tested in `NegotiationTests.Capture_is_fullscreen_unless_a_window_is_asked_for`.
- `--clock auto|fixed|vrr` on `fmp play` and `fmp capture` (default auto). The window goes fullscreen during the pre-roll
  (it used to after playback started); then, for `auto` when fullscreen or for `vrr`, the present thread runs the same
  probe on the pre-roll's last image (24 presents, 0.6 s at most) and variable refresh is used when at least 80% of
  consecutive shows followed their presents (`PresentLoop.Vrr.cs`: `VrrProbeStep`; `VrrProbeResult`). Otherwise fixed refresh, with
  the latch sweep.
- The scheduler's VRR clock (Phase 1's) with the output's rate as its ceiling: V = ts + D + kT/M, slots at least the
  shortest refresh period apart.
- The present thread in VRR (`PresentLoop.Vrr.cs`: `VrrTurn`, `OnShownVrr`): each slot is handed over at its V (the job carries it: `DueNs`), no
  grid, no fillers, repeats not presented (the display holds the last image), at most two in flight as before. A slot whose
  first pixel is more than half the shortest refresh period from its target (V plus the expected delay to the first pixel,
  the median of the last 256, moved 0.1 ms at a time) is missed: late is the display's when it was handed over on time,
  else the pipeline's; one that cannot be shown within half a period of its time is given up. A generated slot the
  scheduler skips because the next real frame came too late is counted apart (`VrrSkippedSlots`), there being no refresh
  that went without a slot.
- Telemetry and the verifier have a VRR mode (no refresh grid: no refreshes without a slot or a present).
- Tests: `SimulatedDisplayTests.Variable_refresh_shows_each_slot_at_its_own_time` (60 fps at 2x on a simulated 240 Hz VRR
  display: intervals 8.332 ms, sd 0.029 ms; real frames D + 0.98 ms after their content time; no miss; the verifier agrees).

### Results

| Run | Slots | Missed (live / verifier) | Output intervals (excluding skipped slots) | Real frame after content |
| --- | --- | --- | --- | --- |
| 4k30-hdr → 60, DP-3 fullscreen, 30 s | 1,680 | 0 / 0 | 16.67 ms, sd 0.206 ms (4 generated slots skipped: the card delivered late) | 46.6 ms after capture |
| 1440p60-hdr (30 fps) → 60, DP-3 fullscreen, 30 s | 1,680 | 0 / 0 | 16.67 ms, sd 0.159 ms (2 skipped) | 31.7 ms after capture |
| 60 fps file → 120, DP-3 fullscreen, 30 s | 3,361 | 0 / 0 | 8.333 ms, sd 0.197 ms (content steps exact) | 10.9 ms after content (p99 12.4; model 9.7); the same file on fixed refresh at 240 Hz (the DP-3 gate): 19.2 |
| 30 fps file → 120 (4x), DP-3 fullscreen, 30 s | 3,361 | 0 / 0 | 8.333 ms, sd 0.130 ms | 27.5 ms (p99 27.7; model 26.3) |
| vivid at 60 fps (58.8 measured) → 120, DP-3 fullscreen, 30 s | 3,360 | 0 / 0 | 8.334 ms, sd 0.418 ms (the source's own timestamp jitter: content steps sd 0.395) | 11.5 ms after capture (p99 11.6); on fixed refresh at 119.88 Hz 23.8 now, 31.7 before |

**HDMI-A-1's ceiling.** Its fastest refresh is 119.88 Hz: 60 fps at 2x (120.00 fps) is 0.1% too fast; under variable
refresh the slots would queue behind the refreshes and one would have to go every few seconds. So `--clock auto` stays on
fixed refresh when the source's rate times the multiplier exceeds the output's (the summary says so; `--clock vrr` forces
it): a 1440p60 → 120 capture on HDMI-A-1 runs fixed (unlocked: almost every slot generated, D toward T + d). Use DP-3.
Checked on the final binary: a 60 fps file fullscreen on HDMI-A-1 says "not variable refresh: the source at its multiplier
needs 120 fps, more than this output's 119.88 Hz" and plays on fixed refresh, no miss; unit-tested (`ResampleTests`).

**What went wrong first** (the first variable-refresh capture run, 4k30-hdr, fixed since): (a) the expected delay to the first
pixel was a tail estimate and jumped to its 4 ms bound after one compositor stall, so every on-time slot was then judged
early (253 "early"): it is a slow median now; (b) the present thread picked up each real frame's job up to a millisecond
late (the wait above), a 0.5 ms jitter between real and generated slots; (c) the pacing thread noticed the card's frames up
to 2 ms late. Also seen there and **not fixed**: D started at 55 to 63 ms and took about 10 s to come down to 46.7 (a hold re-seeded
the source interval: Phase 1 backlog item 1, below); the later runs did not show it.

## 4. Fixed refresh on both outputs

### Gate-length runs (`fmp play c1440p60.mp4 --gate`: VFG Medium 2x, looped, 10 minutes, windowed 1280x720)

The 60 s 1440p60 test clip (the 4b gate's), each run on one output (KWin script), the published binary of this phase as
it stood at 14:00 (later changes: the summary's wording and lead-change log, the pre-roll's grid resend, the VRR ceiling
rule; short runs of the final binary are under "Final binary"). Desktop in use: KWin, Discord and NZXT CAM as the other
GPU clients; load average 0.5 to 2.

| Output | Slots (real / generated / repeated) | Missed: live (whose) / `fmp verify` | Output intervals | Lead | Real frame after content | GC, allocation | Result |
| --- | --- | --- | --- | --- | --- | --- | --- |
| DP-3, 240.016 Hz (60.000 fps stretched −67 ppm: locked, stride 2) | 143,533 (35,882 / 35,873 / 71,778) | 0 / 0, agree | 4.166 ms, sd 0.078 ms; 0 off the grid | 6.46 ms (latch 4.42 to 4.46 + 2; no change) | 19.2 ms (p99 21.2); the stride's rounding 0.02 ms | 0 / 0 / 0, 0 B | **0 missed** (the run's own verdict: PASS) |
| HDMI-A-1, 119.88 Hz (60.000 fps stretched 0.1%: locked, stride 1) | 71,688 (35,113 / 36,561 / 14) | **4** (the pipeline's, one event at 38 s) / 4, agree | 8.342 ms, sd 0.050 ms; 3 more than a quarter refresh off | 4.48 → 5.48 ms (one raise) | 17.9 ms (p99 19.8) | 0 / 0 / 0, 0 B | **4 missed** (the run's own verdict: FAIL). The owner: a handful of misses in a 10-minute run is acceptable for now |

DP-3: every one of 144,019 shows timed by its first pixel, no miss of any cause, the verifier agreeing (the first gate-length
run verified from raw timestamps: 4b's PASS was withdrawn). Real frames reach the screen on the refresh τ reaches (0.02 ms
of rounding p50, against 4.11 ms in the 4b runs: the stride fix). One collection (gen 2) happened in the start-up, outside
the measured window.

HDMI-A-1, the cause of the 4: a host stall at 37.95 s. Generated slot 4553's generation started 6.7 ms after its decision
(0.1 ms usually), past its refresh; it and the real frame queued behind it were shown a refresh late, and in the next 90 ms
the pacing thread woke 5 to 7 ms late twice (two skipped vblanks); the compositor showed two on-time slots 2 ms off the grid
in the same moment. Every thread runs SCHED_OTHER (real-time priority refused for this user). The reduced lead leaves about
3 ms between a generated slot's decision and the compositor's latch for such a stall (12.5 ms left about 11), so the old
lead would most likely have absorbed it; `--present-lead-ms 12.5` gives a file that margin back (a file is not
latency-bound). Not chased further, at the owner's word. Live counters and verifier agree on all 71,687 slots. Otherwise D
rose for a few seconds at each loop of the file and drifted from 14.8 to 10.7 ms over the run (the alignment following a
few ppm between the display's clock and the system's, it seems).

### 60.000 fps files on 119.88 Hz: the file's clock is stretched

A file within 0.5% of a whole number of refreshes per frame, played on fixed refresh, plays that much slower or faster so it
phase-locks (`PlaybackSession.ResampleFactor`, `PacingLoop.MapContent`; `PlaybackOptions.ResampleToDisplay`, on by default):
60.000 fps on 119.88 Hz is stretched by 0.1% (16.683 ms per frame), real frames land on every other refresh, the generator
runs on the other half. On DP-3 a 60 fps file is stretched by −67 ppm (240.016 Hz), which removes the slow phase drift the
alignment otherwise follows. Virtual-clock test `ResampleTests`: 228 real and 229 generated of 457 slots, phase-locked;
without it 29 real and 428 generated; the factor itself is unit-tested (24, 25, 30, 59.94, 60 fps against 119.88, 120 and
240.016 Hz). A file has no live constraint; audio (Phase 8) will have to follow the same stretch.

**Live capture on fixed refresh** cannot be resampled: a 60.00 Hz console on the 119.88 Hz output stays unlocked (almost
every slot generated, D toward T + d). The choices: variable refresh (the answer: it locks by construction, and costs the
least); or an output at exactly 120.00 Hz (DP-3 offers 3840x2160@120.00); or DP-3 at 240.016 Hz, where 60 Hz is within
67 ppm and phase-locks at stride 2. Documented in the summary's recommendation; nothing else changed.

## 5. 3x and 4x

Test clips at 30, 40 and 60 fps (ffmpeg's moving test picture, 1440p, 60 s), VFG Medium, windowed 960x540 on fixed
refresh, 60 s each, looped; then variable refresh. Output intervals from first-pixel times (after the first 10%); the
content step is the difference in content time between consecutive new images shown (even motion: constant).

| Run | Slots (real / generated) | Missed: live / verifier | Output interval, sd | Content step, sd | Steps off by > 0.5 ms | Real frame after content (model ΔL) |
| --- | --- | --- | --- | --- | --- | --- |
| 30 → 120, 4x, HDMI-A-1 119.88 Hz (stretched 0.1%) | 6,954 (1,728 / 5,226) | 0 / 0 | 8.342 ms, **0.018 ms** | 8.341 ms, 0.016 ms | 0 (after the first second) | 35.5 ms (26.4) |
| 40 → 120, 3x, HDMI-A-1 (stretched 0.1%; the long latch: lead 10.5 ms) | 6,954 (2,308 / 4,646) | 0 / 0 | 8.342 ms, **0.018 ms** | 8.341 ms, 0.015 ms | 0 (after the first second) | 33.9 ms (18.1) |
| 60 → 240, 4x, DP-3 240.016 Hz (stretched −67 ppm) | 13,922 (3,427 / 10,492) | 0 / 0 | 4.166 ms, **0.088 ms** | 4.166 ms, 0.015 ms | 0 | 22.5 ms (14.0) |
| 30 → 120, 4x, DP-3 **fullscreen, variable refresh**, 30 s | 3,361 (840 / 2,521) | 0 / 0 | 8.333 ms, **0.130 ms** | 8.333 ms, 0.000 ms | 22 intervals: one every 1.07 s (every 128th present, +1.5 ms; content exact) | 27.5 ms (26.3) |

Every interval sd is well under 0.5 ms; every fixed-refresh run phase-locked, a real frame exactly every M slots, no
miss. Under variable refresh the content steps are exact (each slot shown at its own time) and the only irregularity is
the display's: every 128th present (exactly: 1.07 s at 120 presents a second) is shown about 1.5 ms late, the next one on
time (section 3). The bridge
keeps up: VFG's d is 1.3 ms p50, 1.4 to 1.5 ms p99 at 3x and 4x as at 2x (one generation at a time on the generator
thread: at 240 Hz three of every four refreshes generate, 4.2 ms apart, a third of the budget); the scheduler's generation
lead (p99 d + wake + 0.5 ms margin) is 1.9 ms. The latency over the model is the present lead plus D's alignment (D ≡
−phase mod P, up to a refresh: 4.2 to 4.5 ms here) and the hold's margin; a file is not latency-bound.

**Periodic judder, 59.94 fps on "120 Hz", 10 minutes.** The HDMI-A-1 gate run above is that case: its 60.000 fps file,
stretched by 0.1%, is a 59.94 fps source on 119.88 Hz. Over its ten minutes the content step between consecutive new
images was off its median (8.342 ms) by more than 0.5 ms only at: each loop of the 60 s file (a seam in the content, 9
times), the stall at 38 s, and one scheduled repeat at 129.5 s (a generation decided too late, shown as the previous image
held for two refreshes; no miss). No periodic pattern. On DP-3 (the 60 → 240 run and the DP-3 gate) likewise none. A
59.94 fps source that is *not* locked (59.94 on a 120.000 Hz output, or a live 59.94 source on 240.016 Hz) was not run:
neither output here runs at 120.000 Hz without a mode change on the owner's desktop, and a file is always stretched to
lock (`PlaybackOptions.ResampleToDisplay` has no command-line switch).

## Final binary

Short runs at 14:55 of the NativeAOT publish just before capture became fullscreen by default (so the HDMI-A-1 capture
row is windowed), each with `fmp verify`; the owner's two commands on the final publish are below the table.

| Run | Clock | Missed: live / verifier | Lead | Real frame after its time | `fmp verify` |
| --- | --- | --- | --- | --- | --- |
| `fmp play c1440p60.mp4 --loop --duration-s 20`, HDMI-A-1 windowed | fixed (stretched 0.1%, locked) | 0 / 0 | 2.73 ms (sweep: latest miss 0.73 ms) | 14.3 ms after content (p99 14.9) | 0 missed; **3 unjudged** (reports without a first-pixel time, their refreshes not taken by another), so FAIL by its rule |
| the same, `--fullscreen`, 10 s | **fixed: the ceiling rule** ("needs 120 fps, more than this output's 119.88 Hz") | 0 / 0 | 12.5 ms (sweep not bracketed under KWin's VRR) | 29.3 ms | PASS |
| `fmp capture --mode 4k30-hdr --duration-s 20 --fullscreen`, DP-3 | variable | 0 / 0 | (0.77 ms to first pixel) | **46.6 ms after capture (p99 46.9)** | PASS (`--vrr --rate 240.016`) |
| `fmp capture --mode 4k30-hdr --duration-s 20`, HDMI-A-1 (windowed: the default then) | fixed | 0 / 0 | 3.47 ms | 53.1 ms after capture (p99 57.6) | PASS |

The capture numbers repeat the measurement runs' (46.6 and 53.5). The three untimed reports in the first run are the first
seen at 119.88 Hz in this session (4b saw them at 240 Hz); it ran at the shortest lead measured here (the sweep found the
latch at most 0.73 ms, against 1.2 to 2.5 in the other runs): whether a present handed over that close to KWin's latch
is what loses its timing report was not established.

**The owner's commands on the final publish** (no options but the duration; DP-3, the PS5's home screen):

| Command (+ `--duration-s 30 --csv`) | Clock chosen | Slots | Missed: live / verifier | Intervals (sd, source gaps excluded) | Real frame after capture | `fmp verify --vrr --rate 240.016` |
| --- | --- | --- | --- | --- | --- | --- |
| `fmp capture --mode 4k30-hdr` | variable (fullscreen by default; probe 61 of 63 pairs following) | 1,678 (840 real) | 0 / 0 | 16.69 ms, 0.171 ms (2 gaps) | **46.6 ms (p99 46.9)** = arrival 27.0 + generation 18.8 + display 0.8 | PASS |
| `fmp capture --mode 1440p60-hdr` | variable | 1,679 (840 real; 30 fps content) | 0 / 0 | 16.70 ms, 0.201 ms (3 gaps) | **31.7 ms (p99 32.0)** = arrival 12.2 + generation 18.6 + display 0.9 | PASS |

GC 0, 0 B on every thread, in both. The source gaps are frames the console did not deliver (an interval of two source
frames), followed by the pipeline as they come.

## Tests

New: `CatchUpTests` (3, pure: the review's cascade replayed record by record through the collector, which now agrees with
the verifier on it; a repeat given up after a real frame shown late; a discarded present the display's),
`SimulatedDisplayTests` (7, `Gpu`: the review's case, 119.88 Hz at a 12.5 ms lead, where the compositor gives a missed
refresh back and the present thread must not as well; the same at 240 Hz, where the in-flight cap keeps the compositor
from giving back and the present thread does; a compositor that stays behind; the lead from the latch sweep, with the
lead-change log; the latch moving; variable refresh; nothing missed), `LatchEstimatorTests` (pure: a miss at a lead that
made its refresh is no latch evidence), `CommandLineTests.An_unknown_clock_or_probe_choice_is_a_usage_error` (5 cases),
`ResampleTests` (2 `Gpu` on the
virtual clock; 2 pure theories: which file rates are stretched and by how much, and when `--clock auto` may choose
variable refresh against the output's ceiling),
`MissAccountingTests.A_late_show_that_later_presents_at_the_same_lead_contradict_no_longer_raises_the_deadline`,
`WindowedTests.The_swapchain_takes_the_present_mode_asked_for_and_refuses_one_the_surface_does_not_offer` (Vulkan, a small
window), Core
`FallbackSchedulingTests.At_stride_two_a_phase_locked_source_shows_each_real_frame_at_the_slot_its_time_reaches`.
Shown to fail before the change: the cascade replay (the old collector called the dropped repeat fine: 1 disagreement);
the 240 Hz give-back simulation against the Phase 5 present thread (14 misses for 3 compositor misses, early and dropped
ones the pipeline's, against 6 the display's, at the time the simulated compositor still gave refreshes back there); the
latch test against the old estimator (by construction: it took the miss); the stride test against the old scheduler
(τ − ts 4.1 ms). The simulated tests assert what must hold (the compositor missed what it was told to, live counters and
verifier agree, nothing early, no double give-back, misses bounded near the events and away from them); whose each miss
is, they log: the conservative rule calls a compositor miss the pipeline's whenever the late slot's hand-over is not
provably as early as one that made its refresh, which depends on a millisecond's scheduling (a generated slot's hand-over
is when its content was seen written). No existing test was
changed (Phase 1's scheduler tests and the disturbance sweep pass as they were; the accounting's existing tests pass with
the new rule's default). The simulated tests run the present thread on the real clock: their assertions allow a few
misses away from the scheduled events (a thread woken late on a busy desktop) and say so.

**Runs.** A first whole-solution run at 14:30 (before the last test fixes, fix 7 and the capture default) had 1 failure:
the 240 Hz give-back simulation, on an assertion that counted give-backs with counters that miss the one the in-flight cap
makes (replaced), and its test helper allocated 56 B on the present thread (a list growing in the simulated compositor's
skip schedule; sized up front now). The new and changed tests were then run repeatedly while fixing what they found (the
stay-behind test's attribution assertion, the 240 Hz bound, the review's case at 12.5 ms that showed fix 7) and twice
after the last pipeline change: all passed both times (Core 11, Pipeline 36, Vulkan 1); Pipeline.Tests in full after fix
7: 137 of 137. The final whole-solution run, on the tree as handed over (`dotnet test FrameMotionPlus.slnx -c Release`,
`FMP_VFX_SDK` set), after the safety review's fixes: Core 681, Platform 100 (+1 skipped), Vulkan 98, Sources.V4l2 115,
Gpu 121, Cuda 144, Vfx 92 (+1 skipped), Sources.File 154, Pipeline 142: **1,647 passed, 2 skipped, 0 failed**. After the
lead-raise fix, Pipeline three times in a row, 139 of 139 each (the windowed short-lead test passing, not skipping). Solution build 0 warnings; NativeAOT publish clean (no trim or AOT warnings); `dotnet build
src/FrameMotionPlus.App -c Release -r win-x64` 0 warnings.

## Changes outside the listed scope

| Where | What and why |
| --- | --- |
| `src/FrameMotionPlus.App/Bench/PresentProbe.cs` (new), `src/FrameMotionPlus.App/Cli/BenchCommand.cs:18` and `:27`, its help in `Cli/CommandDispatcher.cs:75-77` and `:183-193` | `fmp bench present`: the compositor measurements of this phase (latch, VRR, cadence) are reproducible with it. A diagnostic command, not the pipeline. |
| `src/FrameMotionPlus.Vulkan/Presentation/OffscreenTarget.cs` (`PresentTimeSource` public, `PresentSubmitted`, `CompletesOnReport`) | The simulated compositor's seam (Presentation, in scope). |
| `src/FrameMotionPlus.Vulkan/Presentation/SwapchainTarget.cs` (`SwapchainPresentMode`, `SwapchainTargetOptions.PresentMode`, `PresentModeName`) | For the probe's present-mode comparison (Presentation, in scope); the pipeline keeps FIFO, the default. |

No change to Platform, Cuda, CudaVulkan, Vfx, Sources.File or Sources.V4l2 (a test added in `Sources.V4l2.Tests` for the
capture default). Core: only `Scheduling/FrameScheduler.cs` (the stride tolerance). `docs/implementation-plan.md` shows a
change made at 14:58 by someone else (the owner's tolerance and a "Perfecting frame pacing" deferral), not by this work.

## What `docs/implementation-plan.md` and the design doc should change (not edited)

- The latency model should carry the presentation term per clock mode: fixed refresh adds the present lead (now the
  measured latch plus 2 ms: 2.7 to 4.5 ms at 119.88 Hz in most runs, 6.4 at 240) plus the rounding to a refresh (up to a refresh, half
  on average when not phase-locked); variable refresh adds the display's hand-over-to-first-pixel delay (0.7 to 0.9 ms here).
- "VRR (preferred)": confirmed; on KWin it needs a fullscreen window (VRR "automatic"), and the output's ceiling matters
  (119.88 Hz cannot take 120.00 fps).
- The "Found in Phase 2" note that present timing cannot target times stands; the latch is measurable from feedback alone.
- KWin's FIFO gives a lost refresh back by itself when two presents are queued (it discards one): a pacing rule.
- The compositor's latch is not a constant of the output: at 119.88 Hz it was 0.7 to 2.5 ms in eleven runs and a refresh plus
  0.13 ms in one; at 240 Hz always a refresh plus 0.27 ms. Measure it per run (done) and expect it to move.
- Phase 1 backlog item 11 (VRR accepts up to 2% over the panel's maximum) is answered for the pipeline by the auto rule
  here (stay on fixed refresh when the rate does not fit); the scheduler itself still accepts it.
- Phase 6's gate: 30 → 120, 40 → 120 and 60 → 240 interval sd under 0.5 ms are met on fixed refresh (0.018 to 0.088
  ms); the 10-minute 59.94 judder check was met by a locked 59.94-equivalent source only (section 5).
- The 4b gate's rule: with a lead near the latch, a host stall of more than about 3 ms between a slot's decision and the
  compositor's latch costs slots that count as the pipeline's (SCHED_OTHER threads on a desktop in use). Either the gate
  accepts that a file may run with a longer lead (`--present-lead-ms`), or it needs real-time scheduling (refused here).

## Known issues / not done

Priority by priority (the brief's order), then the backlogs.

1. **Measurement.** Done. Left: a compositor miss of a present handed over at the usual lead is the display's only when
   the timestamps prove it in time. After one, its late show stays evidence that this lead can miss until a later present
   provably made its refresh with less lead (each one's measured uncertainty included), which presents at one lead seldom
   do; so a second compositor miss within 10 s of a first is often counted as the pipeline's. Seen in the simulated
   stay-behind test (in one of its runs: the second episode, 1.5 s after the first, the pipeline's); not in the hardware
   runs here (no compositor misses close together). Conservative, as the gate's rule asks, but a gate run can be failed by
   two compositor misses. When the compositor does *not* give a lost refresh back with two presents queued (the
   simulation does that in some runs, depending on whether the next present stands beside the late one at a latch), the
   present thread's own catch-up waits for a present submitted after the late show's report to be late too: 3 to 4
   refreshes at 240 Hz, so one lost refresh costs 4 to 7 slots instead of 2 to 3. A generated slot's hand-over is taken
   as when its content was seen written (a present-thread turn coarse), so a compositor miss that lands on one is often
   not provable and counts as the pipeline's (the review's case simulated: the first episode the pipeline's in 2 of 13
   runs). Fix 7 is argued from the one failing run's records and checked by 11 clean runs since; the failing condition
   (the first late show called the pipeline's) recurred once in those, without the double give-back. Reports without a
   first-pixel time: 3 in the final binary's 20 s windowed run at 119.88 Hz (unjudged, so `fmp verify` fails it); none in
   the other runs here.
2. **Latency.** The lead is measured once, before playback, on black frames; it is then raised by evidence, never probed
   lower during playback (that would cost visible misses). The 2 ms margin is a judgement from a handful of runs. The
   stride's extra refresh is gone only where phase-locked (where it occurred). A 60 fps game (true 60 → 120) was not
   available: the 1440p60-hdr rows are 30 fps content (vivid stood in for a 60 fps source). The shorter lead leaves less
   room for a host stall (the HDMI-A-1 gate, section 4). D's start-up rise on a capture (item 6) cost latency for the first
   seconds of one run.
3. **Variable refresh.** Works fullscreen on both outputs; windowed is fixed refresh (KWin's choice). Left: generated slots
   skipped when the card delivers a frame later than D's arrival-lag p99 (2 to 4 in 1,680 slots: the image before stays
   twice as long; a higher percentile or a margin for live sources would trade latency for it); about every 128th present
   is shown 1.5 to 1.9 ms late (driver or compositor); `fmp verify` needs `--vrr` (the CSV does not say which clock it was);
   the VRR misses judged with half the shortest refresh period are generous for a 16.7 ms slot; HDMI-A-1's 119.88 Hz ceiling
   (above); no test drives the pre-roll's VRR probe itself (the simulated display enters VRR directly). Fullscreen on the
   fixed clock (the ceiling rule, or `--clock fixed`) KWin's VRR is still on: the latch sweep cannot bracket (every probe
   made its refresh) and the lead stays at 1.5 refreshes (final binary, HDMI-A-1 fullscreen 60 → 120: no miss, real frames
   29.3 ms after their content time where the windowed run had 14.3); the VRR probe's delay would be the right lead there.
3b. **Variable refresh on HDMI-A-1: the owner's two-hour run "missed" 7.6% of its slots (2026-11-19).** 8,146 s measured,
   487,483 slots, 37,201 missed (36,930 "late present", 270 "display late"), verdict FAIL, while the intervals were even
   (mean 16.711 ms, p99 17.140) and the owner saw nothing wrong. What is **established** (from the code and the run's own
   numbers):
   - Both accountings use one definition under variable refresh: a slot is late when its first pixel comes more than half
     the shortest refresh period (8.342 / 2 = 4.17 ms) after its time (`PresentLoop.OnShownVrr`, `TelemetryVerifier`).
     They **agreed on every one** of the 277,908 slots the verifier's buffer held (the first 77 minutes; 3,929 missed
     there): the printed disagreement, 209,575, is exactly the number of slots beyond its buffer. The FAIL was that
     bookkeeping; 3,929 against 36,930 compares 77 minutes with 136. Fixed: the comparison covers the slots the verifier
     judged, the rest is said to be the live counters' alone, and a run without a duration reserves 4 hours of rows.
   - The label was wrong: a late show counted as the pipeline's whenever the hand-over came later than the expected
     display delay (the lead, 0.85 ms) before its time. Hand-overs ran 0.43 ms (p1) to 0.56 ms (p50) before their time,
     0.3 ms behind the lead, so virtually every late show was "the pipeline's" although handed over in time. Now (after
     review) a late show is the pipeline's when its hand-over was late by more than the display's excess over its
     expected delay, or by more than a quarter of the tolerance (1.04 ms at 119.88 Hz); otherwise the display's. The
     owner's run (0.3 to 0.42 ms late against an excess of 3.75 ms or more) goes to the display; simulated at 119.88 Hz
     with the display's delay varying 0.4–3.1 ms, every hand-over held 2 ms late: 19 of 330 missed, all the pipeline's;
     3.5 ms late: 217, all the pipeline's; hand-overs in time and one show in ten 6 ms slow: 40, all the display's
     (`SimulatedDisplayTests.A_late_show_under_variable_refresh_is_blamed_on_a_late_hand_over_or_a_slow_display`). The
     floor exists because a show is late only when the two add up to more than the tolerance: a hand-over 2 ms late is
     late only with a display over 2.17 ms slow, the larger share, yet a pipeline 2 ms behind must fail the gate. Why the hand-over trailed the lead by 0.3 ms in that capture run is not
     investigated (in the file runs on the TV below it matched the lead: p50 0.82–0.89 ms against 0.85–1.01).
   - These presents were handed over at least 0.43 ms before their time in 99% of cases and shown more than 4.17 ms after
     it: 4.6 ms or more from hand-over to first pixel, beyond the 3.1 ms the pre-roll probe saw at most.
   - With a p99 interval of 17.14 ms (0.47 ms above the mean), at most about 1% of intervals can be uneven, while an
     isolated late slot makes two uneven intervals. So nearly all the late slots came **in runs of consecutive late slots
     at an even cadence**: a steady extra latency of 4–8 ms while a run lasted, not a judder. Only each run's first and
     last interval is uneven. The event list agrees (9.501, 9.568, 9.584, 9.601 … s: consecutive slots).

   **Measured on HDMI-A-1 afterwards (2026-11-19, night, the PS5 off):** `fmp play` of a 4K HDR10 clip at the console's
   29.97 fps (ffmpeg's moving test picture, dimmed to a 65-nit peak), looped, VFG 2x, fullscreen, the default clock
   (the probe chose variable refresh each time: 41 to 44 of 45 pairs following the presents), the window put on the TV
   with the KWin script above:

   | Run | Slots | Missed (live / verifier) | First pixel − slot time: p50 / p99 / max | Hand-over before its time: p1 / p50 |
   | --- | --- | --- | --- | --- |
   | 120 s | 7,072 | 0 / 0, agree | +0.06 / +0.36 / +1.95 ms | 0.69 / 0.82 ms |
   | 150 s | 8,871 | 0 / 0, agree | +0.05 / +0.38 / +2.18 ms | 0.65 / 0.83 ms |
   | 120 s, a 1080p60 NVENC encode running beside it (a stream encoder's GPU load) | 7,072 | 0 / 0, agree | +0.05 / +0.32 / +2.04 ms | 0.76 / 0.89 ms |

   Hand-over to first pixel over all 23,014 shows: p50 0.91 ms, p99 1.16, max 2.99; **none above 3 ms**, where each of the
   owner's late slots needed at least 4.6 ms. So the TV, KWin and variable refresh do not make these late slots from a
   steady source, with or without an encoder's load. What the owner's session had and these runs did not: the capture
   source (USB delivery jitter, the game's holds and loading screens, D re-converging after them: its max was 84.6 ms),
   and **a Discord screen share running the whole time** (KWin capturing the screen for it). The owner's own probe before
   playback already saw first pixels up to 3.13 ms after the present, against at most 1.08 ms in these runs: the display
   path was slower in that session before the first slot. That points at the session's environment, most likely the
   screen share; it could not be reproduced here (Spectacle's background recording exits at once without recording; a
   portal screen cast needs its dialog answered) and the capture source cannot run without the console.

   Still **inference**: that the late slots came in runs of a steady 4–8 ms offset (from the interval percentiles above,
   no CSV); the simulated display shows that a stretch where presents wait for a fixed 119.88 Hz grid gives exactly that
   (`SimulatedDisplayTests.Variable_refresh_on_a_119_88_Hz_output_with_a_varying_display_delay…`: a delay varying 0.4–3.1
   ms misses nothing, 0 of 211; on a fixed grid after 2 s, 147 of 269 judged slots late, the live counters and the
   verifier count the same 147, all the display's; the model's file runs at 30.000 fps against the grid's 59.94, so its
   intervals are less even than a locked console's would be).

   On DP-3 (240 Hz, a 30 fps clip, 60 s, `--clock vrr`): 3,480 slots, 5 late (2.3 to 3.5 ms after their time against a
   tolerance of 2.08 ms, each alone, handed over 0.5–0.9 ms before; the display took 2.9 to 4.2 ms), live and verifier
   agree.

   For the owner, on the TV with the console on and a game moving (2 minutes each, silent), once **with the Discord
   screen share running** and once without it, then send back the whole output of all four commands:
   ```sh
   fmp capture --mode 4k30-hdr --audio off --duration-s 120 --csv tv-share.csv > tv-share.log 2>&1
   fmp verify tv-share.csv --vrr --rate 119.88
   fmp capture --mode 4k30-hdr --audio off --duration-s 120 --csv tv-noshare.csv > tv-noshare.log 2>&1
   fmp verify tv-noshare.csv --vrr --rate 119.88
   ```
   `fmp verify` now prints a `lateness` line: a histogram of how late the late slots were (0.3 ms is bookkeeping, 8 ms a
   refresh) and how many runs of consecutive late slots against slots late alone. Runs with lateness spread up to 8.3 ms
   confirm fixed-refresh stretches. `fmp verify --vrr` needs `--rate` with the output's fastest rate: without it the period
   is the median interval (16.7 ms at 60 fps), which halves nothing like the right tolerance.

   Generated slots skipped under variable refresh (the next real frame came too late for them): 767 in 8,146 s, one every
   10.6 s.

4. **Fixed refresh.** Gate-length runs: DP-3 0 missed; HDMI-A-1 4 missed in one host stall (38 s, the pipeline's by the
   rule; the owner accepts a handful in 10 minutes for now, so not chased): the shorter lead leaves about 3 ms for a stall
   where 12.5 ms left 11. One run per output, not repeated; the strict gate (zero of the pipeline's) is not shown met on
   HDMI-A-1. The latch can change by a whole refresh between runs (KWin); a change during a run is handled only by the
   lead's climb. A window moving between outputs during a run is still untested end to end.
5. **3x and 4x.** Interval sd 0.018 to 0.088 ms, no miss, on both outputs. The 10-minute judder check is the HDMI-A-1 gate
   run (a 59.94-equivalent source on 119.88 Hz: no periodic judder); a 59.94 source *not* locked to its output was not run
   (no 120.000 Hz output without a mode change; files are always stretched to lock).
6. **Scheduler tuning backlog (Phase 1 review): not started** (the brief's "only if time remains"; the owner then asked
   to wrap up). None of its 12 items is done; item 11 (VRR accepting rates over the panel's maximum) is side-stepped for
   the pipeline by the auto rule (fixed refresh when the rate does not fit). Item 1 showed in capture: in the first variable-refresh run D
   started at 55 to 63 ms (a hold and the start-up's primed repeats widen the source interval; that run's pacing thread also
   saw frames up to 2 ms late, since fixed) and took about 10 s to come down; the 1440p60 baseline on fixed refresh had D
   reach 41.9 ms. The later runs' D stayed within 0.3 ms of its value.
7. **Perfecting the pacing** (zero missed slots in every 10-minute run, on every output): deferred by the owner, who
   accepts a handful of misses per run for now.
8. **A compositor that skips refreshes on its own.** At hand-over, with a browser and Discord open, KWin missed one
   refresh every second on DP-3 (windowed, fixed refresh): presents handed over up to 7.8 ms ahead, nearly two refreshes,
   were shown a refresh late too, so no lead helps (and none is raised for it). A user sees one hitch a second (2 slots a
   refresh late, then 2 more behind them) for as long as it lasts; the misses count as the display's, but one within 10 s
   of another can count as the pipeline's by the conservative rule. The windowed short-lead test now says "environment"
   and skips only when every miss of the pipeline's in its second half is cleared by its own timestamps (handed over at
   least 1 ms before presents that made their refresh, or within four refreshes of a miss the display owns); any other
   fails it. It did not skip in the runs since.
   Not investigated on KWin's side (it was absent during the gate runs earlier: 0 misses in 10 minutes on DP-3).
9. **Tracked from the safety review, not fixed:** the latch sweep leaves `_sweepOut` set when a probe's report never comes,
   so the sweep then waits out its budget (`PresentLoop.Vrr.cs`, `SweepStep`); forced `--clock vrr` with the output's rate
   never known gives a half-period of 0 and could freeze the picture (`VrrTurn`, plausible, not reproduced); variable
   refresh never falls back to fixed mid-run if the display stops following the presents (it keeps presenting at the
   slots' times; *2026-11-19: now detected and reported, the fallback designed, not built: see the last section*); `SetFullscreen`'s result is ignored (`PlaybackSession`, the pre-roll), so a refused fullscreen goes
   unreported; `fmp` started as a background job from a non-interactive shell ignores SIGINT (the shell sets it to
   ignored; SIGTERM works). Untested by an automated test: at the end of a run a fullscreen window is now minimised
   before the teardown waits for the threads, so with stuck GPU work the desktop comes back at once instead of after
   the stall timeout (`PlaybackSession`, the `finally` of the run).

The 4b pacing backlog: 1 (gate runs) run on both outputs, verified (DP-3 0 missed; HDMI-A-1 4 missed, one stall); 2 (the latch)
measured directly, the poisoned estimate removed; 3 (240 Hz) regular in this session (the 60 → 240 run: interval sd 0.088
ms, 0 of 13,920 intervals off the grid, every show timed; its latch 4.44 ms, more than a refresh; the 10-minute gate run there
missed nothing, every one of 144,019 shows timed); 4 (the lead) done; 5 (the stride) done where phase-locked; 6 (60 fps files
on 119.88 Hz) done (stretched); 7 (millisecond container timestamps), 8 (moving between outputs), 9 (`FMP_PLAY_FAULT` in
the release binary): not done.

Other: the "decode thread allocated 32 B" failure seen in fullscreen runs of the JIT build (`dotnet build`'s `fmp`, not the
NativeAOT binary) was not chased; NativeAOT runs here allocate nothing on any thread.

## Found by the re-check of the variable-refresh blame rule (2026-11-19), not fixed

- With the hand-over more than a quarter of the tolerance late (1.04 ms at 119.88 Hz), a show the display alone
  would have made late (its excess over the whole tolerance) is still given to the pipeline: 38 of 38 in the
  reviewer's run with hand-overs 1.3 ms late and one show in ten 6 ms slow. It errs towards showing pipeline
  lateness. If unwanted: give a show to the display when its excess alone exceeds the tolerance, and keep the
  floor for the jointly caused case.
- The floor scales with the output: at 240 Hz it is 0.52 ms, close to the 0.3 to 0.42 ms by which hand-overs
  trailed the expected display delay in the owner's capture run, so late shows the display caused on DP-3 could be
  given to the pipeline. Not tested.
- In a fixed-refresh run longer than the verifier's buffer, the one gap straddling the buffer's end can still count
  as a disagreement if a missed slot after the end explains its later part (by reading).
- `SimulatedDisplayTests.The_present_lead_starts_from_the_latch_measured_before_playback` asserts real-clock windows
  (latch 1.9 to 2.7 ms, lead 3.9 to 4.7 ms, no misses) and failed once when the whole solution's test assemblies
  ran at once; it passed 6 of 6 alone. This change does not reach it (it runs fixed refresh).


## The capture rate and variable refresh lost (2026-11-19)

Two findings from the owner's measurements of the day (`tv-share.*`, `tv-noshare.*` in the repository root: two 120 s
runs of `fmp capture --mode 4k30-hdr --audio off`, fullscreen on HDMI-A-1, 119.88 Hz, variable refresh, with and
without a Discord screen share), acted on in one time-boxed pass.

### 1. The card repeated a frame every 33 s: it now captures at the console's rate

**What the owner's runs showed.** Both runs: 3,609 captured → 3,605 unique, 4 repeats dropped (5 in the other); 3 to 4
late shows, each alone, 8 to 12 ms late, at 20.08, 52.98, 85.85 and 118.65 s (32.80 to 32.90 s apart); "generated slots
skipped 8"; intervals max 58.2 ms against a mean of 16.685. The app asked the card for 30.000 fps while a PS5 sends
59.94 Hz: once every 1/(30.000 − 29.97) ≈ 33 s the card has no new frame and sends a duplicate, the app drops it, and the
picture goes 58 ms without a new image. In the owner's two-hour session that is about 220 events (about 450 of its 767
skipped generated slots).

**What the card does** (with the console on, a 30 fps picture): it offers 29.970 and 59.940 beside 30.000 and 60.000 for
these sizes (P010 2160p: 30 and 29.97; P010 1440p: 60, 59.94, 50, 30, 29.97; no 119.88 for the 120 and 144 modes). It
**free-runs on its own crystal**: its frame period is 15 ppm short of nominal against CLOCK_MONOTONIC at every rate tried
(−14.8 and −15.2 ppm at 30.000 in the owner's runs, −15.7 at 29.97, −15.0 at 59.94), so it does not lock to the input.
The console's rate, from the beat at 30.000, is within a few ppm of CLOCK_MONOTONIC.

| Run (offscreen unless said; the published scratch binary of this change for "new") | Repeats | Timestamps | Other |
| --- | --- | --- | --- |
| Owner, 30.000, 2160p P010, TV fullscreen VRR, 120 s (×2) | 4 and 5, periodic 32.8 to 32.9 s | mean 33.333 ms, sd 1.5 to 1.6 µs | 3 and 4 late shows; generated slots skipped 8; intervals sd 1.04 / 0.99 ms, max 58.2 ms; real frame 45.96 / 45.99 ms after its timestamp (p99 46.35 / 46.48) |
| 29.97, 2160p P010, 300 s | **0** (2 black frames at the start) | mean 33.366 ms, sd 9.0 µs (a few ±0.2 ms delivery jitter pairs), card −15.7 ppm | — |
| 59.94, 1440p P010, 240 s | **0 beat repeats**: 7,195 of 7,199 runs were the 30-in-60 cadence (one repeat each); one run of 2 at 79.9 s, two of 0 during two timestamp stretches (17.8 to 22.9 ms intervals at 140.8 and 214.3 s, the card's or the host's) | mean 16.685 ms, sd 80 µs (the stretches), card −15.0 ppm | — |
| **New, `--mode 4k30-hdr` (now 29.97), TV fullscreen VRR, 120 s** | **0** | mean 33.366 ms, sd 25.7 µs, min 33.157, max 33.574 | **0 late shows, 0 missed** (verifier agrees); generated slots skipped 2; intervals sd **0.392 ms**, max **34.0 ms**; real frame **46.74 ms** (p99 47.23) after its timestamp; variable refresh chosen (probe 37 of 45 following); HDR10 with metadata; D 45.71 (hold 16.685 = T/2 exactly); 0 B on every thread, GC 0/0/0; PASS |

So: **at 29.97 / 59.94 the periodic repeats are gone** from every run here (5 + 4 + 2 minutes). They are not gone
forever: the card is about 16 ppm faster than the console, so it should still repeat a frame about every 1/(29.97 ×
16 ppm) ≈ 35 min at 29.97 (17 min at 59.94); predicted from the measured clocks, not yet seen (no run that long was made,
to keep the card free for the owner). In a two-hour session that is 3 or 4 instead of about 220. Latency: 46.74 ms
against 45.96 / 45.99 in the owner's runs of the morning: +0.75 ms, all of it in the arrival lag (USB delivery p50 25.94
against 25.40 ms; the offscreen 29.97 run measured 25.41), so run-to-run variation of the delivery, not the rate; Phase 6
measured 46.6 ms for the same command. The two generated slots skipped came from timestamp jitter (one interval 34.0 ms).

**Default: the console presets ask for the console's rate.** `4k30-hdr` 29.97 (30000/1001), `1440p60-hdr`, `4k60-sdr`,
`1440p60-sdr`, `1080p60-sdr` 59.94 (60000/1001); the 120 fps presets stay at 120 (no 119.88 offered: a console at
119.88 Hz repeats a frame every 8.3 s there, which the beat detector reports). Why not measure at start: the source's
rate cannot be read from this card (UVC reports no input timing; the Elgato's own utility reads it through an
undocumented extension unit), and telling 30.000 from 29.97 by the beat takes a minute or more; a PS5 always sends the
NTSC rates.

**A true 60.000 Hz source** (a PC, some devices) captured at 59.94: the card leaves one source frame out every 16.7 s (at
29.97 of a 60.000 signal: every 33 s). That is a frame skipped, not a frame held: the motion jumps one frame; it does not
make a 58 ms gap (no repeat reaches the app). When the content repeats (a 30 fps picture in the 60 Hz signal), the beat
detector sees it as a run of repeats one shorter than the steady cadence and reports "frames left out" with the period
and "the source looks like 60 Hz: use --capture-fps 60". When every source frame is new (a 60 fps game), the skipped
frame leaves no trace in the repeats and nothing is reported. `--capture-fps 30` / `60` (or `fmp settings set
capture.fps 60`) asks for the whole rate.

**What changed.**
- `CapturePreset` (`src/FrameMotionPlus.App/Capture/CaptureOptions.cs`) carries an exact `FrameRate`; `--capture-fps
  <rate>` sets a preset's rate (or is `--fps` without `--mode`): 29.97, 30, 59.94, 60 or a fraction, parsed exactly
  (`FrameRate.TryParse`: 29.97 → 30000/1001); settings key `capture.fps` (`CaptureSettings.Fps` in Core, `UserSettings`).
- `CaptureFormats.ExactRate` (`src/FrameMotionPlus.Sources.V4l2/CaptureFormat.cs`): a driver interval within 100 ns of a
  whole or NTSC period is taken as that exact rate, so `NegotiatedFormat.Rate` (the source's `NominalRate`, the cadence
  tracker's prior, the summary) is 30000/1001 and not 10000000/333667. The scheduler's `T` was already measured from the
  timestamps (the median): 33.366 ms in the runs above, the hold T/2 = 16.685 ms; the audio delay follows the measured
  latency; the generated frame's timestep stays 0.5.
- `RepeatBeatDetector` (`src/FrameMotionPlus.Sources.V4l2/RepeatBeat.cs`), fed once per judged frame on the capture
  thread (allocation-free): the content's steady run of repeats (0 for a 30 fps game at 30, 1 at 60: one length making
  90% of the last 64 runs), an extra repeat (a run one longer) or a frame left out (one shorter, when the steady run is
  at least 1), and a periodic beat when 3 or more events of one kind agree (within 3 capture intervals or 2%, 1 s to an
  hour apart). The implied source rate is the capture rate less (repeats) or plus (left out) one frame per period; it is
  matched to the capture rate's NTSC or whole sibling (within 10% of the beat period those two make) or to nothing
  ("something in the content, not the clocks": an autosave hitch every minute matches no rate).
- Summary, a new line after `capture th`: `capture fps asked 29.97 fps (30000/1001, 33.3667 ms), set 29.97 fps (...);
  extra repeats ... 0, frames left out 0; no periodic beat`, or `periodic: a repeat every 33.37 s (4 in a row agree), the
  source about 29.970 Hz; the source looks like 29.97 Hz: use --capture-fps 29.97` (or "which the card does not offer at
  this size and format: the beat stays"). On stderr, once when a beat is first seen (preformatted at start: the control
  thread allocates nothing): `fmp: capture: the card repeats a frame periodically at 30 fps: the source looks like 29.97
  Hz: use --capture-fps 29.97`. No automatic switch mid-run: a rate change means STREAMOFF, S_PARM, new buffers and a
  restart of the stream (about 0.6 s of start-up, two black frames), which is not seamless.

**Pacing with T = 33.3667 ms.**
- *Variable refresh, both outputs:* slots follow the source, so nothing changes but T (the hold is T/2). Measured on the
  TV above. At 2x the slots are 16.683 ms apart: exactly two refreshes of 119.88 Hz.
- *Fixed refresh, HDMI-A-1 (119.88 Hz, windowed):* 59.94 × 2 = 119.88 exactly, so the slots become the refreshes
  themselves; the source then drifts against the display only by the card's ~15 ppm (a stride slip about every 1/(59.94
  × 15 ppm) ≈ 18 min) instead of the 0.1% of 30.000 (every 16.7 s). Not measured windowed in this pass.
- *Fixed refresh, DP-3 (240.016 Hz):* 59.94 into 240.016 is 4.004 refreshes per slot (4.0003 at 60.000): the scheduler's
  stride handles it as before (not phase-locked). Not measured in this pass.
- *1440p60-hdr on HDMI-A-1:* 59.94 × 2 = 119.88 now fits the output's ceiling (`VrrFits`, 0.05% tolerance), so `--clock
  auto` chooses **variable refresh** there where 60.000 × 2 = 120.00 used to be refused (fixed refresh, almost every slot
  generated). At the ceiling the card's 15 ppm makes the slots that much faster than the shortest refresh period; the
  shows then fall behind by 0.13 µs a slot and a slot would have to go about every 4 to 5 minutes. **Not measured**: if it
  misbehaves, `--clock fixed` restores the old behaviour.
- File playback is unaffected (its rate is the file's); the SDR presets change rate like the HDR ones (they are console
  presets too) and nothing else.

### 2. Variable refresh lost mid-run: detected and reported

**The detector** (`VrrFollowDetector`, `src/FrameMotionPlus.Pipeline/VrrFollowDetector.cs`; fed by the present thread
for every timed show under variable refresh, allocation-free): over the last 60 timed shows (a second at 60 Hz), the
display is not following when at least half came more than max(1 ms, a quarter of the shortest refresh period) later
after their hand-over than the delay the start-up probe measured while it followed, **and** at least 60% of the pairs of
consecutive shows were a whole number of refreshes apart (within 0.25 ms). The probe's own criterion ("N of M pairs as
far apart as their presents, K on the grid") does not work mid-run: at 2x the slots are already about two refreshes
apart (exactly two at 29.97 → 119.88), so a following display and a gridded one give the same pairs; what differs is the
delay from hand-over to first pixel (0.9 ms when following; anywhere up to a refresh more on the grid). The grid
condition keeps a following display with a delay varying by milliseconds (the owner's 0.4 to 3.1 ms) from being taken
for the grid. An episode starts at the first late-after-hand-over show of the window, ends (back-dated to its last
such show) after 120 shows in a row without one (so a drifting grid's on-time stretch does not split it), and the late
shows of the window before the decision are moved inside it. A grid whose phase sits within the threshold of the
follow delay is not seen; its shows are on time.

**Reported:** a summary line under `missed`: `follow      variable refresh lost 1x for a total of 37.4 s (the longest
37.4 s) (...); late shows inside those 2242, outside 0 (whole run); at 2.6 s for 37.4 s` (or "never lost"); one stderr
line when an episode starts ("fmp: variable refresh lost: the display shows on its refresh grid, ...", at most one every
10 s) and one when it ends ("fmp: variable refresh back after 2.1 s (episode 1)"), from preformatted text and a reused
buffer (the control thread allocates nothing; 0 B measured). `fmp verify <csv> --vrr` prints the same line from the CSV
alone (hand-over = `present_submit_ns`, first pixel = `actual_display_ns`; the follow delay is the run's 10th percentile
of the two's difference): on the reproduced loss below it gives the same episode to the tenth of a second; on the owner's
`tv-share.csv` "never lost; late shows inside 0, outside 4" (the beat repeats).

**Reproduced** (fullscreen `fmp play` of the dim 4K HDR10 29.97 clip on DP-3, 240.016 Hz, the KWin placement script):
in one run of four, variable refresh was lost **2.6 s into playback and never came back** for the rest of the run
(37.4 s): 2,242 of 2,278 slots late (2 to 8 ms; intervals still even, mean 16.686 ms), the lead driven to its 2 ms bound;
the detector said so on stderr at once, and the summary and `fmp verify --vrr` agree. A notification (`notify-send`) was
sent about then; the same notification sent 15 s into playback in a later run caused **no** loss, and two runs without
any notification were clean (0 missed, the committed binary and this one alike). So the cause of that loss is not
established; a notification by itself did not do it the second time. Not tried for lack of time: another window raised
over the fullscreen one, leaving fullscreen and back, the cursor, focus on another output, and anything on HDMI-A-1.
The commands for the owner (each about a minute, then read the `follow` line):

    fmp play <30 fps clip> --fullscreen --loop --duration-s 60 --csv a.csv   # then during the run, one at a time:
    notify-send test "a notification"                 # on the same output and on the other one
    # Alt+Tab to another window on the same output and back; move the mouse onto that output; open a menu over it

The owner's two-hour session (7.6% late in runs at an even cadence) fits this detector's picture; its CSV, run through
`fmp verify --vrr` with this build, would say how much of it was the grid (not available to this pass).

**The clock switch: designed, not built.** Switching to the fixed-refresh behaviour while the display is on its grid
would cross the two clock paths Phase 6 wrote separately: the present thread's `VrrTurn` (hand-over at the slot's time,
no grid, repeats absorbed) against its fixed path (`SendGrid`, the lead, fillers, the grid anchor), and the pacing
thread's scheduler (`UpdateDisplayTiming` with a VRR clock V = ts + D + kT/M against a refresh grid). Not a contained
change; the working variable-refresh path was not touched. The design:
1. On an episode start (present thread), measure the grid from the episode's first pixels (they are on it: the anchor is
   their residual modulo the period, the latch from hand-over to first pixel), and send the pacing thread a
   `DisplayUpdate` with that anchor, the rate and a lead from the latch (the fixed clock's measured lead).
2. The pacing thread's scheduler switches its slot clock to the grid (`UpdateDisplayTiming` as for fixed refresh, the
   multiplier unchanged, the stride as the fixed path computes it); the present thread presents on the fixed path
   (repeats presented, fillers on) while the flag is set.
3. On the episode end (the first pixels following hand-overs again for 2 s while presenting on the grid, which means
   the delay test above has to be judged against the grid's expected time, not the hand-over), switch back: a
   `DisplayUpdate` with the VRR lead, the scheduler back to V = ts + D + kT/M from the next real frame.
4. What must hold: no slot handed over twice or skipped across the switch (the switch at a real frame), the verifier
   and the live counters judging each slot by the clock it was presented under (a per-slot mode bit in the telemetry),
   the summary's miss accounting split by mode, and a test on `SimulatedCompositor` with a grid stretch (follow → 2 s
   grid → follow: no late shows inside the stretch after the first second).

**Tests:** `VrrFollowTests` (Pipeline, 5, pure): follow → 2 s on the 119.88 Hz grid → follow is one episode of about
2 s with the late shows inside and none outside; a following display with a delay varying 0.4 to 3.1 ms and a 6 ms stall
every 2 s has none; an exact multiple (29.97 → 59.94 on 119.88) on the grid is seen when its phase makes shows late;
flapping episodes are each counted; an episode open at the end is closed there. `RepeatBeatTests` (Sources.V4l2, 20,
pure): simulated arrival — 30.000 capture of a 29.97 source repeats every 33.4 s, periodic, 29.97 suggested; 60.000 of a
30 fps picture in 59.94 every 16.7 s; matched rates none; 59.94 of a true 60 Hz source "frames left out" every 16.7 s
when the content repeats, nothing when every frame is new; irregular content repeats and a slow game are no beat; a
period matching no sibling suggests nothing; driver intervals to exact rates (UVC's 100 ns units); the presets' rates
and `--capture-fps` parsing; the chosen mode for 29.97; the summary line. Hardware: `ElgatoTests.Each_target_mode_captures_the_console`
gained 2160p P010 29.97 and 1440p P010 59.94 (negotiated as the exact 30000/1001 and 60000/1001, frames at that rate, no
beat in 4 s); it skips without the card or without a picture on it, like the other cases.

**Whole solution** (one assembly at a time, the console on): Core 711, Platform 100 + 1 skip, Audio 77, Cuda 144, Vulkan
108, Gpu 129, Vfx 93 + 1 skip, Sources.File 154, Pipeline 163 of 164, Sources.V4l2 113 + 30 skips. The one Pipeline
failure, `WindowedTests.A_short_windowed_vfg_run_presents_one_frame_per_refresh` (3 pipeline misses in a fixed-refresh
window on the desktop, a path this change does not touch), passed twice run alone. Two `CaptureCliTests` asserted the old
"@ 30 fps" / "@ 60 fps" and were updated (the whole V4l2 assembly then passed with the card).

**What remains.**
- A run of an hour at 29.97 to see the predicted residual repeat (about every 35 min) and its period.
- The clock switch (above). The causes of a loss on each output (the list above is mostly untried).
- 1440p60-hdr on HDMI-A-1 now under variable refresh at the output's ceiling: measure it.
- Fixed refresh at 29.97 on both outputs (windowed) not re-measured.
- `docs/implementation-plan.md` / the design doc should say: the capture rate is the console's (29.97 / 59.94), the card
  free-runs (a residual beat of about 15 ppm), and variable refresh can be lost mid-run (detected, reported; fallback
  designed).

## Found by the independent review of the capture rate and the follow detector (2026-11-19)

Approved with no must-fix. The reviewer's own runs on the TV with the console on, same build: `4k30-hdr` fullscreen
90 s, variable refresh, 0 missed, never lost, 46.82 ms (p99 47.39), T 33.366 ms, largest interval 33.76 ms;
`1440p60-hdr` fullscreen 90 s with 30 fps content, variable refresh, 0 missed, 31.96 ms; a 59.94 fps file at the
TV's ceiling (119.88 slots), 0 missed, interval sd 0.135 ms. Fixed afterwards: a preset's 29.97 or 59.94 falls back
to 30 or 60 on a device that offers only whole rates (it failed to start there; a rate typed with `--capture-fps`
still fails with the rates offered). Not fixed:

- **`1440p60-hdr` at the TV's ceiling is only partly measured:** no 60 fps game was available. In the file run at
  the ceiling the hand-over to first pixel sat at 1.3 to 1.7 ms p50 against the probe's 0.93 ms and the lead ended
  at 1.91 ms, near its 2 ms bound; it did not grow over 90 s. To do: ten minutes with a 60 fps game on the TV.
  `--clock fixed` is the way back.
- **The follow detector's grid test discriminates nothing** (85 to 99% of pairs are "on the grid" in every healthy
  run); the delay test alone decides, with wide margin in every run seen (at most 1 of 60 shows over the cutoff where
  30 are needed). It compares with the probe's fixed delay, so a steady rise of the real delay by about 1.1 ms on
  DP-3 (cutoff 1.04 ms) would announce a loss on a display that follows. Compare with the adaptive lead instead.
- **A loss can go unannounced on stderr:** an episode that starts within 10 s of the previous "lost" line is never
  printed, at its start or its end (`PlaybackSession.cs:1078-1092`); the summary still counts it.
- **A saved `capture.fps` applies to every preset:** saved `capture.fps=30` with `--mode 1440p60-hdr` captures 1440p
  at 30 fps (`UserSettings.cs:313-322`); the settings line shows it.
- **The card's residual beat will be described wrongly:** after the third 35-minute repeat the notice says it
  "matches no rate next to it" and the summary suggests the content; it is the card's clock (`RepeatBeat.cs:192`).
- A small ordering race in the beat notice (`RepeatBeat.cs:210`: the version is bumped before kind and period).
- The windowed fixed-refresh tests (`WindowedTests`, Blend and None) failed during the review on the committed code
  as well as on this change: the desktop's state at the time.


## Skipped generated slots, variable refresh at start-up, and the review's small fixes (2026-11-19, evening)

The owner's session of the afternoon: 3 h 10 min of `fmp capture --mode 4k30-hdr --audio-sink inzone`, fullscreen on
the TV (HDMI-A-1, 119.88 Hz, variable refresh), streamed on Discord, no CSV; "glorious". Its summary: 680,705 slots
(341,061 real, 339,644 generated), 721 missed (the verifier agrees), 723 late shows of which 593 inside two losses of
variable refresh (at 2.7 s for 12.7 s and at 3509.1 s for 5.7 s), **1,517 generated slots skipped** (one every 7.5 s),
real frames 48.79 ms after their capture timestamps (p99 50.08), D 48.83 ms (arrival 28.81), timestamp → frame
available p50 27.71, p99 28.54, max 30.31 ms, 912 repeats dropped, 102 holds, 402 black frames, 57 extra repeats, and on
stderr "a periodic repeat at 29.97 fps that matches no rate next to it". Late shows had fallen from 7.6% to 0.1%. One
time-boxed pass on the three things left; the console was on with a game moving for the capture runs below.

### 1. Generated slots skipped under variable refresh

**When a slot is skipped** (`FrameScheduler.ScheduleVrr`, the generated branch). A generated slot between real frames
a and b has its time V = ts(a) + D + k·T/M (k = 1 of M = 2 at 29.97 → 59.94). It is decided at max(arrival of b,
V − lead), the lead being p99 of the generation time d + p99 of the pacing thread's wake lateness + the 0.5 ms margin.
It is skipped when that decision comes after V − p99(d) − margin/2 (lateness may eat the wake allowance and half the
margin), or when V would crowd b's own slot (less than a shortest refresh period before it). D = hold + arrival + lead,
where the hold is (M−1)/M of the 90th percentile of the source intervals (T/2 = 16.68 ms here) and **arrival is the
99th percentile of the last 256 frames' timestamp → frame available** (8.5 s at 29.97; recomputed at every decision; D
follows its target up at 50 ms/s at once, down at 5 ms/s past a 0.1 ms deadband). So a slot is skipped when b's arrival
lag exceeds that rolling p99 by more than about (hold − T/2) + wake + margin/2 ≈ 0.4 ms, less the pacing thread's own
delay from the frame's availability to the decision: measured, the skipped ones came 0.16 to 1.64 ms over the rolling
p99. On screen a skip leaves a on screen one source interval (33.4 ms) instead of two halves: a small stutter.

**Two kinds.** The skips counted together two different things, now counted apart (below):
- *In an interval the source made longer* (a repeated or dropped frame: an interval of 2T gets four slots at 2x): the
  first two generated slots need b by ts(a) + D + T/2 and + T, and b comes a whole interval later than in a normal
  interval. They are skipped whatever the budget (only D ≥ 2T would cover them); the third (t = 0.75) is shown. This is
  the game's own hitch, which the output already smooths (a held 50 ms instead of the source's 66.7 ms).
- *In a normal interval*: b arrived late (USB delivery jitter beyond the rolling p99).

**Measured, 600 s each, the TV fullscreen, variable refresh, the same binary, one after the other** (`fmp capture --mode
4k30-hdr --audio off --duration-s 600 --csv`; A with `--late-generated-ms 0`, the old behaviour; B the new default):

| Run (598 s measured each) | Skipped: long intervals / normal | Shown late instead | Late shows (verifier agrees) | Real frame after its timestamp p50 (p99) | D p50 | Intervals sd, max | Timestamp → available p50 / p99 / max |
| --- | --- | --- | --- | --- | --- | --- | --- |
| A, `--late-generated-ms 0` (as before) | **23**: 14 (7 content repeats × 2) / **9** | — | 5 | 46.780 (48.130) ms | 45.78 ms | 0.668, 58.25 ms | 26.63 / 26.98 / 28.63 ms |
| B, default (4 ms) | **6**: 6 (3 content repeats × 2) / **0** | 3, by 0.046 ms mean, 0.063 at most | 3 | 46.784 (47.210) ms | 45.80 ms | 0.467, 58.25 ms | 26.63 / 27.03 / 27.24 ms |

Both: variable refresh chosen by the probe and never lost, 0 pipeline misses, 0 B on every thread, GC 0/0/0 in the
window, PASS; the 58.25 ms intervals are the content's repeated frames (a held 50 ms then t = 0.75). The CSVs say the
same as the live counters (the long/normal split matches a reconstruction from the frame ids exactly). The two runs did
not have the same arrival tail: A had three USB-delivery spikes of 1.6 ms over the rolling p99 at 8.2, 98.3 and 188.3 s
(exactly 90 s apart: something on the host, not chased), B none, so B's "0 normal" is partly the conditions; what B
shows is that the new path engages and costs nothing (its three slots came 0.05 ms past the deadline: shown 0.05 ms
late instead of a 16.7 ms hole). The controlled comparison is the deterministic simulation (`VrrLateGeneratedTests`,
the card's lag: 27.2 ms + uniform 1 ms + 1% of frames up to 2.4 ms more, d 2 ms ± 0.3, 300 s): **43 skipped → 0**, 44
shown late, the same real frames, the same real-frame latency (47.987 ms both), 0 missed, 0 causality or timestep
faults; and with 2% of source frames dropped (long intervals): 142 → 120, every one left in a long interval (120 → 120).

**Where they fall.** Not at holds or cuts: an interval over 2.5 T (a hold) or after a discontinuity is never
interpolated, so its slots are not counted as skipped at all (the session's 102 holds cost none); each run here had one
hold and one black frame, at the start, with no skip near them. The normal-interval skips are spread out (A: 9 in 598 s,
gaps 0.8 to 90 s, median 30 s), three of them on the host's 90 s delivery spikes; the long-interval ones come with the
game's own repeated frames, sometimes in bursts (B: three repeats within 9 s at 67 to 75 s).

**What covering the tail with a larger arrival budget would cost.** In A the normal-interval skips came 0.16 to 1.64 ms
over the rolling p99 (median 0.37); frames over the rolling p99 + 0.5 / 1.0 / 1.5 ms: 4 / 3 / 2 of 17,976. Covering all
of them by budget would need about +1.7 ms of D (+1.7 ms latency for every frame) on this day's card; on the owner's
session (p99 28.54, max 30.31 ms) about +1.8 ms; and a short-window maximum instead of p99 costs its mean distance
above p99 all the time (0.15 ms in A) yet still misses the first spike of each burst. Long-interval skips are out of
reach of any budget under 2T.

**The owner's 1,517.** Not split (no CSV), but bounded: 57 extra repeats in steady cadence make at most about 114
long-interval skips, plus those in stretches without a steady cadence (loading, menus, 912 repeats dropped in all), so
the large majority were normal-interval: an arrival tail of p99 − p50 = 0.83 ms that afternoon against 0.35 ms here.
Those are what the change removes. Per 10 minutes: the session 80; A 23 (9 normal); B 6 (0 normal).

**What changed.**
- `SchedulerOptions.VrrLateGeneratedNs` (Core; **0 by default there: off and inert**, the decision is the old one to the
  nanosecond). When set and a generated slot's decision comes after V − p99(d) − margin/2, the slot is moved to
  now + p99(d) + margin/2 (the allowance an on-time late wake gets) if that is at most the limit after V; the crowding
  test (a shortest refresh period before b's slot) still applies, so b keeps its time; its content time moves by the same
  amount (τ = V − D holds; t = 0.5 + delay/T, inside (0, 1)). Reason `SlotReason.Delayed` ("delayed" in the CSV),
  `SlotDecision.DelayedByNs`, counters `DelayedSlots` and `SkippedInLongIntervals` (a skip in an interval with more slots
  than the multiplier). `FrameScheduler.ScheduleVrr` only; `NextDecisionTime` is unchanged (still pure).
- `fmp capture` / `fmp play --late-generated-ms <ms>` (0 to 8, lowered from 50 after the review below; **default 4**: a quarter of the 16.7 ms between slots;
  every delay seen was under 1.7 ms) → `PlaybackOptions.LateGeneratedNs`. No settings key: there is no latency to trade
  (the option is for A/B and to switch it off).
- Summary, the end of the `missed` line under variable refresh: `generated slots skipped (...) 23: 14 in intervals the
  source made longer (a repeated or dropped frame: the next one cannot come sooner), 9 in its normal intervals (the next
  frame arrived late); generated slots shown late instead of skipped (up to 4 ms) 3, by mean 0.046 ms, at most 0.063
  (content moved to match)`.
- `SimulatedSourceOptions.IngestTailProbability` / `IngestTailNs` (a rare extra arrival delay; no random numbers drawn
  when 0, so every existing simulation is unchanged).

### 2. Variable refresh lost in the first seconds

**What the app does around then** (`PlaybackSession.PreRoll`): the window is created 1600x900 and pre-rolls; once the
refresh grid is known it asks for fullscreen (`SetFullscreen`; the swapchain follows at 3840x2160 on the next present
that sees the new size); the pre-roll goes on at least 0.8 s more (24 presents), 100 ms settle, the 600 ms probe, then
playback. The warm-up's end at 2 s only starts the meters. So the fullscreen transition and the swapchain change come
1.5 to 2 s *before* playback starts, and nothing of the app's falls 2.5 s after it.

**Measured** (the dim 4K HDR10 29.97 clip, `fmp play --fullscreen --loop --duration-s 15`, the KWin placement script;
the committed binary "before" and this change "after"; the pointer at rest on the TV):

| | Starts | Probe found the display following (first try) | Lost during the run |
| --- | --- | --- | --- |
| TV, before / after (pointer at rest over the window) | 3 / 3 | 6 of 6 | 0 of 6 |
| DP-3, before / after (pointer on the other output) | 3 / 3 | 6 of 6 | 0 of 6 |
| `fmp capture` on the TV, 600 s (the runs of item 1, the pointer over the window all along) | 2 | 2 of 2 | 0 |
| `fmp capture` on the TV, 600 s, the first run of the evening (before binary) | 1 | **no**: 32 of 44 pairs following, 8 on the grid, one show 22 ms late | (fixed refresh chosen for the whole run: 64.5 ms instead of 46.8) |

Then one 40 s run per output and binary with desktop actions at set times (`actions.sh` in the scratch directory):

| Action (seconds after launch; playback starts about 3 s after launch) | TV before | TV after | DP-3 before | DP-3 after |
| --- | --- | --- | --- | --- |
| `notify-send` (10 s; Plasma's popup on its screen) | no loss | no loss | no loss | no loss |
| the pointer moving over the window for 3 s (16 s; on the TV: over fmp on the TV runs, on the other output on the DP-3 runs; shown before, hidden after) | no loss | no loss | no loss | no loss |
| **another window activated** (24 s: the Konsole on the TV, raised over fmp on the TV runs, on the other output for the DP-3 runs) | **lost 4.4 s** | **lost 4.4 s** | **lost 5.1 s** | **lost 4.4 s** |
| fmp activated again (28 s) | back | back | back | back |

**So KWin's automatic variable refresh follows the active window**: the fullscreen window must have the focus, and
giving the focus to any other window, on the same output or the other one, puts the display on its grid until fmp is
active again (the episodes lasted as long as fmp was not active, plus 0.4 to 4 s before the display followed again:
4.4 to 5.1 s for a 4 s gap in the four runs, 10.1 s for a 6 s gap in the check below). A notification, the pointer moving or resting over the picture,
did nothing. The owner's episode at 2.7 s (12.7 s) fits the focus going to Discord to start the screen share and
coming back (the recorder attached during it); the file reproduction at 2.6 s that never recovered fits the window
losing the focus for good (a notification was sent then; when a popup that took the focus closes, KWin gives it to the
window active before, the terminal; today's notification did not take the focus). Neither is the app's doing.

**What changed.**
- The "lost" line says when the window is not the active one (SDL's input-focus flag, read when the line is printed):
  `fmp: variable refresh lost: the window is not the active one (another window took the focus, on either output; KWin
  gives variable refresh only to the active fullscreen window): click the picture or Alt+Tab back to it; ...`
  (`SdlWindow.HasInputFocus`, `VrrFollowNotice.Unfocused`); otherwise the old line (an overlay or another window).
  Checked with the final binary on DP-3 (25 s, the Konsole activated at 12 s, fmp again at 18 s): that line, then "back
  after 10.1 s (episode 1)", the summary `lost 1x ... at 9.9 s for 10.1 s`, PASS.
- **The probe is made again** when it does not find the display following (fullscreen, `--clock auto`; up to 3 probes
  of 0.6 s, `PlaybackSession.PreRoll`, `PresentLoop.RequestVrrProbe` now restartable): the first run of the evening
  probed while KWin had not yet granted variable refresh (the grid for the probe's first presents, following for the
  rest) and ran 10 minutes at fixed refresh. The summary's `clock` line says "the second (third) of up to 3" when it took
  more than one. Not seen to engage on the hardware since (14 starts, every first probe following): unverified there.
- The pointer is hidden over the window while it is fullscreen (`SDL_HideCursor`/`SDL_ShowCursor` in
  `SdlWindow.SetFullscreen`): standard for a fullscreen picture; it made no difference to variable refresh here.

**What the owner can do:** after starting a Discord share (or anything that takes the focus), click the fmp picture or
Alt+Tab back to it; the stderr line now says so. The fixed-refresh fallback during a loss stays designed, not built.
Not tried: the Discord share picker itself (expected to take the focus), a menu or tooltip over the picture, KWin's
overview, a keep-above window without focus.

### 3. The review's small fixes

- **The card's own slow beat** (`RepeatBeat.cs`, `CapturePrinter.cs`). A periodic beat whose implied source rate is within
  200 ppm of the capture rate (`RepeatBeat.IsCardClock`: a period of minutes, 34.8 min at 29.97 for the card's
  measured 16 ppm) is the card's crystal free-running against its input. The summary says (the test's card, 16 ppm
  fast, over 4 h) `periodic: a repeat every 34.8 min (... in a row agree), the source about ... Hz; the card's own clock,
  about 16 ppm faster than the source's (it free-runs on its crystal, not locked to its input): expected at the source's
  own rate, nothing to change`.
  **stderr now carries only advice to change the rate** (a beat matching the offered NTSC or whole sibling: "use
  --capture-fps 29.97"); a card-clock beat and one that matches no rate (the content's) are in the summary alone.
  *What "extra repeats" counts:* every run of repeats one longer than the content's steady cadence while the cadence is
  steady (one run length in 90% of the last 64): at 29.97 that is every single repeated picture during play, a game's
  own repeated frame or the first frame of a still as much as the card's beat. The session's 57 against about 5 from
  the clocks were the content's. The line now splits them: `extra repeats (...) 7, frames left out 0 (3 of them in a
  periodic beat, the card's or the clocks'; 4 not: the content's own, a game's repeated frame or the start of a still)`
  (`RepeatBeat.PeriodicEvents`: the events that belonged to a beat of three or more agreeing; the old field of that name,
  the agreeing run, is `AgreeingEvents`). Run A above shows that three content repeats can line up by chance (2%
  tolerance at a minute's spacing); such a "beat" matches no rate and stays in the summary.
- **The beat's version is bumped last** (`RepeatBeatDetector.Record` → `Classify`, then the version), so the control
  thread never sees a new version with the old kind or period.
- **A loss within 10 s of the last "lost" line is announced** (`PlaybackSession.Control`): `IsLost` is tested every
  turn while a loss is unannounced; once the 10 s have passed the "lost" line is printed (and later its "back"); a loss
  over before then stays unannounced (the summary counts it). A "back" is also printed when a new episode replaced the
  announced one between two turns.
- **The follow detector compares with the adaptive lead** (`VrrFollowDetector.OnShown(..., expectedDelayNs)`; the present
  loop passes its `_vrrLeadNs`, the median of the last 256 delays moved 0.1 ms per 64 shows, within 0.3 to 2 ms; `fmp
  verify --vrr` passes each slot's time less its hand-over, which is that lead for a slot handed over on time). A delay
  rising steadily on a display that follows moves the reference with it; a loss is still seen at once (the lead moves
  0.1 ms in the 60 shows the decision needs) and stays one episode (the lead's 2 ms bound keeps half the grid's shows
  over the cutoff even on DP-3). `fmp verify --vrr`, old → new: the recorded loss (`dp3-notify.csv`) "at 3.6 s for
  36.4 s" → **"at 2.6 s for 37.4 s"** (exactly what the live detector said during that run); `dp3-notify2.csv`,
  `tv-share.csv`, `tv-noshare.csv`, `tv-new.csv` and runs A and B: never lost, both.
- **A saved `capture.fps` does not override a typed `--mode`** (`UserSettings.Apply`): the mode's own rate is used and
  the settings line says `capture.fps (--mode given: its own rate)`; without `--mode` the saved rate applies to the saved
  (or default) mode as before, and `--capture-fps` still wins over it.

**Tests added:** `VrrLateGeneratedTests` (Core, 3: the jittered card before/after with every count checked, a dropping
source's long-interval skips, off by default and inert under fixed refresh), `UserSettingsTests.A_saved_capture_rate_
does_not_override_a_mode_typed_on_the_command_line` (Core), `RepeatBeatTests` +2 (Sources.V4l2: the card 16 ppm fast
over 4 h is a card-clock beat with nothing on stderr while 30.000-of-29.97 still advises; irregular content repeats are
counted as not periodic), `VrrFollowTests` +1 (Pipeline: a delay rising 0.93 → 2.3 ms on DP-3 is a loss against the
probe's delay and none against the adaptive lead; a 20 s grid stretch with the lead tracking is still one episode).

### Changes outside the obvious

- `src/FrameMotionPlus.Core/Scheduling/FrameScheduler.cs` `ScheduleVrr` (the generated branch): the only decision change,
  behind `VrrLateGeneratedNs > 0`; the unused `planned` local there removed; `_decisionSkippedLong` reset in `Schedule`.
- `src/FrameMotionPlus.Core/Simulation/SimulatedSource.cs` `Deliver`: the optional arrival tail (test support).
- `src/FrameMotionPlus.Pipeline/PresentLoop.cs` `RequestVrrProbe` and the probe gate in `RunWindow`: a probe can be
  requested again after one finished (a request counter; the present thread resets its probe state on a new request).
- `src/FrameMotionPlus.Vulkan/Windowing/SdlNative.cs` / `SdlWindow.cs`: `SDL_HideCursor`, `SDL_ShowCursor`, the
  input-focus flag (window and pointer only).
- `src/FrameMotionPlus.Sources.V4l2/RepeatBeat.cs`: `RepeatBeat.PeriodicEvents` now means the events that belonged to a
  beat; the old meaning (the agreeing run) is `AgreeingEvents`.
- `src/FrameMotionPlus.App/Cli/VerifyCommand.cs` `Follow`: passes each slot's lead to the detector.

### What remains

- **The card measurement of item 1 over a long session.** The two 10-minute runs were on a calm day (normal-interval
  skips 9 in A, none in B); the owner's afternoon had a heavier arrival tail. To see it on a session like the owner's
  (each with the console on and a game moving; the summary's `missed` line has the split):

      fmp capture --mode 4k30-hdr --audio off --duration-s 1800 --csv a.csv --late-generated-ms 0   # as before
      fmp capture --mode 4k30-hdr --audio off --duration-s 1800 --csv b.csv                          # the default

- The probe retry has not been seen to engage on the hardware (one start of 15 needed it, before it existed).
- The fixed-refresh fallback during a loss: designed (above), not built.
- The host's 90-second USB-delivery spike seen in run A (1.6 ms, at 8.2, 98.3, 188.3 s): not chased (a periodic USB
  poller? NZXT CAM and a Razer helper were running).
- `fmp verify <csv> --vrr` on these runs infers the shortest refresh period as 16.686 ms (the slot spacing) and judges
  fewer misses than the live verifier (1 against 5 and 3): pre-existing, not looked at.

### What `docs/implementation-plan.md` and the design doc should say (not edited)

- Under variable refresh a generated slot whose next real frame comes slightly late is shown late (content moved to
  match) rather than skipped; skips in intervals the source lengthened are inherent and counted apart.
- KWin's automatic variable refresh needs the fullscreen window to be the active one; focus elsewhere (either output)
  is a loss for as long as it lasts.
- The card's own clock makes a residual repeat about every 35 minutes at the console's rate; it is reported, not acted
  on.

### Tests and checks

Whole solution, one assembly at a time (`dotnet test FrameMotionPlus.slnx`, the console on): Core **715** (711 + 4),
Platform 100 + 1 skip, Audio 77, Cuda 144, Vulkan 108, Gpu 129, Vfx 93 + 1 skip, Sources.File 154, Pipeline **163 + 2
skips of 165** (164 + 1; the two skips are `WindowedTests` cases that skip when the compositor takes presents too early
to judge or skips refreshes: the desktop's state, not a failure), Sources.V4l2 **115 + 30 skips of 146** (113 + 2
new; one of the new tests failed in that run on its own expectation of the period's text, "every 35." against the
34.8 min the 16 ppm card makes; corrected, `RepeatBeatTests` 22 of 22 run again). Solution build 0 warnings; `-r
win-x64` build of the app 0 warnings; NativeAOT publish clean. 0 B on every thread and GC 0/0/0 in the measured window
in runs A and B.

## Found by the independent review of the delayed generated slots (2026-11-19)

Approved with no must-fix. A differential run of the committed scheduler against the new one with the option at 0
gave identical records over 14 simulated scenarios (the new unit tests alone do not prove that for variable refresh);
with it on (0.1, 4, 16, 100 ms) nothing was out of order, duplicated, backwards or closer than the shortest refresh
period, and D and latency matched to 0.01 ms. The reviewer's runs with a game moving: on the TV, 180 s, 3 delayed (at
most 1.28 ms), 0 skipped, 0 missed, 46.709 ms (p99 47.200), against 46.728 ms with the option off; on DP-3, 60 s,
2 delayed (at most 1.785 ms), 0 missed: a delayed slot is judged against its moved time, so the 2.08 ms tolerance
there is not used up. The probe retry engaged twice on hardware (the second probe found the display following).
Fixed afterwards: the request count is bumped before "done" is cleared in `RequestVrrProbe` (a retry could be spent
on the previous probe's answer), and the option's limit is 8 ms. Not fixed:

- **At 3x and up, the later slots of a pair do not follow a delayed slot:** only the delayed slot's content moves,
  so the content step after it shrinks (240 Hz 4x: 1.4 ms of content shown 4.1 ms apart; the TV at 4x: 5.7 against
  8.3 ms). Never backwards or duplicated at 8 ms and below. With larger limits in an interval the source made longer
  the next slot became a repeat that was neither shown nor counted as skipped (334 in a 600 s run with 2% drops at
  50 ms), hence the 8 ms limit. Does not arise at 2x.
- **A fullscreen `--clock auto` start on an output that never grants variable refresh** now makes three probes,
  about 1.2 to 1.4 s more (by reading; not reproducible here without changing display settings).
- **The pointer stays hidden for the session over the app's own surface:** it is shown again only by
  `SetFullscreen(false)`, which the session never calls, so if the compositor takes the window out of fullscreen, or
  it loses focus, the pointer is invisible while over the picture (`SdlWindow.cs:241`). It is never hidden elsewhere
  on the desktop and was visible after every exit path tried.
- Not exercised: the keys (Escape, H, A, Q) with the pointer hidden, a crash exit, and a live loss of variable refresh.
- `SimulatedDisplayTests.Variable_refresh_on_a_119_88_Hz_output_with_a_varying_display_delay_is_judged_alike_by_both_
  accounts` failed once in a run of the whole Pipeline test project after the two follow-up fixes and then passed
  9 of 9 (5 alone, 4 with its class). It runs on the real clock; its failure message was not captured. To watch.


## 4x VFG High on the TV: a display behind at its ceiling (2026-11-21)

**The owner's run.** `fmp capture --mode 4k30-hdr --multiplier 4 --quality high`, fullscreen on HDMI-A-1 (119.88 Hz,
variable refresh): 4 × 29.97 = 119.88 slots a second, exactly the TV's ceiling (shortest refresh period 8.342 ms). VFG
Medium is fine there (216 s: 0 missed, 57.2 ms, hand-over to first pixel 2.95 ms); with High (about 6.0 ms a generated
frame, p99 6.4) every frame was shown one refresh late, evenly, for the whole 196 s: 70.2 ms (D 59.1 + about 11 ms from
hand-over to first pixel), all 23,238 slots late and "the display's", intervals 8.342 ms (sd 0.013), "variable refresh
lost at 0.0 s for 195.9 s", and the verifier one slot apart from the live counters (late 23,237 + not shown 1), so FAIL.
3x High is fine (not at the ceiling). An earlier 40 s file run of 4x High on the TV fell two refreshes behind when the
window went fullscreen, regained one when a generated frame happened not to be shown, and stayed exactly one behind.

**Two sessions.** The first (the morning) had no TV: after that day's system update **KWin 6.8.3 throttled any Vulkan
FIFO surface covering HDMI-A-1 to about 28 presents a second, every present completing without a first-pixel time** (a
compositor regression, not the TV or the app); its work was done on DP-3 at 8x (below). The machine then went back to
KWin 6.8.2-2, NVIDIA 620.70.06 and Linux 7.3.4 (the versions of every earlier measurement) and the TV runs below were
made in the afternoon: the real capture path, the console on a moving 30 fps picture (about 30 new frames a second
through the cadence detector), fullscreen on HDMI-A-1 (KWin placement script), `--audio off --no-settings`.

### On the TV

| 4x High, 180 s | Latency p50 (p99) | Hand-over to first pixel p50 | Missed (late / left out) | Intervals sd, max | Frames left out | Start (first shows' lateness) |
| --- | --- | --- | --- | --- | --- | --- |
| `--ceiling-pacing off`, run 1 | 63.77 (65.08) ms | 4.96 ms | 1,152 / 0 | 0.862, 97.3 ms | — | 0.4, −0.1 … then 2.3 within 10 s |
| `--ceiling-pacing off`, run 2 | 63.49 (64.44) ms | 4.61 ms | 0 / 0 | 1.972, 296.1 ms | — | 0.3, 3.1, 3.1 … |
| on, the first build (left out at ¾ of a refresh), run 1 | 63.17 (64.07) ms | 4.32 ms | 37 / 1 | 0.218, 38.7 ms | 1 (124.4 s, regained) | 0.0, 2.6, 2.6 … |
| on, the first build, run 2 | 63.59 (64.84) ms | 4.64 ms | 183 / 0 | 0.609, 97.2 ms | 0 | 0.6, 0.4 … then 2.1 within 10 s |
| **on, final (left out at half a refresh), run 1** | **63.47 (64.29) ms** | 4.61 ms | **0 / 0** | 0.037, 12.4 ms | 0 | 0.7, 0.4, 0.4, 0.4, 3.2 … |
| **on, final, run 2** | **63.70 (64.95) ms** | 4.81 ms | **81 / 1** | 1.985, 298.1 ms | 1 (135.2 s, regained) | 0.5, 0.2 … |

(The long intervals are the game's own pauses: a gap in the source's frames is a gap in the slots.) Every run: D 58.6 to
58.9 ms (arrival lag 27.0 + the generation's 31.6 to 31.8), 0 B on every thread, GC 0/0/0, the live counters and `fmp
verify --vrr` agree. **None of the six fell a whole refresh behind; 4x High on the TV runs at 63.2 to 63.8 ms, not
70.** It is not the 62 the arithmetic suggests because the display takes 4.3 to 5.0 ms (p50) from hand-over to first
pixel, not the 2 to 3 of Medium. How the lateness (first pixel less the slot's time) moves, from the telemetry:

1. **The start is clean**: the first shows come 0.0 to 0.7 ms after their time in every run. Within the first second
   (or within ten) the lateness steps up by about 2.5 ms (to 2 to 3 ms) at one show, with nothing else changing: the GPU
   (the next High generation starts 1.2 to 1.8 ms after the hand-over and takes 6 ms) delays that frame's way to the
   screen. At the ceiling the step is never given back, and later steps add to it.
2. **Then it creeps later, about 1 ms a minute** (0.9 to 1.1 with High in every run, 0.8 with Medium below): the
   console's frames (the card's clock) come about 15 ppm faster than the TV's highest rate allows, so at 4x each show is
   a little later than the one before (slots 8341.53 µs apart over run 1, from the capture timestamps). Nothing in the
   display can undo it.
3. **A pause in the game resets it** (a hold gives the display free refreshes: −0.5 to −0.9 ms after the 97 ms and 296 ms
   gaps), and so does a left-out frame (4.2 → −0.9 ms in the final run 2, 6.4 → −1.1 in the first build's run 1). Without
   either, the lateness passes half a refresh (run 1 with pacing off: the last 20 s, 1,152 slots late at 4.2 ms) and,
   minutes later, a whole refresh: the owner's 70 ms.

**What put the owner's run a whole refresh behind** was not reproduced with the default timing. One change does it at
once: the generation started 0.4 ms later (`scheduler.marginMs` 0.1 instead of 0.5, so High at p50 finishes 0.2 to
0.6 ms before its own hand-over and at p99 about on it): **a refresh behind from the very first shows (10.7, 8.3 ms
late), two at 2.4 s, up to 7 for moments, 74.2 ms, 1,872 hand-overs late**, the recovery's one left-out frame not
helping (the frames late from the GPU, not from a backlog) and so not repeated. So the one way seen here to lose a
whole refresh is a generation finishing at its own hand-over, and the ceiling then keeps it lost; that this is what
happened in the owner's run (High's p99 6.4 there against 6.27 here, or other GPU load) is an inference. The
fullscreen transition is not it (the six starts were clean;
`fmp capture` goes fullscreen 1.5 to 2 s before playback starts). Starting the generation 0.5 ms earlier instead
(`marginMs` 1.0, 90 s): 0 missed, hand-over to first pixel 4.16 ms against 4.61, latency 63.70 ms (D 0.5 ms longer):
no better, so the margin stays.

**What the summary says** (the evening's wording, 4x Medium over 15 minutes): `ceiling  the slots at the output's
ceiling for 107895 decisions (whole run); the display ran about a whole refresh or more behind there (… up to 1 …) for
30 shows in 1 run(s), the longest 0.2 s; …; recovery on: 3 generated frame(s) left out after 30 late shows in a row so
the display could catch up (3 brought the next show half a refresh earlier or on time, 0 did not; each counted above as
a dropped slot)`, and once on stderr `fmp: the display had fallen behind at
its ceiling (every frame shown late, evenly; …): one generated frame was left out so it could`. The "variable refresh
lost" lines still come (the shows sit one shortest period apart at the ceiling, which the follow detector takes for a
grid; 4 to 8 episodes in every run, pacing on or off); a run a whole refresh or more behind (none here) is reported as
running behind instead (`ceiling` line, `follow` line, stderr). So the honest answer: **4x High holds the TV's ceiling
at about 63.5 ms with one generated frame left out every two minutes or so when the game itself does not pause (and
4x Medium at about 58 ms with one every five minutes, below); with pacing off both hold as long as the game pauses often
enough, and otherwise drift into late slots and then a whole refresh behind.**

### On DP-3 (the morning, without the TV)

8x on DP-3 is 239.76 slots a second at a 240.016 Hz ceiling, the same situation 0.1% under it. VFG High (6.3 ms, also at
1440p) cannot run at 8x, so a slow generator was emulated by experiment-only builds (not in the tree): a CUDA kernel
holding all SMs on the generator stream right after each VFG Medium generation (1.31 ms), either counted in d (the
scheduler plans for 3.0 ms: the GPU busy from about 0.6 to 3.6 ms after each hand-over, as High is at 4x on the TV) or
not (the hold then spans the next hand-over). 60 s runs, 4k30-hdr:

| DP-3 | Missed (all the display's) | Hand-over to first pixel p50 | Latency p50 |
| --- | --- | --- | --- |
| 8x Medium (committed binary / the unrun change) | 113 / 37 | — / 1.76 ms | 59.02 / 59.25 ms |
| 8x Medium + 1.7 ms hold spanning the next hand-over | 5,905 | 3.85 ms | 61.13 ms |
| 8x, hold counted (High-like), the unrun change, `--ceiling-pacing off` | **0** | 2.29 ms | 61.37 ms |
| the same, `--ceiling-pacing on` (its generation gate) | **4,263** (12 left out) | 3.18 ms | 62.11 ms |
| the same, the final code | **0** | 2.26 ms | 61.30 ms |

At a ceiling a delay is worked off only at the slots' margin over the shortest period (DP-3 at 8x: 5 µs a slot, the 0.8
to 2 s "lost" episodes of every 8x run are that); GPU work at the hand-over delays the display; **holding generations
for the previous present's first pixel (part 1 of the unrun change) made it worse**: 70% of its 12,587 waits (mean
1.39 ms) ended on the bound, which moved the GPU work onto the next hand-over. The recovery fired on hardware once there:
the GPU 98% busy across every hand-over, the display 3.7 ms late on every frame from contention; one frame left out,
the next show only 1.1 ms earlier, counted as not regained, none other for the rest of the run.

### Ruled out

The generation gate (part 1: removed, above). The fullscreen transition (clean starts). The variable-refresh lead at
its 2.0 ms bound (it is, in every run, but it only places the hand-over). The two-in-flight cap (never the limit: no
back-pressure or backlog skips). The app's own pipeline with the default margin (0 late hand-overs in every default run).

### The fix (`--ceiling-pacing on|off`, on by default)

- `CeilingPacingState` (`src/FrameMotionPlus.Pipeline/CeilingPacing.cs`), its condition set by the pacing thread at each
  write (`PacingLoop.UpdateCeiling`): variable refresh, generating, the slots no more than 1% longer than the shortest
  refresh period (at the ceiling: 4x on the TV, 8x on DP-3), **whatever the generator** (until the evening it also asked
  for a generation over half a slot, which left 4x Medium out; see "Widened to every generator" below). Under fixed
  refresh nothing is computed.
- **Recovery** (present thread, `PresentLoop.Vrr.cs` `VrrTurn`/`OnShownVrr`): after 30 timed shows in a row late (more
  than half a refresh after their slot's time, the accounting's own tolerance), the next generated slot is not
  presented: the image before it stays one refresh longer and the display gets its free refresh. Never a real frame.
  At most one every 5 s; if the next show is neither half a refresh earlier nor on time (within a quarter of a refresh:
  a display really on a fixed grid, or late from the GPU rather than from a backlog), none again until a show is on time.
  A slot given up as late stands it down. Each counts as a dropped slot. The first build of this session left a frame
  out at three quarters of a refresh; the TV showed slots counted late for a minute or more before that (183 in the
  first build's run 2, which never got there), hence half a refresh.
- **Reporting** wherever the slots are at the ceiling, recovery on or off: runs of 30 or more shows at least three
  quarters of a refresh late are counted as the display running behind (how many, up to how many refreshes, the runs,
  the longest), in the `ceiling` line, the `follow` line (those late shows were running behind, not a loss) and stderr
  (`fmp: the display is running a whole refresh behind at its ceiling (every frame shown evenly one refresh late …; not
  an overlay)` instead of "an overlay or another window"). The late shows stay counted as late, the display's (which
  never fails the verdict).
- **Live counters against the verifier**, two fixes in `TelemetryVerifier.Verify`: (1) the owner's one-slot disagreement:
  the run stopped with the last decided slot not yet handed over; with every show more than half a refresh late it fell
  inside "last show plus half a refresh" and was "not shown" where the live accounting did not count it. A slot never
  handed over after the last one handed over is no longer judged (reproduced on DP-3: late 5,433 + not shown 1 against
  5,434 live; now 0 disagreements). (2) Found on the TV (the 0.1 ms margin run, slot 8911): under variable refresh with
  the display 1.5 refreshes behind, a present KWin discarded (no time) had the shows of presents handed over two slots
  earlier near its own time and was judged "refresh taken"; the live accounting judges it by its neighbours in present
  order (unjudged). The verifier now does the same under variable refresh (fixed refresh unchanged). Every TV and DP-3
  CSV of both sessions: 0 disagreements.
- The latency line prints the measured p50 from hand-over to first pixel under variable refresh instead of the 2.000 ms
  bound.

### Unchanged cases on the TV

The committed binary against this change, 90 s each, the same session:

| TV, 90 s | Latency p50 (p99), committed / this change | Missed, committed / this change | `ceiling` line (this change) |
| --- | --- | --- | --- |
| 4x Medium | 57.44 (58.03) / 57.21 (57.84) ms | 0 / 0 | at the ceiling; recovery not yet armed for Medium then (widened below) |
| 3x High | 56.79 (57.12) / 56.75 (57.34) ms | 0 / 0 | never at the output's ceiling |
| 3x Medium | 52.08 (52.42) / 52.16 (52.42) ms | 0 / 0 | never at the output's ceiling |
| 2x Medium | 46.62 (46.96) / 46.67 (46.99) ms | 0 / 0 | never at the output's ceiling |

All variable refresh, every slot shown (119.9, 89.9, 89.9, 59.9 a second), the verifier agreeing; nothing left out.
4x Medium creeps the same way as High (lateness 0.9 → 2.0 ms over the 90 s in both runs, from a start at 0.1 to
1.3 ms: less GPU contention than High's 2.5 ms); without a pause in the game it would pass half a refresh after about four
to five minutes, and the recovery did not act for it then (hence the widening below; the owner's 216 s Medium
run, 2.95 ms from hand-over to first pixel, fits the creep).

On DP-3 (morning): 8x Medium 59.02 / 59.03 ms, 113 / 255 missed (all the display's, in the ceiling's catch-up episodes;
they vary run to run), 2x Medium 45.85 / 45.88 ms, 3 / 0 missed; the recovery inert in both.

### Widened to every generator (evening)

4x Medium on the TV, the setting the owner plays at for hours, creeps the same way (0.9 → 2.0 ms over 90 s above), so
the condition no longer asks for a generation over half a slot: the recovery is armed whenever the slots are at the
output's ceiling under variable refresh (`CeilingPacingState.SetCondition`, `Active`), still inert below a ceiling and
at fixed refresh, `--ceiling-pacing off` still turning it off; trigger, limit and the "did it help" rule unchanged.
Measured with that build, the console on a moving game picture, `--audio off`:

| TV | Latency p50 (p99) | Missed (late / left out) | Frames left out | Lateness per minute (p50), ms |
| --- | --- | --- | --- | --- |
| 4x Medium, 900 s, recovery on | 58.25 (60.09) ms | 300 / 2 (+1 in the warm-up) | 3: at the start, 5 min 43 s, 10 min 37 s; all regained | 0.7, 1.2, 1.9, 2.6, 3.3, 3.8 → left out → 1.1, 1.9, 2.5, 3.3, 3.7 → left out → 1.3, 2.1, 2.8, 3.6 |
| 4x Medium, 900 s, `--ceiling-pacing off` | 57.83 (59.96) ms | 1 / — | — | 0.8, 0.7, 0.9, 1.3, 2.1, 2.0, 1.0, 1.7, 2.5, 3.3, 0.8, 1.4, 2.2, 3.0, 3.8 |
| 4x High, 300 s, recovery on | 63.68 (64.82) ms | 96 / 2 (+1 in the warm-up) | 3: at the start (7.7 ms late), 2 min 16 s, 4 min 30 s; all regained | 2.4, 3.3, 2.3, 3.1, 3.5 (no pause in the game) |

- **The creep, measured over 15 minutes:** 0.6 to 0.7 ms a minute with Medium, from the console's clock against the
  TV's (the run with recovery had no pause in the game at all: it reached half a refresh, 4.17 ms, after 5 min 40 s and
  again 4 min 54 s after the left-out frame). The run with recovery off had ten game pauses of 0.2 to 1.3 s, each
  setting the lateness back to −0.5 to −0.9 ms; it still reached 3.8 ms (p50; one slot late) in its last minute. A
  stretch of play without pauses longer than about five minutes therefore drifts into late frames, and after about ten
  more a whole refresh behind (the owner's 70 ms), unless a frame is left out.
- **The start can be a refresh behind with Medium too:** the recovery run's first show came on time and the next ones
  6.5 ms late (three quarters of a refresh, the display carrying something from the pre-roll); after 30 such shows
  (0.25 s) one generated frame was left out and the next show was 0.1 ms late. This is the owner's "behind from 0.0 s"
  at a smaller size; the runs without it started on time.
- **What the 302 missed were:** the three left-out frames (two in the measured window) and 300 late shows, all within
  4 to 5 ms of their time in the 30 to 40 s before each left-out frame: as the lateness crosses half a refresh, the
  display's jitter puts some shows over it and some under (165 alone, 19 short runs) before 30 come in a row. A rule
  counting 30 late of the last 64 would have left the frame out about a second sooner with about half the late shows
  (85 against 159 before the first); not changed (the trigger was to stay as it was).
- Live counters and `fmp verify --vrr` agree in every run; 0 B on every thread; GC 0/0/0 in the measured window.

**DP-3, 8x Medium, 60 s** (now armed there too; the slots 5 µs longer than the period, so lateness works itself off
slowly): 59.60 ms (p99 60.33), 31 missed: 30 late shows (2.4 ms, over the 2.08 ms tolerance) and one left-out frame at
44.7 s after which the next show came 0.7 ms early, regained; three short "lost" episodes (0.1 to 0.8 s) against 2 to
4 episodes and 37 to 255 late shows in the earlier 60 s runs without it. It left out one frame where a run of 30 late
shows came, and nothing in the rest of the minute.

**Below the ceiling, the TV, 60 s:** 3x Medium 52.07 ms (p99 52.38), 2x Medium 46.65 ms (p99 47.03); 0 missed, the
`ceiling` line "the slots were never at the output's ceiling (--ceiling-pacing on: inert)" (committed binary, 90 s, in
the table above: 52.08 and 46.62 ms).

### Not done

- Taking the remaining 2.5 ms (GPU contention after the hand-over) off: KWin's pass and the app's present work were not
  separated; moving the generation 0.5 ms either way was no better or much worse.
- Correcting the creep at its root (the card's clock against the TV's): the left-out frame handles it, a few minutes
  apart, when the game does not pause.
- Judging a run behind at the ceiling against its shifted time, so real misses inside it stand out.

### Tests

`CeilingPacingTests` (Pipeline, 18 cases): a display a refresh behind at its ceiling catches up with exactly one left-out
generated frame after 30 late shows and is on time after; without the recovery it stays behind and is reported; two
refreshes behind take two, 5 s apart; a source 40 ppm faster than the ceiling (the TV) loses one generated frame each
time it falls half a refresh behind, no more than 30 late shows each time, over 10 minutes; normal play (on time, or 2.5
ms slow with a wobble) never leaves a frame out; a loss of variable refresh on a fixed grid costs one; a display that
keeps falling behind gets one at most every 5 s; the condition for each case (TV 2x, 3x, 4x; DP-3 4x, 6x, 8x; both at
fixed refresh: at the ceiling or below it, whatever the generator) with the option on and off; the `ceiling` line and the
stderr line.
`VerifierTests` +2 (the slot after the last show, never handed over, behind at the ceiling: not judged; behind under
variable refresh, an untimed present judged by its neighbours only, fixed refresh keeping its rule). `UserSettingsTests`
+1 (the option on both commands, its default, a bad value, the help).

**Tests and checks** (afternoon, the TV working, whole solution `dotnet test FrameMotionPlus.slnx` with the console on):
Core **719**, Platform 100 + 1 skip, Audio 76 of 77, Cuda 144, Vulkan 108, Gpu 129, Vfx 94 + 1 skip, Sources.V4l2 116 +
30 skips, Pipeline **198 of 199** (20 new), Sources.File 153 of 154. Not from this change: `OffscreenVerificationTests.
Nothing_is_interpolated_across_a_seek` and the FFmpeg `StructLayoutTests` (both fail on the committed tree since
FFmpeg 9.1 came with that day's update); `PipeWireLoopbackTests` (35 ms target: clicks 100 ms off once; its class run
again: 4 of 4). In the Pipeline project's own run before it, `WindowedTests` failed 3 of 7 with its windows next to the
terminal on the TV (3 late hand-overs and 3 display misses in the first 2 s at fixed refresh); alternating the class
between the committed tree and this change there: committed 7/7 then 5/7, this change 7/7 then 7/7, and 7/7 with the
windows placed on DP-3: the TV's windowed timing, not this change. Build 0 warnings; `-r win-x64` build of the app 0
warnings; NativeAOT publish clean; 0 B on every thread and GC 0/0/0 in the measured window of every TV run.

After the widening (evening): Core **719**, Pipeline **198 of 199** (only `Nothing_is_interpolated_across_a_seek`, the
FFmpeg 9.1 failure); build 0 warnings, NativeAOT publish clean.

### What `docs/implementation-plan.md` and the design doc should say (not edited)

- Under variable refresh with the slots at the output's ceiling (4x 29.97 on 119.88 Hz) a display cannot recover lost
  time by itself, and a capture card's clock a few tens of ppm faster than the output's highest rate makes it lose time
  steadily; one generated slot is left out when the shows have been late for 30 in a row (any generator), and running
  behind is reported apart from a loss of variable refresh.
- A generation that finishes at its own hand-over loses a whole refresh at a ceiling; GPU work right after a hand-over
  costs a few milliseconds of display delay under KWin; holding generations for the previous first pixel makes it worse.

### Found by the independent review of the ceiling recovery (2026-11-21)

Approved with no must-fix. The reviewer's own runs on the TV with the console: 4x Medium 180 s, 58.46 ms (p99 59.61),
none missed, nothing left out (lateness crept from 1.7 to 3.6 ms, short of the trigger); 3x Medium 52.13 ms and
3x High 56.74 ms, inert; one 4x Medium run started a refresh behind (7.2 ms late) and generated slot 33 was left out,
the next show 0.2 ms early. Every left-out slot inspected (eight) was a generated one; `fmp verify --vrr` gives the
same answers as the committed build on 22 of 24 recorded CSVs and differs only on the two behind-at-ceiling runs,
where the disagreements go to 0. Fixed afterwards: an armed recovery is stood down when a show comes back within half
a refresh before the next generated hand-over (it would have left one frame out needlessly). Not fixed:

- **No deterministic test reaches the present loop's wiring.** Removing the "generated slots only" guard
  (`PresentLoop.Vrr.cs:161`) and making `OnDroppedLate` a no-op (`:156`) still passes every test: the unit tests
  apply that rule in their own display model, and no session-level test runs at a ceiling (the simulated-display
  tests use 2x). To add: a simulated session at 4x on a 119.88 Hz display with a one-off 9 ms delay, asserting one
  dropped slot, that it is generated, and on-time shows after it.
- **"Variable refresh lost" is printed in healthy long 4x runs** (the reviewer's run: none missed, yet "lost 2x for a
  total of 120.2 s" and the stderr line about an overlay): at a ceiling, shows sit on the refresh grid by
  construction, which is what the follow detector looks for. When the slots are at the ceiling and no show inside
  the episode is late it should say "at its ceiling", not "lost".
- **A loss of variable refresh can cost more than one frame in one phase band** (simulated, not seen on hardware):
  with the grid within jitter of the latch point a real backlog forms, a left-out frame helps, and it slips again:
  seven frames, one every 5 s for 30 s, in 3 of 80 simulated cases; 0 to 2 in the rest.
- **About 100 slots are counted late before each left-out frame** (the trigger's threshold equals the lateness
  tolerance): invisible, but an earlier threshold would avoid them.
- Not exercised on hardware by the review: a genuine loss of variable refresh, a quality switch, an HDR/SDR switch
  and 4x High (the author's CSVs and reading stand for those).

