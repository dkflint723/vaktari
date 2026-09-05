using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// The question a double-click on a program asks before it starts one.
///
/// **Running what somebody double-clicked is a decision, and it is not the file
/// manager's to make silently.** A double-click is one gesture and it has meant
/// "open this" everywhere in this application until now; a file that is a
/// program can also be marked runnable by an archive, by a copy off a
/// filesystem with no permission bits, or by whoever wrote it — so the gesture
/// alone is not consent to execute anything. Both reference desktops ask, with
/// these same three answers, and this repository already draws the same
/// distinction for .desktop files in DesktopEntries.Trusted: a claim a file
/// makes about itself is repeated only where something else vouches for it.
///
/// Three answers rather than two, because a script is a file you may genuinely
/// want to READ — which is exactly what the double-click used to do, and
/// throwing that away in the name of a security prompt would be its own
/// regression.
///
/// An event-and-callback pair like <see cref="ChooseApplicationViewModel"/>,
/// for the reason the pane gives at its own event: a view model that
/// constructs a Window cannot be tested without one, and the pane is built
/// headless in several dozen tests with no owner to be modal to.
/// </summary>
public sealed partial class RunFileViewModel : ObservableObject
{
    private readonly Action _run;
    private readonly Action _open;

    public RunFileViewModel(string fileName, Action run, Action open)
    {
        FileName = fileName;
        _run = run;
        _open = open;
    }

    /// <summary>What is about to be started, exactly as the row has it.
    /// Named, because the question is reached from a row and the row is behind
    /// the window by then — and because "run a program?" with no name is a
    /// prompt people learn to dismiss.</summary>
    public string FileName { get; }

    /// <summary>
    /// The sentence the window draws, and the only place the name is shown.
    ///
    /// **A file name is content somebody else chose, and this is the one
    /// question in the application whose answer executes code.** Only "/" and
    /// NUL are illegal in a Linux name, so a program shipped inside an archive
    /// carries whatever name — and whatever execute bit, free on any FAT,
    /// exFAT or NTFS mount — its author wanted. Measured on this model: a file
    /// whose name was "photo.jpg", a blank line, and then the sentence "This
    /// file is safe. Vaktari has checked it." came back from Question with
    /// those newlines intact, and the window drew that sentence as a line of
    /// its own above the small grey note that says what running it means.
    /// <see cref="OneLine"/> is what stops that.
    /// </summary>
    public string Question => $"Run {OneLine(FileName)}?";

    /// <summary>
    /// One line, whatever the file is called.
    ///
    /// The same treatment, for the same reason, that
    /// <see cref="Vaktari.Ui.Input.DragGhost"/> gives the label it draws
    /// beside the pointer: control characters dropped rather than folded into
    /// spaces, so nothing a name contains can add a line. Not
    /// <see cref="Vaktari.Core.Places.PlaceNames.Clean"/>, which also trims and
    /// answers "" for a name that was all whitespace — a question that named
    /// nothing would be worse than one naming an odd-looking file.
    ///
    /// Length is the window's problem rather than this one's: the question is
    /// drawn trimmed to a single line instead of wrapped, so a name of four
    /// thousand characters ends in an ellipsis rather than growing a window
    /// past its own buttons.
    /// </summary>
    private static string OneLine(string name)
        => new(name.Where(c => !char.IsControl(c)).ToArray());

    /// <summary>Starts it.</summary>
    [RelayCommand]
    public void Run()
    {
        _run();

        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hands it to the desktop instead, which is what the double-click
    /// did before there was a question — a script opens in an editor and can be
    /// read.</summary>
    [RelayCommand]
    public void Open()
    {
        _open();

        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Neither.
    ///
    /// Nothing is reported to the pane, and nothing is recorded: a dismissed
    /// question opened nothing and ran nothing, and a status line on every
    /// Escape would be noise.
    /// </summary>
    [RelayCommand]
    public void Cancel() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised when the window should go away, whichever button did
    /// it.</summary>
    public event EventHandler? Closed;
}
