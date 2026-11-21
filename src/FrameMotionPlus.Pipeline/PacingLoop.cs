using System.Globalization;
using FrameMotionPlus.Core.Abstractions;
using FrameMotionPlus.Core.Collections;
using FrameMotionPlus.Core.Frames;
using FrameMotionPlus.Core.Scheduling;
using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>Why playback ended.</summary>
public enum StopReason
{
    None = 0,
    Duration = 1,
    SlotLimit = 2,
    EndOfFile = 3,
    User = 4,
    Failure = 5,
}

/// <summary>
/// The pacing thread: the scheduler's thread and the source's consumer. It drains the source into the scheduler (frames
/// as they become available, ahead of their time for a file), takes the generation times and the refresh grid back,
/// sleeps until the scheduler's next decision time, and turns each decision into a ring write for the generator thread
/// and a present for the present thread, in slot order. It never calls the generator (which may block) and never waits
/// for the GPU or the presenter: when the bridge refuses a slot (too many writes unfinished) or the present thread has
/// fallen behind, the slot is not produced (counted), never queued. Frames the scheduler no longer refers to go back to
/// the source with the GPU point of the last ring write that read them. Allocation-free per slot.
/// </summary>
internal sealed class PacingLoop
{
    private const long MaxIdleNs = 2 * Nanoseconds.PerMillisecond;
    private const long LiveIdleNs = 250 * Nanoseconds.PerMicrosecond;
    private const int PresentBacklogLimit = 3;
    private const long ProgressCheckNs = 100 * Nanoseconds.PerMillisecond;

    private readonly FrameScheduler _scheduler;
    private readonly IPlaybackSource _source;
    private readonly IGpuBridge _bridge;
    private readonly IFrameGenerator? _generator;
    private readonly RingWriter _writer;
    private readonly PresentLoop _presenter;
    private readonly SpscRing<DecisionRecord> _decisions;
    private readonly HeldFrame[] _held;
    private readonly bool _virtual;
    private readonly long _feedAheadNs;
    private readonly long _stallTimeoutNs;
    private readonly long _durationNs;
    private readonly long _maxSlots;
    private int _heldHead;
    private int _heldCount;
    private int _fedCount; // held frames already given to the scheduler (a prefix of the held ones)
    private long _virtualNow;
    private volatile bool _stop;
    private long _playbackStartNs = long.MinValue;
    private long _slotsSinceStart;
    private readonly long _warmupNs;
    private readonly int _startupFrames;
    private readonly long _startLeadNs;
    private long _contentOffsetNs = long.MinValue;
    private long _presentLeadNs;
    private long _measureFromNs = long.MinValue;
    private long _lastFrameContentNs = long.MinValue;
    private bool _sourceEnded;
    private long _handedOut;
    private long _writtenSeen = -1;
    private long _progressNs;
    private long _nextProgressCheckNs;
    private long _launchNs = long.MinValue;
    private double _displayPeriodNs;

    // Counters.
    private long _framesRead;
    private long _framesRejected;
    private long _skippedBackPressure;
    private long _skippedBacklog;
    private long _maxOutstanding;
    private long _displayUpdates;
    private long _recordsDropped;
    private long _discontinuities;

    public PacingLoop(
        FrameScheduler scheduler,
        IPlaybackSource source,
        IGpuBridge bridge,
        IFrameGenerator? generator,
        RingWriter writer,
        PresentLoop presenter,
        int heldCapacity,
        int recordCapacity,
        PlaybackClock clock,
        long startNs,
        long feedAheadNs,
        long stallTimeoutNs,
        long durationNs,
        long maxSlots,
        long warmupNs,
        long presentLeadNs,
        int startupFrames,
        long startLeadNs,
        double resample = 1.0)
    {
        _scheduler = scheduler;
        _source = source;
        _bridge = bridge;
        _generator = generator;
        _writer = writer;
        _presenter = presenter;
        _held = new HeldFrame[heldCapacity];
        _decisions = new SpscRing<DecisionRecord>(recordCapacity);
        _virtual = clock == PlaybackClock.Virtual;
        _virtualNow = startNs;
        _feedAheadNs = feedAheadNs;
        _stallTimeoutNs = stallTimeoutNs;
        _durationNs = durationNs;
        _maxSlots = maxSlots;
        _warmupNs = warmupNs;
        _presentLeadNs = presentLeadNs;
        _startupFrames = Math.Max(1, startupFrames);
        _startLeadNs = startLeadNs;
        _resample = resample > 0 ? resample : 1.0;
        _displayPeriodNs = scheduler.Options.DisplayRate.PeriodNs;
    }

