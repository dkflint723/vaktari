using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>What was chosen, and whether to stop asking.</summary>
public readonly record struct ConflictAnswer(ConflictResolution Resolution, bool ApplyToRest);

/// <summary>
/// The question nobody was ever asked.
///
/// **Every call site passed KeepBoth.** The engine has understood Overwrite,
/// Skip, KeepBoth and Cancel since it was written, and the callback that
/// chooses between them was hard-coded at all five places that build one — so
/// dropping a newer copy of a file over an older one silently produced
/// "name (1)", every time, with no way to say what was actually wanted.
///
/// Both sides are shown because the decision is a comparison. Which is newer
/// and which is larger is the whole of what somebody needs to answer
/// "replace?", and a prompt that names only the destination makes them go and
/// look.
/// </summary>
public sealed partial class ConflictViewModel : ObservableObject
{
    private readonly TaskCompletionSource<ConflictAnswer> _answered = new();

    public ConflictViewModel(FileConflict conflict)
    {
        Name = Path.GetFileName(conflict.Target);
        IsDirectory = Directory.Exists(conflict.Target);

        // **A real folder onto a folder is the only shape the engine merges.**
        // A file arriving where a folder sits is not one. Nor is a symbolic
        // link to a folder, though Directory.Exists says yes to one: the plan
        // classifies it as a link and copies the link itself, and SafeWalk
        // descends into whatever root it is handed — so calling that a merge
        // would both name a behaviour the path does not have and count a tree
        // that is not being copied.
        Merges = IsDirectory
                 && Directory.Exists(conflict.Source)
                 && (File.GetAttributes(conflict.Source) & FileAttributes.ReparsePoint) == 0;

        Existing = Describe(conflict.Target);
        Arriving = Describe(conflict.Source);

        Destination = PathRules.Parent(conflict.Target) ?? conflict.Target;

        // Said out loud rather than left to be worked out from two timestamps.
        // "Newer" is the reason somebody overwrites, and reading it off a pair
        // of dates is exactly the small friction a prompt exists to remove.
        //
        // For a folder there are no two timestamps worth comparing, and the
        // question is a different one — what a merge keeps, and how much of it
        // is going to be argued over.
        Verdict = Merges
            ? Merging(conflict.Source, conflict.Target)
            : Compare(conflict.Source, conflict.Target);
    }

    public string Name { get; }
    public bool IsDirectory { get; }
    public string Destination { get; }
    public string Existing { get; }
    public string Arriving { get; }
    public string Verdict { get; }

    /// <summary>
    /// Whether answering the middle button merges two trees rather than
    /// replacing one thing with another.
    /// </summary>
    public bool Merges { get; }

    /// <summary>
    /// **The button said "Overwrite" and the engine merged.** Its Overwrite arm
    /// for a folder is a bare break into Directory.CreateDirectory on a
    /// directory that already exists, so nothing already inside is removed and
    /// each colliding item inside comes back as its own conflict — measured in
    /// FolderCopyTests.Overwriting_a_folder_merges_it_and_asks_about_each_clash.
    /// A word that promises the destination is replaced is wrong about the one
    /// thing somebody is at that moment deciding.
    /// </summary>
    public string OverwriteLabel => Merges ? "Merge" : "Overwrite";

    public string Question => IsDirectory
        ? $"A folder called {Name} is already there."
        : $"A file called {Name} is already there.";

    /// <summary>
    /// **Off by default, and deliberately.** The dangerous answer is the one
    /// given once and applied to five hundred files, so applying to the rest is
    /// something to reach for rather than something to forget to turn off.
    /// </summary>
    [ObservableProperty] private bool _applyToRest;

    public Task<ConflictAnswer> Answer => _answered.Task;

    [RelayCommand] private void Overwrite() => Choose(ConflictResolution.Overwrite);
    [RelayCommand] private void KeepBoth() => Choose(ConflictResolution.KeepBoth);
    [RelayCommand] private void Skip() => Choose(ConflictResolution.Skip);

