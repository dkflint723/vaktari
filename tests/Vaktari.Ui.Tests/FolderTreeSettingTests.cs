using Avalonia.Headless.XUnit;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The folder tree as a setting somebody can reach.
///
/// **Reachable from the dialog or it is not a setting.** The folder-size mode
/// next to it sat in the model for the whole life of the feature, writable
/// only by hand-editing settings.json, because the dialog drew a control that
/// could not express it — and the dialog then preserved the value rather than
/// offering it. A tick box that never reaches the model is the same fault
/// wearing different clothes, so both directions are held here.
/// </summary>
public sealed class FolderTreeSettingTests
{
    private static SettingsState With(bool tree) => new()
    {
        Views = new ViewSettings { ShowFolderTree = tree },
    };

    /// <summary>
    /// **Off is the zero, deliberately.** A key absent from settings.json
    /// arrives as default(T) — the settings model records that measurement —
    /// so a property wanting to default ON would be decorative for every file
    /// written before it existed.
    /// </summary>
    [Fact]
    public void A_settings_file_that_never_heard_of_it_leaves_it_off()
        => Assert.False(new ViewSettings().ShowFolderTree);

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_saved_setting_selects_the_box(bool tree)
        => Assert.Equal(tree, new SettingsViewModel(With(tree)).ShowFolderTree);

    [AvaloniaFact]
    public void Ticking_the_box_saves_the_setting()
    {
        var vm = new SettingsViewModel(With(false)) { ShowFolderTree = true };

        vm.SaveCommand.Execute(null);

        Assert.True(vm.Result.Views.ShowFolderTree);
    }

    /// <summary>And back off again, so the box is not a one-way door.</summary>
    [AvaloniaFact]
    public void Clearing_the_box_saves_that_too()
    {
        var vm = new SettingsViewModel(With(true)) { ShowFolderTree = false };

        vm.SaveCommand.Execute(null);

        Assert.False(vm.Result.Views.ShowFolderTree);
    }

    /// <summary>Opening the dialog and pressing Save without touching anything
    /// gives back what was there — the regression that matters most for a
    /// setting whose control is new.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Saving_without_changing_anything_preserves_it(bool tree)
    {
        var vm = new SettingsViewModel(With(tree));

        vm.SaveCommand.Execute(null);

        Assert.Equal(tree, vm.Result.Views.ShowFolderTree);
    }
}
