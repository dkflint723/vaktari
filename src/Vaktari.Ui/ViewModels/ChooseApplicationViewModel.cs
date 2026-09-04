using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Picking an application that is not in the "Open with" list.
///
/// **Drawn here because there is nothing to hand the job to.** Windows shows
/// its own "How do you want to open this file?" dialog and this window never
/// appears there; a desktop has no such command — xdg-open only ever launches
/// the default — so the alternative to this list was the one that shipped: an
/// "Open with" submenu with nothing in it, on exactly the file types where
/// somebody most needs a way out.
///
/// **One-shot, and deliberately.** Choosing here opens the file with what was
/// picked and changes nothing else. Picking a registered application from the
/// submenu this escapes from is already one-shot, so the two behave alike; and
/// the alternative — writing the type's default into mimeapps.list — changes
/// what every OTHER application on the machine does with that type, which is
/// not something a file manager should do as a side effect of opening one file.
/// The desktop's own settings page is where that decision is made, and Vaktari
/// reads what it writes.
/// </summary>
public sealed partial class ChooseApplicationViewModel : ObservableObject
{
    private readonly IReadOnlyList<LaunchOption> _all;
    private readonly Action<LaunchOption> _pick;

    public ChooseApplicationViewModel(
        string fileName, IReadOnlyList<LaunchOption> applications, Action<LaunchOption> pick)
    {
        _all = applications;
        _pick = pick;

        FileName = fileName;

        Refill();
    }

    /// <summary>What is being opened. Named in the window, because the chooser
    /// is reached from a menu on a row and by then the row is behind it.
    /// </summary>
    public string FileName { get; }

    public string Question => $"Open {FileName} with";

    /// <summary>The applications the filter leaves.</summary>
    public ObservableCollection<LaunchOption> Shown { get; } = new();

    /// <summary>
    /// **A filter, because the list is everything installed.** That is several
    /// hundred entries on an ordinary desktop, which is a list nobody reads —
    /// and the person opening this already knows the name of what they want,
    /// which is why they went looking past the registered ones.
    /// </summary>
    [ObservableProperty] private string _filter = "";

    partial void OnFilterChanged(string value) => Refill();

    [ObservableProperty] private LaunchOption? _selected;

    partial void OnSelectedChanged(LaunchOption? value) => OnPropertyChanged(nameof(CanOpen));

    public bool CanOpen => Selected is not null;

    /// <summary>
    /// The matches, with the selection kept where it survived and moved to the
    /// first row where it did not.
    ///
    /// **The old selection is read before the list is emptied.** A ListBox
    /// writes SelectedItem back the moment its source clears, so reading it
    /// afterwards reads the null the clear just produced — and the selection
    /// would be lost on every keystroke even when the chosen row is still
    /// there. Falling to the first match is what makes typing a name and
    /// pressing Enter open it.
    /// </summary>
    private void Refill()
    {
        var keep = Selected;
        var wanted = Filter.Trim();

        Shown.Clear();

        foreach (var option in _all)
        {
            // The id as well as the name: an application whose Name is
            // "Text Editor" is found by nobody typing "gedit" otherwise, and
            // the id is what the row shows underneath for that reason.
            if (wanted.Length > 0
                && !option.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                && !option.Id.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            Shown.Add(option);
        }

        Selected = keep is not null && Shown.Contains(keep) ? keep : Shown.FirstOrDefault();
    }

    /// <summary>Opens the file with what is selected, and closes.</summary>
    [RelayCommand]
    public void Open()
    {
        if (Selected is not { } chosen) return;

        _pick(chosen);

        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Leaves without opening anything.
    ///
    /// Nothing is reported to the pane: a dismissed chooser is not a failure,
    /// and the status bar saying so on every Escape would be noise.
    /// </summary>
    [RelayCommand]
    public void Cancel() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised when the window should go away, whichever button did
    /// it.</summary>
    public event EventHandler? Closed;
}
