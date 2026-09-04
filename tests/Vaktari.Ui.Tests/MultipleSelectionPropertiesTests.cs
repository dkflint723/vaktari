using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the properties window shows when it was opened on more than one row.
///
/// **Everything below the size line was empty.** The single-item branch fills
/// that half from the platform's own groups; the multi-selection branch never
/// touched Groups at all, so a selection got a title, a location, a kind and a
/// size line, and then nothing. On Windows that is the whole answer — the
/// shell's own sheet is declined for more than one path — so a person who
/// selected forty files could not find out which of them were read-only.
/// </summary>
public sealed class MultipleSelectionPropertiesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-props-" + Guid.NewGuid().ToString("N")[..8]);

    public MultipleSelectionPropertiesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { }
    }

    private string File(string name)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>Answers about a whole list and about one item with different
    /// groups, so which of the two the window used is visible in the result.</summary>
    private sealed class Shares(params PropertyGroup[] shared) : IPropertiesProvider
    {
        public ValueTask<FileDetails> GetAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(new FileDetails
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                IsDirectory = Directory.Exists(path),
                Kind = "File",
                Groups = [new PropertyGroup("one item only", [new PropertyRow("x", "y")])],
            });

        public ValueTask<SizeProgress> MeasureAsync(
            string path, IProgress<SizeProgress> progress, CancellationToken ct)
            => ValueTask.FromResult(new SizeProgress(0, 0, 0));

        public ValueTask<IReadOnlyList<PropertyGroup>> GetSharedAsync(
            IReadOnlyList<string> paths, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PropertyGroup>>(shared);
    }

    /// <summary>A platform that overrides nothing, so the window gets the
    /// interface's own default answer for a selection.</summary>
    private sealed class SaysNothing : IPropertiesProvider
    {
        public ValueTask<FileDetails> GetAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(new FileDetails
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                IsDirectory = false,
            });

        public ValueTask<SizeProgress> MeasureAsync(
            string path, IProgress<SizeProgress> progress, CancellationToken ct)
            => ValueTask.FromResult(new SizeProgress(0, 0, 0));
    }

    /// <summary>Waits only for a positive, so it cannot turn a failure into a
    /// pass — the load finishes on a dispatcher job.</summary>
    private static async Task Settles(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++)
        {
            await Task.Delay(5);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task A_multiple_selection_shows_what_the_platform_says_about_all_of_them()
    {
        var model = new PropertiesViewModel(
            new Shares(new PropertyGroup("attributes", [new PropertyRow("Read-only", "mixed")])),
            [File("a.bin"), File("b.bin")],
            access: null);

        await model.LoadAsync();
        await Settles(() => model.Groups.Count > 0);

        var group = Assert.Single(model.Groups);

        Assert.Equal("attributes", group.Label);
        Assert.Equal("mixed", Assert.Single(group.Rows).Value);
    }

    /// <summary>
    /// The branch boundary: one item still comes from the per-item call, which
    /// is the expensive one and the only one that can answer a kind and a link
    /// target.
    /// </summary>
    [AvaloniaFact]
    public async Task A_single_item_still_shows_its_own_groups()
    {
        var model = new PropertiesViewModel(
            new Shares(new PropertyGroup("attributes", [new PropertyRow("Read-only", "mixed")])),
            [File("a.bin")],
            access: null);

        await model.LoadAsync();
        await Settles(() => model.Groups.Count > 0);

        Assert.Equal("one item only", Assert.Single(model.Groups).Label);
    }

    /// <summary>
    /// **A platform with nothing cheap to say has to leave the window alone.**
    /// Linux takes the interface's default and overrides nothing, so a default
    /// that answered with a section rather than with nothing would hang an
    /// empty heading under every Linux multi-selection — and the whole suite
    /// would have stayed green.
    ///
    /// Not a negative asserted into fire-and-forget work: Title and Groups are
    /// set by the same dispatcher job, so waiting for the title is waiting for
    /// the sections too.
    /// </summary>
    [AvaloniaFact]
    public async Task A_platform_that_answers_with_nothing_adds_no_section()
    {
        var model = new PropertiesViewModel(
            new SaysNothing(), [File("a.bin"), File("b.bin")], access: null);

        await model.LoadAsync();
        await Settles(() => model.Title.Length > 0);

        Assert.Equal("2 items", model.Title);
        Assert.Empty(model.Groups);
    }
}