    /// <summary>
    /// How a file's content times are stretched (1: not at all): to lock a file whose rate is a hair off the display's
    /// (60.000 fps on 119.88 Hz: 1.001, the file plays 0.1% slower), so real frames land on refreshes instead of 96% of the
    /// slots being generated. A file has no live constraint; a live source is never resampled.
    /// </summary>
    public double Resample => _resample;

    private readonly double _resample;
    private long _resampleInNs;
    private long _resampleOutNs;

    /// <summary>A frame's content time on the playback's clock, once the start-up frames have fixed the origin.</summary>
    private long MapContent(long sourceNs) => _resample == 1.0
        ? sourceNs + _contentOffsetNs
        : _resampleOutNs + (long)Math.Round((sourceNs - _resampleInNs) * _resample);

    /// <summary>
    /// The constant added to a file's content times so its first frame is due <paramref name="startLeadNs"/> from now (the
    /// first one shown). None for a live source: its content times are capture times, and moving them would add latency
    /// (or claim frames are due before they were captured).
    /// </summary>
    internal static long StartOffset(bool live, long nowNs, long startLeadNs, long firstContentNs) =>
        live ? 0 : nowNs + startLeadNs - firstContentNs;

    /// <summary>
    /// What was added to every frame's content time so the first frame of the file is the first one shown (see
    /// <see cref="PumpSource"/>); long.MinValue until the start-up frames were in.
    /// </summary>
    public long ContentOffsetNs => Volatile.Read(ref _contentOffsetNs);

    public SpscRing<DecisionRecord> Decisions => _decisions;

    public ThreadMeter Meter { get; } = new();

    public FrameScheduler Scheduler => _scheduler;

    /// <summary>Test hook, pacing thread: once per loop turn (allocation injection).</summary>
    public Action? OnTurn { get; set; }

    /// <summary>Live quality switching (null: one quality): polled every turn, before the decision.</summary>
    public QualitySwitchControl? QualitySwitch { get; init; }

    /// <summary>Told each write whether the slots are at the output's ceiling (null: no generator). See <see cref="CeilingPacingState"/>.</summary>
    public CeilingPacingState? CeilingPacing { get; init; }

    /// <summary>Generation times dropped because they came from the quality before a switch.</summary>
    public long StaleGenerationTimes => Volatile.Read(ref _staleGenerationTimes);

    private long _staleGenerationTimes;

    /// <summary>Set when playback ends by itself (duration, slot limit, end of file); read by the session.</summary>
    public StopReason Ended { get; private set; }

    /// <summary>The target time of the first slot that showed a frame (the start of playback), or long.MinValue.</summary>
    public long PlaybackStartNs => Volatile.Read(ref _playbackStartNs);

    public long SlotsSinceStart => Volatile.Read(ref _slotsSinceStart);

    /// <summary>
    /// How far ahead of its refresh each slot is decided and its present queued. The scheduler is given the refresh grid
    /// shifted earlier by this much (its V is the time a slot's present must be queued by), so the lead is decision lead
    /// and part of the delay, never charged against the generation budget; each present is for the refresh one lead later.
    /// </summary>
    public long PresentLeadNs => Volatile.Read(ref _presentLeadNs);

    /// <summary>The target time the measured window starts at (the start of playback plus the warm-up), once reached.</summary>
    public long MeasureFromNs => Volatile.Read(ref _measureFromNs);

    /// <summary>Process GC counts when the measured window started (taken on this thread).</summary>
    public Platform.Runtime.GcSnapshot GcAtMeasure { get; private set; }

    /// <summary>The other threads' meters, started with this thread's at the start of the measured window.</summary>
    public ThreadMeter[] OtherMeters { get; set; } = [];

