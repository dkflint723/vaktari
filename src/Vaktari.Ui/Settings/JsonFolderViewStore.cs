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
    /// modified. Only the two keys that map cleanly are honoured; the rest of
    /// Dolphin's schema describes features that do not exist here, and guessing
    /// at them would be worse than ignoring them.
    /// </summary>
    private static FolderViewState? ReadDotDirectory(string folder)
    {
        try
        {
            var file = Path.Combine(folder, ".directory");
            if (!File.Exists(file)) return null;

            string? mode = null;
            string? sort = null;
            var descending = false;
            var inDolphin = false;

            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();

                if (line.StartsWith('['))
                {
                    inDolphin = line.Equals("[Dolphin]", StringComparison.Ordinal);
                    continue;
                }

                if (!inDolphin) continue;

                var split = line.IndexOf('=');
                if (split <= 0) continue;

                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();

                if (key.Equals("ViewMode", StringComparison.Ordinal)) mode = value;
                else if (key.Equals("SortRole", StringComparison.Ordinal)) sort = value;
                else if (key.Equals("SortOrder", StringComparison.Ordinal))
                    descending = value is "1" or "Descending";
            }

            if (mode is null && sort is null) return null;

            return new FolderViewState
            {
                // Dolphin: 0 icons, 1 compact, 2 details.
                View = mode switch
                {
                    "0" => ViewMode.Grid,
                    "1" => ViewMode.Compact,
                    _ => ViewMode.Details,
                },

                Sort = sort switch
                {
                    "size" => SortField.Size,
                    "modificationtime" or "modified" => SortField.Modified,
                    "type" => SortField.Kind,
                    _ => SortField.Name,
                },

                SortDescending = descending,
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
