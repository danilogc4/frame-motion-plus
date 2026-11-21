using FrameMotionPlus.Core.Time;

namespace FrameMotionPlus.Pipeline;

/// <summary>
/// Variable refresh with the slots at the output's ceiling (no more than 1% longer than its shortest refresh period: 4x a
/// 29.97 fps console on a 119.88 Hz TV, 8x on 240.016 Hz). There a display that has fallen behind cannot catch up by itself:
/// each flip comes at least a shortest refresh period after the one before, so a show that is late stays late by as much
/// for as long as the slots keep coming at that rate, and every further delay adds to it (measured on HDMI-A-1 at 4x VFG
/// High: each frame's first pixel 2 to 3 ms late within a second of the start from the GPU being busy, then creeping later
/// by about 1 ms a minute, the console about 15 ppm faster than the TV's highest rate, until a pause in the game gives the
/// display time back; the owner's run sat a whole refresh behind for 196 s. On DP-3 at 8x, 0.1% under its ceiling, a
/// show 1 ms late takes about 200 slots to catch up).
/// <para>Two things, both on the present thread except the condition (the pacing thread's):</para>
/// <list type="bullet">
/// <item>Reporting (whenever the slots are at the ceiling): shows at least three quarters of a refresh after their slot's
/// time (about a whole refresh behind) in runs of <see cref="BehindShows"/> or more are counted as running behind at the
/// ceiling (how many, how many refreshes, for how long), apart from the other late shows.</item>
/// <item>Recovery (<see cref="PlaybackOptions.CeilingPacing"/>, whatever the generator: 4x Medium on the TV creeps later
/// just as High does): after <see cref="BehindShows"/> timed shows in a row late (more than half a refresh after their slot's time,
/// the accounting's own tolerance), the next generated slot is not presented (at most one every
/// <see cref="RetimeIntervalNs"/>), which gives the display the free refresh it needs. A left-out slot after which the
/// next show was neither half a refresh earlier nor on time (within a quarter of a refresh: a display really on a fixed
/// grid, or late from the GPU rather than from a backlog, where leaving a slot out changes nothing) stops further ones
/// until a show is on time again.</item>
/// </list>
/// Every field is written by one thread and read by the others; no lock, no allocation.
/// </summary>
internal sealed class CeilingPacingState
{
    /// <summary>Shows in a row late (recovery: more than half a refresh; reporting: three quarters) before either acts.</summary>
    public const int BehindShows = 30;

    /// <summary>At most one left-out slot this often.</summary>
    public const long RetimeIntervalNs = 5 * Nanoseconds.PerSecond;

    private readonly bool _recover;
    private int _atCeiling;
    private long _ceilingDecisions;

    // Present thread only.
    private int _lateRun;
    private int _run;
    private long _runStartNs;
    private long _runLastNs;
    private int _runMaxRefreshes;
    private bool _armed;
    private bool _blocked;
    private long _lastRetimeNs = long.MinValue;
    private long _retimedSlot = -1;
    private long _lateBeforeNs;

    // Present thread → others.
    private long _retimes;
    private long _regained;
    private long _notRegained;
    private long _behindShows;
    private long _behindEpisodes;
    private long _behindLongestNs;
    private int _behindMaxRefreshes;
    private int _behindNow;

    /// <param name="recover">Whether the recovery may leave slots out (<see cref="PlaybackOptions.CeilingPacing"/>);
    /// the reporting is the same either way.</param>
    public CeilingPacingState(bool recover) => _recover = recover;

    /// <summary>Whether the recovery is on (<c>--ceiling-pacing on</c>).</summary>
    public bool Recover => _recover;

    /// <summary>Whether the slots were at the output's ceiling at the latest decision (pacing thread → present thread).</summary>
    public bool AtCeiling => Volatile.Read(ref _atCeiling) != 0;

    /// <summary>Whether the recovery may act: on, and the slots at the ceiling.</summary>
    public bool Active => _recover && Volatile.Read(ref _atCeiling) != 0;

    /// <summary>Decisions made with the slots at the ceiling.</summary>
    public long CeilingDecisions => Volatile.Read(ref _ceilingDecisions);

    /// <summary>Generated slots left out to give the display a free refresh.</summary>
    public long Retimes => Volatile.Read(ref _retimes);

    /// <summary>Left-out slots after which the next show came at least half a refresh earlier.</summary>
    public long Regained => Volatile.Read(ref _regained);

    /// <summary>Left-out slots after which it did not (further ones stopped until a show was on time).</summary>
    public long NotRegained => Volatile.Read(ref _notRegained);

    /// <summary>Timed shows in runs of <see cref="BehindShows"/> or more at least three quarters of a refresh late, at the ceiling.</summary>
    public long BehindShowCount => Volatile.Read(ref _behindShows);

    /// <summary>Those runs.</summary>
    public long BehindEpisodes => Volatile.Read(ref _behindEpisodes);

    /// <summary>The longest of them, first show to last, ns.</summary>
    public long BehindLongestNs => Volatile.Read(ref _behindLongestNs);

    /// <summary>The most whole refreshes behind seen in them.</summary>
    public int BehindMaxRefreshes => Volatile.Read(ref _behindMaxRefreshes);

