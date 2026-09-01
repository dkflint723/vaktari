using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vaktari.Core;
using Vaktari.Core.Places;

namespace Vaktari.Windows;

/// <summary>
/// "Safely Remove Hardware", done properly.
///
/// **The cheaper versions of this are not smaller, they are wrong**, and each
/// one fails in a way that looks like success:
///
/// - <c>IOCTL_STORAGE_EJECT_MEDIA</c> alone is the OPTICAL verb. On a stick the
///   media and the device are the same object, so it sends START STOP UNIT, the
///   device node stays enumerated, the drive letter stays, and the button
///   appears to do nothing at all.
/// - Lock and dismount alone is an unmount, not a removal. The letter goes and
///   the device stays powered and enumerated, so Windows may still be holding
///   write-back for it.
/// - <c>CM_Request_Device_Eject</c> alone does not guarantee the filesystem
///   flushed first. On a device set to "better performance" that is exactly how
///   writes are lost, and losing them on the button whose entire promise is
///   "safe to unplug" is unrecoverable.
///
/// So the sequence is: identify the device, quiesce EVERY volume on it, then
/// ask the PnP manager to remove it — and report honestly when the PnP manager
/// says no.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsEjector : IEjector
{
    /// <summary>How long to keep asking for the volume lock. A lock fails while
    /// another process has an open handle, and handles opened by a scan or a
    /// thumbnailer clear on their own within a second or two.</summary>
    private const int LockAttempts = 10;
    private const int LockPauseMs = 500;

    private const int EjectAttempts = 3;
    private const int EjectPauseMs = 500;

    public Task<EjectResult> EjectAsync(string path, CancellationToken ct)
        => Task.Run(() => Eject(path, ct), ct);

    /// <summary>
    /// Which other drive letters live on the same physical device.
    ///
    /// Pure, and takes the probe results rather than doing the probing, because
    /// this is where the mistake would be: ejecting one partition of a
    /// multi-partition stick leaves its siblings mounted, they veto the removal,
    /// and the feature fails for a reason the person cannot see.
    /// </summary>
    internal static IReadOnlyList<string> SiblingsOf(
        string letter,
        IReadOnlyList<(string Letter, uint DeviceNumber, DriveType Type)> probed)
    {
        var mine = probed.FirstOrDefault(p =>
            string.Equals(p.Letter, letter, StringComparison.OrdinalIgnoreCase));

        if (mine.Letter is null) return [letter];

        return probed
            .Where(p => p.DeviceNumber == mine.DeviceNumber)
            .Where(p => p.Type is DriveType.Removable or DriveType.Fixed)
            .Select(p => p.Letter)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The system's refusal, in words a person can act on.
    ///
    /// **The name is only ever interpolated for the two veto types that name an
    /// application.** The most common refusal by far is OutstandingOpen, whose
    /// "name" is a device instance path like
    /// <c>USBSTOR\Disk&amp;Ven_SanDisk\4C530001…</c> — showing that to someone
    /// asking why their stick will not eject is worse than showing nothing.
    /// </summary>
    internal static string ExplainVeto(Native.PnpVetoType type, string? name, string letter)
    {
        var named = !string.IsNullOrWhiteSpace(name);

        return type switch
        {
            Native.PnpVetoType.WindowsApp when named =>
                $"{name} still has something open on {letter} — close it and try again",
            Native.PnpVetoType.WindowsService when named =>
                $"the {name} service still has {letter} open",
            Native.PnpVetoType.OutstandingOpen or Native.PnpVetoType.PendingClose =>
                $"something still has a file open on {letter} — close it and try again",
            Native.PnpVetoType.Device or Native.PnpVetoType.Driver or Native.PnpVetoType.LegacyDriver =>
                $"a driver is still using {letter}",
            Native.PnpVetoType.NonDisableable =>
                $"{letter} is not something Windows will let go of",
            Native.PnpVetoType.InsufficientRights =>
                $"Windows would not let this account release {letter}",
            _ => $"something still has a file open on {letter} — close it and try again",
        };
    }

    private static EjectResult Eject(string path, CancellationToken ct)
    {
        var letter = Letter(path);

        if (letter is null)
            return EjectResult.NotRemovable("that is not a drive this machine can eject");

        // **Refused before any handle is opened.** Insurance against a hot-plug
        // SATA disk that Windows classifies Removable: nothing should be able to
        // ask for the system volume to be torn out from under the running OS.
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        if (system is not null
            && string.Equals(Root(letter), system, StringComparison.OrdinalIgnoreCase))
            return EjectResult.NotRemovable("the system drive cannot be ejected");

        var probed = Probe();

        var mine = probed.FirstOrDefault(p =>
            string.Equals(p.Letter, letter, StringComparison.OrdinalIgnoreCase));

        if (mine.Letter is null)
            return EjectResult.NotRemovable($"Windows would not say what device {letter} is on");

        // Optical media and a removable device are different verbs, and which
        // one this is comes from the identify step rather than from a caller.
        if (mine.DeviceType == Native.FILE_DEVICE_CD_ROM)
            return EjectDisc(letter, ct);

        var letters = SiblingsOf(letter, probed
            .Select(p => (p.Letter, p.DeviceNumber, p.Type)).ToList());

        // Quiesce every volume on the device. A sibling left mounted vetoes the
        // removal, and half a device cannot be unplugged.
        var allDismounted = true;

        foreach (var volume in letters)
        {
            ct.ThrowIfCancellationRequested();

            if (!Quiesce(volume, ct)) allDismounted = false;
        }

        var devInst = DevNodeFor(mine.DeviceNumber);

        if (devInst is not { } node)
            return allDismounted
                ? EjectResult.Dismounted(
                    $"{letter} is written out and safe to unplug — but Windows would not name the device, so the drive stays listed")
                : EjectResult.InUse($"something still has a file open on {letter} — close it and try again");

        return Remove(node, letter, allDismounted, ct);
    }

    private static EjectResult Remove(uint devInst, string letter, bool allDismounted, CancellationToken ct)
    {
        var veto = Native.PnpVetoType.TypeUnknown;
        var name = (string?)null;
        var code = Native.CR_SUCCESS;

        for (var attempt = 0; attempt < EjectAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // ushort rather than char for the same reason FaceName gives: char
            // is not blittable across a LibraryImport boundary.
            const int capacity = 260;
            var buffer = Marshal.AllocHGlobal(capacity * sizeof(ushort));

            try
            {
                code = Native.CM_Request_Device_Eject(
                    devInst, out veto, buffer, capacity, 0);

                if (code == Native.CR_SUCCESS)
                    return EjectResult.Ejected($"{letter} is safe to unplug");

                name = Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (attempt < EjectAttempts - 1) Thread.Sleep(EjectPauseMs);
        }

        if (code != Native.CR_REMOVE_VETOED)
        {
            // Deliberately not guessing at the other CONFIGRET values: a
            // wrongly-guessed constant on a SUCCESS path would tell someone a
            // device is safe to pull when it is not. The number is shown so it
            // can be quoted.
            return EjectResult.Failed(
                $"Windows would not release {letter} (code {code})");
        }

        // **The honest middle.** The system refused to remove the device, but
        // every filesystem on it was flushed and torn down: the data is safe,
        // and the drive is still there.
        return allDismounted
            ? EjectResult.Dismounted(
                $"{letter} is written out and safe to unplug — but Windows still has the device, so the drive stays listed")
            : EjectResult.InUse(ExplainVeto(veto, name, letter));
    }

    /// <summary>
    /// Locks and dismounts one volume — the step that guarantees the data is
    /// actually on the device.
    ///
    /// **The handle is closed before the eject is attempted.** Leaving it open
    /// makes Vaktari itself the outstanding handle, and the veto then names our
    /// own process for a lock we are still holding.
    /// </summary>
    private static bool Quiesce(string letter, CancellationToken ct)
    {
        var handle = Native.CreateFile(
            $@"\\.\{letter}", Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            0, Native.OPEN_EXISTING, 0, 0);

        if (handle == Native.INVALID_HANDLE_VALUE) return false;

        try
        {
            var locked = false;

            for (var attempt = 0; attempt < LockAttempts && !locked; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                locked = Native.DeviceIoControl(
                    handle, Native.FSCTL_LOCK_VOLUME, 0, 0, 0, 0, out _, 0);

                if (!locked && attempt < LockAttempts - 1) Thread.Sleep(LockPauseMs);
            }

            // Dismount even when the lock never came: the dismount is what
            // flushes, and a failed lock only means someone else has a handle.
            var dismounted = Native.DeviceIoControl(
                handle, Native.FSCTL_DISMOUNT_VOLUME, 0, 0, 0, 0, out _, 0);

            return locked && dismounted;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    /// The optical path: there is no device to remove, only a tray to open.
    /// </summary>
    private static EjectResult EjectDisc(string letter, CancellationToken ct)
    {
        var handle = Native.CreateFile(
            $@"\\.\{letter}", Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            0, Native.OPEN_EXISTING, 0, 0);

        if (handle == Native.INVALID_HANDLE_VALUE)
            return EjectResult.Failed($"Windows would not open {letter}");

        try
        {
            for (var attempt = 0; attempt < LockAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (Native.DeviceIoControl(handle, Native.FSCTL_LOCK_VOLUME, 0, 0, 0, 0, out _, 0))
                    break;

                Thread.Sleep(LockPauseMs);
            }

            Native.DeviceIoControl(handle, Native.FSCTL_DISMOUNT_VOLUME, 0, 0, 0, 0, out _, 0);

            // Unlock the tray. A one-byte BOOLEAN, not a four-byte BOOL, and
            // ERROR_INVALID_FUNCTION here is ordinary — plenty of drives have
            // no lock to release.
            var allow = Marshal.AllocHGlobal(1);

            try
            {
                Marshal.WriteByte(allow, 0);
                Native.DeviceIoControl(
                    handle, Native.IOCTL_STORAGE_MEDIA_REMOVAL, allow, 1, 0, 0, out _, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(allow);
            }

            return Native.DeviceIoControl(
                handle, Native.IOCTL_STORAGE_EJECT_MEDIA, 0, 0, 0, 0, out _, 0)
                ? EjectResult.Ejected($"ejected the disc in {letter}")
                : EjectResult.InUse($"something still has a file open on {letter} — close it and try again");
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Every local drive letter with the physical device behind it.
    ///
    /// Opened with access 0 — which needs no elevation and spins no disk — and
    /// never for a network drive, whose probe is the call that blocks for the
    /// SMB timeout.
    /// </summary>
    private static List<(string Letter, uint DeviceNumber, uint DeviceType, DriveType Type)> Probe()
    {
        var found = new List<(string, uint, uint, DriveType)>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed or DriveType.CDRom))
                continue;

            var letter = drive.Name.TrimEnd(Path.DirectorySeparatorChar);

            if (Identify(letter) is not { } number) continue;

            found.Add((letter, number.DeviceNumber, number.DeviceType, drive.DriveType));
        }

        return found;
    }

    private static Native.STORAGE_DEVICE_NUMBER? Identify(string letter)
    {
        var handle = Native.CreateFile(
            $@"\\.\{letter}", 0,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            0, Native.OPEN_EXISTING, 0, 0);

        if (handle == Native.INVALID_HANDLE_VALUE) return null;

        try
        {
            var size = Marshal.SizeOf<Native.STORAGE_DEVICE_NUMBER>();
            var buffer = Marshal.AllocHGlobal(size);

            try
            {
                return Native.DeviceIoControl(
                    handle, Native.IOCTL_STORAGE_GET_DEVICE_NUMBER,
                    0, 0, buffer, (uint)size, out _, 0)
                    ? Marshal.PtrToStructure<Native.STORAGE_DEVICE_NUMBER>(buffer)
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("places", ex);
            return null;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    /// The device node to ask the PnP manager about.
    ///
    /// **Exactly one level up, and only when the parent is itself removable.**
    /// "Walk to the highest removable ancestor" sounds more thorough and is
    /// actively dangerous: a stick behind a USB hub has a removable HUB as its
    /// grandparent, and ejecting that takes every device on the hub with it.
    /// </summary>
    private static uint? DevNodeFor(uint deviceNumber)
    {
        try
        {
            if (Native.CM_Get_Device_Interface_List_Size(
                    out var length, in Native.GUID_DEVINTERFACE_DISK, 0,
                    Native.CM_GETIDLIST_FILTER_NONE) != Native.CR_SUCCESS || length == 0)
                return null;

            var list = Marshal.AllocHGlobal((int)length * sizeof(ushort));

            try
            {
                if (Native.CM_Get_Device_Interface_List(
                        in Native.GUID_DEVINTERFACE_DISK, 0, list, length,
                        Native.CM_GETIDLIST_FILTER_NONE) != Native.CR_SUCCESS)
                    return null;

                foreach (var interfacePath in MultiString(
                    Marshal.PtrToStringUni(list, (int)length) ?? ""))
                {
                    if (Identify(interfacePath) is not { } number) continue;
                    if (number.DeviceNumber != deviceNumber) continue;

                    if (InstanceId(interfacePath) is not { } id) continue;
                    if (Native.CM_Locate_DevNode(out var devInst, id, 0) != Native.CR_SUCCESS)
                        continue;

                    if (Native.CM_Get_Parent(out var parent, devInst, 0) == Native.CR_SUCCESS
                        && Native.CM_Get_DevNode_Status(out var status, out _, parent, 0)
                            == Native.CR_SUCCESS
                        && (status & Native.DN_REMOVABLE) != 0)
                        return parent;

                    return devInst;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(list);
            }
        }
        catch (Exception ex)
        {
            Quiet.Swallowed("places", ex);
        }

        return null;
    }

    /// <summary>
    /// The device instance id behind an interface path — the string
    /// CM_Locate_DevNode takes.
    ///
    /// Asked twice, as this family of API wants: once with no buffer to learn
    /// the size, once to fill it. The interface path is marshalled ONCE and
    /// freed in the same scope; allocating it inline in the first call is how
    /// a native string gets leaked on every eject.
    /// </summary>
    private static string? InstanceId(string interfacePath)
    {
        var name = Marshal.StringToHGlobalUni(interfacePath);

        try
        {
            uint size = 0;

            Native.CM_Get_Device_Interface_Property(
                name, in Native.DEVPKEY_Device_InstanceId, out _, 0, ref size, 0);

            if (size == 0) return null;

            var buffer = Marshal.AllocHGlobal((int)size);

            try
            {
                return Native.CM_Get_Device_Interface_Property(
                        name, in Native.DEVPKEY_Device_InstanceId, out _, buffer, ref size, 0)
                        == Native.CR_SUCCESS
                    ? Marshal.PtrToStringUni(buffer)
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    /// <summary>Splits a REG_MULTI_SZ-style buffer: strings back to back, the
    /// whole ended by an empty one. Read as one string and split, rather than
    /// walked as a pointer — this is not a hot path, and it keeps the method
    /// free of unsafe code.</summary>
    internal static IEnumerable<string> MultiString(string all)
    {
        foreach (var part in all.Split('\0'))
            if (part.Length > 0) yield return part;
    }

    /// <summary>"E:" from "E:\", "E:", or a path on it.</summary>
    private static string? Letter(string path)
    {
        var root = Path.GetPathRoot(path);

        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return null;

        return root[..2];
    }

    private static string Root(string letter) => letter + Path.DirectorySeparatorChar;
}