    /// <summary>Also what closing the window means: a decision not made is not
    /// a licence to overwrite.</summary>
    [RelayCommand] public void Cancel() => Choose(ConflictResolution.Cancel);

    private void Choose(ConflictResolution resolution)
    {
        // **Answered once, and Closed raised once.**
        //
        // The window closes when this fires, and closing the window answers
        // Cancel — so raising it unconditionally is a loop: choose, close,
        // cancel, close, for as long as the stack holds. The test that renders
        // this window hung outright rather than failing, which is how it was
        // found.
        //
        // Cancel stops the whole operation, so "for the rest" has no meaning
        // alongside it.
        if (!_answered.TrySetResult(new ConflictAnswer(
                resolution, resolution != ConflictResolution.Cancel && ApplyToRest)))
            return;

        Closed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Closed;

    private static string Describe(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                var count = directory.EnumerateFileSystemInfos().Take(1000).Count();

                return $"{count} item{(count == 1 ? "" : "s")} · "
                       + $"{directory.LastWriteTime:d MMM yyyy, HH:mm}";
            }

            var file = new FileInfo(path);

            return file.Exists
                ? $"{ByteSize.Format(file.Length)} · {file.LastWriteTime:d MMM yyyy, HH:mm}"
                : "not there any more";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "could not be read";
        }
    }

    /// <summary>
    /// What merging two folders will do, and how many items inside it is going
    /// to be an argument about.
    ///
    /// **Neither half of this was said anywhere.** Two folders of the same name
    /// are one line in the prompt — "12 items · 3 Feb 2026" against "40 items ·
    /// 9 Aug 2025" — and a button reading "Overwrite" beside them says the
    /// forty are about to go. They are not: the engine keeps every one of them
    /// and only argues about the names that collide, which is a number nobody
    /// could get from that line.
    ///
    /// It states no future prompts, deliberately. "You will be asked about each
    /// one" is false the moment "do the same for the rest" is ticked, and a
    /// count is true either way.
    /// </summary>
    private static string Merging(string arriving, string alreadyThere)
    {
        const string keeps = "Merging keeps what is already in the folder";

        var merge = FolderMerge.Between(arriving, alreadyThere);

        // **"Nothing collides" is a claim, and a cut-short walk cannot make
        // it.** The count stops at a thousand entries, and the first thousand
        // of a large tree colliding with nothing says nothing about the rest.
        if (merge.Clashes == 0)
            return merge.Partial
                ? keeps + ". How much of it collides could not be counted."
                : keeps + ", and nothing arriving has the same name as anything in it.";

        var many = merge.Clashes == 1
            ? "item arriving has the same name as something"
            : "items arriving have the same name as something";

        // A floor is worded as one.
        return merge.Partial
            ? $"{keeps}. At least {merge.Clashes} {many} in it."
            : $"{keeps}. {merge.Clashes} {many} in it.";
    }

    /// <summary>
    /// Which of the two is newer, or that they look identical — the one line
    /// that turns two rows of numbers into an answer.
    /// </summary>
    private static string Compare(string source, string target)
    {
        try
        {
            if (Directory.Exists(source) || Directory.Exists(target)) return "";

            var from = new FileInfo(source);
            var to = new FileInfo(target);

            if (!from.Exists || !to.Exists) return "";

            if (from.Length == to.Length && from.LastWriteTimeUtc == to.LastWriteTimeUtc)
                return "They look like the same file.";

            var difference = from.LastWriteTimeUtc - to.LastWriteTimeUtc;

            // A second either way is not a meaningful difference, and calling
            // it one would be a confident answer to a question nobody asked.
            if (difference.Duration() < TimeSpan.FromSeconds(2))
                return "Both were changed at the same time.";

            return difference > TimeSpan.Zero
                ? "The one arriving is newer."
                : "The one already there is newer.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