    /// <summary>Whole refreshes behind now (a run of <see cref="BehindShows"/> or more going on), else 0.</summary>
    public int BehindNow => Volatile.Read(ref _behindNow);

    /// <summary>
    /// Pacing thread, each decision that writes: whether the slots (<paramref name="slotNs"/> apart) are at the output's
    /// ceiling (no more than 1% longer than its shortest refresh period) under variable refresh with generation on.
    /// </summary>
    public void SetCondition(bool vrrGenerating, long slotNs, double shortestPeriodNs)
    {
        var atCeiling = vrrGenerating && IsAtCeiling(slotNs, shortestPeriodNs);
        Volatile.Write(ref _atCeiling, atCeiling ? 1 : 0);
        if (atCeiling)
        {
            Volatile.Write(ref _ceilingDecisions, _ceilingDecisions + 1);
        }
    }

    /// <summary>Slots no more than 1% longer than the shortest refresh period. Pure.</summary>
    public static bool IsAtCeiling(long slotNs, double shortestPeriodNs) =>
        slotNs > 0 && shortestPeriodNs > 0 && slotNs <= shortestPeriodNs * 1.01;

    /// <summary>
    /// Present thread: a timed show of slot <paramref name="slot"/>, its first pixel <paramref name="lateNs"/> after the
    /// slot's time (negative: early), the shortest refresh period <paramref name="periodNs"/>.
    /// </summary>
    public void OnShown(long slot, long lateNs, long periodNs, long shownNs)
    {
        if (periodNs <= 0 || Volatile.Read(ref _atCeiling) == 0)
        {
            EndRun();
            _lateRun = 0;
            _armed = false;
            _retimedSlot = -1;
            return;
        }

        var behind = lateNs * 4 > periodNs * 3;
        _lateRun = lateNs * 2 > periodNs ? _lateRun + 1 : 0;
        if (behind)
        {
            Behind(lateNs, periodNs, shownNs);
        }
        else
        {
            EndRun();
        }

        if (!Active)
        {
            _armed = false;
            _retimedSlot = -1;
            return;
        }

        if (_retimedSlot >= 0 && slot > _retimedSlot)
        {
            // The first show after the left-out slot: did the display take the free refresh (half a refresh earlier, or on
            // time again)?
            if (lateNs < _lateBeforeNs - (periodNs / 2) || lateNs * 4 < periodNs)
            {
                Volatile.Write(ref _regained, _regained + 1);
            }
            else
            {
                Volatile.Write(ref _notRegained, _notRegained + 1);
                _blocked = true;
            }

            _retimedSlot = -1;
        }

        if (lateNs * 4 < periodNs)
        {
            _blocked = false;
        }

        if (_armed && lateNs * 2 <= periodNs)
        {
            // The display got its time back by itself before the next generated hand-over (a hold in the source, a slot
            // the pacing thread skipped): nothing needs leaving out any more.
            _armed = false;
        }
        else if (_armed)
        {
            _lateBeforeNs = lateNs;
        }
        else if (_lateRun >= BehindShows && !_blocked && _retimedSlot < 0
            && (_lastRetimeNs == long.MinValue || shownNs - _lastRetimeNs >= RetimeIntervalNs))
        {
            _armed = true;
            _lateBeforeNs = lateNs;
        }
    }

    /// <summary>
    /// Present thread, at a generated slot's hand-over: whether to leave it out (armed, and the recovery may act). A real
    /// frame is never asked about.
    /// </summary>
    public bool TakeRetime(long slot, long nowNs)
    {
        if (!_armed)
        {
            return false;
        }

        _armed = false;
        if (!Active)
        {
            return false;
        }

        _lastRetimeNs = nowNs;
        _retimedSlot = slot;
        Volatile.Write(ref _retimes, _retimes + 1);
        return true;
    }

    /// <summary>Present thread: a slot was given up for being late (that frees a refresh too): an armed recovery stands down.</summary>
    public void OnDroppedLate() => _armed = false;

    private void Behind(long lateNs, long periodNs, long shownNs)
    {
        if (_run == 0)
        {
            _runStartNs = shownNs;
            _runMaxRefreshes = 0;
        }

        _run++;
        _runLastNs = shownNs;
        var refreshes = (int)Math.Max(1, (lateNs + (periodNs / 4)) / periodNs);
        _runMaxRefreshes = Math.Max(_runMaxRefreshes, refreshes);
        if (_run < BehindShows)
        {
            return;
        }

        if (_run == BehindShows)
        {
            Volatile.Write(ref _behindEpisodes, _behindEpisodes + 1);
            Volatile.Write(ref _behindShows, _behindShows + BehindShows);
        }
        else
        {
            Volatile.Write(ref _behindShows, _behindShows + 1);
        }

        Volatile.Write(ref _behindNow, refreshes);
        if (_runMaxRefreshes > _behindMaxRefreshes)
        {
            Volatile.Write(ref _behindMaxRefreshes, _runMaxRefreshes);
        }

        if (_runLastNs - _runStartNs > _behindLongestNs)
        {
            Volatile.Write(ref _behindLongestNs, _runLastNs - _runStartNs);
        }
    }

    private void EndRun()
    {
        _run = 0;
        if (_behindNow != 0)
        {
            Volatile.Write(ref _behindNow, 0);
        }
    }
}
