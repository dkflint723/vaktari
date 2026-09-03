using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>One place in the navigation history, as a menu row.</summary>
public sealed record HistoryStep(int Depth, string FullPath, string Name, ICommand Open);

/// <summary>
/// Where Back and Forward can take you.
///
/// **They exposed one step each, out of a history the pane had kept all
/// along** — and had been writing to the session file since tabs became
/// restorable. Nothing anywhere read the stacks' contents, so a pane ten
/// folders deep could only be walked back out one press at a time, with no way
/// to see where the next press went. Both references put that list on the
/// button.
/// </summary>
public sealed partial class PaneViewModel
{
    /// <summary>
    /// How many rows a history menu shows. Deep enough to cover the walk that
    /// makes this worth having, short enough that the flyout does not become a
    /// scrolling list of its own.
    /// </summary>
    private const int HistoryDepth = 12;

    /// <summary>
    /// What a path is called in a menu: a virtual listing by its own name, and
    /// anything else by its leaf — LeafName hands a root back as itself, so a
    /// drive reads "C:\" rather than blank.
    /// </summary>
    public static string PlaceName(string path)
        => VirtualPaths.IsVirtual(path) ? VirtualPaths.Label(path) : PathRules.LeafName(path);

    /// <summary>
    /// A navigation stack as menu rows, nearest first.
    ///
    /// Static and pure — the caller hands over the command for a given depth —
    /// so what the menu will say, and where each row goes, can be read without
    /// a pane behind it.
    /// </summary>
    public static IReadOnlyList<HistoryStep> StepsFor(
        IEnumerable<string> stack, Func<int, ICommand> open)
    {
        var steps = new List<HistoryStep>();
        var depth = 0;

        foreach (var path in stack)
        {
            if (depth == HistoryDepth) break;

            depth++;
            steps.Add(new HistoryStep(depth, path, PlaceName(path), open(depth)));
        }

        return steps;
    }

    /// <summary>
    /// The rows behind the Back button, nearest first.
    ///
    /// A Stack enumerates top-first, which is exactly the order wanted: the
    /// place one press away comes first.
    /// </summary>
    public IReadOnlyList<HistoryStep> BackSteps
        => StepsFor(_back, depth => new RelayCommand(() => _ = GoBackAsync(depth)));

    public IReadOnlyList<HistoryStep> ForwardSteps
        => StepsFor(_forward, depth => new RelayCommand(() => _ = GoForwardAsync(depth)));

    public bool HasBackSteps => _back.Count > 0;
    public bool HasForwardSteps => _forward.Count > 0;

    /// <summary>
    /// Goes back several steps at once, keeping both stacks as a single press
    /// repeated would have left them — everything stepped over lands on the
    /// forward stack, in order, so Forward walks back through it.
    /// </summary>
    public async Task GoBackAsync(int depth)
    {
        if (depth < 1) return;

        string? target = null;

        for (var i = 0; i < depth && _back.Count > 0; i++)
        {
            if (target is not null) _forward.Push(target);
            else _forward.Push(CurrentPath);

            target = _back.Pop();
        }

        if (target is null) return;

        await LoadAsync(target).ConfigureAwait(false);
    }

    public async Task GoForwardAsync(int depth)
    {
        if (depth < 1) return;

        string? target = null;

        for (var i = 0; i < depth && _forward.Count > 0; i++)
        {
            if (target is not null) _back.Push(target);
            else _back.Push(CurrentPath);

            target = _forward.Pop();
        }

        if (target is null) return;

        await LoadAsync(target).ConfigureAwait(false);
    }

    /// <summary>The menus are rebuilt from the stacks, which change on every
    /// navigation.</summary>
    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(BackSteps));
        OnPropertyChanged(nameof(ForwardSteps));
        OnPropertyChanged(nameof(HasBackSteps));
        OnPropertyChanged(nameof(HasForwardSteps));
    }
}
