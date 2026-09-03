namespace Vaktari.Core.FileSystem;

/// <summary>
/// How fast a transfer is going, and how much longer it has.
///
/// **A copy said nothing about how long it would take.** The bar counted items
/// and bytes — "34/1200  1.2 GiB/4.9 GiB" — and that is the one thing a person
/// can work out for themselves by looking twice. What they cannot work out is
/// whether this is a two-minute job or an hour, which is the whole question
/// behind "should I wait for it or go and do something else". Both references
/// show a speed and a time remaining.
///
/// A sliding window rather than an average over the whole run, because the
/// average is wrong exactly when it matters: a copy that ran at 400 MiB/s
/// across the SSD and then hit a USB stick at 8 keeps reporting a speed it will
/// never see again, and its estimate stays wildly optimistic for minutes.
///
/// The clock is injected because everything here is arithmetic about time, and
/// a test that has to sleep to observe it is a test that is slow and flaky at
/// once.
/// </summary>
public sealed class TransferRate(TimeSpan window, Func<DateTimeOffset> now)
{
    private readonly List<(DateTimeOffset At, long Bytes)> _samples = [];

    /// <summary>Five seconds: long enough to ride out one slow file, short
    /// enough that plugging into a slower disk shows up while you are still
    /// looking at the bar.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(5);

    public TransferRate() : this(DefaultWindow, () => DateTimeOffset.UtcNow) { }

    /// <summary>
    /// Takes a reading of the running total.
    ///
    /// The total rather than a delta: the handle counts bytes with an
    /// Interlocked.Add and reports the sum, so a difference computed here
    /// cannot go wrong the way one accumulated here could.
    /// </summary>
    public void Observe(long bytesDone)
    {
        var at = now();

        _samples.Add((at, bytesDone));

        Prune(at);
    }

    private void Prune(DateTimeOffset at)
    {
        // One older sample is kept deliberately — it is the far end of the
        // window, and dropping everything outside leaves nothing to measure
        // against until a second reading arrives inside it.
        var oldest = at - window;

        var keepFrom = 0;

        for (var i = 0; i < _samples.Count && _samples[i].At < oldest; i++) keepFrom = i;

        if (keepFrom > 0) _samples.RemoveRange(0, keepFrom);
    }

    /// <summary>
    /// Bytes a second over the window, or null when there is nothing to say.
    ///
    /// **Null when the last reading has aged out**, which is what a stall looks
    /// like: the engine reports on every buffer, every item start and every
    /// item finish, so a copy stuck inside one file reports nothing at all. A
    /// rate that kept its last value would sit there claiming 10 MiB/s while a
    /// drive that has given up moves nothing — which reads MORE alive than the
    /// bar did before any of this existed.
    /// </summary>
    public double? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2) return null;

            var newest = _samples[^1];
            var oldest = _samples[0];

            // Nothing since the window closed: whatever it was doing, it has
            // stopped telling us, and the honest answer is not a number.
            if (now() - newest.At > window) return null;

            var seconds = (newest.At - oldest.At).TotalSeconds;
            var moved = newest.Bytes - oldest.Bytes;

            // A report fired by an item starting or finishing carries no new
            // bytes, so two readings can share a total.
            if (seconds <= 0 || moved <= 0) return null;

            return moved / seconds;
        }
    }

    /// <summary>
    /// How much longer, or null when it cannot honestly be said.
    ///
    /// Null for a trash or a delete, which report a count and no bytes at all,
    /// and null once the byte count has caught up with the total — a copy at
    /// its last file is finishing, not "0 seconds left".
    /// </summary>
    public static TimeSpan? Remaining(long bytesDone, long bytesTotal, double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } rate || rate <= 0) return null;

        if (bytesDone >= bytesTotal) return null;

        return TimeSpan.FromSeconds((bytesTotal - bytesDone) / rate);
    }

    /// <summary>
    /// "about 4 min left" — deliberately vague, because the number is a guess
    /// made from the last five seconds and a precise-looking one invites
    /// somebody to believe it.
    /// </summary>
    public static string Describe(TimeSpan left)
    {
        if (left < TimeSpan.FromMinutes(1)) return "less than a minute left";

        if (left < TimeSpan.FromHours(1)) return $"about {left.TotalMinutes:F0} min left";

        if (left < TimeSpan.FromDays(1)) return $"about {left.TotalHours:F0} hr left";

        return "more than a day left";
    }
}
