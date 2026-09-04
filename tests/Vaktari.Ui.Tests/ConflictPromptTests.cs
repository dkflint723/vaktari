using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Being asked what to do when something is already there.
///
/// **Nobody ever was.** Copy and move have understood Overwrite, Skip, KeepBoth
/// and Cancel since they were written, and all five callers passed KeepBoth
/// outright — so a newer file dropped over an older one silently became
/// "name (1)", with no way to say what was actually wanted. These pin the
/// asking, and the one place where not asking is still correct.
/// </summary>
public sealed class ConflictPromptTests : IDisposable
{
    private readonly string _root;

    public ConflictPromptTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vaktari-conflict-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        PaneViewModel.AskConflict = null;

        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    private string Write(string name, string content, DateTime? written = null)
    {
        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, content);

        if (written is { } stamp) File.SetLastWriteTimeUtc(path, stamp);

        return path;
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);

        Directory.CreateDirectory(path);

        return path;
    }

    // ---- what the prompt says ---------------------------------------------

    /// <summary>
    /// **Both sides, because the decision is a comparison.** Which is newer and
    /// which is larger is the whole of what anybody needs to answer "replace?",
    /// and the callback used to carry only the destination — so a prompt built
    /// on it could not have shown this even if one had existed.
    /// </summary>
    [Fact]
    public void It_describes_both_files_and_says_which_is_newer()
    {
        var older = Write("target.txt", "old", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var newer = Write("source.txt", "much longer content",
            new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));

        var model = new ConflictViewModel(new FileConflict(newer, older));

        Assert.Contains("target.txt", model.Question, StringComparison.Ordinal);
        Assert.Contains("3 B", model.Existing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("19 B", model.Arriving, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The one arriving is newer.", model.Verdict);
    }

    [Fact]
    public void It_says_when_the_one_already_there_is_newer()
    {
        var newer = Write("target.txt", "a", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        var older = Write("source.txt", "b", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            "The one already there is newer.",
            new ConflictViewModel(new FileConflict(older, newer)).Verdict);
    }

    /// <summary>Same size, same timestamp: worth saying, because it means the
    /// answer probably does not matter.</summary>
    [Fact]
    public void It_says_when_the_two_look_identical()
    {
        var stamp = new DateTime(2026, 3, 3, 9, 0, 0, DateTimeKind.Utc);

        var a = Write("target.txt", "same", stamp);
        var b = Write("source.txt", "same", stamp);

        Assert.Equal("They look like the same file.", new ConflictViewModel(new FileConflict(b, a)).Verdict);
    }

    // ---- a folder is a merge, and says so ----------------------------------

    /// <summary>
    /// **The button said "Overwrite" and the engine merged.** For a folder its
    /// Overwrite arm is a bare break into CreateDirectory on a directory that
    /// already exists, so the destination keeps everything it had — measured in
    /// FolderCopyTests.Overwriting_a_folder_merges_it_and_asks_about_each_clash.
    /// "Overwrite" is a promise of the opposite, offered at the moment somebody
    /// is deciding exactly that.
    /// </summary>
    [Fact]
    public void The_button_says_merge_for_a_folder_and_overwrite_for_a_file()
    {
        var arriving = Folder("arriving");
        var there = Folder("there");

        Assert.Equal("Merge", new ConflictViewModel(new FileConflict(arriving, there)).OverwriteLabel);

        var a = Write("target.txt", "x");
        var b = Write("source.txt", "y");

        Assert.Equal("Overwrite", new ConflictViewModel(new FileConflict(b, a)).OverwriteLabel);
    }

    /// <summary>
    /// **A file arriving onto a folder is not a merge**, and the engine has no
    /// merging arm for it — it is a file write to a path a directory occupies.
    /// Naming it after the folder on one side would put a word on the button
    /// for a behaviour that path does not have.
    /// </summary>
    [Fact]
    public void A_file_arriving_where_a_folder_sits_is_not_called_a_merge()
    {
        var there = Folder("there");
        var arriving = Write("there.txt", "x");

        var model = new ConflictViewModel(new FileConflict(arriving, there));

        Assert.True(model.IsDirectory);
        Assert.False(model.Merges);
        Assert.Equal("Overwrite", model.OverwriteLabel);
    }

    /// <summary>
    /// **And the dialog really asks for the word.** Every assertion above is
    /// about the view model, and the button hard-coded "Overwrite" before this:
    /// the label could be right in the model and the window would go on saying
    /// the wrong thing for ever. Measured — putting the literal back left all
    /// nineteen tests in this file green.
    /// </summary>
    [Fact]
    public void The_button_takes_its_word_from_the_model()
    {
        var avalonia = (XNamespace)"https://github.com/avaloniaui";

        var buttons = XDocument.Parse(RepoSource.Ui("ConflictWindow.axaml"))
            .Descendants(avalonia + "Button")
            .Select(b => (string?)b.Attribute("Content"))
            .ToList();

        Assert.Contains("{Binding OverwriteLabel}", buttons);
        Assert.DoesNotContain("Overwrite", buttons);
    }

    /// <summary>
    /// Nor is a LINK to a folder, though Directory.Exists says yes to one. The
    /// plan classifies it as a link and copies the link itself, so merging is
    /// not what happens and the count would be of a tree nobody is copying.
    ///
    /// **Honest about its own reach:** creating a symbolic link needs Developer
    /// Mode or elevation on Windows, and this returns rather than failing where
    /// the privilege is absent — the SafeWalkTests precedent. On such a machine
    /// the mutation that drops the ReparsePoint clause is unobservable, and
    /// Vaktari.Ui.Tests is not in the Linux CI job that could observe it. The
    /// classification is right on the argument; the pin is best-effort.
    /// </summary>
    [AvaloniaFact]
    public void A_link_to_a_folder_is_not_called_a_merge()
    {
        var there = Folder("there");
        var target = Folder("real");

        var link = Path.Combine(_root, "linked");

        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        var model = new ConflictViewModel(new FileConflict(link, there));

        Assert.True(model.IsDirectory);
        Assert.False(model.Merges);
        Assert.Equal("Overwrite", model.OverwriteLabel);
    }

    /// <summary>
    /// **What is at stake, in a number.** Two folders are one line each — "12
    /// items · 3 Feb" against "40 items · 9 Aug" — and nothing in the prompt
    /// said how much of the two actually collides, which is the whole of what
    /// makes the answer matter.
    /// </summary>
    [Fact]
    public void It_says_what_merging_keeps_and_how_many_items_inside_collide()
    {
        var arriving = Folder("arriving");
        Write(Path.Combine("arriving", "notes.txt"), "new");
        Write(Path.Combine("arriving", "raw", "one.cr2"), "new");
        Write(Path.Combine("arriving", "fresh.txt"), "new");

        var there = Folder("there");
        Write(Path.Combine("there", "notes.txt"), "old");
        Write(Path.Combine("there", "raw", "one.cr2"), "old");

        var model = new ConflictViewModel(new FileConflict(arriving, there));

        // notes.txt, the raw folder, and raw\one.cr2.
        Assert.Equal(
            "Merging keeps what is already in the folder. "
            + "3 items arriving have the same name as something in it.",
            model.Verdict);
    }

    /// <summary>One is singular, and a prompt that says "1 items" reads like a
    /// machine talking.</summary>
    [Fact]
    public void One_collision_is_said_in_the_singular()
    {
        var arriving = Folder("arriving");
        Write(Path.Combine("arriving", "notes.txt"), "new");

        var there = Folder("there");
        Write(Path.Combine("there", "notes.txt"), "old");

        Assert.Equal(
            "Merging keeps what is already in the folder. "
            + "1 item arriving has the same name as something in it.",
            new ConflictViewModel(new FileConflict(arriving, there)).Verdict);
    }

    /// <summary>A merge that costs nothing is worth saying out loud: it means
    /// the answer does not matter, which is the same service the file prompt's
    /// "They look like the same file." does.</summary>
    [Fact]
    public void A_merge_with_nothing_in_common_says_so()
    {
        var arriving = Folder("arriving");
        Write(Path.Combine("arriving", "fresh.txt"), "new");

        var there = Folder("there");
        Write(Path.Combine("there", "of-its-own.txt"), "old");

        Assert.Equal(
            "Merging keeps what is already in the folder, "
            + "and nothing arriving has the same name as anything in it.",
            new ConflictViewModel(new FileConflict(arriving, there)).Verdict);
    }

    /// <summary>
    /// **A tree too big to count must not be reported as clash-free.** The walk
    /// stops at a thousand entries so the prompt still opens at once, and the
    /// first thousand of a large folder colliding with nothing says nothing
    /// about the rest — "nothing arriving has the same name" would be a claim
    /// the walk never checked. Past the ceiling the count is a floor, and both
    /// sentences say so.
    /// </summary>
    [Fact]
    public void Past_the_ceiling_the_count_is_given_as_a_floor()
    {
        var arriving = Folder("arriving");
        var there = Folder("there");

        for (var i = 0; i < FolderMerge.Ceiling + 1; i++) Write(Path.Combine("arriving", $"f{i}.txt"), "x");

        Assert.Equal(
            "Merging keeps what is already in the folder. "
            + "How much of it collides could not be counted.",
            new ConflictViewModel(new FileConflict(arriving, there)).Verdict);

        for (var i = 0; i < FolderMerge.Ceiling + 1; i++) Write(Path.Combine("there", $"f{i}.txt"), "x");

        Assert.Equal(
            "Merging keeps what is already in the folder. "
            + $"At least {FolderMerge.Ceiling} items arriving have the same name as something in it.",
            new ConflictViewModel(new FileConflict(arriving, there)).Verdict);
    }

    /// <summary>The file prompt's verdict is untouched by any of this — two
    /// folders and two files ask different questions.</summary>
    [Fact]
    public void A_file_still_gets_the_comparison_and_not_the_merge_sentence()
    {
        var older = Write("target.txt", "old", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var newer = Write("source.txt", "new", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            "The one arriving is newer.",
            new ConflictViewModel(new FileConflict(newer, older)).Verdict);
    }

    // ---- what the answer does ---------------------------------------------

    [Theory]
    [InlineData(ConflictResolution.Overwrite)]
    [InlineData(ConflictResolution.Skip)]
    [InlineData(ConflictResolution.KeepBoth)]
    public async Task A_choice_is_reported_with_whether_to_keep_asking(ConflictResolution chosen)
    {
        var a = Write("target.txt", "x");
        var b = Write("source.txt", "y");

        var model = new ConflictViewModel(new FileConflict(b, a)) { ApplyToRest = true };

        switch (chosen)
        {
            case ConflictResolution.Overwrite: model.OverwriteCommand.Execute(null); break;
            case ConflictResolution.Skip: model.SkipCommand.Execute(null); break;
            default: model.KeepBothCommand.Execute(null); break;
        }

        Assert.True(model.Answer.IsCompleted);
        Assert.Equal(new ConflictAnswer(chosen, true), await model.Answer);
    }

    /// <summary>
    /// **Cancel stops the operation, so "for the rest" is meaningless.**
    /// Reporting it as remembered would leave the closure holding Cancel and
    /// answering it for items that will never be reached.
    /// </summary>
    [Fact]
    public async Task Cancel_is_never_remembered()
    {
        var a = Write("target.txt", "x");
        var b = Write("source.txt", "y");

        var model = new ConflictViewModel(new FileConflict(b, a)) { ApplyToRest = true };

        model.CancelCommand.Execute(null);

        Assert.Equal(new ConflictAnswer(ConflictResolution.Cancel, false), await model.Answer);
    }

    /// <summary>Closing the window answers Cancel rather than nothing — an
    /// operation is waiting on this from a background thread.</summary>
    [Fact]
    public async Task An_unanswered_prompt_that_goes_away_cancels()
    {
        var a = Write("target.txt", "x");
        var b = Write("source.txt", "y");

        var model = new ConflictViewModel(new FileConflict(b, a));

        Assert.False(model.Answer.IsCompleted);

        model.Cancel();

        Assert.Equal(ConflictResolution.Cancel, (await model.Answer).Resolution);
    }

    // ---- how often it asks -------------------------------------------------

    /// <summary>
    /// **Asked once per clash, until told to stop.** The dangerous answer is
    /// the one given once and applied to five hundred files, so the memory is
    /// opt-in and lasts exactly one operation.
    /// </summary>
    [AvaloniaFact]
    public async Task It_asks_every_time_unless_told_to_apply_to_the_rest()
    {
        var asked = 0;

        PaneViewModel.AskConflict = _ =>
        {
            asked++;

            return ValueTask.FromResult(new ConflictAnswer(ConflictResolution.Overwrite, false));
        };

        var settle = Conflicts();

        for (var i = 0; i < 3; i++)
            Assert.Equal(ConflictResolution.Overwrite, await settle(Clash(i)));

        Assert.Equal(3, asked);

        asked = 0;
        PaneViewModel.AskConflict = _ =>
        {
            asked++;

            return ValueTask.FromResult(new ConflictAnswer(ConflictResolution.Skip, true));
        };

        settle = Conflicts();

        for (var i = 0; i < 3; i++)
            Assert.Equal(ConflictResolution.Skip, await settle(Clash(i)));

        Assert.Equal(1, asked);
    }

    /// <summary>
    /// **A remembered answer belongs to one operation.** Otherwise "overwrite
    /// the rest", said once about a folder of duplicates, would silently apply
    /// to the next paste an hour later.
    /// </summary>
    [AvaloniaFact]
    public async Task A_remembered_answer_does_not_outlive_its_operation()
    {
        var asked = 0;

        PaneViewModel.AskConflict = _ =>
        {
            asked++;

            return ValueTask.FromResult(new ConflictAnswer(ConflictResolution.Overwrite, true));
        };

        var first = Conflicts();
        await first(Clash(1));
        await first(Clash(2));

        var second = Conflicts();
        await second(Clash(3));

        Assert.Equal(2, asked);
    }

    /// <summary>With nothing to ask with — a headless run — the behaviour is
    /// what the application did before there was a prompt, which is the answer
    /// that destroys nothing.</summary>
    [AvaloniaFact]
    public async Task With_no_way_to_ask_nothing_is_overwritten()
    {
        PaneViewModel.AskConflict = null;

        Assert.Equal(ConflictResolution.KeepBoth, await Conflicts()(Clash(1)));
    }

    private FileConflict Clash(int i) =>
        new(Path.Combine(_root, $"s{i}.txt"), Path.Combine(_root, $"t{i}.txt"));

    /// <summary>Reaches the per-operation closure the pane builds.</summary>
    private static Func<FileConflict, ValueTask<ConflictResolution>> Conflicts() =>
        (Func<FileConflict, ValueTask<ConflictResolution>>)typeof(PaneViewModel)
            .GetMethod("Conflicts", System.Reflection.BindingFlags.NonPublic
                                    | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null)!;
}
