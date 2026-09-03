using System.Text;

using Tmds.DBus.Protocol;

using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// The interface every freedesktop desktop uses to say "show me this file where
/// it lives": org.freedesktop.FileManager1, with ShowItems, ShowFolders and
/// ShowItemProperties.
///
/// **This is the other half of being the default file manager, and Vaktari had
/// only the first.** The MIME association LinuxDefaultFileManager writes covers
/// opening a folder. It does not cover showing a FILE inside one, because
/// nothing in the MIME system can express "and select this" — so a browser's
/// "Open Containing Folder", a chat client's download button and Plasma's own
/// "Open Containing Folder" all go to the bus instead, and found nobody home.
///
/// **The name is claimed only when Vaktari is the desktop's file manager**, and
/// that is the whole policy. org.freedesktop.FileManager1 is a singleton that
/// Dolphin, Nautilus, Nemo and Thunar all want; whoever asks first gets it. An
/// application that grabbed it merely because it happened to be running would
/// make "show in folder" land wherever the last-started file manager was, which
/// is a bug nobody can reproduce on purpose. The user has already answered this
/// question by choosing a default folder handler, so that answer is the one
/// used, and turning the setting on or off takes effect at once.
///
/// **Tmds.DBus.Protocol rather than a hand-rolled client.** UdisksEjector and
/// LinuxDiskImages both record the standing objection to speaking D-Bus by
/// hand, and it is right — for a CLIENT, which can drive a daemon's own command
/// line instead. There is no CLI that can OWN a bus name: busctl and gdbus can
/// call a method, neither can serve one. Serving is the entire feature.
/// </summary>
internal sealed class FreedesktopFileManager : IFileManagerService, IPathMethodHandler
{
    internal const string BusName = "org.freedesktop.FileManager1";
    internal const string ObjectPath = "/org/freedesktop/FileManager1";
    internal const string InterfaceName = "org.freedesktop.FileManager1";

    /// <summary>
    /// The one signature all three methods take: an array of URIs and a startup
    /// id. Checked rather than assumed, because a malformed body would send the
    /// reader off the end of the message, and a stray call from anything on the
    /// bus must not be able to take a file manager down.
    /// </summary>
    private const string Arguments = "ass";

    /// <summary>
    /// What `busctl introspect` and d-feet see. It earns its place: a browser
    /// button that does nothing cannot tell you whether the name was unclaimed,
    /// the object path is wrong, or the call arrived and was dropped, and this
    /// is the only way to ask the running process by hand. A test checks it
    /// against <see cref="KindOf"/> so the two cannot drift.
    /// </summary>
    internal static readonly byte[] InterfaceXml = Encoding.UTF8.GetBytes(
        """
        <interface name="org.freedesktop.FileManager1">
          <method name="ShowFolders">
            <arg type="as" name="URIs" direction="in"/>
            <arg type="s" name="StartupId" direction="in"/>
          </method>
          <method name="ShowItems">
            <arg type="as" name="URIs" direction="in"/>
            <arg type="s" name="StartupId" direction="in"/>
          </method>
          <method name="ShowItemProperties">
            <arg type="as" name="URIs" direction="in"/>
            <arg type="s" name="StartupId" direction="in"/>
          </method>
        </interface>
        """);

    private readonly IDefaultFileManager _defaults;
    private readonly string? _address;
    private DBusConnection? _serving;

    internal FreedesktopFileManager(IDefaultFileManager defaults)
        : this(defaults, Address.Session) { }

    /// <summary>
    /// A bus of somebody else's choosing, for tests only.
    ///
    /// **They must not use the real session bus.** This name is per-USER, the
    /// suite runs while the author's desktop is up, and claiming
    /// org.freedesktop.FileManager1 there either fails because Nautilus holds
    /// it or takes every "show in folder" on the machine into a test process.
    /// </summary>
    internal FreedesktopFileManager(IDefaultFileManager defaults, string? address)
    {
        _defaults = defaults;
        _address = address;
    }

    /// <summary>The session bus, or null when this session has none — a TTY, a
    /// container, a CI runner.</summary>
    private static class Address
    {
        internal static string? Session
            => Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
    }

    public event EventHandler<ShowRequest>? Requested;

    /// <summary>
    /// The method names to the three verbs, and null for anything else.
    ///
    /// Separate from the handler so it can be asked without a bus, which is the
    /// difference between a test that runs on a CI runner and one that does not.
    /// </summary>
    internal static ShowKind? KindOf(string? member) => member switch
    {
        "ShowItems" => ShowKind.Items,
        "ShowFolders" => ShowKind.Folders,
        "ShowItemProperties" => ShowKind.ItemProperties,
        _ => null,
    };

    // ---- claiming and giving up the role ------------------------------------

