using FrameMotionPlus.Core.Time;
using FrameMotionPlus.Platform.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>
/// The present thread's measurements before playback (the variable-refresh probe and the latch sweep) and its
/// variable-refresh presentation. The state is with the rest of the class.
/// </summary>
internal sealed partial class PresentLoop
{
    /// <summary>One step of the variable-refresh probe (<see cref="RequestVrrProbe"/>). Returns when to look again.</summary>
    private long VrrProbeStep(long now)
    {
        var period = (long)_grid.PeriodNs;
        if (_vrrProbeUntilNs == long.MinValue)
        {
            _vrrProbeUntilNs = now + Volatile.Read(ref _vrrProbeBudgetNs);
            _vrrProbeNextNs = now + (2 * period);
        }

        if (_vrrProbeCount >= _vrrProbeSubmit.Length || now > _vrrProbeUntilNs || period <= 0 || !_haveLast)
        {
            if (_inFlightCount > 0 && now <= _vrrProbeUntilNs + (100 * Nanoseconds.PerMillisecond))
            {
                return now + PollNs; // the last ones' reports
            }

            FinishVrrProbe(period);
            return now;
        }

        if (now < _vrrProbeNextNs)
        {
            return Math.Min(_vrrProbeNextNs, now + Nanoseconds.PerMillisecond);
        }

        if (_inFlightCount == 0 && Present(_lastRequest, -4, PresentKind.Repeat, 0, now, now, now, PresentOutcome.Probe, 0, default))
        {
            // Its submission as the in-flight entry has it (after the presenter's call), so the feedback finds it.
            _vrrProbeSubmit[_vrrProbeCount] = _inFlight[(_inFlightHead + _inFlightCount - 1) % InFlightCapacity].SubmitNs;
            _vrrProbeShown[_vrrProbeCount] = 0;
            _vrrProbeCount++;
        }

        // Off the grid: 1.37 and 1.71 refreshes in turn (never a whole number of refreshes apart).
        _vrrProbeNextNs = now + (long)((_vrrProbeCount % 2 == 0 ? 1.37 : 1.71) * period);
        return now + PollNs;
    }

    private void OnVrrProbeShown(long submitNs, long shownNs)
    {
        for (var i = _vrrProbeCount - 1; i >= 0; i--)
        {
            if (_vrrProbeSubmit[i] == submitNs)
            {
                _vrrProbeShown[i] = shownNs;
                return;
            }
        }
    }

    private void FinishVrrProbe(long period)
    {
        int pairs = 0, following = 0, onGrid = 0, timed = 0;
        var delays = new long[_vrrProbeCount];
        for (var i = 0; i < _vrrProbeCount; i++)
        {
            if (_vrrProbeShown[i] > 0)
            {
                delays[timed++] = _vrrProbeShown[i] - _vrrProbeSubmit[i];
            }

            if (i == 0 || _vrrProbeShown[i] <= 0 || _vrrProbeShown[i - 1] <= 0 || period <= 0)
            {
                continue;
            }

            pairs++;
            var shown = _vrrProbeShown[i] - _vrrProbeShown[i - 1];
            var submitted = _vrrProbeSubmit[i] - _vrrProbeSubmit[i - 1];
            following += Math.Abs(shown - submitted) < 250 * Nanoseconds.PerMicrosecond ? 1 : 0;
            var periods = shown / (double)period;
            onGrid += Math.Abs(periods - Math.Round(periods)) * period < 250_000 ? 1 : 0;
        }

        Array.Sort(delays, 0, timed);
        var p50 = timed > 0 ? delays[timed / 2] : 0;
        var max = timed > 0 ? delays[timed - 1] : 0;
        var active = pairs >= 6 && following * 5 >= pairs * 4 && following > onGrid;
        Volatile.Write(ref _vrrProbeResult, new VrrProbeResult(_vrrProbeCount, pairs, following, onGrid, p50, max, active));
        Volatile.Write(ref _vrrProbeDone, true);
    }

