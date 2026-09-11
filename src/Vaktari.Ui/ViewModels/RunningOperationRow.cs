using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// One running operation, as the list behind the transfer bar's count shows it.
///
/// **The bar is one operation and there was no way to reach any of the
/// others.** Nothing serialises them — the shell keeps a LIST of handles
/// precisely because a second operation used to take the first's slot — and the
/// bar follows the newest, so the line, the fraction, the speed, Pause and
/// Cancel all belonged to one handle while another went on writing. "2 running"
/// said there were others and named none of them, and the only Cancel in the
/// application reached <c>ActiveOperation</c> alone: the way to stop the copy
/// underneath was to wait for the one on top to finish.
///
/// A row per handle answers both halves — what each one is doing, and a cancel
/// and a pause that reach THAT one.
///
/// **A row could cancel and could not pause**, and the reason was on the
/// interface: it offered <see cref="IOperationHandle.Progressed"/> and no
/// member for state, so a row that said "Resume" had no way to learn its
/// operation had been paused — by its own button or by the bar's — and the
/// pause stayed the bar's, which reaches the newest handle only.
/// <see cref="IOperationHandle.StateChanged"/> is on the interface now, for
/// exactly this, and a row follows it for as long as it is shown: the list is
/// rebuilt whenever an operation starts or ends, and a row that has been
/// replaced lets go of its handle in <see cref="Dispose"/>. The progress line,
/// the fraction and the speed stay on the bar, where there is a subscription
/// behind them.
/// </summary>
public sealed partial class RunningOperationRow : ObservableObject, IDisposable
{
    private readonly IOperationHandle _handle;
    private readonly EventHandler _onStateChanged;

    internal RunningOperationRow(IOperationHandle handle)
    {
        _handle = handle;
        Description = Describe(handle.Kind, handle.Paths);
        CanCancel = handle.CanCancel;
        CanPause = handle.CanPause;
        _isPaused = handle.State == OperationState.Paused;

        // The engine sets state from its own thread and the label is read by
        // the view; a change made on the UI thread — a press on this row's
        // own button — lands at once rather than a dispatcher turn later.
        _onStateChanged = (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess()) Follow();
            else Dispatcher.UIThread.Post(Follow);
        };

        handle.StateChanged += _onStateChanged;
    }

    private void Follow() => IsPaused = _handle.State == OperationState.Paused;

    /// <summary>What this operation is doing, in words — "Copying 3 items to
    /// Photos", "Moving report.txt to the bin".</summary>
    public string Description { get; }

    /// <summary>
    /// Whether this row's Cancel means anything, asked of the handle rather
    /// than assumed.
    ///
    /// **A Windows recycle is one blocking SHFileOperation**: no loop between
    /// items to read a token in, so a Cancel on its row would accept the press
    /// and do nothing, which reads as the application being broken rather than
    /// as the operation being uninterruptible. The bar already asks the same
    /// question of the handle it follows; a row has to ask it of its own.
    /// </summary>
    public bool CanCancel { get; }

    /// <summary>Whether this row's Pause means anything — asked of the handle
    /// for the reason <see cref="CanCancel"/> is: the same blocking recycle
    /// has no gate to wait at.</summary>
    public bool CanPause { get; }

    /// <summary>Whether the operation is paused right now, from the handle's
    /// own state — set from its own button or from the bar's alike.</summary>
    [ObservableProperty] private bool _isPaused;

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseLabel));

    /// <summary>One button, two words — the operation's state decides which,
    /// the same as the bar's own button.</summary>
    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    /// <summary>Stops THIS operation, which is the whole of the finding: the
    /// bar's Cancel reaches only the handle it happens to be following.</summary>
    [RelayCommand]
    private void Cancel() => _handle.Cancel();

    /// <summary>Pauses or resumes THIS operation, whichever one the bar
    /// happens to be following.</summary>
    [RelayCommand]
    private void Pause()
    {
        if (_handle.State == OperationState.Paused) _handle.Resume();
        else _handle.Pause();
    }

    /// <summary>Lets go of the handle. A replaced row still subscribed would
    /// go on following a label nothing shows, for as long as the handle lived.</summary>
    public void Dispose() => _handle.StateChanged -= _onStateChanged;

    /// <summary>
    /// The sentence, from the verb the engine recorded and the paths it was
    /// given.
    ///
    /// **The count and the destination are both worth the room.** Two copies
    /// into the same folder are still told apart by their sizes on the bar, but
    /// a list that read "Copying" four times over would be no more use than the
    /// count it replaces. A single source is named outright — the leaf, since
    /// the full path is longer than a flyout row — and several are counted,
    /// which is the same choice the problems list under "Details" makes.
    ///
    /// Internal so it can be pinned without a shell: the whole of what a row
    /// says is decided here.
    /// </summary>
    internal static string Describe(OperationKind kind, IReadOnlyList<string> paths)
        => kind switch
        {
            // The destination is the last path, by the arrangement
            // IOperationHandle.Kind documents and all three builders spell —
            // the two engines and ElevatedRun, which never sees a copy or a
            // move without one.
            OperationKind.Copy => Transfer("Copying", paths),
            OperationKind.Move => Transfer("Moving", paths),

            // No second place: a trash and a delete carry sources only.
            OperationKind.Trash => paths.Count == 0
                ? "Moving to the bin"
                : $"Moving {Naming(paths)} to the bin",
            OperationKind.Delete => paths.Count == 0
                ? "Deleting"
                : $"Deleting {Naming(paths)}",

            // A handle nobody named. Measured: every construction site in this
            // repository sets a kind, so this arm is reached by handles built
            // in tests and by an implementor written outside the assembly — but
            // a row still appears for such a handle, because it is holding the
            // drive and worth cancelling, and inventing a verb for it would put
            // "Deleting" over a copy.
            _ => "Working",
        };

    /// <summary>
    /// A copy or a move: sources, then where they are going.
    ///
    /// Guarded against a handle that carries no paths at all — which
    /// <see cref="IOperationHandle.Paths"/> says is legal and means "nowhere in
    /// particular" — because <c>paths[^1]</c> on an empty list is an exception
    /// thrown while drawing a flyout. No engine builds a transfer that way; the
    /// guard is against the shape the type allows, not against a caller
    /// measured doing it.
    /// </summary>
    private static string Transfer(string verb, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return verb;

        var into = Leaf(paths[^1]);

        return paths.Count == 1
            ? $"{verb} to {into}"
            : $"{verb} {Naming(paths.Take(paths.Count - 1).ToList())} to {into}";
    }

    /// <summary>One thing by name, several by count.</summary>
    private static string Naming(IReadOnlyList<string> sources)
        => sources.Count == 1 ? Leaf(sources[0]) : $"{sources.Count} items";

    /// <summary>
    /// The last part of a path, for a row that has no room for the whole of
    /// one.
    ///
    /// A trailing separator is trimmed first, or a destination handed in as
    /// <c>D:\Photos\</c> would name nothing at all; a root, which has no leaf
    /// to find, keeps the path it came with.
    /// </summary>
    private static string Leaf(string path)
    {
        var name = Path.GetFileName(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return name.Length > 0 ? name : path;
    }
}
