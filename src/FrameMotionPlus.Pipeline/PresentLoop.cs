using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Collections;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Time;
using FrameMotionPlus.Vulkan;

namespace FrameMotionPlus.Pipeline;

/// <summary>What a test sees of each present in the virtual-clock mode (present thread, right after it was accepted).</summary>
public readonly record struct PresentedFrame
{
    public long SlotIndex { get; init; }

    /// <summary>The scheduler's decision for it: Real, Generated or Repeat.</summary>
    public Core.Scheduling.SlotKind Kind { get; init; }

    public long FrameA { get; init; }

    public long FrameB { get; init; }

    public double Timestep { get; init; }

    /// <summary>The content time shown (the frame's, or τ for a generated frame).</summary>
    public long ContentNs { get; init; }

    public long ContentA { get; init; }

    public long ContentB { get; init; }

    public long TargetNs { get; init; }

    public ulong Write { get; init; }

    /// <summary>FrameB follows a discontinuity (a loop, seek or timestamp jump).</summary>
    public bool BreakBeforeB { get; init; }
}

/// <summary>
/// The present thread: owns the presenter (every call into it), shows each slot at its refresh and turns present
/// feedback into the display's refresh grid for the pacing thread.
/// <para>
/// <b>Window (FIFO, fixed refresh).</b> This surface cannot be asked for a present time, only told when a present was
/// shown (<c>present_wait2</c>), and FIFO shows one queued present per refresh in order. So the thread keeps the FIFO
/// fed with exactly one present per refresh, at most two in flight: the slot decided for a refresh is submitted once the
/// present before it is queued; a scheduler Repeat is presented as the last image again; when nothing is ready for a
/// refresh shortly before its latch, the last image is put in as a filler (counted: the pipeline was late). It predicts
/// which refresh each submission lands on (the last one in flight plus one), checks it against the report, and when a
/// present shows later than predicted (the compositor skipped a refresh, or a present came too late), every present
/// behind it in the FIFO is late by as much: with two queued, KWin gives the refresh back itself (it discards one), so new
/// presents keep their own refresh; otherwise, or when it did not, the thread drops a slot to get back on time: later
/// content on time, never a queue that plays behind. Before playback it measures the compositor's latch (the present
/// lead starts from it) or, fullscreen, whether the display follows the presents (variable refresh).
/// </para>
/// <para>
/// <b>Window, variable refresh.</b> Each slot is handed over at its own time (the scheduler's V), the display refreshing
/// for it; no grid, no fillers; repeats are not presented.
/// </para>
/// <para>
/// <b>Offscreen, real clock.</b> No refreshes: each slot is submitted just before its time and counts as shown when
/// its GPU work completes. <b>Offscreen, virtual clock.</b> Each slot is submitted as soon as the presenter takes it
/// (waiting if it must), in order, and a test may read it back right then.
/// </para>
/// Allocation-free per present.
/// </summary>
internal sealed partial class PresentLoop : IDisposable
{
    private const long PollNs = 250_000;
    private const long OffscreenLeadNs = 300_000;
    private const long FillerGraceNs = 250_000;
    private const long EmptyQueueGuardNs = 250_000;
    private const int InFlightCapacity = 32;

    /// <summary>How long misses after a change of the output's refresh rate count as the change's.</summary>
    private const long RateChangeWindowNs = 2 * Nanoseconds.PerSecond;

    private readonly VulkanPresenter _presenter;
    private readonly DisplayKind _display;
    private readonly PlaybackClock _clock;
    private readonly long _latchLeadNs;
    private readonly long _stallTimeoutNs;
    private readonly SpscRing<PresentJob> _jobs;
    private readonly SpscRing<PresentRecord> _records;
    private readonly SpscRing<DisplayUpdate> _displayUpdates = new(64);
    // An AutoResetEvent, not a ManualResetEventSlim: under NativeAOT the slim event's Monitor-based slow path allocates
    // lazily created state the first time a thread contends for it, which can be at any point of a long run (seen in the
    // 10-minute gate: 120 B, once, on the generator and decode threads). The wait subsystem's event allocates nothing after a thread's first
    // wait. A Set while nobody waits leaves it signalled, so a wake-up between the check and the wait is never lost.
    private readonly AutoResetEvent _wake = new(false);
    private readonly DisplayGrid _grid;
    private readonly InFlight[] _inFlight = new InFlight[InFlightCapacity];
    private int _inFlightHead;
    private int _inFlightCount;
    private long _lastPredictedNs = long.MinValue;
    private long _lastShownNs;
    private long _lastTimedNs;
    private int _untimedSinceTimed;
    private long _presentId;
    private PresentRequest _lastRequest;
    private bool _haveLast;
    private PresentJob _pending;
    private bool _havePending;
    private long _sentAnchorNs = long.MinValue;
    private FrameRate _sentRate;
    private volatile bool _stop;
    private volatile bool _fillersOn;
    private int _showsSinceRateKnown;
    private readonly MissAccounting _accounting = new();
    private readonly LeadController _lead;
    private readonly long _configuredLeadNs;
    private readonly double _leadRefreshes;
    private FrameRate _knownRate;
    private bool _forceGrid;
    private bool _lateRealOutstanding;
    private long _pendingSinceNs;
    private readonly LatchEstimator _latch = new();
    private long _earlyShows;
    private MissKind _staleCause = MissKind.None;
    private bool _lastWasLateReal;
    private int _gridSeq;
    private long _sentRateNum;
    private long _sentRateDen;
    private long _sentLeadNs;
    private long _currentLeadNs;
    private long _untimedShows;

    // Catching up after a late show. KWin gives a lost refresh back by itself: of two presents ready at a latch after a
    // late show it shows the newer and discards the older (its report then comes without a first-pixel time); measured in
    // every late show of the Phase 4b and 5 runs that had them. Dropping a slot as well then put the FIFO a refresh ahead,
    // and the next two slots were shown a refresh early (the Phase 5 review's 180 s 4K run). So after a late show the
    // presents already queued are taken to be behind, but new ones are submitted for their own refresh (the compositor is
    // expected to give the refresh back), and the present thread drops slots to catch up only once a present submitted
    // after the late show is late too (it did not). Each late show starts over expecting the compositor.
    private int _expectedDiscards;
    private MissKind _expectedDiscardCause = MissKind.CatchUp;
    private long _lastLateNs = long.MinValue;

    // The variable-refresh probe before playback, and the mode.
    private bool _vrrProbeRequested;
    private int _vrrProbeRequests;
    private int _vrrProbeStarted;
    private bool _vrrProbeDone;
    private long _vrrProbeBudgetNs;
    private long _vrrProbeUntilNs = long.MinValue;
    private long _vrrProbeNextNs = long.MinValue;
    private int _vrrProbeCount;
    private readonly long[] _vrrProbeSubmit = new long[64];
    private readonly long[] _vrrProbeShown = new long[64];
    private VrrProbeResult? _vrrProbeResult;
    private bool _vrrRequested;
    private bool _vrr;
    private long _vrrLeadNs;
    private VrrFollowDetector? _vrrFollow;
    private readonly long[] _vrrDelays = new long[256];
    private int _vrrDelayCount;
    private int _vrrDelayHead;

    /// <summary>
    /// Whether to expect the compositor to give back a refresh lost to a late show. Seen only with two presents queued (a
    /// lead of a refresh or more: the Phase 4b runs at 240 Hz and the Phase 5 review's at 119.88 Hz, every time); with one
    /// (a lead under a refresh, the measured latch at 119.88 Hz) KWin stayed a refresh behind.
    /// </summary>
    private bool ExpectGiveBack(long periodNs) => _lead.LeadNs >= periodNs;

    // The latch sweep before playback (RequestLatchSweep).
    private bool _sweepRequested;
    private bool _sweepDone;
    private long _sweepBudgetNs;
    private long _sweepUntilNs = long.MinValue;
    private long _sweepXNs;
    private long _sweepAtNs = long.MinValue;
    private long _sweepRefreshNs;
    private bool _sweepOut;
    private int _sweepProbes;
    private int _sweepMade;
    private int _sweepLate;
    private int _sweepEarly;
    private int _sweepReversals;
    private int _sweepLastStep;
    private long _sweepLateMax = long.MinValue;
    private long _sweepMadeMin = long.MaxValue;
    private LatchSweepResult? _sweepResult;
    private long _compositorCatchUps;
    private long _ownCatchUps;

