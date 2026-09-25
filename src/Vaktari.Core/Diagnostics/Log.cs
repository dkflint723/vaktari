using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Vaktari.Core.Diagnostics;

/// <summary>
/// A small rolling log on disk, and the crash marker beside it.
///
/// **Nothing survived a crash.** The fatal and unobserved-exception handlers
/// wrote to stderr, and a windowed process on Windows has no stderr — so
/// Vaktari could exit mid-copy and leave no evidence anywhere. <c>Quiet</c>
/// swallowed exceptions to stderr under an environment variable, which is the
/// same nowhere. A bug report said "it closed" and there was nothing to read.
///
/// So: <c>vaktari.log</c> under the state directory, one megabyte at a time,
/// three files kept; Warn and above by default, which is what <c>Quiet</c>
/// hands over. Never throws — a logger that fails a copy because the disk is
/// full has its priorities backwards — and never runs until
/// <see cref="Configure"/> has been told where to write, so a test that
/// forgets to configure it writes nowhere rather than into the developer's
/// state directory.
///
/// **Paths are redacted unless asked otherwise.** Ninety-odd stderr lines in
/// this tree carry a path, and a log that ends up in a bug report carries the
/// user's folder names with it. The leaf name is kept, because it is usually
/// the thing a diagnosis needs; the directory becomes a short hash, so two
/// lines about the same folder can still be matched up. Set
/// <c>VAKTARI_QUIET_DEBUG=1</c> — the same switch <c>Quiet</c> already honours
/// — to keep paths whole on a machine where nobody else will read them.
/// </summary>
public static class Log
{
    /// <summary>Where lines go, or null until <see cref="Configure"/>.</summary>
    public static string? Directory { get; private set; }

    /// <summary>Whether paths are written whole. False until configured otherwise.</summary>
    public static bool IncludePaths { get; private set; }

    public const string FileName = "vaktari.log";
    public const string CrashMarker = "crash.marker";

    /// <summary>One file grows to this before rolling.</summary>
    public const long RollAt = 1024 * 1024;

    /// <summary>How many rolled files are kept beside the live one.</summary>
    public const int Kept = 2;

    private static readonly object Gate = new();