    public long FramesRead => Volatile.Read(ref _framesRead);

    public long FramesRejected => Volatile.Read(ref _framesRejected);

    public long SkippedBackPressure => Volatile.Read(ref _skippedBackPressure);

    public long SkippedPresenterBacklog => Volatile.Read(ref _skippedBacklog);

    public long MaxOutstanding => Volatile.Read(ref _maxOutstanding);

    public long DisplayUpdatesApplied => Volatile.Read(ref _displayUpdates);

    public long RecordsDropped => Volatile.Read(ref _recordsDropped);

    public long Discontinuities => Volatile.Read(ref _discontinuities);

    /// <summary>The current time on this loop's clock (virtual or <c>CLOCK_MONOTONIC</c>).</summary>
    public long Now => _virtual ? Volatile.Read(ref _virtualNow) : Monotonic.NowNs;

    public void RequestStop() => _stop = true;

    /// <summary>Where the loop is (diagnostics: 1 pumping, 2 waiting, 3 deciding, 4 waiting for the source, 5 for the bridge,
    /// 6 for the present thread, 7 for a queue).</summary>
    public int Phase => Volatile.Read(ref _phase);

    public int HeldCount => Volatile.Read(ref _heldCount);

    public int FedCount => Volatile.Read(ref _fedCount);

    private int _phase;

    public void Run()
    {
        PreRoll();
        _launchNs = Now;
        var waiter = DeadlineWaiter.Sleep;
        while (!_stop)
        {
            Meter.Tick();
            OnTurn?.Invoke();
            var now = Now;
            _phase = 1;
            PumpSource(now);
            PumpGenerationTimes();
            PumpDisplay();
            PumpQuality(now);
            CheckProgress();
            if (CheckEnd(now))
            {
                break;
            }

            var next = _scheduler.NextDecisionTime(now);
            if (next > now)
            {
                _phase = 2;
                if (_virtual)
                {
                    // The virtual clock jumps to the decision; the next turn feeds what is due by then and decides.
                    Volatile.Write(ref _virtualNow, next == long.MaxValue ? now + MaxIdleNs : next);
                }
                else
                {
                    // A live source's next frame comes when it comes: look for it often (its arrival is part of the
                    // latency; under variable refresh a real frame is decided the moment it is there).
                    waiter.WaitUntil(Math.Min(next, now + (_source.IsLive ? LiveIdleNs : MaxIdleNs)));
                }

                continue;
            }

            _phase = 3;
            var decision = _scheduler.Schedule(now);
            Execute(decision);
            ApplyDeferredDisplay();
            ReleaseUnreferenced();
            if (CheckLimits(decision))
            {
                break;
            }
        }

        // Frames still held stay held: the generator thread may not have enqueued the writes that read them yet, so they
        // cannot go back to the source (whose decode thread is stopped next, then the whole source freed).
        Meter.Stop();
    }

    private void PreRoll()
    {
        _ = Monotonic.NowNs;
        _ = ThreadCpuClock.CurrentThreadNs;
        _ = _bridge.Outstanding;
        _ = _scheduler.NextDecisionTime(Now);
        _ = _scheduler.OldestReferencedFrameId;
        _ = _scheduler.Diagnostics;
        DeadlineWaiter.Sleep.WaitUntil(Monotonic.NowNs + 100_000);
        DeadlineWaiter.Sleep.WaitUntil(Monotonic.NowNs + 100_000);
    }

