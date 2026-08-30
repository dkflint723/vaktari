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
/// </summary>
public sealed class JsonDriveLinkStore
{
    private readonly string _path;
    private readonly string _tempPath;
    private readonly object _gate = new();

    public JsonDriveLinkStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "drive-links.json");
        _tempPath = _path + ".tmp";
    }

    public IReadOnlyList<DriveLink> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];

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

    public void Save(IReadOnlyList<DriveLink> links)
    {
        lock (_gate)
        {
            try
            {
                var stored = links
                    .Select(l => new StoredLink
                    {
                        LocalPath = l.LocalPath,
                        RemotePath = l.RemotePath,
                        Url = l.Url,
                    })
                    .ToList();

                using (var stream = File.Create(_tempPath))
                    JsonSerializer.Serialize(
                        stream, stored, DriveLinkJsonContext.Default.ListStoredLink);

                File.Move(_tempPath, _path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Vaktari.Core.Quiet.Swallowed("drive-links", e);
            }
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
