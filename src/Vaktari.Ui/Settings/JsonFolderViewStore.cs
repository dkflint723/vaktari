using System.Text.Json;
using System.Text.Json.Serialization;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Ui.Settings;

/// <summary>
/// Per-folder view state in one file, plus a read-only fallback to any
/// <c>.directory</c> Dolphin has already written.
///
/// Debounced like the session store rather than written per change: changing a
/// sort order is a keystroke-frequency action, and a folder in a deep tree
/// should not cost a disk write per click.
/// </summary>
public sealed class JsonFolderViewStore : IFolderViewStore
{
    private readonly string _path;
    private readonly string _tempPath;
    private readonly object _gate = new();

    private Dictionary<string, FolderViewState> _states;
    private bool _dirty;

    public JsonFolderViewStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "folder-views.json");
        _tempPath = _path + ".tmp";

        _states = Load();
    }

    private Dictionary<string, FolderViewState> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);

            using var stream = File.OpenRead(_path);

            return JsonSerializer.Deserialize(stream, FolderViewJsonContext.Default.FolderViewFile)
                   ?.Folders ?? new(StringComparer.Ordinal);
        }
        catch
        {
            // Same rule as everywhere else: a bad file must never block startup.
            return new(StringComparer.Ordinal);
        }
    }

    public FolderViewState? Read(string path)
    {
        var key = Normalise(path);

        lock (_gate)
        {
            if (_states.TryGetValue(key, out var stored)) return stored;
        }

        // Only consulted when we have nothing of our own, so our answer always
        // wins once the user has expressed a preference here.
        return ReadDotDirectory(key);
    }

    public void Write(string path, FolderViewState state)
    {
        lock (_gate)
        {
            _states[Normalise(path)] = state;
            _dirty = true;
        }
    }

    public void Forget(string path)
    {
        lock (_gate)
        {
            if (_states.Remove(Normalise(path))) _dirty = true;
        }
    }

    public int Remembered
    {
        get { lock (_gate) return _states.Count; }
    }

    public int ForgetAll()
    {
        lock (_gate)
        {
            var had = _states.Count;

            if (had == 0) return 0;

            _states.Clear();
            _dirty = true;

            return had;
        }
    }

    /// <summary>Called on shutdown and on the session flush.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty) return;

            try
            {
                using (var stream = File.Create(_tempPath))
                {
                    JsonSerializer.Serialize(
                        stream,
                        new FolderViewFile { Folders = _states },
                        FolderViewJsonContext.Default.FolderViewFile);

                    stream.Flush();
                }

                File.Move(_tempPath, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vaktari] folder views write failed: {ex.Message}");
            }
        }
    }

    private static string Normalise(string path)
        => PathRules.Normalise(path);

    /// <summary>
    /// Dolphin's own per-folder file. Read only — never created, never
    /// modified. Only the keys that map cleanly are honoured; the rest of
    /// Dolphin's schema describes features that do not exist here, and guessing
    /// at them would be worse than ignoring them.
    ///
    /// **The column set was NOT decoded from <c>VisibleRoles</c>.** No Dolphin
    /// was available to this codebase to measure what that key actually holds,
    /// and a column list guessed at from an unmeasured format would silently
    /// blank columns the pane was showing. It is skipped, so the folder keeps
    /// whatever columns the pane had — the same answer a file that never
    /// mentioned columns gets.
    ///
    /// <c>HiddenFilesShown</c> is taken from <c>[Settings]</c> as well as
    /// <c>[Dolphin]</c>. Which heading a given file uses was likewise not
    /// measured here; what WAS measured is that the reader below cannot be hurt
    /// by accepting both, because the key can only ever set the answer to true.
    ///
    /// **And true is the only answer it may give.** A <c>.directory</c> is
    /// content of the folder being listed — an extracted archive, a network
    /// share and a synced directory all carry whatever their producer put in
    /// them — so honouring a <c>HiddenFilesShown</c> of false handed that
    /// producer a switch that turns the reader's hidden files off. Measured:
    /// with a pane at Ctrl+H on, arriving in a folder holding a
    /// <c>[Dolphin]</c> group with that key set to false turned them off, and
    /// walking back out left them off. Concealing files the reader asked to see
    /// is the one direction with no way to notice it has happened, and revealing
    /// is safe in a way concealing is not — so the key is read one way only.
    /// </summary>
    private static FolderViewState? ReadDotDirectory(string folder)
    {
        try
        {
            var file = Path.Combine(folder, ".directory");
            if (!File.Exists(file)) return null;

            string? mode = null;
            string? sort = null;
            bool? descending = null;
            bool? hidden = null;
            var inDolphin = false;
            var inSettings = false;

            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();

                if (line.StartsWith('['))
                {
                    inDolphin = line.Equals("[Dolphin]", StringComparison.Ordinal);
                    inSettings = line.Equals("[Settings]", StringComparison.Ordinal);
                    continue;
                }

                if (!inDolphin && !inSettings) continue;

                var split = line.IndexOf('=');
                if (split <= 0) continue;

                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();

                // One direction only — see the note above.
                if (key.Equals("HiddenFilesShown", StringComparison.Ordinal)
                    && value is "true" or "1")
                    hidden = true;

                if (!inDolphin) continue;

                if (key.Equals("ViewMode", StringComparison.Ordinal)) mode = value;
                else if (key.Equals("SortRole", StringComparison.Ordinal)) sort = value;

                // Dolphin writes SortOrder 0 for ascending, so a present key
                // that is neither "1" nor "Descending" is still an answer, and
                // only an absent one leaves this null.
                else if (key.Equals("SortOrder", StringComparison.Ordinal))
                    descending = value is "1" or "Descending";
            }

            if (mode is null && sort is null && hidden is null) return null;

            // **Every field here is null unless its own key was present.** The
            // record used to be non-nullable, so one key in the file produced
            // an opinion about all four, and a `.directory` saying only
            // `SortRole=size` pulled a pane out of the grid it was in on
            // arrival. A file Dolphin wrote says what it says and nothing else.
            return new FolderViewState
            {
                // Dolphin: 0 icons, 1 compact, 2 details.
                View = mode switch
                {
                    null => null,
                    "0" => ViewMode.Grid,
                    "1" => ViewMode.Compact,
                    _ => ViewMode.Details,
                },

                Sort = sort switch
                {
                    null => null,
                    "size" => SortField.Size,
                    "modificationtime" or "modified" => SortField.Modified,
                    "type" => SortField.Kind,
                    _ => SortField.Name,
                },

                SortDescending = descending,
                ShowHidden = hidden,
            };
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Wrapper so the file has a versioned root rather than a bare map.</summary>
public sealed record FolderViewFile
{
    public int Version { get; init; } = 1;
    public Dictionary<string, FolderViewState> Folders { get; init; } = new(StringComparer.Ordinal);
}

[JsonSerializable(typeof(FolderViewFile))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
public partial class FolderViewJsonContext : JsonSerializerContext;
