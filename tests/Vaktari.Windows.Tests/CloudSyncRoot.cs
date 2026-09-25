using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Vaktari.Windows.Tests;

/// <summary>
/// A cloud files sync root of the test's own, holding placeholders whose data
/// is not on the disk — what OneDrive or Proton Drive leave for a file kept
/// online — and counting every time something asks for that data.
///
/// **Why a real provider rather than an attribute.** A test can set
/// FILE_ATTRIBUTE_OFFLINE on an ordinary file, and several do; it cannot set
/// RECALL_ON_DATA_ACCESS, and an ordinary file wearing OFFLINE is read like any
/// other when something opens it, so "was it downloaded" has no answer there.
/// Here the cloud files filter itself holds the file back, and the only way its
/// bytes reach anyone is through <see cref="FetchCount"/>'s callback. A code
/// path that promised not to download a file is measured, not trusted.
///
/// **Its own temp folder, its own registration, and nothing else.** The root is
/// a fresh folder under the system temp; <see cref="Dispose"/> disconnects,
/// unregisters it and deletes the folder, and does so whatever the test did.
/// No other sync root on the machine is touched or asked about: even the sweep
/// below asks only of folders this fixture's own naming made.
///
/// The fetch is answered — the bytes are served — so a path that does read a
/// placeholder gets what it asked for, returns, and leaves the file hydrated for
/// the test to see; a fetch left unanswered would hang the read for the
/// filter's whole timeout.
///
/// Fetches are kept in two lists by the process that asked: this one's in
/// <see cref="Fetched"/>, everyone else's — a scanner, an indexer, the
/// shell's out-of-process helpers — in <see cref="OtherFetches"/>. Both are
/// counted: OnlineOnlyFilesTests' AssertNothingFetched fails on either, because
/// a file another process fetched has been downloaded all the same, and a
/// fetch the code under test caused through some other process would otherwise
/// pass. The split is for the failure message, which names who asked.
///
/// **A root is never left registered by a test host that died.** Each folder
/// carries the id of the process that made it, and <see cref="Create"/> first
/// sweeps the temp folder for this fixture's roots whose process is gone — see
/// <see cref="SweepOrphans"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class CloudSyncRoot : IDisposable
{
    private static readonly Guid ProviderId = new("5b1f3c6e-8d7a-4c61-9a0e-1f2d3c4b5a69");

    private readonly ConcurrentDictionary<string, byte[]> _contents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _fetched = new();
    private readonly ConcurrentQueue<string> _others = new();
    private readonly int _self = Environment.ProcessId;

    private GCHandle _handle;
    private long _connection;
    private bool _registered;
    private bool _connected;

    public string Root { get; }

    /// <summary>Fetches this process asked for, since the root was made.</summary>
    public int FetchCount => _fetched.Count;

    /// <summary>The names fetched for this process, in order.</summary>
    public IReadOnlyCollection<string> Fetched => [.. _fetched];

    /// <summary>
    /// Fetches another process asked for, with its id. Counted as fetches all
    /// the same — AssertNothingFetched fails on any — and kept apart only so a
    /// failure says who asked.
    /// </summary>
    public IReadOnlyCollection<string> OtherFetches => [.. _others];

    /// <summary>The HRESULT CfRegisterSyncRoot answered, for a report when it refused.</summary>
    public int RegisterResult { get; }

    private CloudSyncRoot(string root)
    {
        Root = root;

        var registration = new CF_SYNC_REGISTRATION();
        var policies = new CF_SYNC_POLICIES
        {
            StructSize = (uint)sizeof(CF_SYNC_POLICIES),
            // FULL: a read of any part of a placeholder asks for all of it,
            // which is what a sync client does and what makes a header read
            // of a photo the same cost as opening it.
            HydrationPrimary = CF_HYDRATION_POLICY_FULL,
            // ALWAYS_FULL: the folders are complete as created, so the filter
            // never asks this provider to list one.
            PopulationPrimary = CF_POPULATION_POLICY_ALWAYS_FULL,
        };

        fixed (char* name = ProviderName)
        fixed (char* version = "1.0")
        {
            registration.StructSize = (uint)sizeof(CF_SYNC_REGISTRATION);
            registration.ProviderName = name;
            registration.ProviderVersion = version;
            registration.ProviderId = ProviderId;

            RegisterResult = CfRegisterSyncRoot(root, &registration, &policies, 0);
        }

        if (RegisterResult < 0) return;
        _registered = true;

        // **A failure from here on left the root registered.** The constructor
        // threw, so Create never had an object to dispose and deleted the
        // folder alone — and once connected, the registration outlived the
        // folder: made again at the same path, it read as a sync root for as
        // long as the test host ran (CloudSyncRootTests; gone once the host
        // had exited). Everything after the registration is undone here, by
        // the same Dispose a finished test uses, before the failure goes on.
        try
        {
            _handle = GCHandle.Alloc(this);

            var table = stackalloc CF_CALLBACK_REGISTRATION[2];
            table[0] = new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE_FETCH_DATA,
                Callback = &OnFetchData,
            };
            table[1] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_NONE };

            long key;
            Check(CfConnectSyncRoot(
                root, table, (void*)GCHandle.ToIntPtr(_handle),
                CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
                &key), "CfConnectSyncRoot");

            _connection = key;
            _connected = true;

            AfterConnecting?.Invoke(root);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The name every root this fixture registers carries, and the
    /// only one <see cref="SweepOrphans"/> will unregister.</summary>
    internal const string ProviderName = "Vaktari tests";

    /// <summary>What every root folder's name starts with, under the system temp.</summary>
    internal const string FolderPrefix = "vaktari-cloud-";

    /// <summary>
    /// The roots this process made and has not disposed or abandoned, which
    /// the sweep never touches whatever their folder's name says.
    ///
    /// **The process id in the name was not enough on its own.** The test that
    /// plays a killed host names a root after a process that is not running,
    /// and a sweep from another class creating a root in parallel unregistered
    /// it between registration and connect — measured, twice in six full
    /// runs, as CfConnectSyncRoot answering "not a cloud sync root".
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> Live = new(StringComparer.OrdinalIgnoreCase);

    [ThreadStatic] private static Action<string>? _afterConnecting;

    /// <summary>
    /// Runs last in the constructor, with the root registered and connected,
    /// and its path: for the test that makes the constructor fail with the most
    /// it can leave behind. Per thread, because the Windows tests run classes in
    /// parallel and another class may be making a root of its own at the same
    /// moment. Null otherwise.
    /// </summary>
    internal static Action<string>? AfterConnecting
    {
        get => _afterConnecting;
        set => _afterConnecting = value;
    }

    /// <summary>
    /// A new sync root in a fresh temp folder. Throws, carrying the HRESULT,
    /// when the system refuses to register it — the caller reports that rather
    /// than working round it.
    /// </summary>
    public static CloudSyncRoot Create() => Create(Environment.ProcessId);

    /// <summary>
    /// One whose folder names <paramref name="owner"/> as the process that made
    /// it: for the test that leaves a root behind as a killed host would.
    /// </summary>
    internal static CloudSyncRoot Create(int owner)
    {
        SweepOrphans();

        var root = Path.Combine(
            Path.GetTempPath(), $"{FolderPrefix}{owner}-{Guid.NewGuid().ToString("N")[..12]}");

        Live[root] = 0;
        Directory.CreateDirectory(root);

        CloudSyncRoot? made = null;

        try
        {
            made = new CloudSyncRoot(root);

            if (made.RegisterResult < 0)
                throw new InvalidOperationException(
                    $"CfRegisterSyncRoot refused '{root}': HRESULT 0x{made.RegisterResult:X8}");

            return made;
        }
        catch
        {
            if (made is not null) made.Dispose();
            else Delete(root);

            Live.TryRemove(root, out _);
            throw;
        }
    }

    /// <summary>
    /// An online-only file directly in the root: a placeholder of
    /// <paramref name="contents"/>' length whose bytes are held here, served
    /// only when the filter fetches them.
    /// </summary>
    public string Placeholder(string name, byte[] contents, bool inSync = false)
    {
        _contents[name] = contents;

        var identity = Encoding.Unicode.GetBytes(name);
        var now = DateTime.UtcNow.ToFileTimeUtc();

        fixed (char* relative = name)
        fixed (byte* id = identity)
        {
            var info = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = relative,
                FileIdentity = id,
                FileIdentityLength = (uint)identity.Length,
                // Not marked in sync, and created without data: the state a
                // provider leaves a file it has not downloaded.
                Flags = inSync ? CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC : 0,
            };

            info.FsMetadata.CreationTime = now;
            info.FsMetadata.LastAccessTime = now;
            info.FsMetadata.LastWriteTime = now;
            info.FsMetadata.ChangeTime = now;
            info.FsMetadata.FileAttributes = FILE_ATTRIBUTE_NORMAL;
            info.FsMetadata.FileSize = contents.Length;

            uint processed;
            Check(CfCreatePlaceholders(Root, &info, 1, 0, &processed), "CfCreatePlaceholders");
            Check(info.Result, $"CfCreatePlaceholders for '{name}'");
        }

        return Path.Combine(Root, name);
    }

    /// <summary>
    /// A file's attributes as a process that exposes placeholders sees them.
    /// Read through GetFileAttributesEx, not through Vaktari's own exposed
    /// directory read: a check sharing its implementation with the code under
    /// test would agree with it whether or not either was right.
    /// </summary>
    public static FileAttributes ExposedAttributes(string path)
    {
        var previous = RtlSetThreadPlaceholderCompatibilityMode(PHCM_EXPOSE_PLACEHOLDERS);

        try
        {
            return File.GetAttributes(path);
        }
        finally
        {
            RtlSetThreadPlaceholderCompatibilityMode(previous);
        }
    }

    /// <summary>
    /// Whether a file is still online-only: a placeholder whose data the
    /// filter would have to fetch before anyone could read it.
    /// </summary>
    public static bool IsOnlineOnly(string path)
    {
        var attributes = ExposedAttributes(path);

        return (attributes & FileAttributes.ReparsePoint) != 0
               && (attributes & (FileAttributes)FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        // Nothing may escape an unmanaged callback: an exception here ends the
        // test host. A failure is answered as a failed fetch instead, which the
        // read that asked reports as an error.
        try
        {
            var root = (CloudSyncRoot)GCHandle.FromIntPtr((IntPtr)info->CallbackContext).Target!;
            root.Serve(info);
        }
        catch
        {
            TryComplete(info, null, 0, STATUS_UNSUCCESSFUL);
        }
    }

    private void Serve(CF_CALLBACK_INFO* info)
    {
        var name = info->FileIdentity is null
            ? ""
            : new string((char*)info->FileIdentity, 0, (int)info->FileIdentityLength / sizeof(char));

        var pid = info->ProcessInfo is null ? 0 : info->ProcessInfo->ProcessId;

        if (pid == _self) _fetched.Enqueue(name);
        else _others.Enqueue($"{name} (process {pid})");

        if (!_contents.TryGetValue(name, out var bytes))
        {
            TryComplete(info, null, 0, STATUS_UNSUCCESSFUL);
            return;
        }

        fixed (byte* data = bytes)
            TryComplete(info, data, bytes.Length, 0);
    }

    /// <summary>
    /// The whole file in one transfer, from offset 0 — legal because it ends
    /// at the end of the file, which is the one place a transfer need not be a
    /// multiple of 4 KiB.
    /// </summary>
    private static void TryComplete(CF_CALLBACK_INFO* info, byte* data, long length, int status)
    {
        var operation = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = info->ConnectionKey,
            TransferKey = info->TransferKey,
            RequestKey = info->StructSize >= (uint)sizeof(CF_CALLBACK_INFO) ? info->RequestKey : 0,
        };

        var parameters = new CF_OPERATION_PARAMETERS
        {
            ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS),
            CompletionStatus = status,
            Buffer = data,
            Offset = 0,
            Length = status == 0 ? length : info->FileSize,
        };

        CfExecute(&operation, &parameters);
    }

    /// <summary>
    /// Unregisters and deletes the roots this fixture made in a test host that
    /// is no longer running.
    ///
    /// **A killed test host left its root registered and its folder behind**,
    /// because Dispose never ran — and nothing afterwards knew the folder was
    /// a sync root of nobody's. Swept at the start of the next Create.
    ///
    /// **Only this fixture's own, by three tests at once**: a folder directly
    /// under the system temp named with <see cref="FolderPrefix"/>; whose
    /// process — the id after the prefix — is not running and which is not one
    /// of this process's own <see cref="Live"/> roots, so a root in use here or
    /// in another host running beside this one is left alone; and which
    /// the cloud files filter reports as a root registered under
    /// <see cref="ProviderName"/>. Every other sync root on the machine is
    /// neither enumerated nor asked about, and a folder that matches the name
    /// but is not such a root is not touched. A folder from before the id was
    /// in the name has no id and counts as orphaned.
    ///
    /// Never throws: a root that will not go is left for the next sweep rather
    /// than failing a test that did not make it.
    /// </summary>
    internal static void SweepOrphans()
    {
        string[] folders;

        try
        {
            folders = Directory.GetDirectories(Path.GetTempPath(), FolderPrefix + "*");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var folder in folders)
        {
            if (Live.ContainsKey(folder) || OwnerIsRunning(Path.GetFileName(folder))) continue;

            if (RegisteredProvider(folder) != ProviderName) continue;

            try
            {
                if (CfUnregisterSyncRoot(folder) < 0 || IsSyncRoot(folder)) continue;

                Delete(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Whether the process a root folder is named after is still running. A
    /// name without an id is from before there was one, and its host is gone.
    /// </summary>
    private static bool OwnerIsRunning(string name)
    {
        var rest = name[FolderPrefix.Length..];
        var dash = rest.IndexOf('-', StringComparison.Ordinal);

        if (dash <= 0 || !int.TryParse(rest[..dash], out var pid)) return false;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);

            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The provider name the filter holds for the sync root at or above
    /// <paramref name="path"/>, or null when there is none.
    /// </summary>
    internal static string? RegisteredProvider(string path)
    {
        CF_SYNC_ROOT_PROVIDER_INFO info;
        uint returned;

        if (CfGetSyncRootInfoByPath(
                path, CF_SYNC_ROOT_INFO_PROVIDER, &info, (uint)sizeof(CF_SYNC_ROOT_PROVIDER_INFO), &returned) < 0)
            return null;

        return new string(info.ProviderName);
    }

    /// <summary>
    /// Disconnects and walks away, leaving the root registered and the folder
    /// on the disk: what a test host killed mid-test leaves, since the filter
    /// closes a dead process's connection itself. Dispose still unregisters.
    /// </summary>
    internal void Abandon()
    {
        if (_connected)
        {
            _connected = false;
            CfDisconnectSyncRoot(_connection);
        }

        Live.TryRemove(Root, out _);
    }

    /// <summary>Whether the filter holds <paramref name="path"/> as a registered sync root.</summary>
    public static bool IsSyncRoot(string path)
    {
        long basic;
        uint returned;

        return CfGetSyncRootInfoByPath(path, CF_SYNC_ROOT_INFO_BASIC, &basic, sizeof(long), &returned) >= 0;
    }

    /// <summary>What CfUnregisterSyncRoot answered, once disposed.</summary>
    public int UnregisterResult { get; private set; }

    /// <summary>Whether the root still read as a sync root after unregistering, asked before the folder went.</summary>
    public bool RegisteredAfterDispose { get; private set; }

    public void Dispose()
    {
        try
        {
            if (_connected)
            {
                _connected = false;
                CfDisconnectSyncRoot(_connection);
            }
        }
        finally
        {
            try
            {
                if (_registered)
                {
                    _registered = false;
                    UnregisterResult = CfUnregisterSyncRoot(Root);
                    RegisteredAfterDispose = IsSyncRoot(Root);
                }
            }
            finally
            {
                if (_handle.IsAllocated) _handle.Free();
                Delete(Root);
                Live.TryRemove(Root, out _);
            }
        }
    }

    /// <summary>
    /// The folder and every placeholder in it. Retried briefly, because a
    /// scanner that noticed the new files may still hold one open.
    /// </summary>
    private static void Delete(string root)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                return;
            }
            catch (Exception e) when (attempt < 20 && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void Check(int hresult, string what)
    {
        if (hresult < 0)
            throw new InvalidOperationException($"{what} failed: HRESULT 0x{hresult:X8}");
    }

    // ---- cldapi.dll, as cfapi.h declares it (x64 and ARM64 layouts) ----

    private const ushort CF_HYDRATION_POLICY_FULL = 2;
    private const ushort CF_POPULATION_POLICY_ALWAYS_FULL = 3;
    private const int CF_CALLBACK_TYPE_FETCH_DATA = 0;
    private const int CF_CALLBACK_TYPE_NONE = unchecked((int)0xFFFFFFFF);
    private const uint CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO = 0x2;
    private const uint CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH = 0x4;
    private const int CF_OPERATION_TYPE_TRANSFER_DATA = 0;
    private const int CF_SYNC_ROOT_INFO_BASIC = 0;
    private const int CF_SYNC_ROOT_INFO_PROVIDER = 2;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC = 0x2;
    internal const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x0040_0000;
    private const int STATUS_UNSUCCESSFUL = unchecked((int)0xC0000001);
    private const sbyte PHCM_EXPOSE_PLACEHOLDERS = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_SYNC_REGISTRATION
    {
        public uint StructSize;
        public char* ProviderName;
        public char* ProviderVersion;
        public void* SyncRootIdentity;
        public uint SyncRootIdentityLength;
        public void* FileIdentity;
        public uint FileIdentityLength;
        public Guid ProviderId;
    }

    /// <summary>CF_MAX_PROVIDER_NAME_LENGTH and _VERSION_LENGTH are 255, plus the terminator.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CF_SYNC_ROOT_PROVIDER_INFO
    {
        public int ProviderStatus;
        public fixed char ProviderName[256];
        public fixed char ProviderVersion[256];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_SYNC_POLICIES
    {
        public uint StructSize;
        public ushort HydrationPrimary;
        public ushort HydrationModifier;
        public ushort PopulationPrimary;
        public ushort PopulationModifier;
        public uint InSync;
        public uint HardLink;
        public uint PlaceholderManagement;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_CALLBACK_REGISTRATION
    {
        public int Type;
        public delegate* unmanaged[Stdcall]<CF_CALLBACK_INFO*, CF_CALLBACK_PARAMETERS*, void> Callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_PROCESS_INFO
    {
        public uint StructSize;
        public int ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_CALLBACK_INFO
    {
        public uint StructSize;
        public long ConnectionKey;
        public void* CallbackContext;
        public char* VolumeGuidName;
        public char* VolumeDosName;
        public uint VolumeSerialNumber;
        public long SyncRootFileId;
        public void* SyncRootIdentity;
        public uint SyncRootIdentityLength;
        public long FileId;
        public long FileSize;
        public void* FileIdentity;
        public uint FileIdentityLength;
        public char* NormalizedPath;
        public long TransferKey;
        public byte PriorityHint;
        public void* CorrelationVector;
        public CF_PROCESS_INFO* ProcessInfo;
        public long RequestKey;
    }

    /// <summary>Only the header: the union behind it is not read here.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CF_CALLBACK_PARAMETERS
    {
        public uint ParamSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_OPERATION_INFO
    {
        public uint StructSize;
        public int Type;
        public long ConnectionKey;
        public long TransferKey;
        public void* CorrelationVector;
        public void* SyncStatus;
        public long RequestKey;
    }

    /// <summary>
    /// ParamSize and the TransferData arm of the union, which is all
    /// CF_SIZE_OF_OP_PARAM(TransferData) covers.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct CF_OPERATION_PARAMETERS
    {
        [FieldOffset(0)] public uint ParamSize;

        // The union is 8-aligned, because other arms of it hold pointers.
        [FieldOffset(8)] public uint Flags;
        [FieldOffset(12)] public int CompletionStatus;
        [FieldOffset(16)] public byte* Buffer;
        [FieldOffset(24)] public long Offset;
        [FieldOffset(32)] public long Length;
    }

    /// <summary>CF_FS_METADATA: FILE_BASIC_INFO, then the size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CF_FS_METADATA
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
        public long FileSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CF_PLACEHOLDER_CREATE_INFO
    {
        public char* RelativeFileName;
        public CF_FS_METADATA FsMetadata;
        public void* FileIdentity;
        public uint FileIdentityLength;
        public uint Flags;
        public int Result;
        public long CreateUsn;
    }

    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CfRegisterSyncRoot(
        string syncRootPath, CF_SYNC_REGISTRATION* registration, CF_SYNC_POLICIES* policies, uint flags);

    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CfUnregisterSyncRoot(string syncRootPath);

    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CfConnectSyncRoot(
        string syncRootPath, CF_CALLBACK_REGISTRATION* callbackTable, void* callbackContext,
        uint connectFlags, long* connectionKey);

    [LibraryImport("cldapi.dll")]
    private static partial int CfDisconnectSyncRoot(long connectionKey);

    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CfCreatePlaceholders(
        string baseDirectoryPath, CF_PLACEHOLDER_CREATE_INFO* placeholderArray, uint placeholderCount,
        uint createFlags, uint* entriesProcessed);

    [LibraryImport("cldapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CfGetSyncRootInfoByPath(
        string filePath, int infoClass, void* infoBuffer, uint infoBufferLength, uint* returnedLength);

    [LibraryImport("cldapi.dll")]
    private static partial int CfExecute(CF_OPERATION_INFO* opInfo, CF_OPERATION_PARAMETERS* opParams);

    [LibraryImport("ntdll.dll")]
    private static partial sbyte RtlSetThreadPlaceholderCompatibilityMode(sbyte mode);
}
