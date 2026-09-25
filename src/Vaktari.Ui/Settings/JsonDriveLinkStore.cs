using System.Text.Json;
using System.Text.Json.Serialization;
using Vaktari.Core.Sharing;

namespace Vaktari.Ui.Settings;

/// <summary>
/// The drive links Vaktari has created, in one file.
///
/// **Remembered locally because the links outlive the app.** A copyparty share
/// dies with the process, so its list can live in memory; a Proton link keeps
/// working after the machine reboots, and a share you cannot SEE is a share
/// you cannot revoke — the sidebar's whole reason for listing them. The CLI is
/// the authority on what exists; this file only records what Vaktari itself
/// made, so the sidebar can offer the kill switch without a network question
/// on every launch.
///
/// Same manners as the other stores: atomic temp-and-rename, a bad file reads
/// as empty rather than blocking startup.
///
/// **One manner the others do not need: the file is the owner's alone.** Every
/// URL here carries its decryption key in the fragment, so the file is a list
/// of working keys to the user's Proton files, and on Linux it was written
/// 0644 — readable by any account that could get into the state folder. It is
/// created 0600 now, and an older file is tightened the first time it is read.
/// </summary>
public sealed class JsonDriveLinkStore
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _path;
    private readonly string _tempPath;
    private readonly object _gate = new();

    public JsonDriveLinkStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "drive-links.json");
        _tempPath = _path + ".tmp";
    }

    /// <summary>
    /// The links on disk, once every save already asked for has landed.
    ///
    /// **Each window reads this file when it opens**, and saves now finish on
    /// the pool: a window opened a moment after another saved a link would
    /// otherwise read the list from before it, and its next save would write
    /// that older list back over the link. Waiting here keeps "saved" meaning
    /// what it meant when a save returned only once the file was down.
    /// </summary>
    public IReadOnlyList<DriveLink> Load()
    {
        Writes.Idle.GetAwaiter().GetResult();

        try
        {
            if (!File.Exists(_path)) return [];

            Tighten();

            using var stream = File.OpenRead(_path);

            var stored = JsonSerializer.Deserialize(
                stream, DriveLinkJsonContext.Default.ListStoredLink) ?? [];

            return stored
                .Where(l => l is { LocalPath.Length: > 0, RemotePath.Length: > 0, Url.Length: > 0 })
                .Select(l => new DriveLink(l.LocalPath!, l.RemotePath!, l.Url!))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt or unreadable: an empty sidebar section, not a crash. The
            // links themselves still exist at Proton and can be re-managed from
            // the web app.
            return [];
        }
    }

    /// <summary>Saves and waits for the disk.</summary>
    public void Save(IReadOnlyList<DriveLink> links) => SaveAsync(links).GetAwaiter().GetResult();

    /// <summary>
    /// Saves on the pool, behind any save still in flight; the task completes
    /// when the file is on the disk. The list is copied here, on the caller's
    /// thread. See <see cref="Vaktari.Core.WriteBehind"/> for why the rest is
    /// not done here: this is called from the window, after a link is made
    /// or revoked.
    /// </summary>
    public Task SaveAsync(IReadOnlyList<DriveLink> links)
    {
        var stored = links
            .Select(l => new StoredLink
            {
                LocalPath = l.LocalPath,
                RemotePath = l.RemotePath,
                Url = l.Url,
            })
            .ToList();

        return Writes.Enqueue(() => Write(stored));
    }

    /// <summary>This store's writes, in order and off the caller's thread;
    /// <see cref="Load"/> and the way out await their
    /// <see cref="Vaktari.Core.WriteBehind.Idle"/>.</summary>
    internal Vaktari.Core.WriteBehind Writes { get; } = new();

    private void Write(List<StoredLink> stored)
    {
        lock (_gate)
        {
            try
            {
                // Deleted first, because the create mode below applies only
                // to a file that is CREATED: a 0644 temp left by a crashed
                // write under the older build would be truncated and reused
                // with its mode intact, and the rename would carry that mode
                // onto the real file.
                File.Delete(_tempPath);

                var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };

                if (!OperatingSystem.IsWindows()) create.UnixCreateMode = OwnerOnly;

                using (var stream = new FileStream(_tempPath, create))
                {
                    JsonSerializer.Serialize(
                        stream, stored, DriveLinkJsonContext.Default.ListStoredLink);

                    // To the disk, not just out of the process: see
                    // JsonSettingsStore.Save for why a rename alone is not
                    // enough to survive a power cut.
                    stream.Flush(flushToDisk: true);
                }

                File.Move(_tempPath, _path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Vaktari.Core.Quiet.Swallowed("drive-links", e);
            }
        }
    }

    /// <summary>
    /// A file written by a build before this one, made 0600. Its own catch,
    /// so a file that cannot be tightened — somebody else's, on a filesystem
    /// with no modes — still reads: the links in it are the user's way to
    /// revoke them.
    /// </summary>
    private void Tighten()
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            if (File.GetUnixFileMode(_path) != OwnerOnly) File.SetUnixFileMode(_path, OwnerOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Vaktari.Core.Quiet.Swallowed("drive-links", e);
        }
    }

    /// <summary>Serialized shape, kept separate from the domain record so the
    /// file format does not change when the record grows.</summary>
    internal sealed class StoredLink
    {
        public string? LocalPath { get; set; }
        public string? RemotePath { get; set; }
        public string? Url { get; set; }
    }
}

[JsonSerializable(typeof(List<JsonDriveLinkStore.StoredLink>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class DriveLinkJsonContext : JsonSerializerContext;
