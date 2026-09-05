using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui.Settings;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Emptying the store that fills itself.
///
/// **It fills up on its own, and nothing could empty it.** A folder is recorded
/// the moment its layout is changed, `Forget(path)` had never been called from
/// anywhere in the application, and the file it writes was invisible — so
/// turning "remember the view for each folder" off stopped NEW folders being
/// recorded and left every folder already recorded exactly as it was. A listing
/// that had once been given a layout kept it with the feature switched off, and
/// the only way to say otherwise was to find the file and delete it.
/// </summary>
public sealed class FolderViewStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-folder-views-" + Guid.NewGuid().ToString("N")[..8]);

    public FolderViewStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private JsonFolderViewStore Store() => new(_root);

    private static FolderViewState Grid => new() { View = ViewMode.Grid };

    /// <summary>Nothing recorded, nothing to say.</summary>
    [Fact]
    public void A_new_store_remembers_nothing()
        => Assert.Equal(0, Store().Remembered);

    /// <summary>And counts what it has been given.</summary>
    [Fact]
    public void It_counts_the_folders_it_holds()
    {
        var store = Store();

        store.Write(@"C:\one", Grid);
        store.Write(@"C:\two", Grid);

        Assert.Equal(2, store.Remembered);
    }

    /// <summary>**The whole finding**: every folder can be forgotten at once.</summary>
    [Fact]
    public void Forgetting_them_all_empties_it()
    {
        var store = Store();

        store.Write(@"C:\one", Grid);
        store.Write(@"C:\two", Grid);

        Assert.Equal(2, store.ForgetAll());
        Assert.Equal(0, store.Remembered);
        Assert.Null(store.Read(@"C:\one"));
    }

    /// <summary>
    /// And it says how many, because the dialog reports that back and a wrong
    /// number there is worse than none.
    /// </summary>
    [Fact]
    public void And_says_how_many_it_forgot()
    {
        var store = Store();

        store.Write(@"C:\one", Grid);

        Assert.Equal(1, store.ForgetAll());
        Assert.Equal(0, store.ForgetAll());
    }

    /// <summary>
    /// Forgetting reaches the FILE, not just the memory. The store writes on
    /// Flush, and a clear that never reached disk would come back on the next
    /// launch — which is exactly the shape of the bug being fixed.
    /// </summary>
    [Fact]
    public void What_it_forgets_stays_forgotten_across_a_reload()
    {
        var first = Store();

        first.Write(@"C:\one", Grid);
        first.Flush();

        Assert.Equal(1, Store().Remembered);

        first.ForgetAll();
        first.Flush();

        Assert.Equal(0, Store().Remembered);
    }

    /// <summary>
    /// An empty store is not marked dirty by being cleared. Flush is called on
    /// every session write, and a store that reported work to do on every one
    /// would rewrite the same empty file for the life of the process.
    /// </summary>
    [Fact]
    public void Forgetting_nothing_leaves_nothing_to_write()
    {
        var store = Store();

        Assert.Equal(0, store.ForgetAll());

        store.Flush();

        Assert.False(File.Exists(Path.Combine(_root, "folder-views.json")),
                     "an empty clear wrote the file anyway");
    }
}
