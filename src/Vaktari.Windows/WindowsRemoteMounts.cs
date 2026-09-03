using System.Runtime.InteropServices;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;

namespace Vaktari.Windows;

/// <summary>
/// Connects and lists network shares through the Windows redirector.
///
/// The same bargain LinuxRemoteMounts strikes with gvfs: Vaktari does not speak
/// SMB, and does not need to. The redirector has spoken it since before this
/// application existed, exposes every share as an ordinary path, and gains
/// WebDAV as well whenever the WebClient service is running. So the whole of
/// network browsing is a path and an enumeration.
///
/// **Deviceless connections, not mapped drive letters.** WNetAddConnection2 can
/// map `\\nas\media` to a free letter, and this deliberately does not: a
/// lettered connection is a DriveType.Network drive, and WindowsPlacesProvider
/// already lists those under Network in the sidebar. Mapping one here would put
/// the same share on screen twice, in two groups, under two names. So Vaktari
/// connects without a letter, browses the UNC path directly, and
/// <see cref="Discover"/> reports only the letterless connections — leaving
/// anything the user mapped themselves exactly where they already expect it.
///
/// **Credentials are Windows' business.** A share needing a password is
/// retried with CONNECT_INTERACTIVE | CONNECT_PROMPT, which brings up the
/// system credential dialog with its own "remember me". Vaktari never sees,
/// stores or transmits the password — and gets Credential Manager for free,
/// which is the same reason LinuxRemoteMounts tells the user to connect once
/// from their file manager and let the desktop keep it.
/// </summary>
public sealed class WindowsRemoteMounts : IRemoteMounts
{
    /// <summary>
    /// mpr.dll ships with Windows and the redirector is always running, so
    /// unlike the Linux side there is no helper whose absence to report.
    /// </summary>
    public bool IsAvailable => true;

    public string AddressPrefill => @"\\";

    public string AddressHint => @"\\server\share · smb:// · http:// for WebDAV";

    public IReadOnlyList<RemoteMount> Discover()
    {
        var found = new List<RemoteMount>();

        foreach (var (local, remote) in Connections())
        {
            // A connection WITH a drive letter is already a drive, and Places
            // lists it. Reporting it here too would duplicate it in the sidebar.
            if (!string.IsNullOrEmpty(local)) continue;
            if (string.IsNullOrEmpty(remote)) continue;

            found.Add(Build(remote));
        }

        return found
            .GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every current connection, lettered or not, as (localName, remoteName).
    ///
    /// The buffer is grown rather than guessed: WNetEnumResource answers
    /// ERROR_MORE_DATA and writes the size it wanted, and the strings live
    /// inside the same buffer the structures do, so one entry can need far more
    /// than the structure's own 56 bytes.
    /// </summary>
    private static List<(string Local, string Remote)> Connections()
    {
        var found = new List<(string, string)>();

        var status = Native.WNetOpenEnum(
            Native.RESOURCE_CONNECTED, Native.RESOURCETYPE_DISK, 0, 0, out var handle);

        if (status != Native.NO_ERROR) return found;

        var size = 16 * 1024;
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            while (true)
            {
                var count = uint.MaxValue;
                var bytes = (uint)size;

                status = Native.WNetEnumResource(handle, ref count, buffer, ref bytes);

                if (status == Native.ERROR_MORE_DATA)
                {
                    // Grow to what it asked for and ask again; the enumeration
                    // has not advanced, so nothing is skipped.
                    Marshal.FreeHGlobal(buffer);
                    size = (int)bytes;
                    buffer = Marshal.AllocHGlobal(size);
                    continue;
                }

                if (status != Native.NO_ERROR) break;

                for (var i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<Native.NETRESOURCEW>(
                        buffer + i * Marshal.SizeOf<Native.NETRESOURCEW>());

                    found.Add((
                        entry.lpLocalName == 0 ? "" : Marshal.PtrToStringUni(entry.lpLocalName) ?? "",
                        entry.lpRemoteName == 0 ? "" : Marshal.PtrToStringUni(entry.lpRemoteName) ?? ""));
                }
            }
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("mounts", ex);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Native.WNetCloseEnum(handle);
        }

        return found;
    }

    private static RemoteMount Build(string remote) => new()
    {
        Path = remote,
        Label = LabelFor(remote),
        Protocol = Protocol(remote),
        Reachable = IsReachable(remote),
    };