    /// <summary>
    /// Reads every frame the source has and feeds the scheduler. The first frame of the file is the first one shown: the
    /// scheduler starts once it has its start-up frames and then shows the content due at its first refresh, so until
    /// those frames are in, nothing is fed; then every content time (from then on, too) gets one offset that puts the first
    /// frame just after the scheduler's first decision (a file has no live constraint; the offset is constant, so later
    /// stalls still never make playback slow). Otherwise the frames due while the decoder was starting would be skipped. Real clock: a frame is fed as soon as it is read; a file's
    /// frame counts as arrived no later than its content time (decoded ahead, its availability is not latency, so it must
    /// not raise D). Virtual clock: frames are fed up to <see cref="_feedAheadNs"/> past now, all of them (the loop waits
    /// for the decoder), each as arrived exactly that lead before its content time: the same frames are fed before the
    /// same decisions in every run.
    /// </summary>
    private void PumpSource(long now)
    {
        var frames = _source.Frames;
        var giveUp = 0L;
        while (true)
        {
            if (_heldCount < _held.Length && frames.TryRead(out var frame))
            {
                _framesRead++;
                var contentNs = _contentOffsetNs == long.MinValue ? frame.ContentTimeNs : MapContent(frame.ContentTimeNs);
                if (contentNs <= _lastFrameContentNs)
                {
                    // Out of order (cannot happen for the file source): never referenced, given back at once.
                    _framesRejected++;
                    frames.Release(frame.FrameId, GpuSignal.None);
                    continue;
                }

                _lastFrameContentNs = contentNs;
                _held[(_heldHead + _heldCount) % _held.Length] = new HeldFrame(frame, contentNs, GpuSignal.None);
                _heldCount++;
                continue;
            }

            if (!_virtual)
            {
                break;
            }

            // Virtual clock: every frame due by now + lead must be read before deciding (bounded wait for the decoder); at
            // the start, the start-up frames.
            var bound = now + _feedAheadNs;
            var enough = _contentOffsetNs == long.MinValue ? _heldCount >= _startupFrames : _lastFrameContentNs > bound;
            if (_heldCount >= _held.Length - 1 || enough || _source.IsEndOfStream || _source.Failure is not null || _stop)
            {
                break;
            }

            _phase = 4;
            if (giveUp == 0)
            {
                giveUp = Monotonic.NowNs + _stallTimeoutNs;
            }
            else if (Monotonic.NowNs > giveUp)
            {
                throw new TimeoutException($"The source delivered no frame for {_stallTimeoutNs / 1e9:0.#} s (virtual clock, frame due by {bound}).");
            }

            Thread.Sleep(0);
        }

        if (_contentOffsetNs == long.MinValue)
        {
            var allIn = _heldCount >= _startupFrames || _heldCount >= _held.Length - 1 || _source.IsEndOfStream || _source.Failure is not null;
            if (!allIn || _heldCount == 0)
            {
                return;
            }

            // The scheduler's first decision is at the next refresh, and τ = V − D is never past it: a first frame due a
            // start lead from now is shown first.
            var offset = StartOffset(_source.IsLive, now, _startLeadNs, _held[_heldHead].ContentNs);
            _resampleInNs = _held[_heldHead].Frame.ContentTimeNs;
            _resampleOutNs = _held[_heldHead].ContentNs + offset;
            Volatile.Write(ref _contentOffsetNs, offset);
            for (var i = 0; i < _heldCount; i++)
            {
                ref var h = ref _held[(_heldHead + i) % _held.Length];
                h = h with { ContentNs = MapContent(h.Frame.ContentTimeNs) };
                _lastFrameContentNs = h.ContentNs;
            }
        }

        while (_fedCount < _heldCount)
        {
            ref var held = ref _held[(_heldHead + _fedCount) % _held.Length];
            var content = held.ContentNs;
            if (_virtual && content > now + _feedAheadNs && _fedCount >= _startupFrames)
            {
                break; // (the start-up frames go in at once: the scheduler starts on them)
            }

            var arrival = _virtual ? content - _feedAheadNs
                : _source.IsLive ? held.Frame.ArrivalTimeNs
                : Math.Min(held.Frame.ArrivalTimeNs, content);
            var discontinuity = (held.Frame.Marks & SourceFrameMarks.Discontinuity) != 0;
            if (discontinuity)
            {
                // Before the pair across it could be enqueued: the generator then never interpolates across the cut
                // (the scheduler does not schedule one anyway; this leaves nothing armed when it does not).
                // The generator compares it with its frames' own content times (the source's clock, not the start-shifted one).
                _generator?.NotifyDiscontinuity(held.Frame.ContentTimeNs);
                Volatile.Write(ref _discontinuities, _discontinuities + 1);
            }

            _ = _scheduler.OnFrameArrived(new FrameArrival(held.Frame.FrameId, content, arrival, discontinuity));
            _fedCount++;
        }
    }

