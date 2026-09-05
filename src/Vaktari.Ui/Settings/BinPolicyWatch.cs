using Vaktari.Core.Settings;

namespace Vaktari.Ui.Settings;

/// <summary>
/// Runs the bin sweep when the bin policy changes, and only then.
///
/// **A new policy did nothing until the next hourly tick.** The sweep ran once
/// at startup and then on a one-hour timer, and settings knew about neither —
/// so setting "delete after 7 days" and pressing Save left a bin full of
/// three-week-old files exactly as it was, for up to an hour, with nothing on
/// screen to say the setting had been read at all. The honest reading of that
/// is that it did not work; the second honest reading, from somebody who
/// waited, is that it works unpredictably.
///
/// **Hung on <see cref="AppSettings.Changed"/> rather than on the settings
/// dialog closing.** Apply is the one door every route goes through — the
/// dialog's Save, and replacing the settings from an exported file, which
/// applies the same way and would otherwise have the same hour-long silence.
/// One subscription covers both and whatever else ever calls Apply.
/// </summary>
internal sealed class BinPolicyWatch : IDisposable
{
    private readonly Action _sweep;
    private readonly EventHandler _onChanged;

    private TrashSettings _swept;

    internal BinPolicyWatch(Action sweep)
    {
        _sweep = sweep;
        _swept = AppSettings.Current.Trash;

        _onChanged = (_, _) => Check();
        AppSettings.Changed += _onChanged;
    }

    /// <summary>
    /// Every control on all six settings pages ends up raising this one event,
    /// so the comparison is what keeps an unattended delete scan from running
    /// because somebody turned tooltips off.
    ///
    /// A whole-record comparison rather than a check of the field somebody
    /// remembered: TrashSettings is a record, so this covers the age, the size
    /// limit and what to do when it is reached, and stays right when a seventh
    /// field is added to it.
    /// </summary>
    private void Check()
    {
        var now = AppSettings.Current.Trash;

        if (now == _swept) return;

        // Before the sweep, not after: the sweep is asynchronous everywhere it
        // is actually used, so "after" is a promise about an ordering nothing
        // here controls.
        _swept = now;

        _sweep();
    }

    /// <summary>
    /// <see cref="AppSettings.Changed"/> is static and outlives everything, so
    /// a watch that never let go would keep sweeping — through a maintenance
    /// object belonging to an application that has closed.
    /// </summary>
    public void Dispose() => AppSettings.Changed -= _onChanged;
}