    // Counters (this thread writes; others read snapshots).
    private long _shown;
    private long _fillers;
    private long _droppedLate;
    private long _droppedRefused;
    private long _refusals;
    private long _lateShows;
    private long _rateChanges;
    private long _maxInFlight;
    private long _recordsDropped;
    private long _timedShows;
    private readonly long[] _stallAt = new long[64];
    private readonly long[] _stallNs = new long[64];
    private readonly byte[] _stallWhat = new byte[64];
    private int _stalls;
    private long _turnStart;

    public PresentLoop(
        VulkanPresenter presenter,
        DisplayKind display,
        PlaybackClock clock,
        FrameRate initialRate,
        long latchLeadNs,
        long stallTimeoutNs,
        int jobCapacity,
        int recordCapacity,
        long presentLeadNs = -1,
        double leadRefreshes = 1.5,
        bool adaptiveLead = true)
    {
        _presenter = presenter;
        _display = display;
        _clock = clock;
        _latchLeadNs = latchLeadNs;
        _stallTimeoutNs = stallTimeoutNs;
        _jobs = new SpscRing<PresentJob>(jobCapacity);
        _records = new SpscRing<PresentRecord>(recordCapacity);
        _grid = new DisplayGrid(initialRate);
        _configuredLeadNs = presentLeadNs;
        _leadRefreshes = leadRefreshes;
        var window = display == DisplayKind.Window && clock == PlaybackClock.Real;
        _lead = new LeadController(window ? BaseLead(initialRate) : 0, window ? MaxLead(initialRate) : 0, window && adaptiveLead);
        _currentLeadNs = _lead.LeadNs;
    }

    /// <summary>Jobs → this thread (one producer at a time: the pre-roll, then the pacing thread).</summary>
    public SpscRing<PresentJob> Jobs => _jobs;

    /// <summary>Present records → collector.</summary>
    public SpscRing<PresentRecord> Records => _records;

    /// <summary>The refresh grid → pacing thread.</summary>
    public SpscRing<DisplayUpdate> DisplayUpdates => _displayUpdates;

    public ThreadMeter Meter { get; } = new();

    /// <summary>Test hook, present thread: before each job is handled (stalls, allocation injection).</summary>
    public Action<PresentJob>? BeforeJob { get; set; }

    /// <summary>The slots at the output's ceiling: reporting, and the recovery of <see cref="PlaybackOptions.CeilingPacing"/> (null: neither).</summary>
    public CeilingPacingState? CeilingPacing { get; init; }

    /// <summary>Test hook, present thread, virtual clock only: after each present is accepted (read it back here).</summary>
    public Action<PresentedFrame>? OnPresented { get; set; }

    /// <summary>Test hook, present thread: a ring write's present for which it returns true is dropped (as refused).</summary>
    public Func<PresentJob, bool>? DropWhen { get; set; }

    /// <summary>The present lead now (windowed; 0 offscreen): how long before its refresh a slot's content must be ready. Any thread.</summary>
    public long LeadNs => Volatile.Read(ref _currentLeadNs);

    /// <summary>Times the lead went up because a present's content missed the compositor's latch.</summary>
    public long LeadRaises => _lead.Raises;

    /// <summary>The largest present lead used, ns.</summary>
    public long PeakLeadNs => _lead.PeakLeadNs;

    /// <summary>Every change of the present lead (after the run: the present thread has stopped).</summary>
    public void CopyLeadChanges(List<(long AtNs, long LeadNs, LeadChange Why)> into)
    {
        for (var i = 0; i < _lead.ChangeCount; i++)
        {
            into.Add(_lead.Change(i));
        }
    }

    /// <summary>Shows reported without a first-pixel time although the surface reports them.</summary>
    public long UntimedShows => Volatile.Read(ref _untimedShows);

    /// <summary>Shows a refresh (or more) earlier than predicted (the FIFO was ahead; corrected at once).</summary>
    public long EarlyShows => Volatile.Read(ref _earlyShows);

    /// <summary>The latch measured before playback (<see cref="RequestLatchSweep"/>); null until it is done or when it was not run.</summary>
    public LatchSweepResult? LatchSweep => Volatile.Read(ref _sweepResult);

    /// <summary>The sweep asked for has finished (successfully or not).</summary>
    public bool LatchSweepDone => Volatile.Read(ref _sweepDone);

    /// <summary>
    /// Before playback (the pre-roll is shown and nothing else is queued): measures how far ahead of a refresh the
    /// compositor takes a present, for at most <paramref name="durationNs"/>, and starts the present lead from it (the
    /// latest hand-over that missed its refresh, plus <see cref="SweepMarginNs"/>). Windowed only; any thread.
    /// </summary>
    public void RequestLatchSweep(long durationNs)
    {
        Volatile.Write(ref _sweepBudgetNs, durationNs);
        Volatile.Write(ref _sweepRequested, true);
        _wake.Set();
    }

    /// <summary>
    /// What the lead adds to the latch measured before playback: a present is handed over up to a present-thread turn (0.25
    /// ms) after its content deadline; the latch moves with the compositor's load (measured: it came 0.3 to 1 ms earlier
    /// once playback was under way than over the pre-roll's black frames); and a present or pacing thread is now and then
    /// woken a millisecond or two late on a busy desktop (Phase 2: 1 to 4% of wakes under heavy load).
    /// </summary>
    public const long SweepMarginNs = 2 * Nanoseconds.PerMillisecond;

    /// <summary>
    /// Before playback (fullscreen, the pre-roll shown, nothing else queued): presents the last pre-roll image at off-grid
    /// intervals (1.37 and 1.71 refreshes in turn) into an empty queue and looks at where the first pixels land: following
    /// the presents (variable refresh) or on the refresh grid. At most <paramref name="durationNs"/>. Any thread.
    /// </summary>
    public void RequestVrrProbe(long durationNs)
    {
        // Again after a probe that finished: the present thread starts afresh when it sees the new request.
        Volatile.Write(ref _vrrProbeBudgetNs, durationNs);
        // The request count first: a present thread that ran between the two writes would otherwise step the previous
        // (expired) probe with "done" cleared and finish at once with the old answer.
        Volatile.Write(ref _vrrProbeRequests, _vrrProbeRequests + 1);
        Volatile.Write(ref _vrrProbeDone, false);
        Volatile.Write(ref _vrrProbeRequested, true);
        _wake.Set();
    }

    public bool VrrProbeDone => Volatile.Read(ref _vrrProbeDone);

    public VrrProbeResult? VrrProbe => Volatile.Read(ref _vrrProbeResult);

    /// <summary>
    /// From now on (before playback) slots are presented as variable refresh wants them: each handed over at its slot's
    /// time, no refresh grid, no fillers, repeats not presented (the display holds the last image), and a slot shown more
    /// than half the output's shortest refresh period away from its time is missed. <paramref name="expectedDelayNs"/> is
    /// how long after its hand-over a present's first pixel comes (measured by the probe): the present lead in this mode.
    /// </summary>
    public void EnterVrr(long expectedDelayNs)
    {
        Volatile.Write(ref _vrrLeadNs, Math.Clamp(expectedDelayNs, 300 * Nanoseconds.PerMicrosecond, 2 * Nanoseconds.PerMillisecond));
        Volatile.Write(ref _vrrFollow, new VrrFollowDetector(expectedDelayNs));
        Volatile.Write(ref _vrrRequested, true);
        _wake.Set();
    }

    /// <summary>Presenting for variable refresh (<see cref="EnterVrr"/> applied).</summary>
    public bool IsVrr => Volatile.Read(ref _vrr);

    /// <summary>Under variable refresh: whether the display follows the presents, episodes where it did not (read the
    /// volatile members from any thread; <see cref="VrrFollowDetector.Finish"/> once the present thread has stopped).</summary>
    public VrrFollowDetector? VrrFollow => Volatile.Read(ref _vrrFollow);

    /// <summary>Late shows after which the compositor gave the refresh back itself (it discarded a queued present).</summary>
    public long CompositorCatchUps => Volatile.Read(ref _compositorCatchUps);