    private void PumpGenerationTimes()
    {
        var epoch = QualitySwitch?.Epoch ?? 0;
        while (_writer.GenerationTimes.TryDequeue(out var sample))
        {
            if (sample.Epoch != epoch)
            {
                // Generated at the quality before the last switch (still in flight when it was committed): not this
                // quality's cost, so it must not move the estimate the switch started again.
                Volatile.Write(ref _staleGenerationTimes, _staleGenerationTimes + 1);
                continue;
            }

            if (!_virtual)
            {
                _scheduler.ReportGenerationTime(sample.Nanoseconds);
            }
        }
    }

    /// <summary>
    /// A quality switch: prepared on the scheduler, then committed, and the generator's switch queued behind every write
    /// already handed out (in order with them), so the slots decided before the commit are generated at the old quality and
    /// every later one at the new.
    /// </summary>
    private void PumpQuality(long now)
    {
        if (QualitySwitch is not { } control || !control.Poll(_scheduler, now, out var to, out var epoch))
        {
            return;
        }

        EnqueueOrWait(_writer.Jobs, new WriteJob { Kind = WriteKind.Quality, SlotIndex = epoch, Quality = to });
        _writer.Wake();
    }

    private void PumpDisplay()
    {
        while (_presenter.DisplayUpdates.TryDequeue(out var update))
        {
            if (_virtual)
            {
                continue;
            }

            if (update.LeadNs > _presentLeadNs && _scheduler.Counters.Slots > 0)
            {
                // A higher lead moves every decision earlier: the slot about to be decided could find its (earlier) time
                // already past and be skipped, a refresh lost to the raise. It is decided on the old grid; the raise
                // applies from the slot after it (ApplyDeferredDisplay).
                _deferredDisplay = update;
                _haveDeferredDisplay = true;
                continue;
            }

            _haveDeferredDisplay = false;
            ApplyDisplay(update);
        }
    }

    /// <summary>The present thread's lead (it adapts): the scheduler's grid is the refreshes shifted earlier by it, so a
    /// change moves the decisions, never the refreshes the slots are for.</summary>
    private void ApplyDisplay(in DisplayUpdate update)
    {
        Volatile.Write(ref _presentLeadNs, update.LeadNs);
        _scheduler.UpdateDisplayTiming(update.AnchorNs - _presentLeadNs, update.Rate);
        if (update.Rate.IsValid)
        {
            _displayPeriodNs = update.Rate.PeriodNs;
        }
        Volatile.Write(ref _displayUpdates, _displayUpdates + 1);
    }

    private void ApplyDeferredDisplay()
    {
        if (_haveDeferredDisplay)
        {
            _haveDeferredDisplay = false;
            ApplyDisplay(_deferredDisplay);
        }
    }

    private DisplayUpdate _deferredDisplay;
    private bool _haveDeferredDisplay;