    /// <summary>
    /// Points the log at a directory, creating it. Called once at startup by
    /// the process that owns the state directory; tests point it at a temp
    /// folder and call <see cref="Reset"/> after.
    /// </summary>
    public static void Configure(string directory, bool includePaths)
    {
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                Directory = directory;
                IncludePaths = includePaths;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vaktari] log: cannot use {directory}: {ex.Message}");
                Directory = null;
            }
        }
    }

    /// <summary>Forgets the directory, so nothing more is written. For tests.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Directory = null;
            IncludePaths = false;
        }
    }

    public static void Warn(string area, string message) => Write("warn", area, message);

    public static void Error(string area, string message) => Write("error", area, message);

    /// <summary>A fatal line, and the marker that says the process did not end
    /// on its own terms. The next start reads the marker and can say so.</summary>
    public static void Fatal(string area, string message)
    {
        Write("FATAL", area, message);
        MarkCrash(message);
    }

    /// <summary>
    /// Leaves <c>crash.marker</c> holding the time and the first line of what
    /// happened. Separate from the log so a start-up check is one
    /// <c>File.Exists</c> rather than a parse of the last megabyte.
    /// </summary>
    public static void MarkCrash(string message)
    {
        if (Directory is not { } dir) return;

        try
        {
            var first = message.Split('\n', 2)[0].Trim();
            File.WriteAllText(
                Path.Combine(dir, CrashMarker),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n{Redact(first)}\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] log: cannot write crash marker: {ex.Message}");
        }
    }

    /// <summary>
    /// The marker's contents if the last run crashed, deleting it so the next
    /// start does not say it again. Null when the last run ended normally.
    /// </summary>
    public static string? TakeCrashMarker()
    {
        if (Directory is not { } dir) return null;

        var path = Path.Combine(dir, CrashMarker);

        try
        {
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path);
            File.Delete(path);
            return text;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[vaktari] log: cannot read crash marker: {ex.Message}");
            return null;
        }
    }

    /// <summary>The last <paramref name="lines"/> lines of the live log, for a
    /// diagnostics bundle. Empty when there is no log.</summary>
    public static string Tail(int lines)
    {
        if (Directory is not { } dir) return "";

        try
        {
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) return "";

            var all = File.ReadAllLines(path);
            return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception ex)
        {
            return $"(could not read the log: {ex.Message})";
        }
    }

    /// <summary>
    /// A path with its directory replaced by a short hash of itself, and the
    /// leaf kept. Applied to whole lines, so a message that quotes two paths
    /// has both hidden; a line with no path in it is returned untouched.
    /// </summary>
    public static string Redact(string text)
    {
        if (IncludePaths || text.Length == 0) return text;

        return PathPattern.Replace(text, m =>
        {
            var whole = m.Value;
            var cut = whole.LastIndexOfAny(['\\', '/']);

            // A bare root or a path with no leaf keeps nothing of itself.
            if (cut < 0 || cut == whole.Length - 1) return "<" + Hash(whole) + ">";

            return "<" + Hash(whole[..cut]) + ">" + whole[cut..];
        });
    }

    // A drive-rooted Windows path, a UNC path, or an absolute Unix path.
    // Deliberately greedy about what counts: hiding one path too many costs a
    // diagnosis nothing, and leaking one costs the user something.
    //
    // **A match stopped at the first space, so every folder after it was
    // written out whole.** "C:\Users\John Smith\AppData\...\vaktari.exe" hid
    // "C:\Users" and kept "\John", then left " Smith\AppData\..." as ordinary
    // text — and the diagnostics bundle opens with exactly that line, so a
    // per-user install under a spaced name put its owner's full name at the
    // top of every bug report. A FOLDER may now hold a space, as long as the
    // space does not start a new root; that exception is what keeps
    // "C:\a\one.txt to D:\b\two.txt" two paths rather than one path whose
    // folder is "one.txt to D:". The leaf still stops at a space, so prose
    // after a path is not swallowed into it. The lookahead after the root
    // keeps a bare "/" in "3 / 5" out, which the one-or-more of the old
    // pattern did.
    //
    // **And every folder after an apostrophe was written out the same way.**
    // A folder stopped at ', so "C:\Users\O'Brien\AppData\..." hid
    // "C:\Users", kept "\O", and left "'Brien\AppData\..." as plain text — the
    // same name at the top of the same bundle, for an account Windows allows.
    // A folder may hold one now. What keeps a quoted path from running into
    // the next is the space rule, which looks past an opening quote: in
    // "'C:\a\one.txt' to 'D:\b\two.txt'" the space before 'D:\ starts a new
    // root, so "one.txt' to" can never become a folder of the second path's.
    // The leaf still stops at a quote, so a quoted path ends where it closes.
    private static readonly Regex PathPattern = new(
        @"(?:[A-Za-z]:\\|\\\\|/)(?=[^\s""'<>|])(?:(?:[^\s""<>|\\/]|[ \t](?![""']?(?:[A-Za-z]:\\|\\\\|/)))*[\\/])*[^\s""'<>|]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static void Write(string level, string area, string message)
    {
        if (Directory is not { } dir) return;

        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {area}: {Redact(message)}";

        lock (Gate)
        {
            try
            {
                var path = Path.Combine(dir, FileName);

                if (File.Exists(path) && new FileInfo(path).Length >= RollAt) Roll(dir, path);

                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // The one place this file says anything to stderr about
                // itself: a log that cannot write is worth one line, not a
                // failure of whatever was being logged.
                Console.Error.WriteLine($"[vaktari] log: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// <c>vaktari.log</c> → <c>vaktari.1.log</c> → <c>vaktari.2.log</c> → gone.
    /// Renames rather than copies, so a roll is three cheap operations however
    /// large the file got.
    /// </summary>
    private static void Roll(string dir, string live)
    {
        for (var i = Kept; i >= 1; i--)
        {
            var older = Path.Combine(dir, $"vaktari.{i}.log");
            var newer = i == 1 ? live : Path.Combine(dir, $"vaktari.{i - 1}.log");

            if (i == Kept && File.Exists(older)) File.Delete(older);
            if (File.Exists(newer)) File.Move(newer, older, overwrite: true);
        }
    }
}
