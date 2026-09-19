using Avalonia.Headless.XUnit;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the Size column shows for a folder, as a setting somebody can reach.
///
/// **One of the three modes had no control at all.** The dialog drew a tick
/// box, which can say "count them" or "say nothing" and has no way to say "how
/// big they are" — so <c>FolderSizeMode.ContentSize</c> sat in the settings
/// model, was written only by hand-editing settings.json, and the dialog
/// carefully preserved it rather than ever offering it. The reason given was
/// that it needed recursive summing nothing had; <c>SpaceUsage.Measure</c> is
/// that summing.
///
/// Three radios now, and these hold the two halves that matter: every mode
/// survives a round trip through the dialog, and each one picks its row.
/// </summary>
public sealed class FolderSizeSettingTests
{
    private static SettingsState With(FolderSizeMode folders) => new()
    {
        Views = new ViewSettings
        {
            Details = new DetailsViewSettings { FolderSize = folders },
        },
    };

    [AvaloniaTheory]
    [InlineData(FolderSizeMode.ItemCount)]
    [InlineData(FolderSizeMode.ContentSize)]
    [InlineData(FolderSizeMode.None)]
    public void Opening_the_dialog_and_saving_keeps_the_mode(FolderSizeMode folders)
    {
        var vm = new SettingsViewModel(With(folders));

        vm.SaveCommand.Execute(null);

        Assert.Equal(folders, vm.Result.Views.Details.FolderSize);
    }

    [AvaloniaTheory]
    [InlineData(FolderSizeMode.ItemCount, true, false, false)]
    [InlineData(FolderSizeMode.ContentSize, false, true, false)]
    [InlineData(FolderSizeMode.None, false, false, true)]
    public void The_saved_mode_selects_the_matching_row(
        FolderSizeMode folders, bool counts, bool contents, bool nothing)
    {
        var vm = new SettingsViewModel(With(folders));

        Assert.Equal(counts, vm.FolderSizeCounts);
        Assert.Equal(contents, vm.FolderSizeContents);
        Assert.Equal(nothing, vm.FolderSizeNothing);
    }

    /// <summary>
    /// **The half that was impossible.** Picking the middle row writes the mode
    /// that no control could previously write.
    /// </summary>
    [AvaloniaFact]
    public void Choosing_the_size_of_the_contents_saves_that_mode()
    {
        var vm = new SettingsViewModel(With(FolderSizeMode.ItemCount))
        {
            FolderSizeCounts = false,
            FolderSizeContents = true,
        };

        vm.SaveCommand.Execute(null);

        Assert.Equal(FolderSizeMode.ContentSize, vm.Result.Views.Details.FolderSize);
    }

    /// <summary>And back the other way, so the new rows are not one-way doors.</summary>
    [AvaloniaFact]
    public void Choosing_counts_again_saves_counts()
    {
        var vm = new SettingsViewModel(With(FolderSizeMode.ContentSize))
        {
            FolderSizeContents = false,
            FolderSizeCounts = true,
        };

        vm.SaveCommand.Execute(null);

        Assert.Equal(FolderSizeMode.ItemCount, vm.Result.Views.Details.FolderSize);
    }

    [AvaloniaFact]
    public void Choosing_nothing_saves_nothing()
    {
        var vm = new SettingsViewModel(With(FolderSizeMode.ContentSize))
        {
            FolderSizeContents = false,
            FolderSizeNothing = true,
        };

        vm.SaveCommand.Execute(null);

        Assert.Equal(FolderSizeMode.None, vm.Result.Views.Details.FolderSize);
    }
}
