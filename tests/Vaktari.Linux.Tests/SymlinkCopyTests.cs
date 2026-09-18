using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Copying and moving symbolic links.
///
/// **The link was followed instead of reproduced, and on a move that emptied
/// the real folder.** BuildPlan tested Directory.Exists — which a link to a
/// directory answers yes to — and then walked it with
/// SearchOption.AllDirectories, which follows links. So copying a folder that
/// held a link to a photo library duplicated the library, and MOVING it deleted
/// every file out of the library after copying them, because the walk had
/// enumerated the target's contents as though they lived inside the thing being
/// moved.
///
/// WindowsFileOperations plans links as leaves and reproduces them; the Linux
/// copy did neither. These run on Linux only: creating a symlink on Windows
/// needs Developer Mode, and the behaviour under test is what Linux does.
/// </summary>
public sealed class SymlinkCopyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-symlink-" + Guid.NewGuid().ToString("N"));

    private readonly string _from;
    private readonly string _into;
    private readonly string _library;

    public SymlinkCopyTests()
    {
        _from = Path.Combine(_root, "from");
        _into = Path.Combine(_root, "into");
        _library = Path.Combine(_root, "library");

        Directory.CreateDirectory(_from);
        Directory.CreateDirectory(_into);
        Directory.CreateDirectory(_library);

        File.WriteAllText(Path.Combine(_library, "photo.jpg"), "photo");
        File.WriteAllText(Path.Combine(_library, "another.jpg"), "another");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static async Task Run(IOperationHandle handle)
    {
        await handle.Completion;
        Assert.Equal(OperationState.Completed, handle.State);
    }

    private static ValueTask<ConflictResolution> Overwrite(FileConflict _)
        => ValueTask.FromResult(ConflictResolution.Overwrite);

    /// <summary>
    /// **The one that destroys data.** Moving a folder that contains a link to
    /// somewhere else must not touch what the link points at.
    /// </summary>
    [Fact]
    public async Task Moving_a_folder_holding_a_link_leaves_the_real_folder_alone()
    {
        if (!OperatingSystem.IsLinux()) return;

        var holder = Path.Combine(_from, "holder");
        Directory.CreateDirectory(holder);
        File.WriteAllText(Path.Combine(holder, "own.txt"), "own");
        Directory.CreateSymbolicLink(Path.Combine(holder, "photos"), _library);

        await Run(new LinuxFileOperations().Move([holder], _into, Overwrite));

        // The library still has everything it started with.
        Assert.Equal(
            ["another.jpg", "photo.jpg"],
            Directory.GetFiles(_library).Select(Path.GetFileName).Order());

        // And the link came across as a link, not as a copy of the contents.
        var landed = Path.Combine(_into, "holder", "photos");
        Assert.True(Directory.Exists(landed));
        Assert.Equal(_library, new DirectoryInfo(landed).LinkTarget);
    }

    /// <summary>Copying reproduces the link rather than the tree behind it.</summary>
    [Fact]
    public async Task Copying_a_link_to_a_folder_reproduces_the_link()
    {
        if (!OperatingSystem.IsLinux()) return;

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        await Run(new LinuxFileOperations().Copy([link], _into, Overwrite));

        var landed = Path.Combine(_into, "photos");

        Assert.Equal(_library, new DirectoryInfo(landed).LinkTarget);

        // One entry arrived — the link itself. A followed link would have
        // produced a real folder holding copies of the library's files.
        Assert.Single(new DirectoryInfo(_into).EnumerateFileSystemInfos());
        Assert.True(
            (File.GetAttributes(landed) & FileAttributes.ReparsePoint) != 0,
            "what landed should be a link, not a folder of copies");
    }

    /// <summary>A link to a FILE is reproduced too, and keeps its target text
    /// exactly — a relative link is usually relative on purpose.</summary>
    [Fact]
    public async Task A_relative_link_keeps_its_target_verbatim()
    {
        if (!OperatingSystem.IsLinux()) return;

        File.WriteAllText(Path.Combine(_from, "real.txt"), "real");
        File.CreateSymbolicLink(Path.Combine(_from, "alias.txt"), "real.txt");

        await Run(new LinuxFileOperations().Copy(
            [Path.Combine(_from, "alias.txt")], _into, Overwrite));

        Assert.Equal("real.txt", new FileInfo(Path.Combine(_into, "alias.txt")).LinkTarget);
    }

    /// <summary>
    /// Moving the link itself removes the link and nothing else — the file it
    /// pointed at stays exactly where it was.
    /// </summary>
    [Fact]
    public async Task Moving_a_link_moves_only_the_link()
    {
        if (!OperatingSystem.IsLinux()) return;

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        await Run(new LinuxFileOperations().Move([link], _into, Overwrite));

        Assert.False(Directory.Exists(link) || File.Exists(link), "the link should have gone");
        Assert.True(Directory.Exists(_library), "what it pointed at should not have");
        Assert.Equal(2, Directory.GetFiles(_library).Length);
    }

    // ---- a name already taken -----------------------------------------------

    private static ValueTask<ConflictResolution> Skip(FileConflict _)
        => ValueTask.FromResult(ConflictResolution.Skip);

    /// <summary>
    /// **Moving a link onto a name already taken deleted the link and wrote
    /// nothing.** CopyLink returned without a word when something was already
    /// there, and the arm above it deleted the source anyway — so a clash
    /// answered Overwrite left the bystander standing and the link gone, and
    /// the item was still recorded as landed. An undo built on that record
    /// would move the bystander to where the link had been.
    ///
    /// Answered Overwrite, the answer is carried out: the file that was there
    /// is replaced by the link. A FOLDER at that name is refused, below.
    /// </summary>
    [Fact]
    public async Task Moving_a_link_onto_a_taken_name_replaces_what_was_there()
    {
        if (!OperatingSystem.IsLinux()) return;

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        var taken = Path.Combine(_into, "photos");
        File.WriteAllText(taken, "somebody else's");

        await Run(new LinuxFileOperations().Move([link], _into, Overwrite));

        Assert.True(
            (File.GetAttributes(taken) & FileAttributes.ReparsePoint) != 0,
            "the answer was Overwrite, so the link should be standing there now");

        Assert.Equal(_library, new DirectoryInfo(taken).LinkTarget);
        Assert.False(Path.Exists(link), "the link was moved, so it should have gone");

        // And what it pointed at is untouched, as ever.
        Assert.Equal(2, Directory.GetFiles(_library).Length);
    }

    /// <summary>
    /// **A folder at the name is refused.** Moving a link onto one deleted the
    /// link and wrote nothing, as onto a file — measured. Replacing a file is
    /// what Overwrite asks for; a folder may hold anything, and emptying one to
    /// stand a link in its place is a far larger act than the answer gave.
    ///
    /// The source is left standing, and nothing is recorded: a link that was
    /// not written is not a landing.
    /// </summary>
    [Fact]
    public async Task Moving_a_link_onto_a_taken_folder_is_refused_with_a_sentence()
    {
        if (!OperatingSystem.IsLinux()) return;

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        var taken = Path.Combine(_into, "photos");
        Directory.CreateDirectory(taken);
        File.WriteAllText(Path.Combine(taken, "not-mine.txt"), "somebody else's");

        var handle = new LinuxFileOperations().Move([link], _into, Overwrite);

        await handle.Completion;

        Assert.True(Path.Exists(link), "nothing was written, so the link must still be here");
        Assert.Equal(
            "somebody else's", File.ReadAllText(Path.Combine(taken, "not-mine.txt")));

        Assert.Empty(handle.Landed);
        Assert.Contains("folder", Assert.Single(handle.Problems).Error.Message);
    }

    /// <summary>
    /// GUARD, not a test of this fix, and it says so. Answered Skip, the name
    /// keeps what it had, the link stays where it is, and nothing is reported
    /// landed — which was already true: the clash switch moves on to the next
    /// item before the link arm is reached, so this passed before any of this
    /// changed and no mutation of CopyLink can turn it red. It is here to catch
    /// Skip ever reaching the arm.
    /// </summary>
    [Fact]
    public async Task A_link_answered_Skip_stays_where_it_is()
    {
        if (!OperatingSystem.IsLinux()) return;

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        var taken = Path.Combine(_into, "photos");
        Directory.CreateDirectory(taken);
        File.WriteAllText(Path.Combine(taken, "not-mine.txt"), "somebody else's");

        var handle = new LinuxFileOperations().Move([link], _into, Skip);

        await handle.Completion;

        Assert.True(Path.Exists(link), "nothing landed, so the link must still be here");
        Assert.Equal(
            "somebody else's", File.ReadAllText(Path.Combine(taken, "not-mine.txt")));

        Assert.Empty(handle.Landed);
    }

    /// <summary>
    /// GUARD, not a test of this fix, and it says so. A copy answered Skip
    /// records no landing — already true before this change, since Skip never
    /// reaches the link arm. The copy's own fault, a landing recorded for a link
    /// that was never made, needed Overwrite to appear, and the test after this
    /// one is where that is pinned.
    /// </summary>
    [Fact]
    public async Task A_link_copied_and_answered_Skip_is_not_recorded()
    {
        if (!OperatingSystem.IsLinux()) return;

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        var taken = Path.Combine(_into, "photos");
        Directory.CreateDirectory(taken);
        File.WriteAllText(Path.Combine(taken, "not-mine.txt"), "somebody else's");

        var handle = new LinuxFileOperations().Copy([link], _into, Skip);

        await handle.Completion;

        Assert.Empty(handle.Landed);
        Assert.Equal(
            "somebody else's", File.ReadAllText(Path.Combine(taken, "not-mine.txt")));
    }

    /// <summary>
    /// **The copy's own half of the fault.** Answered Overwrite, the arm was
    /// reached, CopyLink declined in silence because the name was taken — the
    /// link was never written, measured — and the item was recorded as landed
    /// regardless. An undo of that copy would have taken away the bystander.
    /// </summary>
    [Fact]
    public async Task Copying_a_link_onto_a_taken_name_answered_Overwrite_replaces_it()
    {
        if (!OperatingSystem.IsLinux()) return;

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        var taken = Path.Combine(_into, "photos");
        File.WriteAllText(taken, "somebody else's");

        var handle = new LinuxFileOperations().Copy([link], _into, Overwrite);

        await handle.Completion;

        Assert.True(
            (File.GetAttributes(taken) & FileAttributes.ReparsePoint) != 0,
            "the answer was Overwrite, so the link should be standing there now");

        Assert.Equal(_library, new DirectoryInfo(taken).LinkTarget);

        // The copy leaves its source alone, always.
        Assert.True(Path.Exists(link), "a copy must not remove what it copied");

        Assert.Equal([taken], handle.Landed);
    }

    /// <summary>
    /// **What stood at the name survives a link that cannot be made.** The
    /// replacement is made first, under a staging name, and nothing at the name
    /// is touched until it exists — so a filesystem that refuses the link, as
    /// FAT and exFAT refuse every symbolic link, leaves the name as it was
    /// rather than leaving neither. The refusal is stood in for here, because
    /// no filesystem to hand refuses a link while letting the name be taken.
    /// </summary>
    [Fact]
    public async Task A_link_that_cannot_be_made_leaves_what_was_at_the_name()
    {
        if (!OperatingSystem.IsLinux()) return;

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        var taken = Path.Combine(_into, "photos");
        File.WriteAllText(taken, "somebody else's");

        var ops = new LinuxFileOperations
        {
            BeforeLinking = _ => throw new IOException("this filesystem takes no symbolic links"),
        };

        var handle = ops.Move([link], _into, Overwrite);

        await handle.Completion;

        Assert.Equal("somebody else's", File.ReadAllText(taken));
        Assert.True(new FileInfo(link).LinkTarget is not null, "the link was not made, so it must still be here");

        Assert.Empty(handle.Landed);
        Assert.Single(handle.Problems);

        // And nothing half-made is left beside it.
        Assert.Empty(Directory.GetFileSystemEntries(_into, ".*.vaktari-*"));
    }

    /// <summary>
    /// **A link at the name is replaced, like a file.** A link to a folder
    /// answers to Directory.Exists, and is still not a folder; replacing it
    /// removes the link and never what it pointed at.
    /// </summary>
    [Fact]
    public async Task Moving_a_link_onto_a_taken_link_replaces_it()
    {
        if (!OperatingSystem.IsLinux()) return;

        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "kept.txt"), "kept");

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        var taken = Path.Combine(_into, "photos");
        Directory.CreateSymbolicLink(taken, elsewhere);

        await Run(new LinuxFileOperations().Move([link], _into, Overwrite));

        Assert.Equal(_library, new DirectoryInfo(taken).LinkTarget);
        Assert.True(new FileInfo(link).LinkTarget is null, "the link was moved, so it should have gone");

        // Neither link's target was touched.
        Assert.Equal("kept", File.ReadAllText(Path.Combine(elsewhere, "kept.txt")));
        Assert.Equal(2, Directory.GetFiles(_library).Length);
    }

    /// <summary>An EMPTY folder at the name is refused as well: the rule is
    /// about what a folder is, not about what happens to be in it.</summary>
    [Fact]
    public async Task Moving_a_link_onto_a_taken_empty_folder_is_refused()
    {
        if (!OperatingSystem.IsLinux()) return;

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        var taken = Path.Combine(_into, "photos");
        Directory.CreateDirectory(taken);

        var handle = new LinuxFileOperations().Move([link], _into, Overwrite);

        await handle.Completion;

        Assert.True(new FileInfo(link).LinkTarget is not null, "nothing was written, so the link must still be here");

        Assert.True(
            Directory.Exists(taken) && new DirectoryInfo(taken).LinkTarget is null,
            "the empty folder at the name was replaced");

        Assert.Empty(handle.Landed);
        Assert.Contains("folder", Assert.Single(handle.Problems).Error.Message);
    }

    // ---- a name that IS what the link points at ------------------------------

    /// <summary>
    /// **Overwriting the very file a link points at destroyed it.** A link on
    /// the desktop to Documents/report.pdf, moved into Documents and answered
    /// Overwrite: the file at the name was the only copy of the data, it was
    /// removed to make room, and the link written there pointed at itself.
    /// Refused instead — the name is what the link is FOR.
    ///
    /// Both facts are read without following the link, since a link that points
    /// at itself cannot be followed.
    /// </summary>
    [Fact]
    public async Task A_link_moved_onto_the_file_it_points_at_leaves_that_file_alone()
    {
        if (!OperatingSystem.IsLinux()) return;

        var real = Path.Combine(_into, "report.pdf");
        File.WriteAllText(real, "the only copy");

        var link = Path.Combine(_from, "report.pdf");
        File.CreateSymbolicLink(link, real);

        var handle = new LinuxFileOperations().Move([link], _into, Overwrite);

        await handle.Completion;

        AssertLeftAlone(real, link, handle);
    }

    /// <summary>
    /// The same with a relative link, which names the file from where the link
    /// stands: "../into/report.pdf" from the source folder is the file at the
    /// name, and from the destination folder it is that name itself.
    /// </summary>
    [Fact]
    public async Task A_relative_link_moved_onto_the_file_it_points_at_leaves_that_file_alone()
    {
        if (!OperatingSystem.IsLinux()) return;

        var real = Path.Combine(_into, "report.pdf");
        File.WriteAllText(real, "the only copy");

        var link = Path.Combine(_from, "report.pdf");
        File.CreateSymbolicLink(link, Path.Combine("..", "into", "report.pdf"));

        var handle = new LinuxFileOperations().Move([link], _into, Overwrite);

        await handle.Completion;

        AssertLeftAlone(real, link, handle);
    }

    /// <summary>
    /// **Asked from where the link stands.** One folder deeper,
    /// "../../into/report.pdf" names the file at the name from where the link
    /// stands, and from the destination folder names a place outside this tree
    /// altogether — so only the question asked from the source catches it.
    /// </summary>
    [Fact]
    public async Task A_relative_link_from_a_deeper_folder_moved_onto_the_file_it_points_at_leaves_that_file_alone()
    {
        if (!OperatingSystem.IsLinux()) return;

        var real = Path.Combine(_into, "report.pdf");
        File.WriteAllText(real, "the only copy");

        var deeper = Path.Combine(_from, "nested");
        Directory.CreateDirectory(deeper);

        var link = Path.Combine(deeper, "report.pdf");
        File.CreateSymbolicLink(link, Path.Combine("..", "..", "into", "report.pdf"));

        var handle = new LinuxFileOperations().Move([link], _into, Overwrite);

        await handle.Completion;

        AssertLeftAlone(real, link, handle);
    }

    /// <summary>
    /// **Asked from where the link is going.** One folder deeper,
    /// "../into/report.pdf" names nothing that exists; moved into "into" onto a
    /// file called report.pdf, the same words name that very file. Replacing it
    /// would destroy the file and leave a link pointing at itself, and only the
    /// question asked from the destination catches it.
    /// </summary>
    [Fact]
    public async Task A_relative_link_that_would_point_at_itself_once_moved_leaves_the_file_at_the_name_alone()
    {
        if (!OperatingSystem.IsLinux()) return;

        var real = Path.Combine(_into, "report.pdf");
        File.WriteAllText(real, "the only copy");

        var deeper = Path.Combine(_from, "nested");
        Directory.CreateDirectory(deeper);

        var link = Path.Combine(deeper, "report.pdf");
        File.CreateSymbolicLink(link, Path.Combine("..", "into", "report.pdf"));

        var handle = new LinuxFileOperations().Move([link], _into, Overwrite);

        await handle.Completion;

        AssertLeftAlone(real, link, handle);
    }

    /// <summary>A copy reaches the same place: the source link survives a copy
    /// regardless, so what is at stake is the file it points at.</summary>
    [Fact]
    public async Task A_link_copied_onto_the_file_it_points_at_leaves_that_file_alone()
    {
        if (!OperatingSystem.IsLinux()) return;

        var real = Path.Combine(_into, "report.pdf");
        File.WriteAllText(real, "the only copy");

        var link = Path.Combine(_from, "report.pdf");
        File.CreateSymbolicLink(link, real);

        var handle = new LinuxFileOperations().Copy([link], _into, Overwrite);

        await handle.Completion;

        AssertLeftAlone(real, link, handle);
    }

    private static void AssertLeftAlone(string real, string link, IOperationHandle handle)
    {
        // Both in one assertion, so a failure reports each rather than stopping
        // at whichever is checked first.
        var becameLink = new FileInfo(real).LinkTarget is not null;
        var sourceKept = new FileInfo(link).LinkTarget is not null;

        Assert.True(
            !becameLink && sourceKept,
            $"the file at the name became a link: {becameLink}; the link kept at the source: {sourceKept}");

        Assert.Equal("the only copy", File.ReadAllText(real));
        Assert.Empty(handle.Landed);
    }

    // ---- the same file, reached another way ----------------------------------

    /// <summary>
    /// **The refusal above compared the spelling of two paths, so the same file
    /// reached by another name was destroyed anyway.** The question was
    /// <c>PathRules.Same(Path.GetFullPath(pointsAt, …), target)</c>, which is
    /// text and nothing else: it cannot say whether two names are one file on
    /// disk. A SYMLINKED folder is the everyday way to hold two names for one
    /// place — Fedora Atomic's /home is a link to var/home, and a pane opened
    /// through such a folder keeps the name it was opened by.
    ///
    /// So the file at the name was renamed aside and deleted to make room, a
    /// link pointing at itself was left standing in its place, and the move was
    /// reported Completed with the item landed. Measured in WSL Fedora by the
    /// review of this change, on a move and on a copy, through a folder link as
    /// here, through a chain of links, and through ".." past a link.
    /// </summary>
    /// <summary>
    /// **And the folder with two names can be the link's OWN folder.** Every
    /// alias test beside this one puts the second name on the DESTINATION; put
    /// it on the folder the link already lives in and the move is a move to
    /// where it already is, spelled differently.
    ///
    /// PathRules.Same compares path text, so the guard that catches a paste into
    /// the folder it already lives in does not fire. The name is then taken — by
    /// the link itself — so the clash is prompted, CopyLink does its replace
    /// dance on the link's own directory entry, and the unconditional delete
    /// after it removes what was just landed. The link is gone from both names
    /// while the operation reports Completed and Ctrl+Z says it undid a move.
    ///
    /// The same shape 3c9a45c was written to end, in the one case its four
    /// tests did not cover.
    /// </summary>
    [Fact]
    public async Task A_link_moved_into_its_own_folder_under_a_second_name_is_not_lost()
    {
        if (!OperatingSystem.IsLinux()) return;

        var link = Path.Combine(_from, "photos");
        Directory.CreateSymbolicLink(link, _library);

        // The link's OWN folder, under a second name.
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, _from);

        await Run(new LinuxFileOperations().Move([link], alias, Overwrite));

        // One entry under two spellings: it must still be there, and still be a
        // link to the library rather than a copy of it.
        Assert.True(
            Path.Exists(link) || Path.Exists(Path.Combine(alias, "photos")),
            "the link was deleted from the only folder it was ever in");

        Assert.Equal(_library, new DirectoryInfo(link).LinkTarget);

        // And the library it points at is untouched.
        Assert.Equal(
            ["another.jpg", "photo.jpg"],
            Directory.EnumerateFileSystemEntries(_library).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task A_link_moved_into_a_symlinked_folder_leaves_the_file_it_points_at_alone()
    {
        if (!OperatingSystem.IsLinux()) return;

        var real = Path.Combine(_into, "report.pdf");
        File.WriteAllText(real, "the only copy");

        // The same folder under a second name, which is all it takes.
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, _into);

        var link = Path.Combine(_from, "report.pdf");
        File.CreateSymbolicLink(link, real);

        var handle = new LinuxFileOperations().Move([link], alias, Overwrite);

        await handle.Completion;

        AssertLeftAlone(real, link, handle);
    }

    /// <summary>
    /// A copy through the same alias. The source link survives a copy whatever
    /// happens, so what is at stake here is only the file at the name — and it
    /// was destroyed, with the copy recording a landing for it.
    /// </summary>
    [Fact]
    public async Task A_link_copied_into_a_symlinked_folder_leaves_the_file_it_points_at_alone()
    {
        if (!OperatingSystem.IsLinux()) return;

        var real = Path.Combine(_into, "report.pdf");
        File.WriteAllText(real, "the only copy");

        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, _into);

        var link = Path.Combine(_from, "report.pdf");
        File.CreateSymbolicLink(link, real);

        var handle = new LinuxFileOperations().Copy([link], alias, Overwrite);

        await handle.Completion;

        AssertLeftAlone(real, link, handle);
    }

    /// <summary>
    /// **A chain: the link names another link, which names the file.** Following
    /// one hop is not enough — what the first link records is the second link's
    /// path, which is not the name being replaced, so the text comparison was
    /// satisfied and the file at the end of the chain went.
    /// </summary>
    [Fact]
    public async Task A_link_that_reaches_the_file_through_another_link_leaves_it_alone()
    {
        if (!OperatingSystem.IsLinux()) return;

        var real = Path.Combine(_into, "report.pdf");
        File.WriteAllText(real, "the only copy");

        var middle = Path.Combine(_from, "current.pdf");
        File.CreateSymbolicLink(middle, real);

        var link = Path.Combine(_from, "report.pdf");
        File.CreateSymbolicLink(link, middle);

        var handle = new LinuxFileOperations().Move([link], _into, Overwrite);

        await handle.Completion;

        AssertLeftAlone(real, link, handle);
    }

    /// <summary>
    /// **".." after a link means the parent of what the link points AT**, and
    /// the comparison collapsed it as text before following anything: the text
    /// "…/sym/../report.pdf" reads as a file beside "sym", while the kernel
    /// reads it as a file in the folder holding what "sym" points at — which is
    /// the very name being replaced.
    ///
    /// "sym" stands at the root of this tree rather than in either folder, so
    /// that collapsing the text names a third place that has nothing in it, and
    /// only following the link reaches the file.
    /// </summary>
    [Fact]
    public async Task A_link_whose_text_climbs_out_of_a_symlinked_folder_leaves_the_file_it_points_at_alone()
    {
        if (!OperatingSystem.IsLinux()) return;

        var real = Path.Combine(_into, "report.pdf");
        File.WriteAllText(real, "the only copy");

        var deeper = Path.Combine(_into, "deeper");
        Directory.CreateDirectory(deeper);

        Directory.CreateSymbolicLink(Path.Combine(_root, "sym"), deeper);

        var link = Path.Combine(_from, "report.pdf");
        File.CreateSymbolicLink(link, Path.Combine(_root, "sym", "..", "report.pdf"));

        var handle = new LinuxFileOperations().Move([link], _into, Overwrite);

        await handle.Completion;

        AssertLeftAlone(real, link, handle);
    }

    /// <summary>An ordinary folder with no links behaves exactly as before.</summary>
    [Fact]
    public async Task An_ordinary_folder_still_copies_whole()
    {
        if (!OperatingSystem.IsLinux()) return;

        var plain = Path.Combine(_from, "plain");
        Directory.CreateDirectory(Path.Combine(plain, "inner"));
        File.WriteAllText(Path.Combine(plain, "a.txt"), "a");
        File.WriteAllText(Path.Combine(plain, "inner", "b.txt"), "b");

        await Run(new LinuxFileOperations().Copy([plain], _into, Overwrite));

        Assert.Equal("a", File.ReadAllText(Path.Combine(_into, "plain", "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(_into, "plain", "inner", "b.txt")));
    }
}
