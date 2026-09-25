using System.Globalization;
using Avalonia.Headless.XUnit;
using Vaktari.Core.Session;
using Vaktari.Ui.Session;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A session file that parses and still cannot be restored.
///
/// **`"windows": null` stopped the application starting.** Only the version
/// was checked, the serializer hands a null list over as null whatever the
/// model's annotations say, and the first window's constructor asked that
/// null for its first entry. The store's own contract says any load failure
/// returns null so startup proceeds empty; a file with a hole where a list
/// should be is a load failure.
///
/// Its own folder per test, and never the shared state directory: these
/// write files a window would restore from.
/// </summary>
public sealed class SessionFileShapeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vaktari-session-shape").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }
    }

    private string SessionFile => Path.Combine(_dir, "session.json");

    /// <summary>The current version in place of <c>@V</c>, so a bump does not
    /// turn every case here into "wrong version" — which also loads as null,
    /// and would pass for the wrong reason.</summary>
    private static string Versioned(string json)
        => json.Replace("@V", SessionState.CurrentVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    [AvaloniaTheory]
    [InlineData("""{"version":@V,"windows":null}""")]
    [InlineData("""{"version":@V,"windows":[null]}""")]
    [InlineData("""{"version":@V,"windows":[{"panes":null}]}""")]
    [InlineData("""{"version":@V,"windows":[{"panes":[{"tabs":null}]}]}""")]
    [InlineData("""{"version":@V,"windows":[{"panes":[{"tabs":[null]}]}]}""")]
    [InlineData("""{"version":@V,"windows":[{"panes":[],"rememberedRightPane":{"tabs":null}}]}""")]
    public async Task A_file_with_a_null_list_loads_as_nothing(string json)
    {
        File.WriteAllText(SessionFile, Versioned(json));

        var store = new JsonSessionStore(_dir);

        Assert.Null(store.Load());

        await store.DisposeAsync();
    }

    /// <summary>
    /// Refused, not repaired — so the backup gets its turn, exactly as it
    /// does for a truncated file. A session one save old beats an empty one.
    /// </summary>
    [AvaloniaFact]
    public async Task The_backup_is_read_in_its_place()
    {
        File.WriteAllText(SessionFile, Versioned("""{"version":@V,"windows":null}"""));
        File.WriteAllText(
            SessionFile + ".bak",
            Versioned("""{"version":@V,"windows":[{"panes":[{"tabs":[{"path":"/kept"}]}]}]}"""));

        var store = new JsonSessionStore(_dir);

        var window = Assert.Single(store.Load()!.Windows);
        Assert.Equal("/kept", Assert.Single(Assert.Single(window.Panes).Tabs).Path);

        await store.DisposeAsync();
    }
}
