using System.Text.Json;
using System.Text.Json.Serialization;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.Settings;

/// <summary>
/// Recently opened files and folders in one file, flushed on close.
///
/// Same shape as the visit store it outlived — dirty flag rather
/// than a write per change, atomic temp-and-rename, a bad file falling back to
/// empty rather than blocking startup. Two stores that behave differently would
/// be two things to remember.
///
/// **Two dictionaries rather than one keyed by (kind, path).** The lists are
/// always read one kind at a time, they are bounded separately, and a folder and
/// a file can legitimately share a path over time — a key that has to encode the
/// kind is a key you can get wrong.
/// </summary>
public sealed class JsonRecentStore : IRecentStore
{
    /// <summary>
    /// Kept per kind. Dolphin shows about thirty; this holds more so that
    /// forgetting a few entries does not leave a short list shorter, and so the
    /// bands below "Today" still have something in them.
    /// </summary>
    private const int Keep = 200;

    private readonly string _path;
    private readonly string _tempPath;
    private readonly object _gate = new();

    private Dictionary<string, DateTimeOffset> _files;
    private Dictionary<string, DateTimeOffset> _folders;
    private bool _dirty;

    public event EventHandler? Changed;

    public JsonRecentStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "recents.json");
        _tempPath = _path + ".tmp";

        var file = Load();

        _files = file.Files;
        _folders = file.Folders;
    }

    private RecentFile Load()
    {
        try
        {
            if (!File.Exists(_path)) return new RecentFile();

            using var stream = File.OpenRead(_path);

            return JsonSerializer.Deserialize(stream, RecentJsonContext.Default.RecentFile)
                   ?? new RecentFile();
        }
        catch
        {
            // A bad file must never block startup.
            return new RecentFile();
        }
    }

    private Dictionary<string, DateTimeOffset> MapFor(RecentKind kind)
        => kind == RecentKind.File ? _files : _folders;

    public void Record(string path, RecentKind kind)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var key = Normalise(path);

        lock (_gate)
        {
            var map = MapFor(kind);

            // Assignment rather than an add: re-opening something moves it to
            // the top instead of appearing twice.
            map[key] = DateTimeOffset.Now;
            _dirty = true;

            // Bounded, and trimmed by TIME rather than by count — the opposite
            // of the visit store, and the reason these are separate files.
            if (map.Count > Keep + 100)
            {
                var trimmed = map.OrderByDescending(pair => pair.Value)
                                 .Take(Keep)
                                 .ToDictionary(pair => pair.Key, pair => pair.Value,
                                               StringComparer.Ordinal);

                if (kind == RecentKind.File) _files = trimmed; else _folders = trimmed;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<RecentEntry> Recent(RecentKind kind, int count)
    {
        lock (_gate)
        {
            return MapFor(kind)
                .OrderByDescending(pair => pair.Value)
                .Take(count)
                .Select(pair => new RecentEntry(pair.Key, kind, pair.Value))
                .ToList();
        }
    }

    public int Count
    {
        get { lock (_gate) return _files.Count + _folders.Count; }
    }

    public int ForgetAll()
    {
        int had;

        lock (_gate)
        {
            had = _files.Count + _folders.Count;

            if (had == 0) return 0;

            _files.Clear();
            _folders.Clear();
            _dirty = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return had;
    }

    public void Forget(string path)
    {
        var key = Normalise(path);
        bool removed;

        lock (_gate)
        {
            // Both maps: the caller knows a path, not which list it came from.
            removed = _files.Remove(key) | _folders.Remove(key);

            if (removed) _dirty = true;
        }

        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

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
                        new RecentFile { Files = _files, Folders = _folders },
                        RecentJsonContext.Default.RecentFile);

                    stream.Flush();
                }

                File.Move(_tempPath, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vaktari] recents write failed: {ex.Message}");
            }
        }
    }

    private static string Normalise(string path)
        => PathRules.Normalise(path);
}

public sealed record RecentFile
{
    public int Version { get; init; } = 1;
    public Dictionary<string, DateTimeOffset> Files { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateTimeOffset> Folders { get; init; } = new(StringComparer.Ordinal);
}

[JsonSerializable(typeof(RecentFile))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class RecentJsonContext : JsonSerializerContext;