    /// <summary>Late shows after which the present thread had to give refreshes back (the compositor did not).</summary>
    public long OwnCatchUps => Volatile.Read(ref _ownCatchUps);

    /// <summary>The compositor's latch as measured: at least this far ahead of a refresh, ns (any thread; a snapshot).</summary>
    public long LatchLowNs => Volatile.Read(ref _latchLowSnapshot);

    private long _latchLowSnapshot;

    /// <summary>The window's refresh grid is known: the rate is the output's and shows have been seen on it.</summary>
    public bool GridReady => Volatile.Read(ref _showsSinceRateKnown) >= 16;

    public long Shown => Volatile.Read(ref _shown);

    public long Fillers => Volatile.Read(ref _fillers);

    public long DroppedLate => Volatile.Read(ref _droppedLate);

    public long DroppedRefused => Volatile.Read(ref _droppedRefused);

    public long Refusals => Volatile.Read(ref _refusals);

    public long LateShows => Volatile.Read(ref _lateShows);

    public long RateChanges => Volatile.Read(ref _rateChanges);

    public long MaxInFlight => Volatile.Read(ref _maxInFlight);

    public long RecordsDropped => Volatile.Read(ref _recordsDropped);

    /// <summary>
    /// Calls on this thread that took over 2 ms (diagnostics: 1 feedback, 2 readiness, 3 present, 4 wait, 5 loop turn),
    /// the first 64.
    /// </summary>
    public string Stalls
    {
        get
        {
            var n = Math.Min(Volatile.Read(ref _stalls), _stallAt.Length);
            var parts = new string[n];
            for (var i = 0; i < n; i++)
            {
                parts[i] = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_stallWhat[i]}:{_stallNs[i] / 1e6:0.00}ms@{_stallAt[i]}");
            }

            return string.Join(' ', parts);
        }
    }

    private void Stall(byte what, long startNs, long endNs)
    {
        if (endNs - startNs > 2_000_000 && _stalls < _stallAt.Length)
        {
            _stallAt[_stalls] = startNs;
            _stallNs[_stalls] = endNs - startNs;
            _stallWhat[_stalls] = what;
            Volatile.Write(ref _stalls, _stalls + 1);
        }
    }

    /// <summary>Shows whose time is the first pixel's (present timing) rather than the report's.</summary>
    public long TimedShows => Volatile.Read(ref _timedShows);

    /// <summary>Presents submitted and not yet reported shown (diagnostics).</summary>
    public int InFlightCount => Volatile.Read(ref _inFlightCount);

    public bool HasPending => Volatile.Read(ref _havePending);

    /// <summary>
    /// The grid and lead as last sent to the pacing thread (any thread; a consistent snapshot: the present thread writes
    /// them under a sequence count, and a reader retries while one is being written).
    /// </summary>
    public (long AnchorNs, FrameRate Rate, long LeadNs) Grid
    {
        get
        {
            while (true)
            {
                var before = Volatile.Read(ref _gridSeq);
                if ((before & 1) == 0)
                {
                    var anchor = Volatile.Read(ref _sentAnchorNs);
                    var num = Volatile.Read(ref _sentRateNum);
                    var den = Volatile.Read(ref _sentRateDen);
                    var lead = Volatile.Read(ref _sentLeadNs);
                    if (Volatile.Read(ref _gridSeq) == before)
                    {
                        return (anchor, num > 0 && den > 0 ? new FrameRate(num, den) : default, lead);
                    }
                }

                Thread.SpinWait(1);
            }
        }
    }

    /// <summary>Lets the thread put fillers in (from the start of playback; the pre-roll shows its own frames).</summary>
    public void EnableFillers() => _fillersOn = true;

    /// <summary>
    /// Sends the grid and lead again on the next turn (the session drains the pre-roll's updates, which nobody read and which
    /// may have filled the queue, before the pacing thread starts: it must begin from the current ones). Any thread.
    /// </summary>
    public void ResendGrid()
    {
        Volatile.Write(ref _resendGrid, true);
        _wake.Set();
    }

    /// <summary>Grid updates sent so far (it changes when one has gone out). Any thread.</summary>
    public int GridSends => Volatile.Read(ref _gridSeq);

    private bool _resendGrid;

    public void Wake() => _wake.Set();

    public void RequestStop()
    {
        _stop = true;
        _wake.Set();
    }

    public void Run()
    {
        PreRoll();
        switch (_clock == PlaybackClock.Virtual ? 2 : _display == DisplayKind.Window ? 0 : 1)
        {
            case 0:
                RunWindow();
                break;
            case 1:
                RunOffscreen();
                break;
            default:
                RunVirtual();
                break;
        }

        ReleaseUnpresented();
        Meter.Stop();
    }

    /// <summary>
    /// Stopping (the pacing and generator threads have stopped): the ring writes still queued here will not be shown.
    /// Released like dropped ones, so the bridge drains at teardown.
    /// </summary>
    private void ReleaseUnpresented()
    {
        var haveLast = false;
        var last = default(PresentJob);
        if (_havePending && _pending.Kind != PresentKind.Repeat)
        {
            last = _pending;
            haveLast = true;
        }

        _havePending = false;
        while (_jobs.TryDequeue(out var job))
        {
            if (job.Kind != PresentKind.Repeat)
            {
                last = job;
                haveLast = true;
            }
        }

        if (haveLast && last.Write > _presenter.LastReleasedWrite)
        {
            _presenter.Release(last.Request);
        }
    }

    private bool DroppedByHook(long now)
    {
        if (DropWhen is not { } drop || _pending.Kind != PresentKind.Write || !drop(_pending))
        {
            return false;
        }

        Drop(_pending, now, _pending.TargetNs, PresentOutcome.DroppedRefused);
        return true;
    }

    public void Dispose() => _wake.Dispose();

    private void PreRoll()
    {
        _ = _wake.WaitOne(1); // the thread's first wait sets up its wait state (allocates once)
        _ = Monotonic.NowNs;
        _ = ThreadCpuClock.CurrentThreadNs;
        _ = _presenter.IsReadyForPresent();
        _ = _presenter.TryGetFeedback(out _);
        _ = _presenter.Timing;
        _ = _presenter.IsTimingKnown;
        DeadlineWaiter.Sleep.WaitUntil(Monotonic.NowNs + 100_000);
    }

    private void RunWindow()
    {
        var waiter = DeadlineWaiter.Sleep;
        while (!_stop)
        {
            Meter.Tick();
            var now = Monotonic.NowNs;
            if (_turnStart != 0)
            {
                Stall(5, _turnStart, now);
            }

            _turnStart = now;
            PollFeedback(now, offscreen: false);
            CheckRate(now);
            PollReady(now);
            _latch.Expire(now);
            Volatile.Write(ref _latchLowSnapshot, _latch.LowNs);
            if (_lead.Tick(now))
            {
                _forceGrid = true;
            }

            if (Volatile.Read(ref _resendGrid))
            {
                Volatile.Write(ref _resendGrid, false);
                _forceGrid = true;
            }

            if (!_vrr && Volatile.Read(ref _vrrRequested))
            {
                _vrr = true;
                Volatile.Write(ref _currentLeadNs, _vrrLeadNs);
                _forceGrid = true;
            }

            SendGrid(now);
            if (Volatile.Read(ref _vrrProbeRequested) && Volatile.Read(ref _vrrProbeRequests) != _vrrProbeStarted)
            {
                _vrrProbeStarted = Volatile.Read(ref _vrrProbeRequests);
                _vrrProbeUntilNs = long.MinValue;
                _vrrProbeCount = 0;
            }

            if (Volatile.Read(ref _vrrProbeRequested) && !Volatile.Read(ref _vrrProbeDone) && !_havePending && _jobs.IsEmpty)
            {
                waiter.WaitUntil(VrrProbeStep(now));
                continue;
            }

            if (Volatile.Read(ref _sweepRequested) && !_sweepDone && !_havePending && _jobs.IsEmpty)
            {
                waiter.WaitUntil(SweepStep(now));
                continue;
            }

            if (_vrr)
            {
                VrrTurn(now, waiter);
                continue;
            }

            if (!_havePending)
            {
                _havePending = _jobs.TryDequeue(out _pending);
                if (_havePending)
                {
                    BeforeJob?.Invoke(_pending);
                    now = Monotonic.NowNs;
                    _pendingSinceNs = now;
                    if (DroppedByHook(now))
                    {
                        continue;
                    }
                }
            }

            var period = (long)_grid.PeriodNs;
            if (_havePending)
            {
                if (_pending.Kind == PresentKind.PreRoll || !_grid.HasPhase)
                {
                    // The pre-roll (and anything before the first show): as soon as the presenter takes it.
                    if (Submit(_pending, now, PredictNext(now), PresentOutcome.Shown, 0, default))
                    {
                        _havePending = false;
                    }
                    else
                    {
                        waiter.WaitUntil(now + PollNs);
                    }

                    continue;
                }

                var predicted = PredictNext(now);
                var target = _pending.TargetNs;
                var lateBy = period > 0 ? (predicted - target + (period / 2)) / period : 0;
                if (lateBy >= 1)
                {
                    // Out of reach as soon as it came, with nothing queued ahead: the compositor's latch (measured) is further
                    // ahead of the refresh than the slot was decided. The lead goes up (else, with every such slot given up
                    // and none shown late, nothing would ever raise it).
                    if (!FifoBusy(now) && now - _pendingSinceNs <= 2 * PollNs && _lead.OnLateContent(now))
                    {
                        _forceGrid = true;
                    }

                    // Its own refresh is out of reach (the FIFO is behind, or it came late). Catch up without losing a new
                    // real frame where a repeat or a generated frame can be given up instead.
                    var action = CatchUpPolicy.Decide(_pending.Kind, _pending.SlotKind, lateBy, _lastWasLateReal, _lateRealOutstanding);
                    if (action == CatchUpAction.SubmitLate)
                    {
                        var cause = _accounting.OnCatchUp(now, givesBackARefresh: false, shownLate: true);
                        if (Submit(_pending, now, predicted, PresentOutcome.ShownLate, (int)lateBy, cause))
                        {
                            _havePending = false;
                            _lateRealOutstanding = true;
                            _lastWasLateReal = true;
                        }
                        else
                        {
                            waiter.WaitUntil(now + PollNs);
                        }

                        continue;
                    }

                    Drop(_pending, now, predicted, action == CatchUpAction.DropAbsorbed ? PresentOutcome.DroppedAbsorbed : PresentOutcome.DroppedLate);
                    _lastWasLateReal = false;
                    continue;
                }

                if (target > predicted + (period / 2))
                {
                    if (FifoBusy(now))
                    {
                        // A refresh before this slot's has nothing (the scheduler skipped it): the last image again, so
                        // the FIFO stays one present per refresh and this slot lands on its own refresh.
                        if (!SubmitFiller(now, predicted))
                        {
                            waiter.WaitUntil(now + PollNs);
                        }

                        continue;
                    }

                    // Nothing queued: a present handed over now could be taken for a refresh before the slot's and show
                    // early. Wait until the latch of the refresh before the slot's has certainly passed (the measured
                    // latch's lower bound): from then on it can only be taken for its own refresh or a later one.
                    var at = target - period - EarliestLatchNs + EmptyQueueGuardNs;
                    waiter.WaitUntil(Math.Min(at, now + (2 * PollNs)));
                    continue;
                }

                if (Submit(_pending, now, predicted, PresentOutcome.Shown, 0, default))
                {
                    _havePending = false;
                    _accounting.OnOnTime();
                    _lateRealOutstanding = false;
                    _lastWasLateReal = false;
                }
                else
                {
                    waiter.WaitUntil(now + PollNs);
                }

                continue;
            }

            if (_fillersOn && _haveLast && _grid.HasPhase)
            {
                // Nothing for the next refresh yet: a quarter millisecond after its hand-over deadline (the present lead,
                // which has been making its refreshes on this output), keep the FIFO fed with the last image. Not before
                // it: the slot's own job is decided only the scheduler's margin (0.5 ms) ahead of the lead, so a pacing
                // thread waking a few hundred microseconds late would lose its refresh to a filler (found at 240 Hz: the
                // slot then shown a refresh late). A filler that late still makes its refresh while the lead is above the
                // latch, which a late show restores (the lead goes up a millisecond).
                var predicted = PredictNext(now);
                var fillAt = predicted - HandoverDeadlineNs + FillerGraceNs;
                if (now >= fillAt)
                {
                    if (!SubmitFiller(now, predicted))
                    {
                        waiter.WaitUntil(now + PollNs);
                    }

                    continue;
                }

                WaitForJob(Math.Min(fillAt, now + (_inFlightCount > 0 ? PollNs : IdleWaitNs)));
                continue;
            }

            WaitForJob(now + (_inFlightCount > 0 ? PollNs : IdleWaitNs));
        }
    }

    /// <summary>
    /// When a slot's present is due at the latest (fillers go in for a refresh nothing has come for by then): its content
    /// deadline, the present lead. Not the compositor's latch: a present handed over that early into an empty queue could
    /// be taken for the refresh before its own (see <see cref="EarliestLatchNs"/>). Windowed only.
    /// </summary>
    private long HandoverDeadlineNs => Math.Max(_latchLeadNs, _lead.LeadNs);

    /// <summary>
    /// The compositor's latch at the earliest (the measured lower bound less a margin; 0 without evidence): a present
    /// handed over now may be taken for the first refresh at least this far ahead. Offscreen: the configured latch.
    /// </summary>
    private long EarliestLatchNs => _display == DisplayKind.Window && _clock == PlaybackClock.Real
        ? Math.Max(0, Math.Max(_latch.LowNs, _sweepResult is { Bracketed: true } sweep ? sweep.LateMaxNs : 0) - LatchMarginNs)
        : _latchLeadNs;

    private const long LatchMarginNs = 250 * Nanoseconds.PerMicrosecond;

    /// <summary>Presents are queued ahead of the next refresh (the last one submitted is not yet due): FIFO order then decides where a new one lands.</summary>
    private bool FifoBusy(long now) => _inFlightCount > 0 && _lastPredictedNs != long.MinValue && _lastPredictedNs >= now - (long)(_grid.PeriodNs / 2);

    /// <summary>
    /// Notes when the content of in-flight writes is first seen written (one read of the ring's <em>written</em> counter
    /// per turn while any is outstanding): the "content ready" fact the miss accounting and the lead use. Host clock;
    /// at most a loop turn (≤ 250 µs) late, which only makes a hand-over look later (the conservative direction).
    /// </summary>
    private void PollReady(long now)
    {
        var any = false;
        for (var i = 0; i < _inFlightCount; i++)
        {
            ref readonly var e = ref _inFlight[(_inFlightHead + i) % InFlightCapacity];
            if (e.ReadyNs == 0 && e.Write != 0)
            {
                any = true;
                break;
            }
        }

        if (!any || _presenter.Ring is not { } ring)
        {
            return;
        }

        var written = ring.ReadWrittenValue();
        for (var i = 0; i < _inFlightCount; i++)
        {
            ref var e = ref _inFlight[(_inFlightHead + i) % InFlightCapacity];
            if (e.ReadyNs == 0 && e.Write != 0 && e.Write <= written)
            {
                e = e with { ReadyNs = now };
            }
        }
    }

    private void RunOffscreen()
    {
        var waiter = DeadlineWaiter.Sleep;
        while (!_stop)
        {
            Meter.Tick();
            var now = Monotonic.NowNs;
            PollFeedback(now, offscreen: true);
            if (!_havePending)
            {
                _havePending = _jobs.TryDequeue(out _pending);
                if (_havePending)
                {
                    BeforeJob?.Invoke(_pending);
                    now = Monotonic.NowNs;
                    if (DroppedByHook(now))
                    {
                        continue;
                    }
                }
            }

            if (!_havePending)
            {
                WaitForJob(now + (_inFlightCount > 0 ? PollNs : IdleWaitNs));
                continue;
            }

            var period = (long)_grid.PeriodNs;
            var target = _pending.TargetNs;
            if (_pending.Kind != PresentKind.PreRoll)
            {
                if (now < target - OffscreenLeadNs)
                {
                    waiter.WaitUntil(Math.Min(target - OffscreenLeadNs, now + (_inFlightCount > 0 ? PollNs : 2_000_000)));
                    continue;
                }

                if (now > target + (period / 2))
                {
                    Drop(_pending, now, target, PresentOutcome.DroppedLate);
                    continue;
                }
            }

            if (Submit(_pending, now, target, PresentOutcome.Shown, 0, default))
            {
                _havePending = false;
            }
            else
            {
                waiter.WaitUntil(now + 100_000);
            }
        }
    }

    private void RunVirtual()
    {
        while (!_stop || !_jobs.IsEmpty || _havePending)
        {
            Meter.Tick();
            DrainFeedbackVirtual();
            if (!_havePending)
            {
                _havePending = _jobs.TryDequeue(out _pending);
                if (!_havePending)
                {
                    if (_stop)
                    {
                        break;
                    }

                    WaitForJob(Monotonic.NowNs + 1_000_000);
                    continue;
                }

                BeforeJob?.Invoke(_pending);
                if (DroppedByHook(Monotonic.NowNs))
                {
                    continue;
                }
            }

            if ((_pending.Kind == PresentKind.Repeat && !_haveLast) || SubmitVirtual(_pending))
            {
                _havePending = false;
            }
        }
    }

    /// <summary>Virtual clock: waits (real time, bounded) until the presenter takes it; then the test hook and its record.</summary>
    private bool SubmitVirtual(in PresentJob job)
    {
        var request = job.Kind == PresentKind.Repeat ? _lastRequest : job.Request;
        var giveUp = Monotonic.NowNs + _stallTimeoutNs;
        while (!_presenter.IsReadyForPresent() || !_presenter.TryPresent(request with { SlotIndex = _presentId + 1, TargetTimeNs = job.TargetNs }))
        {
            DrainFeedbackVirtual();
            if (Monotonic.NowNs > giveUp)
            {
                throw new TimeoutException($"The presenter refused slot {job.SlotIndex} for {_stallTimeoutNs / 1e9:0.#} s (virtual clock).");
            }

            if (_stop && Monotonic.NowNs > giveUp - _stallTimeoutNs + 500_000_000)
            {
                return true; // stopping: do not hold the teardown on a presenter that will not take it
            }

            Thread.Sleep(0);
        }

        _presentId++;
        _lastRequest = request;
        _haveLast = true;
        if (job.Kind == PresentKind.PreRoll)
        {
            return true; // the pre-roll's black frames are not slots: no hook, no record, not counted
        }

        Volatile.Write(ref _shown, _shown + 1);

        OnPresented?.Invoke(new PresentedFrame
        {
            SlotIndex = job.SlotIndex,
            Kind = job.SlotKind,
            FrameA = job.FrameA,
            FrameB = job.FrameB,
            Timestep = job.Timestep,
            ContentNs = job.ContentNs,
            ContentA = job.ContentA,
            ContentB = job.ContentB,
            TargetNs = job.TargetNs,
            Write = job.Kind == PresentKind.Repeat ? 0 : job.Write,
            BreakBeforeB = job.BreakBeforeB,
        });
        Emit(new PresentRecord
        {
            SlotIndex = job.SlotIndex,
            Kind = job.Kind,
            Outcome = PresentOutcome.Shown,
            Write = job.Write,
            TargetNs = job.TargetNs,
            SubmitNs = job.TargetNs,
            PredictedNs = job.TargetNs,
            ShownNs = job.TargetNs,
            PreviousShownNs = _lastShownNs,
            PeriodNs = (long)_grid.PeriodNs,
            InFlight = 1,
        });
        _lastShownNs = job.TargetNs;
        return true;
    }

    private void DrainFeedbackVirtual()
    {
        while (_presenter.TryGetFeedback(out _))
        {
        }
    }

    /// <summary>
    /// Waits for a job, until <paramref name="untilNs"/> at the latest (the caller's loop goes round again either way). Two
    /// milliseconds or more ahead: on the event, which a new job sets at once. Closer: a precise sleep, a tenth of a
    /// millisecond at a time, so a job that comes meanwhile waits at most that. (It used to sleep through any wait under a
    /// millisecond, and the idle wait of a millisecond always fell under it by the time it was measured: a job came up to a
    /// millisecond before the present thread saw it, which variable refresh showed as a 0.5 ms jitter of real frames.)
    /// </summary>
    private void WaitForJob(long untilNs)
    {
        if (!_jobs.IsEmpty || _stop)
        {
            return;
        }

        var left = untilNs - Monotonic.NowNs;
        if (left <= 0)
        {
            return;
        }

        if (left < 2 * Nanoseconds.PerMillisecond)
        {
            DeadlineWaiter.Sleep.WaitUntil(Math.Min(untilNs, Monotonic.NowNs + JobPollNs));
            return;
        }

        _ = _wake.WaitOne((int)Math.Min(50, (left / Nanoseconds.PerMillisecond) - 1));
    }

    /// <summary>How long a job can wait for the present thread while it sleeps briefly (polling feedback).</summary>
    private const long JobPollNs = 100 * Nanoseconds.PerMicrosecond;

    /// <summary>With nothing in flight and nothing to do: wait on the event this long at most.</summary>
    private const long IdleWaitNs = 5 * Nanoseconds.PerMillisecond;

    /// <summary>The refresh a present submitted now would be shown at: behind the last one in flight, else the next one it can make.</summary>
    private long PredictNext(long now)
    {
        if (!_grid.HasPhase)
        {
            return now;
        }

        // Behind the last present queued: the refresh after its one, stepped on the grid as it is now (never by adding the
        // period, which drifts off the refreshes; see DisplayGrid.NextAfter). With nothing queued: the first refresh it
        // could be taken for, the measured latch at the earliest ahead (the earliest it could show: a slot due later must
        // wait, never be handed over early enough to show a refresh early).
        var first = _grid.NextAtOrAfter(now + EarliestLatchNs);
        return FifoBusy(now) ? Math.Max(_grid.NextAfter(_lastPredictedNs), first) : first;
    }

    private bool Submit(in PresentJob job, long now, long predicted, PresentOutcome outcome, int lateAtSubmit, MissKind cause)
    {
        if (job.Kind == PresentKind.Repeat)
        {
            if (!_haveLast)
            {
                // Nothing shown yet to repeat (start-up): the slot simply has nothing new.
                Drop(job, now, predicted, PresentOutcome.DroppedRefused);
                return true;
            }

            return Present(_lastRequest, job.SlotIndex, job.Kind, 0, job.TargetNs, now, predicted, outcome, lateAtSubmit, cause);
        }

        return Present(job.Request, job.SlotIndex, job.Kind, job.Write, job.TargetNs, now, predicted, outcome, lateAtSubmit, cause);
    }

    private bool SubmitFiller(long now, long predicted)
    {
        var submitted = Present(_lastRequest, -1, PresentKind.Repeat, 0, predicted, now, predicted, PresentOutcome.Filler, 0, default);
        if (submitted)
        {
            _lastWasLateReal = false;
        }

        return submitted;
    }

    private bool Present(in PresentRequest request, long slotIndex, PresentKind kind, ulong write, long target, long now, long predicted, PresentOutcome outcome, int lateAtSubmit, MissKind cause)
    {
        if (_inFlightCount == InFlightCapacity)
        {
            return false;
        }

        var id = _presentId + 1;
        var t0 = Monotonic.NowNs;
        var ready = _presenter.IsReadyForPresent();
        var t1 = Monotonic.NowNs;
        Stall(2, t0, t1);
        var presented = ready && _presenter.TryPresent(request with { SlotIndex = id, TargetTimeNs = target });
        var t2 = Monotonic.NowNs;
        Stall(3, t1, t2);
        if (!presented)
        {
            _refusals++;
            return false;
        }

        _presentId = id;
        var stale = MissKind.None;
        if (kind != PresentKind.Repeat)
        {
            _lastRequest = request;
            _haveLast = true;
            _staleCause = MissKind.None;
        }
        else if (slotIndex >= 0)
        {
            stale = _staleCause; // a repeat of a new image that was given up shows an older one
        }

        // Its content: a repeat or filler shows the presenter's own copy (ready now); a write is ready once the ring's
        // written counter has reached it (checked now, then each turn until it has).
        var readyNs = write == 0 ? t2 : _presenter.Ring is { } ring && ring.ReadWrittenValue() >= write ? t2 : 0;
        var queuedNs = outcome == PresentOutcome.Filler ? t2 : Math.Min(_pendingSinceNs == 0 ? t2 : _pendingSinceNs, t2);
        _inFlight[(_inFlightHead + _inFlightCount) % InFlightCapacity] =
            new InFlight(id, slotIndex, kind, outcome, write, target, t2, predicted, readyNs, lateAtSubmit, cause, _lead.LeadNs, queuedNs, stale)
            {
                AfterLate = _expectedDiscards > 0,
                CallNs = t2 - t1,
            };
        _inFlightCount++;
        _lastPredictedNs = predicted;
        if (_inFlightCount > Volatile.Read(ref _maxInFlight))
        {
            Volatile.Write(ref _maxInFlight, _inFlightCount);
        }

        if (outcome == PresentOutcome.Filler)
        {
            Volatile.Write(ref _fillers, _fillers + 1);
        }

        return true;
    }

    /// <summary>
    /// A slot's present is not submitted. A ring write's slot is released here (GPU-side, once written): nothing else
    /// would release it until a newer write is handed to the presenter, and the generator thread's write into the same
    /// slot a ring's length later waits for that release, so a run of dropped writes would hold the generator (and with
    /// it the source's frames and every later write) for good. Whose miss it is: a refused present is the pipeline's; a
    /// late one is the consequence of whatever put the FIFO behind (<see cref="MissAccounting.OnCatchUp"/>); a repeat of
    /// the real frame just shown late is no miss (its refresh showed that image).
    /// </summary>
    private void Drop(in PresentJob job, long now, long predicted, PresentOutcome outcome)
    {
        _havePending = false;
        if (job.Kind != PresentKind.Repeat && job.Write > _presenter.LastReleasedWrite)
        {
            _presenter.Release(job.Request);
        }

        var isMiss = true;
        var miss = MissKind.Refused;
        switch (outcome)
        {
            case PresentOutcome.DroppedAbsorbed:
                // Not a miss by itself (its refresh shows the image it repeats); the attribution stays in the record for
                // the collector, which checks what that refresh did show.
                miss = _accounting.OnCatchUp(now, givesBackARefresh: true, shownLate: false);
                isMiss = false;
                break;
            case PresentOutcome.DroppedLate:
                miss = _accounting.OnCatchUp(now, givesBackARefresh: true, shownLate: false);
                break;
            default:
                break;
        }

        if (job.Kind == PresentKind.Repeat)
        {
            // A repeat's refresh shows the image it repeats anyway (the last one flipped), unless that image was given up.
            // (Or unless that image is never shown, or shown late, or a newer one early: the collector checks the refresh
            // against the shows around it, with this record's attribution.)
            isMiss = _staleCause != MissKind.None;
            miss = isMiss ? _staleCause : miss;
        }

        if (job.Kind == PresentKind.Write && isMiss)
        {
            // Its image is not shown: the repeats scheduled after it show an older one, misses of the same cause.
            _staleCause = miss;
        }

        if (outcome is PresentOutcome.DroppedLate or PresentOutcome.DroppedAbsorbed)
        {
            Volatile.Write(ref _droppedLate, _droppedLate + 1);
        }
        else
        {
            Volatile.Write(ref _droppedRefused, _droppedRefused + 1);
        }

        Emit(new PresentRecord
        {
            SlotIndex = job.SlotIndex,
            Kind = job.Kind,
            Outcome = outcome,
            Write = job.Write,
            TargetNs = job.TargetNs,
            SubmitNs = now,
            PredictedNs = predicted,
            PeriodNs = (long)_grid.PeriodNs,
            InFlight = _inFlightCount,
            LeadNs = _lead.LeadNs,
            IsMiss = isMiss,
            Miss = miss,
        });
    }

    private void PollFeedback(long now, bool offscreen)
    {
        var t0 = Monotonic.NowNs;
        var got = _presenter.TryGetFeedback(out var feedback);
        Stall(1, t0, Monotonic.NowNs);
        for (; got; got = _presenter.TryGetFeedback(out feedback))
        {
            // Feedback comes in present order; find the present it is for (older ones lost from the presenter's queue
            // are dropped from the in-flight list too).
            var found = false;
            while (_inFlightCount > 0)
            {
                var entry = _inFlight[_inFlightHead];
                _inFlightHead = (_inFlightHead + 1) % InFlightCapacity;
                _inFlightCount--;
                if (entry.Id == feedback.SlotIndex)
                {
                    OnShown(entry, now, feedback.ActualTimeNs, offscreen);
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                break;
            }
        }
    }

    /// <summary>
    /// A present was reported shown. With a first-pixel time: it feeds the refresh grid; a show later than predicted makes
    /// everything behind it in the FIFO late by as much (their predictions move), and the miss is judged from host-clock
    /// facts (<see cref="MissAccounting"/>); a write whose content was ready only after it was submitted raises the present
    /// lead (<see cref="LeadController"/>). Presents submitted after a late show are predicted for their own refresh while
    /// the compositor is expected to give the lost refresh back (it discards a queued present: with two queued); if one of
    /// them is late too, it did not, and the present thread catches up itself (<see cref="CatchUpPolicy"/>). Without a
    /// first-pixel time on a surface that reports them, the present was discarded by the compositor (or its report lost
    /// the time): counted as untimed, kept out of the grid, the lateness test and the intervals, and given the attribution
    /// the collector uses if a timed neighbour took its refresh.
    /// </summary>
    private void OnShown(in InFlight entry, long reportedNs, long actualNs, bool offscreen)
    {
        var timed = actualNs > 0;
        var untimed = !timed && !offscreen && _presenter.ReportsPresentTimes;
        var shownNs = timed ? actualNs : reportedNs;
        if (entry.Outcome == PresentOutcome.Probe)
        {
            if (entry.SlotIndex == -4)
            {
                OnVrrProbeShown(entry.SubmitNs, timed ? shownNs : 0);
                return;
            }

            if (timed && _presenter.IsTimingKnown)
            {
                _grid.AddShow(shownNs);
            }

            OnProbeShown(entry, shownNs, timed);
            _lastShownNs = timed ? shownNs : 0;
            return;
        }

        if (_vrr && !offscreen)
        {
            OnShownVrr(entry, reportedNs, shownNs, timed, untimed);
            return;
        }

        var period = (long)Math.Round(_grid.PeriodNs);
        var isMiss = false;
        var miss = MissKind.LatePresent;
        var handoverLead = 0L;
        var required = 0L;
        if (entry.Kind == PresentKind.PreRoll)
        {
            // The pre-roll's black frames are not counted with the playback's shows.
        }
        else if (untimed)
        {
            Volatile.Write(ref _untimedShows, _untimedShows + 1);
            miss = DiscardCause(entry, reportedNs);
        }
        else if (timed)
        {
            Volatile.Write(ref _timedShows, _timedShows + 1);
        }

        if (!offscreen && !untimed)
        {
            if (_presenter.IsTimingKnown)
            {
                _grid.AddShow(shownNs);
                if (_showsSinceRateKnown < int.MaxValue)
                {
                    Volatile.Write(ref _showsSinceRateKnown, _showsSinceRateKnown + 1);
                }
            }

            if (_grid.HasPhase && entry.PredictedNs > 0 && period > 0 && entry.Kind != PresentKind.PreRoll)
            {
                // Handed over: submitted and its content written (host-observed; if it was never seen written before
                // the report, as late as the report: the conservative reading).
                var ready = entry.ReadyNs > 0 ? entry.ReadyNs : reportedNs;
                var handover = Math.Max(entry.SubmitNs, ready);
                handoverLead = entry.PredictedNs - handover;

                // The compositor's latch, from this show: taken for this refresh means handed over by its latch; and if
                // nothing flipped at the refresh before, it missed that one's latch. Only a timed show just before tells
                // that (none, or an untimed one, is no evidence: the refresh before may have been taken).
                // Shown later than predicted: everything behind it in the FIFO is late by as much. With first-pixel times that
                // is exact (half a period); a report's own time lands some time after the refresh (three quarters).
                var late = (shownNs - entry.PredictedNs + (timed ? period / 2 : period / 4)) / period;
                var previousFlip = _lastShownNs;
                if (timed)
                {
                    // An upper bound only. A free refresh before a show is no lower bound: the compositor may simply have
                    // missed that refresh, or the present was held back to its own (the empty-queue rule); such bounds
                    // claimed latches far earlier than the latch sweep measures (5 to 6 ms against 2 at 119.88 Hz) and
                    // made presents look unreachable, which dropped them.
                    _latch.Add(reportedNs, shownNs, handover, period, couldHaveMadeEarlier: false);
                }

                var lateCause = MissKind.LatePresent;
                if (late >= 1)
                {
                    Volatile.Write(ref _lateShows, _lateShows + 1);

                    // Held back by the in-flight cap with its content ready: the lead could not have helped. After a late
                    // show the cap holds the next present until the late one is shown, which can be past that present's
                    // latch: then it is the late show's consequence, not the pipeline's.
                    var capLimited = entry.SubmitNs - entry.QueuedNs > 2 * PollNs && ready <= entry.SubmitNs;
                    required = _accounting.RequiredLeadNs(reportedNs);
                    lateCause = _accounting.OnLateShow(reportedNs, timed ? handoverLead : long.MinValue, (int)late,
                        capLimited && entry.AfterLate ? _expectedDiscardCause : MissKind.None);

                    if (entry.AfterLate || !ExpectGiveBack(period))
                    {
                        // Submitted for its own refresh after a late show, and late as well: the compositor did not give the
                        // refresh back. Or not expected at all. The present thread catches up itself (CatchUpPolicy, on the
                        // next slots).
                        _expectedDiscards = 0;
                        Shift(late * period, lateCause, chain: true);
                        Volatile.Write(ref _ownCatchUps, _ownCatchUps + 1);
                    }
                    else
                    {
                        // The presents queued behind it are late too; new ones keep their own refresh (the compositor
                        // discards one of the queued presents to give the refresh back).
                        Shift(late * period, lateCause, chain: false);
                        _expectedDiscards += (int)late;
                        _expectedDiscardCause = CatchUpCause(lateCause);
                    }

                    if (timed && lateCause == MissKind.LatePresent && previousFlip != 0 && previousFlip <= entry.PredictedNs - period + (period / 2))
                    {
                        // Evidence about the latch only from the pipeline's late shows: one the display's (handed over in
                        // time, the compositor missed the refresh) would claim a latch as early as its hand-over.
                        _latch.AddMissed(reportedNs, entry.PredictedNs, handover);
                    }

                    // A late show of a present handed over as soon as it was there (its job came, or its content was
                    // written, only then) means it was decided too late for the latch: the lead goes up. Once the
                    // pipeline's; the display's (handed over in time: the compositor missed the refresh, a higher lead
                    // would not help) only when another came within a second: a latch that moved, not a missed refresh.
                    // (KWin skipping one refresh every second, as seen on DP-3 with a browser open, shows presents handed
                    // over 7.8 ms ahead a refresh late as well: no lead fixes that, so it must not raise one.)
                    var own = entry.Outcome != PresentOutcome.Filler && !capLimited && !entry.AfterLate && lateCause != MissKind.RateChange;
                    var again = _lastLateNs != long.MinValue && reportedNs - _lastLateNs < Nanoseconds.PerSecond;
                    if (own && (lateCause == MissKind.LatePresent || again) && _lead.OnLateContent(reportedNs, timed ? handoverLead : long.MinValue))
                    {
                        _forceGrid = true;
                    }

                    if (own)
                    {
                        _lastLateNs = reportedNs;
                    }
                }
                else
                {
                    if (timed)
                    {
                        // How much earlier than seen it may really have been handed over: the presenter's call (the present
                        // went out somewhere in it), and a turn when its content was seen written only after it was submitted.
                        _accounting.RecordOnTime(reportedNs, handoverLead, entry.CallNs + (entry.ReadyNs > entry.SubmitNs || entry.ReadyNs == 0 ? PollNs : 0));
                    }

                    var early = shownNs < entry.PredictedNs - (period / 2) ? (entry.PredictedNs - shownNs + (period / 2)) / period : 0;
                    var expected = Math.Min(early, entry.Behind);
                    if (expected > 0)
                    {
                        // It was predicted behind a late show and the compositor gave that refresh back (it discarded a
                        // present queued ahead of it): the presents still behind it are back on their own refreshes.
                        Unshift(expected * period);
                    }

                    if (early > expected)
                    {
                        // Shown earlier than predicted: the FIFO is ahead of the refreshes it was meant for, and so is every
                        // present behind it. The predictions follow at once, so the next slot finds a refresh before its own
                        // free and a filler goes in for it: the FIFO is back on the slots' refreshes from there. Behind a
                        // present the compositor discarded (its report came without a time), that is the compositor's doing.
                        Volatile.Write(ref _earlyShows, _earlyShows + 1);
                        Shift(-(early - expected) * period, _untimedSinceTimed > 0 ? MissKind.Discarded : MissKind.Early, chain: true);
                    }
                    else if (entry.AfterLate && early == 0)
                    {
                        // A present submitted for its own refresh after a late show made it: the FIFO is back on time.
                        _expectedDiscards = 0;
                    }
                }

                // The slot's own verdict, against the refresh it was scheduled for (not the prediction).
                if (entry.SlotIndex >= 0 && entry.Outcome != PresentOutcome.Filler)
                {
                    if (timed)
                    {
                        var deviation = shownNs - entry.TargetNs;
                        if (deviation < -(period / 2))
                        {
                            isMiss = true;
                            miss = entry.EarlyCause == MissKind.Discarded || _untimedSinceTimed > 0 ? MissKind.Discarded : MissKind.Early;
                        }
                        else if (deviation > period / 2)
                        {
                            isMiss = true;
                            miss = late >= 1 ? (entry.LateAtSubmit > 0 ? entry.Cause : lateCause)
                                : entry.LateAtSubmit > 0 ? entry.Cause
                                : entry.Inherited != MissKind.None ? entry.Inherited
                                : MissKind.LatePresent;
                        }
                        else if (entry.StaleCause != MissKind.None)
                        {
                            isMiss = true; // a repeat of a new image that was given up: its refresh shows an older one
                            miss = entry.StaleCause;
                        }
                    }
                    else if (late >= 1 || entry.LateAtSubmit > 0)
                    {
                        isMiss = true; // a surface without first-pixel times: judged against the prediction
                        miss = entry.LateAtSubmit > 0 ? entry.Cause : lateCause;
                    }
                }
            }
        }

        if (entry.Kind != PresentKind.PreRoll)
        {
            Volatile.Write(ref _shown, _shown + 1);
        }

        Emit(new PresentRecord
        {
            SlotIndex = entry.SlotIndex,
            Kind = entry.Kind,
            Outcome = entry.Outcome,
            Write = entry.Write,
            TargetNs = entry.TargetNs,
            SubmitNs = entry.SubmitNs,
            PredictedNs = entry.PredictedNs,
            ShownNs = shownNs,
            ReportedNs = reportedNs,
            Timed = timed,
            Untimed = untimed,
            PreviousShownNs = untimed ? 0 : _lastShownNs,
            PreviousTimedNs = timed ? _lastTimedNs : 0,
            UntimedBefore = _untimedSinceTimed,
            PeriodNs = period,
            InFlight = _inFlightCount + 1,
            ReadyNs = entry.ReadyNs,
            HandoverLeadNs = handoverLead,
            RequiredLeadNs = required,
            LeadNs = entry.LeadNs,
            IsMiss = isMiss,
            Miss = miss,
        });

        // An untimed show breaks the chain of intervals: the next timed show's interval is not taken from it.
        _lastShownNs = untimed ? 0 : shownNs;
        if (timed)
        {
            _lastTimedNs = shownNs;
            _untimedSinceTimed = 0;
        }
        else if (untimed)
        {
            _untimedSinceTimed++;
        }
    }

    /// <summary>
    /// Whose a present reported without a first-pixel time is, should a timed neighbour have taken its refresh (the
    /// collector judges that): the compositor's give-back after a late show carries the late show's attribution; otherwise
    /// a present handed over in time (at least the measured deadline before its refresh) that the compositor did not show is
    /// the display's (<see cref="MissKind.Discarded"/>), one handed over later the pipeline's (<see cref="MissKind.NotShown"/>).
    /// </summary>
    private MissKind DiscardCause(in InFlight entry, long nowNs)
    {
        if (_expectedDiscards > 0)
        {
            _expectedDiscards--;
            Volatile.Write(ref _compositorCatchUps, _compositorCatchUps + 1);
            return _expectedDiscardCause;
        }

        if (_accounting.InRateChange(nowNs))
        {
            return MissKind.RateChange;
        }

        var ready = entry.ReadyNs > 0 ? entry.ReadyNs : nowNs;
        var lead = entry.TargetNs - Math.Max(entry.SubmitNs, ready);
        return lead >= _accounting.RequiredLeadNs(nowNs) ? MissKind.Discarded : MissKind.NotShown;
    }

    /// <summary>The attribution of a refresh given back after a late show of <paramref name="lateCause"/>.</summary>
    private static MissKind CatchUpCause(MissKind lateCause) => lateCause switch
    {
        MissKind.DisplayLate or MissKind.CatchUp or MissKind.Discarded => MissKind.CatchUp,
        MissKind.RateChange => MissKind.RateChange,
        _ => MissKind.NotShown,
    };

    /// <summary>
    /// Moves the predictions of every present in flight (they are behind or ahead of their refreshes by as much as the one
    /// just shown); <paramref name="cause"/> is whose that is, for the ones that will be shown away from their refresh.
    /// <paramref name="chain"/>: new presents are predicted behind the moved ones (otherwise they keep their own refresh,
    /// the compositor being expected to give the refresh back).
    /// </summary>
    private void Shift(long deltaNs, MissKind cause, bool chain)
    {
        var periods = _grid.PeriodNs > 0 ? (int)Math.Round(deltaNs / _grid.PeriodNs) : 0;
        for (var i = 0; i < _inFlightCount; i++)
        {
            ref var e = ref _inFlight[(_inFlightHead + i) % InFlightCapacity];
            e = e with
            {
                PredictedNs = e.PredictedNs + deltaNs,
                Inherited = e.Inherited == MissKind.None && deltaNs > 0 ? cause : e.Inherited,
                EarlyCause = deltaNs < 0 && e.EarlyCause == MissKind.None ? cause : e.EarlyCause,
                Behind = deltaNs > 0 ? e.Behind + periods : e.Behind,
            };
        }

        if (chain && _lastPredictedNs != long.MinValue)
        {
            _lastPredictedNs += deltaNs;
        }
    }

    /// <summary>The compositor gave back a refresh the presents in flight were predicted behind: they move back (only those
    /// that were moved).</summary>
    private void Unshift(long deltaNs)
    {
        var periods = _grid.PeriodNs > 0 ? (int)Math.Round(deltaNs / _grid.PeriodNs) : 0;
        for (var i = 0; i < _inFlightCount; i++)
        {
            ref var e = ref _inFlight[(_inFlightHead + i) % InFlightCapacity];
            var back = Math.Min(e.Behind, periods);
            if (back > 0)
            {
                e = e with { PredictedNs = e.PredictedNs - (long)Math.Round(back * _grid.PeriodNs), Behind = e.Behind - back };
            }
        }
    }

    /// <summary>
    /// The output's rate: when it first becomes known, the grid and the lead are set up for it; when it changes later (the
    /// window moved to another output), they are learnt again and misses for a while count as the change's.
    /// </summary>
    private void CheckRate(long now)
    {
        var rate = _presenter.Timing.RefreshRate;
        if (!rate.IsValid || !_presenter.IsTimingKnown || rate == _knownRate)
        {
            return;
        }

        var moved = _knownRate.IsValid;
        _knownRate = rate;
        if (rate != _grid.Rate || moved)
        {
            _grid.Reset(rate);
            Volatile.Write(ref _showsSinceRateKnown, 0);
        }

        if (moved)
        {
            Volatile.Write(ref _rateChanges, _rateChanges + 1);
            _accounting.BeginRateChange(now, RateChangeWindowNs);
            Volatile.Write(ref _sweepResult, null); // measured on the old output
        }

        _latch.Clear();

        if (_display == DisplayKind.Window && _clock == PlaybackClock.Real)
        {
            _lead.Reset(BaseLead(rate), MaxLead(rate), now, moved ? LeadChange.NewOutput : LeadChange.Start);
        }

        _forceGrid = true;
    }

    private long BaseLead(FrameRate rate) =>
        _configuredLeadNs >= 0 ? _configuredLeadNs : rate.IsValid ? (long)Math.Round(_leadRefreshes * rate.PeriodNs) : 0;

    /// <summary>The most a lead can usefully be: about the two refreshes the in-flight cap lets a present be handed over before its own.</summary>
    private static long MaxLead(FrameRate rate) => rate.IsValid ? Math.Max(0, (long)(2 * rate.PeriodNs) - (2 * PollNs)) : 0;

    /// <summary>Sends the grid and lead to the pacing thread when first known, when the rate or lead changes, or when the phase moved by 100 µs.</summary>
    private void SendGrid(long now)
    {
        if (_vrr)
        {
            // No grid under variable refresh: the rate (the shortest refresh period) and the expected delay to the first
            // pixel (the lead: the slots' targets are their hand-over plus that).
            if (_forceGrid && _displayUpdates.TryEnqueue(new DisplayUpdate(now, _grid.Rate, _vrrLeadNs)))
            {
                _forceGrid = false;
                Interlocked.Increment(ref _gridSeq);
                Volatile.Write(ref _sentAnchorNs, now);
                Volatile.Write(ref _sentRateNum, _grid.Rate.Numerator);
                Volatile.Write(ref _sentRateDen, _grid.Rate.Denominator);
                Volatile.Write(ref _sentLeadNs, _vrrLeadNs);
                Interlocked.Increment(ref _gridSeq);
            }

            return;
        }

        Volatile.Write(ref _currentLeadNs, _lead.LeadNs);
        if (!_grid.HasPhase || !_presenter.IsTimingKnown)
        {
            return;
        }

        var anchor = _grid.TimeOf(_grid.IndexOf(now));
        var moved = _sentAnchorNs == long.MinValue || _sentRate != _grid.Rate || _forceGrid || Math.Abs(_grid.Residual(_sentAnchorNs)) > 100_000;
        if (moved && _displayUpdates.TryEnqueue(new DisplayUpdate(anchor, _grid.Rate, _lead.LeadNs)))
        {
            _sentRate = _grid.Rate;
            _forceGrid = false;
            Interlocked.Increment(ref _gridSeq);
            Volatile.Write(ref _sentAnchorNs, anchor);
            Volatile.Write(ref _sentRateNum, _grid.Rate.Numerator);
            Volatile.Write(ref _sentRateDen, _grid.Rate.Denominator);
            Volatile.Write(ref _sentLeadNs, _lead.LeadNs);
            Interlocked.Increment(ref _gridSeq);
        }
    }

    private void Emit(in PresentRecord record)
    {
        if (!_records.TryEnqueue(record))
        {
            Volatile.Write(ref _recordsDropped, _recordsDropped + 1);
        }
    }

    private readonly record struct InFlight(
        long Id,
        long SlotIndex,
        PresentKind Kind,
        PresentOutcome Outcome,
        ulong Write,
        long TargetNs,
        long SubmitNs,
        long PredictedNs,
        long ReadyNs,
        int LateAtSubmit,
        MissKind Cause,
        long LeadNs,
        long QueuedNs,
        MissKind StaleCause = MissKind.None,
        MissKind Inherited = MissKind.None)
    {
        /// <summary>Refreshes its prediction was moved later because a present ahead of it in the FIFO was shown late.</summary>
        public int Behind { get; init; }

        /// <summary>Submitted for its own refresh while the compositor was expected to give back a refresh lost to a late show.</summary>
        public bool AfterLate { get; init; }

        /// <summary>Whose it is if it is shown early: a compositor's discard ahead of it (the display's) or the FIFO being ahead.</summary>
        public MissKind EarlyCause { get; init; }

        /// <summary>How long the presenter's call took: the present went to the compositor somewhere in it, before <see cref="SubmitNs"/>.</summary>
        public long CallNs { get; init; }
    }
}
