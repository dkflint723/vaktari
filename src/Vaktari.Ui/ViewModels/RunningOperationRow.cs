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
/// that reaches THAT one.
///
/// **Built once per row rather than tracking the handle**, which is what its
/// sentence can honestly promise. The INTERFACE offers
/// <see cref="IOperationHandle.Progressed"/> and no member for state:
/// OperationHandle does raise a <c>StateChanged</c> of its own from SetState,
/// which every one of Begin, Pause, Resume, Complete, Cancelled and Failed goes
/// through, but it is declared on the concrete class, nothing in the repository
/// subscribes to it, and reaching past the interface for it is the leak the
/// platform split exists to prevent. So a row that said "paused" would need
/// that member promoted to IOperationHandle first, which this change
/// deliberately did not do: as things stand a row is rebuilt only when an
/// operation starts or ends, and would say "paused" only if a pause happened to
/// coincide with one. The progress line, the fraction and the speed stay on the
/// bar, where there is a subscription behind them.
/// </summary>
public sealed partial class RunningOperationRow
{
    private readonly IOperationHandle _handle;

    internal RunningOperationRow(IOperationHandle handle)
    {
        _handle = handle;
        Description = Describe(handle.Kind, handle.Paths);
        CanCancel = handle.CanCancel;
    }

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

    /// <summary>Stops THIS operation, which is the whole of the finding: the
    /// bar's Cancel reaches only the handle it happens to be following.</summary>
    [RelayCommand]
    private void Cancel() => _handle.Cancel();

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