    public async Task<FileManagerServiceState> ReconcileAsync()
    {
        if (!_defaults.IsDefault())
        {
            Release();

            // **The activation file goes with the name, from the same boolean.**
            // Left behind, it starts Vaktari for a role Vaktari will decline the
            // moment it is up — a window nobody asked for, once.
            FileManager1ServiceFile.Remove();

            return FileManagerServiceState.NotDefault;
        }

        // Written whether or not the name is free, and before the attempt.
        // Being outbid by a running Dolphin says nothing about whether the user
        // wants Vaktari started when nothing else is up, and the file is also
        // how a binary that has moved repoints itself.
        FileManager1ServiceFile.Install(Environment.ProcessPath);

        if (_serving is not null) return FileManagerServiceState.Serving;

        // No session bus at all — a TTY, a container, a CI runner.
        if (string.IsNullOrEmpty(_address)) return FileManagerServiceState.Unavailable;

        DBusConnection? bus = null;

        try
        {
            // Our own connection rather than the shared session one, so its
            // lifetime is ours: giving the role back is then a Dispose, and the
            // daemon releases the name for us — which is also what happens when
            // the process is killed, so there is exactly one path and not two.
            bus = new DBusConnection(_address);
            await bus.ConnectAsync().ConfigureAwait(false);

            // **Registered BEFORE the name is claimed.** dbus-daemon delivers a
            // queued activation call the instant the name is acquired, and a
            // name owned by a path with nothing behind it answers UnknownObject
            // to the very call it was claimed for — which is the one case this
            // whole feature exists for.
            bus.AddMethodHandler(this);

            // **RequestNameOptions.None, and this is the trap in this file.**
            // The enum's `Default` member is AllowReplacement|ReplaceExisting —
            // 3, not 0 — so the option that reads like "the sensible one" would
            // take the name off a running Dolphin without asking, and let the
            // next file manager take it off us. Neither is a rule a user could
            // predict, and MakeDefault already states the rule this obeys:
            // another handler is somebody's deliberate choice.
            //
            // Not a queued request either — a queued one hands us the name
            // later, when the other file manager exits, silently moving "show
            // in folder" in the middle of a session.
            if (!await bus.TryRequestNameAsync(BusName, RequestNameOptions.None)
                          .ConfigureAwait(false))
            {
                bus.Dispose();

                return FileManagerServiceState.Taken;
            }

            _serving = bus;

            return FileManagerServiceState.Serving;
        }
        catch (Exception ex)
        {
            // Same posture as the single-instance listener: without this we are
            // still a working file manager, other applications simply cannot
            // reach us. Nothing here is worth failing to start over.
            Console.Error.WriteLine($"[vaktari] FileManager1 unavailable: {ex.Message}");

            try { bus?.Dispose(); } catch (Exception e) { Quiet.Swallowed("filemanager1", e); }

            return FileManagerServiceState.Unavailable;
        }
    }

    private void Release()
    {
        if (_serving is not { } bus) return;

        _serving = null;

        try { bus.Dispose(); }
        catch (Exception ex) { Quiet.Swallowed("filemanager1", ex); }
    }

    public void Dispose() => Release();

    // ---- answering ----------------------------------------------------------

    string IPathMethodHandler.Path => ObjectPath;

    /// <summary>False: this object is a leaf, and claiming its children would
    /// answer for paths the interface says nothing about.</summary>
    bool IPathMethodHandler.HandlesChildPaths => false;

    /// <summary>
    /// Not async, deliberately. The connection reads no further messages until
    /// this returns, so the only correct thing to do with a request is hand it
    /// on and reply — a window opening a folder is not something a bus reader
    /// thread waits for.
    /// </summary>
    ValueTask IPathMethodHandler.HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            // **ReadOnlySpan<string>.Empty rather than [].** There are two
            // ReplyIntrospectXml overloads — one taking ReadOnlySpan<string>,
            // one taking IList<string> — and a collection expression matches
            // both, so `[]` is an ambiguous call that does not compile. There
            // are no child paths to name either way.
            context.ReplyIntrospectXml(
                [InterfaceXml.AsMemory()], ReadOnlySpan<string>.Empty);

            return ValueTask.CompletedTask;
        }

        var request = context.Request;

        // **The interface name is half the question.** A method handler is
        // registered per PATH, not per interface, so a call to some other
        // interface's ShowItems on this same object arrives here too — and
        // answering it would be this service claiming a contract it has never
        // read.
        if (request.InterfaceAsString != InterfaceName
            || KindOf(request.MemberAsString) is not { } kind)
        {
            context.ReplyUnknownMethodError();

            return ValueTask.CompletedTask;
        }

        if (request.SignatureAsString != Arguments)
        {
            context.ReplyError(
                $"{InterfaceName}.Error.InvalidArguments",
                "ShowItems, ShowFolders and ShowItemProperties take an array of "
                + "URIs and a startup id.");

            return ValueTask.CompletedTask;
        }

        string[] uris;

        try
        {
            var reader = request.GetBodyReader();

            uris = reader.ReadArrayOfString();

            // Read, then discarded, and the reading is the point: a body left
            // half-consumed is a decoding fault that gets blamed on the caller.
            //
            // Discarded because it is an X11 startup-notification id or a
            // Wayland activation token, and the toolkit offers no way to hand
            // one to Window.Activate. Raising the window is all that can
            // honestly be done, and under a compositor that refuses focus
            // stealing that may be a flashing task entry rather than a raise.
            _ = reader.ReadString();
        }
        catch (Exception ex)
        {
            context.ReplyError($"{InterfaceName}.Error.InvalidArguments", ex.Message);

            return ValueTask.CompletedTask;
        }

        // **Replied before anything is shown, not after.** All three methods
        // return nothing, so the caller is waiting purely for an
        // acknowledgement — while the work behind them has to reach the UI
        // thread, which may be part way through loading a folder on a dead SMB
        // host. A handler that waited would hold the caller's button spinning
        // for exactly as long as that took.
        using (var reply = context.CreateReplyWriter(""))
        {
            context.Reply(reply.CreateMessage());
        }

        var paths = new List<string>(uris.Length);

        foreach (var uri in uris)
            if (FileUri.ToLocalPath(uri) is { Length: > 0 } path)
                paths.Add(path);

        if (paths.Count > 0) Requested?.Invoke(this, new ShowRequest(kind, paths));

        return ValueTask.CompletedTask;
    }
}
