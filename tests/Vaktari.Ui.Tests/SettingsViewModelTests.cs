using Avalonia.Headless.XUnit;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The Colour controls, tested where they actually connect.
///
/// **A settings control fails silently in one specific way**: it renders, it
/// accepts a click, it saves — and the value never reaches the thing it was
/// supposed to change. That is exactly how the font setting shipped broken, and
/// nothing in a build or a test run said so.
///
/// So these go through the real surface the markup binds to. The ComboBox binds
/// SelectedIndex to <c>ThemeModeIndex</c> and the checkbox binds IsChecked to
/// <c>FollowDesktopColours</c>; the dialog's Save button invokes SaveCommand and
/// the window reads <c>Result</c>. Both directions are checked, because loading
/// the wrong index is the failure that quietly rewrites a user's choice the
/// moment they open the dialog and press Save.
/// </summary>
public class SettingsViewModelTests
{
    private static SettingsState With(ThemeMode mode, bool followColours) => new()
    {
        Views = new ViewSettings { ThemeMode = mode, FollowDesktopColours = followColours },
    };

    [AvaloniaTheory]
    [InlineData(ThemeMode.FollowDesktop, 0)]
    [InlineData(ThemeMode.Light, 1)]
    [InlineData(ThemeMode.Dark, 2)]
    public void The_saved_mode_selects_the_matching_row(ThemeMode mode, int expected)
    {
        var vm = new SettingsViewModel(With(mode, followColours: false));

        Assert.Equal(expected, vm.ThemeModeIndex);
    }

    [AvaloniaTheory]
    [InlineData(0, ThemeMode.FollowDesktop)]
    [InlineData(1, ThemeMode.Light)]
    [InlineData(2, ThemeMode.Dark)]
    public void The_selected_row_is_what_gets_saved(int index, ThemeMode expected)
    {
        var vm = new SettingsViewModel(With(ThemeMode.FollowDesktop, followColours: false))
        {
            ThemeModeIndex = index,
        };

        vm.SaveCommand.Execute(null);

        Assert.Equal(expected, vm.Result.Views.ThemeMode);
    }

    /// <summary>
    /// Opening the dialog and pressing Save without touching anything must give
    /// back what was there. This is the regression that matters most for a
    /// setting with no control for a long time: a value only reachable by
    /// hand-editing the file is exactly the value a round-trip is most likely to
    /// silently drop.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ThemeMode.Light, true)]
    [InlineData(ThemeMode.Dark, false)]
    [InlineData(ThemeMode.FollowDesktop, true)]
    public void Saving_without_changing_anything_preserves_both(ThemeMode mode, bool colours)
    {
        var vm = new SettingsViewModel(With(mode, colours));

        vm.SaveCommand.Execute(null);

        Assert.Equal(mode, vm.Result.Views.ThemeMode);
        Assert.Equal(colours, vm.Result.Views.FollowDesktopColours);
    }

    /// <summary>
    /// The footer names a real build, and names it by the same route
    /// <c>--version</c> does.
    ///
    /// **Two ways of asking the assembly its version is how a window and a
    /// command line end up disagreeing**, and a version display that is wrong is
    /// worse than none — it is quoted into bug reports and believed. So this
    /// checks the value is the one Program publishes, not merely that the
    /// property returns a non-empty string.
    /// </summary>
    [AvaloniaFact]
    public void The_footer_shows_the_same_build_as_the_command_line()
    {
        var vm = new SettingsViewModel(With(ThemeMode.FollowDesktop, followColours: false));

        Assert.Equal($"Vaktari {Vaktari.Ui.Program.Version}", vm.VersionLine);
        Assert.Equal(Vaktari.Ui.Program.RunningFrom, vm.VersionPath);

        // And that it is a version rather than the "unknown" fallback, which
        // would otherwise let the assertions above pass while the dialog showed
        // nothing useful.
        Assert.Matches(@"^Vaktari \d+\.\d+\.\d+$", vm.VersionLine);
    }

    /// <summary>
    /// The sentences the settings page actually shows, in full, for each
    /// platform's word.
    ///
    /// **Asserting the whole string rather than "contains Recycle Bin"**, since
    /// the failure this guards against is grammatical, not lexical: dropping an
    /// article gives "Moving files to Recycle Bin", and adding one where the
    /// platform does not gives "Empty the Recycle Bin…". Both contain the right
    /// noun and both read wrong.
    ///
    /// Naming is a process-wide static, so each case sets it and the class
    /// restores it — otherwise the first test to run would decide the words for
    /// every test after it.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Recycle Bin", "windows", "Moving files to the Recycle Bin",
        "Limit the Recycle Bin to a share of the disk", "Recycle Bin")]
    [InlineData("trash", "linux", "Moving files to the trash",
        "Limit the trash to a share of the disk", "Trash")]
    public void The_settings_page_uses_the_platform_word(
        string bin, string platform, string confirm, string limit, string title)
    {
        var previousBin = Core.Naming.BinName;
        var previousPlatform = Core.Naming.Platform;

        try
        {
            Core.Naming.Adopt(bin, platform);

            var vm = new SettingsViewModel(With(ThemeMode.FollowDesktop, followColours: false));

            Assert.Equal(confirm, vm.ConfirmTrashLabel);
            Assert.Equal(limit, vm.LimitBinLabel);
            Assert.Equal(title, vm.BinTitle);

            // And no other file manager gets named in copy that ships.
            Assert.DoesNotContain("Dolphin", vm.BinSweepExplanation, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Plasma", vm.BinSweepExplanation, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(bin, vm.BinSweepExplanation, StringComparison.Ordinal);
        }
        finally
        {
            Core.Naming.Adopt(previousBin, previousPlatform);
        }
    }

    /// <summary>
    /// **"Not covered" was not the whole story on Linux.** A drive whose own
    /// trash cannot be used — its top folder is not this user's to write in,
    /// or it will not say where it is mounted — sends what is deleted from it
    /// to the home trash (XdgTrash.PrepareRoot, RootFor), which the sweep
    /// does cover. Windows has no such fallback, and its sentence stays whole.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("trash", "linux", true)]
    [InlineData("Recycle Bin", "windows", false)]
    public void The_bin_page_says_where_another_drives_files_are_swept(string bin, string platform, bool fallsBack)
    {
        var previousBin = Core.Naming.BinName;
        var previousPlatform = Core.Naming.Platform;

        try
        {
            Core.Naming.Adopt(bin, platform);

            var vm = new SettingsViewModel(With(ThemeMode.FollowDesktop, followColours: false));

            Assert.Contains("are not covered", vm.BinSweepExplanation, StringComparison.Ordinal);
            Assert.Equal(fallsBack, vm.BinSweepExplanation.Contains("home folder", StringComparison.Ordinal));
        }
        finally
        {
            Core.Naming.Adopt(previousBin, previousPlatform);
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_desktop_colours_switch_saves_what_it_shows(bool on)
    {
        var vm = new SettingsViewModel(With(ThemeMode.FollowDesktop, !on))
        {
            FollowDesktopColours = on,
        };

        vm.SaveCommand.Execute(null);

        Assert.Equal(on, vm.Result.Views.FollowDesktopColours);
    }
}
