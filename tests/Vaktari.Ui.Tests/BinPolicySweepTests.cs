using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// When a new bin policy is acted on.
///
/// **Saving one did nothing until the next hourly tick.** The sweep ran once at
/// startup and then on a one-hour DispatcherTimer, and the settings dialog knew
/// about neither — so setting "delete after 7 days" over a bin full of
/// three-week-old files, and pressing Save, changed nothing on screen and
/// nothing on disk for up to an hour. Which reads as a setting that does not
/// work; and to somebody who waited long enough, as one that works sometimes.
/// </summary>
public sealed class BinPolicySweepTests : IDisposable
{
    private readonly SettingsState _before = AppSettings.Current;
    private readonly ITrashMaintenance? _binBefore = PaneViewModel.Trash;

    public void Dispose()
    {
        AppSettings.Apply(_before);
        PaneViewModel.Trash = _binBefore;

        GC.SuppressFinalize(this);
    }

    /// <summary>Two policies that differ in the one field a person would set.</summary>
    private static SettingsState WithAge(int days) => AppSettings.Current with
    {
        Trash = AppSettings.Current.Trash with { DeleteOldFiles = true, DeleteAfterDays = days },
    };

    // ---- the watch itself ----------------------------------------------------

    /// <summary>The whole point: a changed policy is acted on now.</summary>
    [AvaloniaFact]
    public void A_new_bin_policy_sweeps_at_once()
    {
        var sweeps = 0;
        using var watch = new BinPolicyWatch(() => sweeps++);

        AppSettings.Apply(WithAge(7));

        Assert.Equal(1, sweeps);
    }

    /// <summary>
    /// **And nothing else is.** Every control on all six settings pages ends up
    /// raising the one Changed event, so without the comparison an unattended
    /// delete scan would run because somebody turned tooltips off.
    /// </summary>
    [AvaloniaFact]
    public void Changing_anything_else_does_not_sweep()
    {
        var sweeps = 0;
        using var watch = new BinPolicyWatch(() => sweeps++);

        AppSettings.Apply(AppSettings.Current with
        {
            General = AppSettings.Current.General with { ShowTooltips = false },
        });

        Assert.Equal(0, sweeps);
    }

    /// <summary>
    /// Pressing Save twice over the same policy sweeps once. The watch has to
    /// remember what it last swept under, or every subsequent save of any
    /// setting at all would sweep again.
    /// </summary>
    [AvaloniaFact]
    public void The_same_policy_saved_again_does_not_sweep_again()
    {
        var sweeps = 0;
        using var watch = new BinPolicyWatch(() => sweeps++);

        AppSettings.Apply(WithAge(7));
        AppSettings.Apply(WithAge(7));

        Assert.Equal(1, sweeps);
    }

    /// <summary>And a second, different policy is a second sweep.</summary>
    [AvaloniaFact]
    public void A_second_change_is_a_second_sweep()
    {
        var sweeps = 0;
        using var watch = new BinPolicyWatch(() => sweeps++);

        AppSettings.Apply(WithAge(7));
        AppSettings.Apply(WithAge(30));

        Assert.Equal(2, sweeps);
    }

    /// <summary>
    /// **Changed is static and outlives everything**, so a watch that never let
    /// go would keep sweeping — through a maintenance object belonging to an
    /// application that has closed.
    /// </summary>
    [AvaloniaFact]
    public void A_watch_that_has_been_let_go_stops_sweeping()
    {
        var sweeps = 0;
        var watch = new BinPolicyWatch(() => sweeps++);

        watch.Dispose();

        AppSettings.Apply(WithAge(7));

        Assert.Equal(0, sweeps);
    }

    // ---- and that the application actually has one ---------------------------

    /// <summary>Counts sweeps and answers nothing, which is all this needs.</summary>
    private sealed class CountingBin : ITrashMaintenance
    {
        internal int Sweeps;

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
        {
            Sweeps++;
            return ValueTask.FromResult(TrashSweepResult.Nothing);
        }

        public IReadOnlyList<TrashedItem> List() => [];

        public void Delete(string trashName) { }

        public string Restore(string trashName) => trashName;

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);
    }

    /// <summary>
    /// The wiring, through the real services rather than around them: the watch
    /// above is only worth anything if the application builds one. Goes through
    /// StartTrashMaintenance because that is the only door — it is where the
    /// hourly timer and the startup sweep are set up, and a watch created
    /// anywhere else would be a second answer to the same question.
    /// </summary>
    [AvaloniaFact]
    public async Task The_application_sweeps_when_the_policy_is_saved()
    {
        var bin = new CountingBin();
        var services = WindowServices.Create();

        try
        {
            services.StartTrashMaintenance(bin);

            var startup = await SweepsAfterSettling(bin);

            AppSettings.Apply(WithAge(7));

            Assert.Equal(startup + 1, await SweepsAfterSettling(bin));
        }
        finally
        {
            await Release(services);
        }
    }

    /// <summary>
    /// **And stops when the application lets go.** AppSettings.Changed is
    /// static and outlives every window, so a watch left subscribed would go on
    /// sweeping through a bin belonging to an application that has closed — and
    /// in this assembly, through a fake belonging to a test that has finished.
    /// </summary>
    [AvaloniaFact]
    public async Task And_stops_once_the_application_has_let_go()
    {
        var bin = new CountingBin();
        var services = WindowServices.Create();

        services.StartTrashMaintenance(bin);

        await Release(services);

        var settled = await SweepsAfterSettling(bin);

        AppSettings.Apply(WithAge(7));

        Assert.Equal(settled, await SweepsAfterSettling(bin));
    }

    /// <summary>
    /// Nothing awaits a sweep anywhere in the application — the timer, the
    /// startup call and the watch all discard the task — so the count is only
    /// meaningful once the dispatcher has run what they queued.
    /// </summary>
    private static async Task<int> SweepsAfterSettling(CountingBin bin)
    {
        await Task.Yield();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return bin.Sweeps;
    }

    /// <summary>
    /// The application-half teardown, which is the only door that disposes the
    /// watch. Null because no window was ever built: ReleaseAsync reads the
    /// argument for identity and list removal only, and with no windows at all
    /// it takes the last-one-out branch, which is the branch being tested.
    /// </summary>
    private static Task Release(WindowServices services) => services.ReleaseAsync(null!);
}