    /// <summary>
    /// One turn of variable-refresh presentation: each slot is handed over at its time (its target less the expected delay
    /// to the first pixel), repeats are not presented (the display holds the last image), and a slot that comes later
    /// than half the shortest refresh period after its target is given up. The presenter's in-flight cap still holds.
    /// </summary>
    private void VrrTurn(long now, DeadlineWaiter waiter)
    {
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
                    return;
                }
            }
        }

        if (!_havePending)
        {
            WaitForJob(now + (_inFlightCount > 0 ? PollNs : IdleWaitNs));
            return;
        }

        var half = (long)(_grid.PeriodNs / 2);
        if (_pending.Kind == PresentKind.PreRoll)
        {
            if (Submit(_pending, now, now, PresentOutcome.Shown, 0, default))
            {
                _havePending = false;
            }
            else
            {
                waiter.WaitUntil(now + PollNs);
            }

            return;
        }

        if (_pending.Kind == PresentKind.Repeat)
        {
            // Nothing new for this slot: the display holds the last image (presenting it again would only spend a refresh).
            Drop(_pending, now, _pending.TargetNs, PresentOutcome.DroppedAbsorbed);
            return;
        }

        // Its content is due at V (the pacing thread's target less its lead): handed over then, the first pixel follows.
        var handover = _pending.DueNs > 0 ? _pending.DueNs : _pending.TargetNs - _vrrLeadNs;
        if (now < handover - (50 * Nanoseconds.PerMicrosecond))
        {
            waiter.WaitUntil(Math.Min(handover, now + PollNs));
            return;
        }

        if (now > handover + half)
        {
            // Its first pixel could not come within half a refresh of its time: given up, a newer slot follows.
            CeilingPacing?.OnDroppedLate();
            Drop(_pending, now, now + _vrrLeadNs, PresentOutcome.DroppedLate);
            return;
        }

        if (_pending.Kind == PresentKind.Write && _pending.SlotKind == Core.Scheduling.SlotKind.Generated
            && CeilingPacing is { } ceiling && ceiling.TakeRetime(_pending.SlotIndex, now))
        {
            // At the output's ceiling the display has shown every present late (more than half a refresh) for a while and
            // cannot catch up by itself: this generated slot is not presented (the image before it stays one
            // refresh longer), so the next one finds the display free. Counted as a dropped slot.
            Drop(_pending, now, now + _vrrLeadNs, PresentOutcome.DroppedLate);
            return;
        }

        if (Submit(_pending, now, Math.Max(_pending.TargetNs, now + _vrrLeadNs), PresentOutcome.Shown, 0, default))
        {
            _havePending = false;
            _accounting.OnOnTime();
        }
        else
        {
            waiter.WaitUntil(now + PollNs);
        }
    }

    /// <summary>
    /// A slot shown under variable refresh: judged against its own time (half the shortest refresh period either way).
    /// A late show is the pipeline's when its hand-over was late (handed over later than its target less the expected delay
    /// to the first pixel) by more than the display's excess (how much longer than that expected delay the first pixel took
    /// after the hand-over), or by more than a quarter of the tolerance (an eighth of the shortest refresh period: 1.04 ms at
    /// 119.88 Hz); otherwise the display's. The floor matters because a show is late only when the two add up to more than
    /// the tolerance: a hand-over 2 ms late at 119.88 Hz is late only with a display over 2.17 ms slow, the larger share,
    /// yet a pipeline 2 ms behind is a fault the gate must see. (Until 2026-11-19 any hand-over later than the expected
    /// delay made a late show the pipeline's: hand-overs ran 0.3 ms behind it on HDMI-A-1, so the display's late shows of
    /// a two-hour run, 3.75 ms or more over the expected delay, were all labelled "late present".) Its delay from hand-over to first pixel feeds the expected delay (the lead,
    /// <see cref="UpdateVrrLead"/>), sent to the pacing thread when it moves.
    /// </summary>
    private void OnShownVrr(in InFlight entry, long reportedNs, long shownNs, bool timed, bool untimed)
    {
        var period = (long)Math.Round(_grid.PeriodNs);
        var half = period / 2;
        var isMiss = false;
        var miss = MissKind.LatePresent;
        var ready = entry.ReadyNs > 0 ? entry.ReadyNs : reportedNs;
        var handover = Math.Max(entry.SubmitNs, ready);
        var handoverLead = entry.TargetNs - handover;
        if (entry.Kind != PresentKind.PreRoll)
        {
            if (untimed)
            {
                Volatile.Write(ref _untimedShows, _untimedShows + 1);
                miss = handoverLead >= _vrrLeadNs ? MissKind.Discarded : MissKind.NotShown;
            }
            else if (timed)
            {
                Volatile.Write(ref _timedShows, _timedShows + 1);
                if (entry.SlotIndex >= 0)
                {
                    var deviation = shownNs - entry.TargetNs;
                    if (deviation > half)
                    {
                        isMiss = true;
                        var handoverLateness = Math.Max(0, _vrrLeadNs - handoverLead);
                        var displayExcess = shownNs - handover - _vrrLeadNs;
                        miss = handoverLateness > displayExcess || handoverLateness > half / 4 ? MissKind.LatePresent : MissKind.DisplayLate;
                        Volatile.Write(ref _lateShows, _lateShows + 1);
                    }
                    else if (deviation < -half)
                    {
                        isMiss = true;
                        miss = MissKind.Early;
                        Volatile.Write(ref _earlyShows, _earlyShows + 1);
                    }

                    // Whether the display still follows the presents (episodes where it showed on its grid instead).
                    _vrrFollow?.OnShown(handover, shownNs, _grid.PeriodNs, deviation > half, _vrrLeadNs);

                    // At the output's ceiling: running a whole refresh behind (reported) and its recovery.
                    CeilingPacing?.OnShown(entry.SlotIndex, deviation, period, shownNs);
                }

                if (shownNs > handover)
                {
                    _vrrDelays[_vrrDelayHead] = shownNs - handover;
                    _vrrDelayHead = (_vrrDelayHead + 1) % _vrrDelays.Length;
                    _vrrDelayCount = Math.Min(_vrrDelayCount + 1, _vrrDelays.Length);
                    if (_vrrDelayHead % 64 == 0)
                    {
                        UpdateVrrLead();
                    }
                }
            }

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
            RequiredLeadNs = _vrrLeadNs,
            LeadNs = entry.LeadNs,
            IsMiss = isMiss,
            Miss = miss,
        });
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
    /// The expected delay from hand-over to first pixel (the lead under variable refresh: a slot's target is its hand-over
    /// plus this): the median of the last 256, moved at most 0.1 ms at a time and kept within 0.3 to 2 ms. A robust middle,
    /// not a tail: it is only where a slot's first pixel is expected (half the shortest refresh period either side is in
    /// time); a tail estimate jumped to its 4 ms bound after one stall of the compositor's and then judged every on-time slot
    /// early (the first variable-refresh capture run). Allocation-free (a counting pass over the ring).
    /// </summary>
    private void UpdateVrrLead()
    {
        // The median by bisection on the value: 16 passes over 256 delays.
        long lo = 0, hi = 8 * Nanoseconds.PerMillisecond;
        for (var pass = 0; pass < 16 && hi - lo > 10 * Nanoseconds.PerMicrosecond; pass++)
        {
            var mid = (lo + hi) / 2;
            var below = 0;
            for (var i = 0; i < _vrrDelayCount; i++)
            {
                below += _vrrDelays[i] <= mid ? 1 : 0;
            }

            if (below * 2 >= _vrrDelayCount)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        var step = 100 * Nanoseconds.PerMicrosecond;
        var lead = Math.Clamp(Math.Clamp(hi, _vrrLeadNs - step, _vrrLeadNs + step), 300 * Nanoseconds.PerMicrosecond, 2 * Nanoseconds.PerMillisecond);
        if (Math.Abs(lead - _vrrLeadNs) > 20 * Nanoseconds.PerMicrosecond)
        {
            _vrrLeadNs = lead;
            Volatile.Write(ref _currentLeadNs, lead);
            _forceGrid = true;
        }
    }

    /// <summary>
    /// One step of the latch sweep: a staircase on the hand-over time. Each probe (the last pre-roll image again) goes into
    /// an empty queue at R − x for a refresh R: shown at R means the compositor took it, so its latch is at most x before
    /// R; a refresh later, that it is more than x (shown a refresh early: x was more than a refresh plus the latch). x
    /// steps down 0.25 ms after a probe that made its refresh and up after one that did not, so the probes gather at the
    /// latch. Then the lead starts at the latest hand-over that missed plus <see cref="SweepMarginNs"/>. Returns when to
    /// look again.
    /// </summary>
    private long SweepStep(long now)
    {
        var period = (long)_grid.PeriodNs;
        if (_sweepUntilNs == long.MinValue)
        {
            _sweepUntilNs = now + Volatile.Read(ref _sweepBudgetNs);
            _sweepXNs = Math.Clamp((long)(1.2 * period), 3 * Nanoseconds.PerMillisecond, (2 * period) - Nanoseconds.PerMillisecond);
        }

        if (_sweepOut || _inFlightCount > 0)
        {
            return now + PollNs; // the probe out (or the pre-roll's last) is not shown yet
        }

        var enough = _sweepReversals >= 12 && _sweepProbes >= 24;
        if (enough || now > _sweepUntilNs || !_grid.HasPhase || period <= 0 || !_haveLast)
        {
            FinishSweep(now);
            return now;
        }

        if (_sweepAtNs == long.MinValue)
        {
            _sweepRefreshNs = _grid.NextAtOrAfter(now + _sweepXNs + Nanoseconds.PerMillisecond);
            _sweepAtNs = _sweepRefreshNs - _sweepXNs;
        }

        if (now < _sweepAtNs)
        {
            return Math.Min(_sweepAtNs, now + Nanoseconds.PerMillisecond);
        }

        if (now > _sweepAtNs + PollNs)
        {
            _sweepAtNs = long.MinValue; // woke too late for this one: aim at another refresh
            return now;
        }

        if (Present(_lastRequest, -3, PresentKind.Repeat, 0, _sweepRefreshNs, now, _sweepRefreshNs, PresentOutcome.Probe, 0, default))
        {
            _sweepOut = true;
            _sweepProbes++;
        }

        _sweepAtNs = long.MinValue;
        return now + PollNs;
    }

    /// <summary>A probe was shown: where, against the refresh it was handed over for.</summary>
    private void OnProbeShown(in InFlight entry, long shownNs, bool timed)
    {
        _sweepOut = false;
        var period = (long)_grid.PeriodNs;
        if (!timed || period <= 0)
        {
            return;
        }

        var handoverLead = entry.PredictedNs - Math.Max(entry.SubmitNs, entry.ReadyNs);
        var refreshes = (shownNs - entry.PredictedNs + (period / 2)) / period;
        if (shownNs < entry.PredictedNs - (period / 2))
        {
            refreshes = -1;
        }

        var step = 0;
        if (refreshes == 0)
        {
            _sweepMade++;
            _sweepMadeMin = Math.Min(_sweepMadeMin, handoverLead);
            step = -1;
        }
        else if (refreshes > 0)
        {
            _sweepLate++;
            _sweepLateMax = Math.Max(_sweepLateMax, handoverLead);
            step = 1;
        }
        else
        {
            _sweepEarly++;
            step = -1;
        }

        if (_sweepLastStep != 0 && step != _sweepLastStep)
        {
            _sweepReversals++;
        }

        _sweepLastStep = step;
        _sweepXNs = Math.Clamp(_sweepXNs + (step * 250 * Nanoseconds.PerMicrosecond), 250 * Nanoseconds.PerMicrosecond, (2 * period) - Nanoseconds.PerMillisecond);
    }

    private void FinishSweep(long now)
    {
        var bracketed = _sweepLate > 0 && _sweepMade > 0;
        var lead = bracketed ? Math.Clamp(_sweepLateMax + SweepMarginNs, _latchLeadNs, MaxLead(_grid.Rate)) : _lead.BaseLeadNs;
        var result = new LatchSweepResult(_sweepProbes, _sweepMade, _sweepLate, _sweepEarly, bracketed ? _sweepLateMax : 0, bracketed ? _sweepMadeMin : 0, lead, SweepMarginNs, bracketed);
        if (bracketed && _configuredLeadNs < 0 && _lead.Adaptive)
        {
            _lead.Reset(lead, MaxLead(_grid.Rate), now, LeadChange.Measured);
            _forceGrid = true;
        }

        Volatile.Write(ref _sweepResult, result);
        Volatile.Write(ref _sweepDone, true);
    }
}
