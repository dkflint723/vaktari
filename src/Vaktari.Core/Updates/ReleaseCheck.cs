using System.Net.Http;
using System.Text.Json;
using Vaktari.Core.Diagnostics;

namespace Vaktari.Core.Updates;

/// <summary>
/// Asks, at most once a day and only when asked to, whether a newer release
/// exists — and says so, with a link. Nothing is downloaded, ever.
///
/// **A person running 0.9 had no way to learn that 0.10 fixed the fault
/// they were living with**, short of visiting the releases page on a hunch.
/// A file manager is the kind of program that stays installed for years,
/// and the security fixes in this changelog are worth a line on the status
/// bar to somebody who never reads a changelog.
///
/// Opt-in, and not on by default: a request to github.com on every start is
/// a fact about the user's network traffic that they did not choose. The
/// request carries a User-Agent naming the program, because the API refuses
/// one without, and nothing else — no version, no id, no platform. The
/// answer is the newest release's tag and its page; whether to go there is
/// theirs.
/// </summary>
public sealed class ReleaseCheck
{
    public const string LatestUrl = "https://api.github.com/repos/dkflint723/vaktari/releases/latest";

    /// <summary>Once a day at most, whatever the answer was.</summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromDays(1);

    private const string StampFile = "update-check.txt";

    /// <summary>A newer release: its version, and the page to read about it.</summary>
    public sealed record Available(string Version, string Url);

    private readonly string _stampPath;

    /// <summary>Stands in for the network in tests: the body the API would
    /// have answered. Null fetches. Settable rather than init-only, because
    /// the window tests reach the application's own instance after it is
    /// built.</summary>
    internal Func<CancellationToken, Task<string>>? FetchOverride { get; set; }

    /// <summary>Stands in for the clock, so a day can pass in a test.</summary>
    internal Func<DateTimeOffset>? Clock { get; init; }

    /// <param name="stateDirectory">Where the day's stamp is kept, beside the
    /// settings — so "once a day" survives a restart.</param>
    public ReleaseCheck(string stateDirectory)
        => _stampPath = Path.Combine(stateDirectory, StampFile);

    /// <summary>
    /// A newer release than <paramref name="running"/>, or null: because there
    /// is none, because the day's check has already been made, because the
    /// network did not answer, or because this is a development build.
    ///
    /// **A development build never asks.** It reports 0.0.0, which every
    /// release is newer than, so a developer running from source would be
    /// told on every start that the release they are working towards is
    /// available.
    /// </summary>
    public async Task<Available?> CheckAsync(string running, CancellationToken ct)
    {
        if (running is "0.0.0" or "unknown") return null;

        var now = (Clock ?? (() => DateTimeOffset.UtcNow))();

        if (LastChecked() is { } last && now - last < Cadence) return null;

        // Stamped before the answer rather than after it, so a network that
        // never answers is asked once a day too — not once per start.
        Stamp(now);

        try
        {
            var body = await (FetchOverride ?? FetchAsync)(ct).ConfigureAwait(false);

            if (Parse(body) is not { } latest) return null;

            return IsNewer(latest.Version, running) ? latest : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            // Offline, refused, slow, or an answer in a shape this does not
            // read: none of it is news, and none of it is worth a line.
            Quiet.Swallowed("updates", ex);
            return null;
        }
    }

    /// <summary>
    /// The newest release's version and page, out of the API's answer — read
    /// with JsonDocument rather than a type, because two fields of a large
    /// document are not worth a source-generated context.
    /// </summary>
    internal static Available? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object) return null;

        if (!root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { Length: > 0 } name) return null;
        if (!root.TryGetProperty("html_url", out var page) || page.GetString() is not { Length: > 0 } url) return null;

        return new Available(name.TrimStart('v', 'V'), url);
    }

    /// <summary>
    /// Whether a release tag names a version above the one running. Both are
    /// read as dotted numbers — 0.10.2 is above 0.9.9, which a string
    /// comparison gets wrong — and anything that does not parse is not newer,
    /// because a line saying "v2026-preview is available" over a number would
    /// be a claim nothing here can back.
    /// </summary>
    internal static bool IsNewer(string tag, string running)
    {
        if (!System.Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return false;
        if (!System.Version.TryParse(running, out var current)) return false;

        return latest > current;
    }

    private DateTimeOffset? LastChecked()
    {
        try
        {
            return File.Exists(_stampPath)
                   && DateTimeOffset.TryParse(File.ReadAllText(_stampPath).Trim(), null,
                       System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
                ? when
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Stamp(DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stampPath)!);
            File.WriteAllText(_stampPath, now.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Quiet.Swallowed("updates", ex);
        }
    }

    private static async Task<string> FetchAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // The name and nothing else: the API refuses a request with no
        // User-Agent, and a version in it would tell the server which
        // install is asking.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vaktari");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.GetAsync(LatestUrl, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