    private void Execute(in SlotDecision decision)
    {
        var outcome = DecisionOutcome.Empty;
        ulong write = 0;
        var capture = 0L;
        var arrival = 0L;
        switch (decision.Kind)
        {
            case SlotKind.Repeat:
                outcome = EnqueuePresent(decision, PresentKind.Repeat, default, 0, 0, 0) ? DecisionOutcome.Repeat : DecisionOutcome.SkippedPresenterBacklog;
                break;
            case SlotKind.Real:
            case SlotKind.Generated:
                {
                    var a = Find(decision.FrameA);
                    var b = decision.Kind == SlotKind.Generated ? Find(decision.FrameB) : a;
                    if (a < 0 || b < 0)
                    {
                        throw new InvalidOperationException($"Slot {decision.SlotIndex} names frame {decision.FrameA}/{decision.FrameB}, which is not held.");
                    }

                    ref var frameA = ref _held[a];
                    ref var frameB = ref _held[b];
                    capture = frameB.ContentNs;
                    arrival = frameB.Frame.ArrivalTimeNs;
                    outcome = Produce(decision, ref frameA, ref frameB, out write);
                    break;
                }

            default:
                break;
        }

        if (decision.Kind is SlotKind.Real or SlotKind.Generated && _playbackStartNs == long.MinValue)
        {
            Volatile.Write(ref _playbackStartNs, decision.TargetTimeNs);
        }

        if (_playbackStartNs != long.MinValue)
        {
            Volatile.Write(ref _slotsSinceStart, _slotsSinceStart + 1);
            if (_measureFromNs == long.MinValue && decision.TargetTimeNs >= _playbackStartNs + _warmupNs)
            {
                // The measured window starts on this thread's clock (the virtual clock runs far faster than real time).
                GcAtMeasure = Platform.Runtime.GcSnapshot.Take();
                Meter.Begin();
                foreach (var meter in OtherMeters)
                {
                    meter.Begin();
                }

                Volatile.Write(ref _measureFromNs, _playbackStartNs + _warmupNs);
            }
        }

        var record = new DecisionRecord
        {
            Decision = decision,
            Outcome = outcome,
            Write = write,
            CaptureNs = capture,
            ArrivalNs = arrival,
            Multiplier = _scheduler.Diagnostics.Multiplier,
            Outstanding = write != 0 ? _bridge.Outstanding : 0,
            PresentLeadNs = _presentLeadNs,
            Quality = QualitySwitch?.Committed ?? FrameGenQuality.Default,
            SwitchPending = QualitySwitch is { Pending: true },
            QualityEpoch = QualitySwitch?.Epoch ?? 0,
        };
        if (!_decisions.TryEnqueue(record))
        {
            Volatile.Write(ref _recordsDropped, _recordsDropped + 1);
        }
    }

    private DecisionOutcome Produce(in SlotDecision decision, ref HeldFrame a, ref HeldFrame b, out ulong write)
    {
        write = 0;
        if (!WaitForPresentRoom())
        {
            Volatile.Write(ref _skippedBacklog, _skippedBacklog + 1);
            return DecisionOutcome.SkippedPresenterBacklog;
        }

        if (!AcquireSlot(out var slot))
        {
            Volatile.Write(ref _skippedBackPressure, _skippedBackPressure + 1);
            return DecisionOutcome.SkippedBackPressure;
        }

        _handedOut++;

        var outstanding = _bridge.Outstanding;
        if (outstanding > _maxOutstanding)
        {
            Volatile.Write(ref _maxOutstanding, outstanding);
        }

        UpdateCeiling();
        var job = new WriteJob
        {
            SlotIndex = decision.SlotIndex,
            Kind = decision.Kind == SlotKind.Generated ? WriteKind.Generated : WriteKind.Real,
            Slot = slot,
            Prev = a.Frame.Frame,
            Curr = b.Frame.Frame,
            Timestep = (float)decision.Timestep,
        };
        EnqueueOrWait(_writer.Jobs, job);
        _writer.Wake();

        // The frames' last reader is this write (writes are signalled in order, so it covers every earlier one too).
        a = a with { LastReader = slot.ProducerSignal, LastWrite = slot.Write };
        b = b with { LastReader = slot.ProducerSignal, LastWrite = slot.Write };
        write = slot.Write;
        _ = EnqueuePresent(
            decision,
            PresentKind.Write,
            // The slot carries the colour of the newer real frame (the content's, for the presenter to follow: a source
            // that switches between SDR and HDR10 marks the switch a discontinuity, so a generated frame never spans it).
            new PresentRequest { SlotIndex = decision.SlotIndex, Frame = slot.ConsumerView with { Color = b.Frame.Frame.Color }, TargetTimeNs = decision.TargetTimeNs + _presentLeadNs },
            slot.Write,
            a.ContentNs,
            b.ContentNs,
            force: true,
            breakBeforeB: (b.Frame.Marks & SourceFrameMarks.Discontinuity) != 0);
        return DecisionOutcome.Written;
    }