    /// <summary>
    /// "media on nas", matching the phrasing the gvfs reader produces.
    ///
    /// Two things are tidied on the way, both of them redirector syntax rather
    /// than anything a person typed. A WebDAV host carries its port as
    /// `host@8080` or its scheme as `host@SSL`, which reads as an email
    /// address; the port is restored to the `host:8080` a person would
    /// recognise and `@SSL` simply dropped. And a connection to the root of a
    /// WebDAV server has `DavWWWRoot` as its share name — the redirector's own
    /// name for "the whole server", which says nothing to anybody — so the host
    /// stands alone instead.
    /// </summary>
    internal static string LabelFor(string unc)
    {
        var parts = unc.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0) return unc;

        var host = Host(parts[0]);

        if (parts.Length == 1) return host;

        return parts[^1].Equals("DavWWWRoot", StringComparison.OrdinalIgnoreCase)
            ? host
            : $"{parts[^1]} on {host}";
    }

    private static string Host(string host)
    {
        var at = host.IndexOf('@');
        if (at < 0) return host;

        var suffix = host[(at + 1)..];

        // A port becomes the punctuation everyone reads as a port; @SSL only
        // says the redirector chose https, which the entry's protocol covers.
        return suffix.All(char.IsAsciiDigit) ? $"{host[..at]}:{suffix}" : host[..at];
    }

    /// <summary>
    /// WebDAV shares arrive as `\\host@SSL\path` or `\\host@8080\path`, which is
    /// how the redirector spells an HTTP endpoint. Everything else here is SMB.
    /// </summary>
    internal static string Protocol(string unc)
    {
        var host = unc.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return host.Contains('@', StringComparison.Ordinal) ? "dav" : "smb";
    }

    private static bool IsReachable(string path)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch
        {
            // A share whose far end has gone lists as an empty folder otherwise,
            // which is indistinguishable from an empty share.
            return false;
        }
    }

    /// <summary>
    /// Accepts what a Windows user would type and what the rest of Vaktari
    /// passes around.
    ///
    /// `smb://nas/media` is the form the discovery side produces — DNS-SD
    /// advertises services, not UNC paths — so it has to be understood here or
    /// double-clicking a discovered share would do nothing. `http://` is left
    /// alone: the redirector hands those to WebClient, which is how WebDAV is
    /// mounted on Windows.
    /// </summary>
    internal static string ToUnc(string uri)
    {
        var trimmed = (uri ?? "").Trim();

        if (trimmed.Length == 0)
            throw new ArgumentException("Type an address to connect to.", nameof(uri));

        // Already a UNC path, or a drive-relative one; hand it back unchanged.
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal)) return TrimTrailing(trimmed);

        var at = trimmed.IndexOf("://", StringComparison.Ordinal);

        var scheme = at >= 0 ? trimmed[..at].ToLowerInvariant() : "";
        var rest = at >= 0 ? trimmed[(at + 3)..] : trimmed;

        switch (scheme)
        {
            // The redirector speaks these itself.
            case "http":
            case "https":
            case "dav":
            case "davs":
                return trimmed;

            case "smb":
            case "cifs":
            case "file":
            case "":
                break;

            // Named rather than swallowed: "could not connect" would send the
            // user looking for a network fault that is not there.
            default:
                throw new NotSupportedException(
                    $"Windows cannot mount {scheme}:// — it connects to SMB shares and, with the "
                    + "WebClient service, WebDAV over http://.");
        }

        // "//nas/media" and "nas/media" both mean the same thing here.
        var unc = @"\\" + rest.Replace('/', '\\').TrimStart('\\');

        return TrimTrailing(unc);
    }

    private static string TrimTrailing(string unc)
        => unc.Length > 2 ? unc.TrimEnd('\\') : unc;

    public async Task<RemoteMount> MountAsync(string uri, CancellationToken ct)
    {
        var unc = ToUnc(uri);

        // What is connected before, so the new arrival can be told apart from
        // it afterwards. See the end of this method for why that is necessary.
        var before = Discover().Select(m => m.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var status = await Task.Run(() => Connect(unc, prompt: false), ct).ConfigureAwait(false);

        // Only the failures a password would actually fix are worth a dialog.
        // Prompting for a name that does not resolve just moves the error.
        if (status is Native.ERROR_ACCESS_DENIED
                   or Native.ERROR_INVALID_PASSWORD
                   or Native.ERROR_LOGON_FAILURE
                   or Native.ERROR_SESSION_CREDENTIAL_CONFLICT)
        {
            ct.ThrowIfCancellationRequested();
            status = await Task.Run(() => Connect(unc, prompt: true), ct).ConfigureAwait(false);
        }

        if (status != Native.NO_ERROR) throw new IOException(Explain(status, unc));

        // **The redirector renames what it connected, so ask it rather than
        // assume.** A WebDAV endpoint goes in as `http://host:port/` and comes
        // back as `\\host@port\DavWWWRoot`; returning the string that was passed
        // in produced a mount whose Path was a URL, whose Protocol read as smb
        // because there was no `@` in it, and which reported itself unreachable
        // because no directory answers to `http://…`. The shell then navigated
        // to it and resolved it against the working directory.
        //
        // Discover() had the right answer the whole time, which is what made
        // this survive a real test: the sidebar entry comes from there and was
        // correct, while the navigation immediately after connecting was not.
        // LinuxRemoteMounts polls for the new mount for the same reason -- gio
        // also decides where a share lands.
        var landed = Discover().FirstOrDefault(m => !before.Contains(m.Path));

        // Falls back for the case with no new entry: an SMB share that was
        // already connected, where the name asked for is the name it has.
        return landed ?? Build(unc);
    }

    private static int Connect(string unc, bool prompt)
    {
        var remote = Marshal.StringToHGlobalUni(unc);

        try
        {
            var resource = new Native.NETRESOURCEW
            {
                dwType = Native.RESOURCETYPE_DISK,
                dwDisplayType = Native.RESOURCEDISPLAYTYPE_SHARE,
                dwUsage = Native.RESOURCEUSAGE_CONNECTABLE,

                // Null: deviceless, so this does not become a drive letter.
                lpLocalName = 0,
                lpRemoteName = remote,
            };

            var flags = prompt ? Native.CONNECT_INTERACTIVE | Native.CONNECT_PROMPT : 0;

            // Null credentials mean "whoever I am already", which is what makes
            // a domain or Microsoft-account share connect without asking.
            return Native.WNetAddConnection2(ref resource, null, null, flags);
        }
        finally
        {
            Marshal.FreeHGlobal(remote);
        }
    }

    private static string Explain(int status, string unc) => status switch
    {
        Native.ERROR_BAD_NET_NAME or Native.ERROR_BAD_NETPATH =>
            $"could not find {unc} — check the server name and that the share exists",

        Native.ERROR_ACCESS_DENIED or Native.ERROR_INVALID_PASSWORD
            or Native.ERROR_LOGON_FAILURE =>
            "the server refused those credentials",

        Native.ERROR_SESSION_CREDENTIAL_CONFLICT =>
            "already connected to that server as a different user — disconnect that "
            + "connection first, since Windows allows only one set of credentials per server",

        Native.ERROR_CANCELLED => "cancelled",

        _ => $"could not connect to {unc} (error {status})",
    };

    /// <summary>
    /// What to hand WNetCancelConnection2, and with which flags.
    ///
    /// **A drive letter is spelled "Z:", not "Z:" with a backslash.** A place's Path is the
    /// root the rest of the application navigates to, which carries the
    /// trailing separator; the connection table is keyed on the device name
    /// without it, and the call answers ERROR_NOT_CONNECTED for the other
    /// spelling.
    ///
    /// The profile is only cleared for a letter. A letterless connection is one
    /// Vaktari made and never persisted, so there is nothing in the profile to
    /// take out — and asking to update a profile entry that does not exist is
    /// a difference worth not inventing.
    /// </summary>
    internal static (string Name, uint Flags) CancelTarget(string? path)
    {
        var trimmed = (path ?? "").TrimEnd('\\', '/');

        return trimmed.Length == 2 && trimmed[1] == ':'
            ? (trimmed, Native.CONNECT_UPDATE_PROFILE)
            : (trimmed, 0);
    }

    public Task<bool> UnmountAsync(RemoteMount mount, CancellationToken ct)
        => DisconnectAsync(mount.Path, ct);

    /// <summary>
    /// Gives a connection back, by the path it appears at.
    ///
    /// By path rather than by RemoteMount because a mapped drive is not one:
    /// that list holds the letterless connections Vaktari made itself, and
    /// Z: comes from the drive table.
    /// </summary>
    public async Task<bool> DisconnectAsync(string path, CancellationToken ct)
    {
        var (name, flags) = CancelTarget(path);

        var status = await Task.Run(
            () => Native.WNetCancelConnection2(name, flags, force: false), ct).ConfigureAwait(false);

        // Worth saying rather than retrying behind the user's back, which is
        // what the interface asks for.
        if (status is Native.ERROR_OPEN_FILES or Native.ERROR_DEVICE_IN_USE)
            throw new IOException(
                "something still has a file open on that share — close it and try again");

        // Already gone is not a failure: something else may have disconnected
        // it, and reporting "something may still be using it" about a drive
        // nothing is using is the wrong sentence twice over.
        return status is Native.NO_ERROR or Native.ERROR_NOT_CONNECTED;
    }
}
