using System.Text.Json;
using System.Text.Json.Serialization;
using Vaktari.Core.Search;

namespace Vaktari.Ui.Settings;

/// <summary>
/// The searches that have been run, in one file, flushed on close.
///
/// Same shape as the recents store beside it — dirty flag rather than a write
/// per change, atomic temp-and-rename, a bad file falling back to empty rather
/// than blocking startup. Three stores that behave differently would be three
/// things to remember.
///
/// **Trimmed on every write, where the recents store trims in batches.** That
/// store is written to by every folder you open, so it sorts a hundred spare
/// entries at a time to keep navigation off the sort; this one is written to
/// once per deliberate search, so the amortisation buys nothing and costs the
/// reader a rule to hold.
///
/// **Ordinal keys, and the paths go in exactly as they arrive.**
/// <c>PathRules.Normalise</c> — which the recents store does apply — swaps '/'
/// for '\' on Windows and trims a trailing separator, and it lower-cases
/// nothing; a search path is percent-escaped precisely so neither has anything
/// to rewrite. Storing it raw keeps the string the pane navigates to identical
/// to the string it was recorded from, which is what makes a history row land
/// on the same results it was born from.
///
/// **Stored raw, but not COMPARED raw**, which is the one place the two part
/// company: a path carries the folder a search was started from whether or not
/// that folder is the scope, and when it is not the scope nothing reads it.
/// See <see cref="Record"/> — one question asked from two folders was two
/// entries drawing two identical rows.
/// </summary>
public sealed class JsonSearchHistory : ISearchHistory
{
    /// <summary>
    /// How many searches are kept. Larger than the twelve a menu shows, so
    /// that the list behind the menu still has something in it after a run of
    /// one-off questions, and small enough that this is never a record of
    /// somebody's year.
    /// </summary>
    private const int Keep = 50;

    private readonly string _path;
    private readonly string _tempPath;
    private readonly object _gate = new();

    /// <summary>
    /// Raised outside the lock, which is the rule the recents store beside
    /// this one already follows: a handler rebuilds a menu, and a menu rebuilt
    /// under this store's own lock is a reader holding it for the length of a
    /// layout pass.
    /// </summary>
    public event EventHandler? Changed;

    private Dictionary<string, DateTimeOffset> _searches;
    private bool _dirty;

    /// <summary>
    /// The newest stamp this store has issued or read, so the next one can be
    /// made to beat it. See <see cref="Record"/> for the two measurements.
    /// </summary>
    private DateTimeOffset _latest;

    public JsonSearchHistory(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "searches.json");
        _tempPath = _path + ".tmp";

        // Rebuilt around the comparer rather than adopted as deserialized: the
        // ordinal rule above is the whole reason two spellings of one question
        // stay two questions, and a dictionary that arrived from the file
        // carries whichever comparer the serializer felt like giving it.
        _searches = new Dictionary<string, DateTimeOffset>(Load().Searches, StringComparer.Ordinal);

        // The high-water mark comes from the FILE as well as from this run, or
        // a stamp written ahead of the clock would outrank every search made
        // after it for as long as the clock took to catch up.
        _latest = _searches.Count == 0 ? default : _searches.Values.Max();
    }

    private SearchHistoryFile Load()
    {
        try
        {
            // A fast path rather than a correctness line: the catch below
            // covers a missing file too. It is here so the ordinary case — no
            // file yet, on every first launch — does not cost a thrown
            // exception, which is the same reason the recents store beside it
            // asks first.
            if (!File.Exists(_path)) return new SearchHistoryFile();

            using var stream = File.OpenRead(_path);

            return JsonSerializer.Deserialize(
                       stream, SearchHistoryJsonContext.Default.SearchHistoryFile)
                   ?? new SearchHistoryFile();
        }
        catch
        {
            // A bad file must never block startup.
            return new SearchHistoryFile();
        }
    }

    public void Record(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        lock (_gate)
        {
            // **The stamp is forced past the newest one this store knows
            // about, and both halves of that are measured rather than
            // defensive.**
            //
            // Two hundred consecutive reads of DateTimeOffset.Now on this
            // machine produced 199 distinct values — so two searches made in
            // one tick tie, and a tie is decided by dictionary order, which is
            // insertion order. Everything downstream reads recency out of this
            // number: Recent orders by it and the trim below keeps the top
            // fifty by it, so a tie put the search just made at the BOTTOM of
            // its own tick and the trim then dropped it first.
            //
            // The file half is the same fault over a longer interval: a stamp
            // written while the clock was ahead — a machine corrected by NTP, a
            // settings directory carried over from another one — outranks every
            // search made afterwards until real time catches up.
            var now = DateTimeOffset.Now;

            if (now <= _latest) now = _latest.AddTicks(1);

            _latest = now;

            // **Two "everywhere" searches for the same words, started from two
            // different folders, were two entries drawing two identical rows.**
            // The origin is in the path whether or not it is the scope, and it
            // is read by nothing when it is not: SearchListing builds its query
            // out of QueryOf, ScopeOf and MatchesCase, and ScopeOf is null for
            // both. So the menu offered the same question twice, spelled the
            // same and answering the same, and the store's own promise that
            // asking again moves an entry rather than adding one was off for
            // every unscoped search.
            //
            // The older spelling goes and the NEW path stays, rather than the
            // other way round: the origin the entry keeps is the folder the
            // question was last asked from, which is what "This folder only"
            // narrows to when the row is opened again.
            var identity = VirtualPaths.SearchIdentity(path);

            // The path being recorded is not excluded, because excluding it
            // would change nothing: it is put back on the next line with a
            // fresh stamp, and order here comes from the stamp rather than from
            // the dictionary.
            foreach (var twin in _searches.Keys
                         .Where(held => VirtualPaths.SearchIdentity(held) == identity)
                         .ToList())
            {
                _searches.Remove(twin);
            }

            // Assignment rather than an add: asking the same question again
            // moves it to the top instead of appearing twice.
            _searches[path] = now;
            _dirty = true;

            if (_searches.Count > Keep)
            {
                _searches = _searches
                    .OrderByDescending(pair => pair.Value)
                    .Take(Keep)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> Recent(int count)
    {
        lock (_gate)
        {
            return _searches
                .OrderByDescending(pair => pair.Value)
                .Take(count)
                .Select(pair => pair.Key)
                .ToList();
        }
    }

    public int Count
    {
        get { lock (_gate) return _searches.Count; }
    }

    public int ForgetAll()
    {
        int had;

        lock (_gate)
        {
            had = _searches.Count;

            // **Nothing to forget leaves nothing to write.** Flush runs on the
            // way out of every session, and a store that reported work to do
            // after an empty clear would rewrite the same empty file for the
            // life of the process.
            if (had == 0) return 0;

            _searches.Clear();
            _dirty = true;
        }

        // Nothing forgotten is nothing to announce, for the same reason the
        // early return above exists: a notice sent when the list did not change
        // is a menu rebuilt for nothing.
        Changed?.Invoke(this, EventArgs.Empty);

        return had;
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
                        new SearchHistoryFile { Searches = _searches },
                        SearchHistoryJsonContext.Default.SearchHistoryFile);

                    stream.Flush();
                }

                File.Move(_tempPath, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vaktari] searches write failed: {ex.Message}");
            }
        }
    }
}

public sealed record SearchHistoryFile
{
    public int Version { get; init; } = 1;

    public Dictionary<string, DateTimeOffset> Searches { get; init; }
        = new(StringComparer.Ordinal);
}

[JsonSerializable(typeof(SearchHistoryFile))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class SearchHistoryJsonContext : JsonSerializerContext;