    /// <summary>
    /// Tells <see cref="CeilingPacingState"/> whether the slots are at the output's ceiling under variable refresh (the
    /// present thread reads it; nothing here decides). Allocation-free.
    /// </summary>
    private void UpdateCeiling()
    {
        if (CeilingPacing is not { } ceiling)
        {
            return;
        }

        if (_scheduler.ClockMode != ClockMode.Vrr)
        {
            ceiling.SetCondition(false, 0, 0); // fixed refresh: never at a ceiling (and nothing computed)
            return;
        }

        var d = _scheduler.Diagnostics;
        var multiplier = Math.Max(1, d.Multiplier);
        ceiling.SetCondition(
            d.ClockMode == ClockMode.Vrr && d.GenerationEnabled && d.Multiplier > 1,
            d.SourceIntervalNs / multiplier,
            _displayPeriodNs);
    }

    /// <summary>
    /// Real clock, every 100 ms: writes handed out and none of them finished for the stall timeout means the generator
    /// thread or its GPU work is stuck (a write waiting for a slot release that never comes, a generator that never
    /// returns). Playback would otherwise go on showing the last image for as long as it runs; it fails instead. (The
    /// virtual clock waits for the bridge and the source with the same bound.)
    /// </summary>
    private void CheckProgress()
    {
        var now = Monotonic.NowNs;
        if (_virtual || now < _nextProgressCheckNs)
        {
            return;
        }

        _nextProgressCheckNs = now + ProgressCheckNs;
        var outstanding = _bridge.Outstanding;
        var written = _handedOut - outstanding;
        if (outstanding == 0 || written != _writtenSeen)
        {
            _writtenSeen = written;
            _progressNs = now;
            return;
        }

        if (now - _progressNs > _stallTimeoutNs)
        {
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"No ring write finished for {_stallTimeoutNs / 1e9:0.#} s ({outstanding} handed out and unfinished): the generator thread or its GPU work is stuck."));
        }
    }

    /// <summary>Real clock: false when the bridge refuses (skip). Virtual clock: waits (bounded) until it hands one out.</summary>
    private bool AcquireSlot(out BridgeSlot slot)
    {
        if (_bridge.TryAcquireSlot(out slot))
        {
            return true;
        }

        if (!_virtual)
        {
            return false;
        }

        _phase = 5;
        var giveUp = Monotonic.NowNs + _stallTimeoutNs;
        while (!_bridge.TryAcquireSlot(out slot))
        {
            if (Monotonic.NowNs > giveUp || _stop)
            {
                throw new TimeoutException($"The bridge refused a slot for {_stallTimeoutNs / 1e9:0.#} s (virtual clock): the GPU is not finishing writes.");
            }

            Thread.Sleep(0);
        }

        return true;
    }

    /// <summary>Real clock: false when the present thread is too far behind. Virtual clock: waits (bounded) for it.</summary>
    private bool WaitForPresentRoom()
    {
        if (_presenter.Jobs.Count < PresentBacklogLimit)
        {
            return true;
        }

        if (!_virtual)
        {
            return false;
        }

        _phase = 6;
        var giveUp = Monotonic.NowNs + _stallTimeoutNs;
        while (_presenter.Jobs.Count >= PresentBacklogLimit)
        {
            if (Monotonic.NowNs > giveUp || _stop)
            {
                throw new TimeoutException($"The present thread took no present for {_stallTimeoutNs / 1e9:0.#} s (virtual clock).");
            }

            Thread.Sleep(0);
        }

        return true;
    }

    private bool EnqueuePresent(in SlotDecision decision, PresentKind kind, in PresentRequest request, ulong write, long contentA, long contentB, bool force = false, bool breakBeforeB = false)
    {
        if (!force && !WaitForPresentRoom())
        {
            Volatile.Write(ref _skippedBacklog, _skippedBacklog + 1);
            return false;
        }

        var job = new PresentJob
        {
            SlotIndex = decision.SlotIndex,
            Kind = kind,
            Request = request,
            Write = write,
            TargetNs = decision.TargetTimeNs + _presentLeadNs,
            DueNs = decision.TargetTimeNs,
            SlotKind = decision.Kind,
            FrameA = decision.FrameA,
            FrameB = decision.FrameB,
            Timestep = decision.Timestep,
            ContentNs = decision.ContentTimeNs,
            ContentA = contentA,
            ContentB = contentB,
            BreakBeforeB = breakBeforeB,
        };
        EnqueueOrWait(_presenter.Jobs, job);
        _presenter.Wake();
        return true;
    }

    /// <summary>
    /// The queues are sized above the bridge's lead and the present backlog limit, so they cannot be full here; should one
    /// be (a bug), wait for it boundedly rather than lose a write that must be signalled.
    /// </summary>
    private void EnqueueOrWait<T>(SpscRing<T> ring, in T item)
    {
        if (ring.TryEnqueue(item))
        {
            return;
        }

        _phase = 7;
        var giveUp = Monotonic.NowNs + _stallTimeoutNs;
        while (!ring.TryEnqueue(item))
        {
            if (Monotonic.NowNs > giveUp)
            {
                throw new TimeoutException("A pipeline queue stayed full: the generator or present thread is stuck.");
            }

            Thread.Sleep(0);
        }
    }

    private int Find(long frameId)
    {
        for (var i = 0; i < _heldCount; i++)
        {
            var index = (_heldHead + i) % _held.Length;
            if (_held[index].Frame.FrameId == frameId)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Gives back every held frame older than the oldest one the scheduler can still refer to, once the last write that
    /// reads it has been enqueued by the generator thread (until then the frame's Ready event must not be re-recorded:
    /// the source's Release contract is "no work enqueued after this call reads it").
    /// </summary>
    private void ReleaseUnreferenced()
    {
        var oldest = _scheduler.OldestReferencedFrameId;
        if (oldest < 0)
        {
            return;
        }

        var enqueued = _writer.EnqueuedWrite;
        while (_fedCount > 0)
        {
            ref var held = ref _held[_heldHead];
            if (held.Frame.FrameId >= oldest || held.LastWrite > enqueued)
            {
                break;
            }

            _source.Frames.Release(held.Frame.FrameId, held.LastReader);
            held = default;
            _heldHead = (_heldHead + 1) % _held.Length;
            _heldCount--;
            _fedCount--;
        }
    }

    /// <summary>The end of a file without looping: the last frame has been shown for one frame interval.</summary>
    private bool CheckEnd(long now)
    {
        if (_source.Failure is not null)
        {
            Ended = StopReason.Failure;
            return true;
        }

        if (!_sourceEnded && _source.IsEndOfStream && _fedCount == _heldCount)
        {
            _sourceEnded = true;
        }

        if (!_sourceEnded || _lastFrameContentNs == long.MinValue)
        {
            return false;
        }

        var interval = _scheduler.Diagnostics.SourceIntervalNs;
        if (now - _scheduler.PipelineDelayNs > _lastFrameContentNs + Math.Max(interval, 16 * Nanoseconds.PerMillisecond))
        {
            Ended = StopReason.EndOfFile;
            return true;
        }

        return false;
    }

    private bool CheckLimits(in SlotDecision decision)
    {
        if (_playbackStartNs == long.MinValue)
        {
            // Nothing shown yet: a run with a duration counts it from launch, so a source that never delivers a frame (a
            // capture with no buffers coming) ends with a reason instead of running for ever.
            if (_durationNs > 0 && _launchNs != long.MinValue && decision.TargetTimeNs - _launchNs >= _durationNs)
            {
                throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                    $"No frames arrived from the source in the run's {_durationNs / 1e9:0.###} s ({_framesRead} read, none shown): nothing to play."));
            }

            return false;
        }

        if (_maxSlots > 0 && _slotsSinceStart >= _maxSlots)
        {
            Ended = StopReason.SlotLimit;
            return true;
        }

        if (_durationNs > 0 && decision.TargetTimeNs - _playbackStartNs >= _durationNs)
        {
            Ended = StopReason.Duration;
            return true;
        }

        return false;
    }

    /// <summary>A frame the pacing thread holds: <see cref="ContentNs"/> is its content time on the playback's clock (the
    /// file's plus the start offset), the one the scheduler and every record use.</summary>
    private readonly record struct HeldFrame(SourceFrame Frame, long ContentNs, GpuSignal LastReader, ulong LastWrite = 0);
}
