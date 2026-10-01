using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Choosing a folder to serve, when you did not get here by right-clicking one.
///
/// Both ways in matter: typing is faster when you know the path, and browsing
/// is the only option when you do not. Neither is a good enough default to be
/// the only one.
/// </summary>
public sealed partial class ShareRequestViewModel : ObservableObject
{
    private readonly Func<string, ShareOptions, Task> _share;

    public ShareRequestViewModel(string startingPath, Func<string, ShareOptions, Task> share)
    {
        _share = share;
        _path = startingPath;

        Refresh();
    }

    /// <summary>
    /// Subfolders of the current path.
    ///
    /// Browsing is done here rather than through the platform folder picker for
    /// two reasons. Avalonia's picker is documented to fail or hang when opened
    /// from a window shown with ShowDialog on Linux (AvaloniaUI/Avalonia#10998
    /// and #6589) — which is exactly this situation. And more to the point,
    /// this is a file manager: listing folders is the one thing it definitely
    /// knows how to do, so borrowing someone else's browser for it was the
    /// wrong instinct even before it broke.
    /// </summary>
    public ObservableCollection<string> Folders { get; } = new();

    public bool CanGoUp => PathRules.Parent(Folder) is not null;

    /// <summary>
    /// The folder the box names: the text exactly as it stands when that
    /// folder exists, and otherwise the text with what was typed around it
    /// taken off.
    ///
    /// **Every question here trimmed the path first**, so a folder called
    /// "album " — legal on Linux, and on Windows through WSL or a share — was
    /// shared as "album", its neighbour, and browsed as it too (the fix-8
    /// verification). A trailing space that belongs to a name is part of the
    /// name; one a person typed after a path that exists without it is not.
    /// </summary>
    private string Folder => Exists(AsWritten(Path)) ? Path : Path.Trim();

    /// <summary>
    /// The text as Win32 will read its names, with none of them folded: "/"
    /// is a separator, and a device spelling — "\\.\X:\…", or "//?/" and the
    /// other forms that are not a literal "\\?\" — reads the names under it
    /// as a plain path's (ReachablePath.Unopenable reads them the same way),
    /// so it is asked here through "\\?\", which keeps the last one as it is.
    ///
    /// **Only to ask whether the exact folder is there.** "D:/x/album " and
    /// "\\.\D:\x\album " have no spelling ReachablePath.Exact will make,
    /// so the question answered no, the text was trimmed, and "album" — the
    /// neighbour — was shared (fix-9 verification). Asked this way, the folder
    /// is found, kept as typed, and refused by the hand-off rule, which reads
    /// every spelling. What is shared is never this text: an ordinary folder
    /// goes on as it was typed.
    ///
    /// **"." and ".." are walked away first, as Win32 walks them.** "\\?\"
    /// takes them as names, so ReachablePath.Exact had no spelling for
    /// "D:\x\.\album " or "D:\x\album\..\album ", the question answered no,
    /// the text was trimmed, and the server — whose GetFullPath folds the
    /// same way — was handed "album", the neighbour (changelog check for
    /// 0.11.1).
    /// </summary>
    private static string AsWritten(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;

        // A literal "\\?\" or "\??\" is opened as written, "/" and all.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal))
            return path;

        var unified = path.Replace('/', '\\');

        return unified.StartsWith(@"\\.\", StringComparison.Ordinal) || unified.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? WithoutDots(@"\\?\" + unified[4..], device: true)
            : WithoutDots(unified, device: false);
    }

    /// <summary>
    /// A full path with its "." and ".." names walked away and its doubled
    /// separators closed up, as Win32 does before it opens anything — and
    /// nothing else: **the trailing space or dot of every name left stays**,
    /// the one step of Win32's walk this must not take. ".." never climbs
    /// past the root. A path that is not full comes back as it is.
    ///
    /// **A device spelling's root is the prefix alone.** Win32 lets ".."
    /// climb past the drive in "\\.\C:\..\C:\x\album ", to "\\.\" and back
    /// down to "C:\x\album " — GetFullPath gives the same — and a walk
    /// that stopped at "C:\" asked about "C:\C:\x\album ", found nothing,
    /// and shared the neighbour (fix-11 verification).
    /// </summary>
    private static string WithoutDots(string path, bool device)
    {
        var root = device ? @"\\?\" : System.IO.Path.GetPathRoot(path);

        if (string.IsNullOrEmpty(root) || !System.IO.Path.IsPathFullyQualified(path)) return path;

        var names = new List<string>();

        foreach (var name in path[root.Length..].Split('\\'))
        {
            if (name is "" or ".") continue;

            if (name == "..")
            {
                if (names.Count > 0) names.RemoveAt(names.Count - 1);
                continue;
            }

            names.Add(name);
        }

        var joined = string.Join('\\', names);

        return root.EndsWith('\\') || joined.Length == 0 ? root + joined : root + '\\' + joined;
    }

    /// <summary>Whether this exact folder is there: asked through the spelling
    /// that reaches it, so on Windows "album " is not answered for by "album".</summary>
    private static bool Exists(string path)
        => ReachablePath.Exact(path) is { Length: > 0 } exact && Directory.Exists(exact);

    /// <summary>
    /// Why this folder cannot be served, or null. On Windows a name Win32 folds
    /// would be served as its neighbour — the server is another program, handed
    /// the folder by name — so it is the hand-off rule, in its own words. On
    /// Linux it is null: the name is ordinary and the folder is served exactly.
    ///
    /// **And not a device that only looks like a folder.** "\\.\pipe\" answers
    /// Directory.Exists true — so does "\\.\mailslot\" — and the dialog listed
    /// the machine's named pipes as folders and offered to share them (0.11.1
    /// QA). See <see cref="NotOnAVolume"/>.
    /// </summary>
    private string? Refused => NotOnAVolume(Folder) ?? ReachablePath.RefuseHandedOut(Folder);

    /// <summary>
    /// Why this text names something in the Win32 device namespace other than
    /// a folder on a drive ("\\.\C:\…", "\\?\C:\…"), on a share
    /// ("\\?\UNC\server\share\…") or on a volume ("\\?\Volume{…}\…") — the
    /// three forms VolumeRoots reads — or null. On Linux, null.
    ///
    /// **Read as Win32 reads the names**, through <see cref="AsWritten"/>, so
    /// "//./pipe/" and "\\.\C:\..\pipe\" are the pipe namespace as surely as
    /// "\\.\pipe\" is. Everything else under the device prefix — pipes,
    /// mailslots, GLOBALROOT and the object manager's other names — is
    /// refused rather than chased: nothing in Vaktari hands one out, and a
    /// folder that is really on a volume can be typed by its ordinary name.
    /// </summary>
    private static string? NotOnAVolume(string folder)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var written = AsWritten(folder);

        if (!written.StartsWith(@"\\?\", StringComparison.Ordinal) && !written.StartsWith(@"\??\", StringComparison.Ordinal))
            return null;

        var first = written[4..].Split('\\')[0];

        var drive = first.Length == 2 && char.IsAsciiLetter(first[0]) && first[1] == ':';
        var share = first.Equals("UNC", StringComparison.OrdinalIgnoreCase);
        var volume = first.Length == 44
                     && first.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase) && first.EndsWith('}')
                     && Guid.TryParse(first.AsSpan(6), out _);

        return drive || share || volume
            ? null
            : $"\"{folder}\" is not a folder on a drive, a network share or a volume — only those can be shared";
    }

    private void Refresh()
    {
        Folders.Clear();

        var current = Folder;

        // Said, and not browsed: listing it would list the neighbour's folders.
        if (Refused is { } why)
        {
            Status = why;
            OnPropertyChanged(nameof(CanGoUp));
            return;
        }

        if (!Exists(current)) return;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(current)
                         .Select(System.IO.Path.GetFileName)
                         .Where(n => !string.IsNullOrEmpty(n) && !n!.StartsWith('.'))
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                Folders.Add(directory!);
        }
        catch (Exception ex)
        {
            Status = ex is UnauthorizedAccessException
                ? "no permission to list that folder"
                : ex.Message;
        }

        OnPropertyChanged(nameof(CanGoUp));
    }

    [RelayCommand]
    private void Enter(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;

        Path = System.IO.Path.Combine(Folder, name);
    }

    [RelayCommand]
    private void GoUp()
    {
        // PathRules.Parent, not Directory.GetParent: the latter returns a
        // DirectoryInfo whose FullName touches the current working directory for
        // a relative path, and this dialog only ever asks a question about the
        // shape of the string it holds.
        if (PathRules.Parent(Folder) is { } parent) Path = parent;
    }

    [ObservableProperty] private string _path = "";
    [ObservableProperty] private bool _writable;

    /// <summary>Off unless ticked: announcing puts the share by name into
    /// every file manager on the network, which is the opposite of handing
    /// one person an address.</summary>
    [ObservableProperty] private bool _announce;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;

    partial void OnPathChanged(string value)
    {
        OnPropertyChanged(nameof(CanShare));
        Status = "";
        Refresh();
    }

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanShare));

    /// <summary>
    /// Checked here rather than on submit, so a wrong path is visible while
    /// typing instead of becoming an error afterwards.
    /// </summary>
    public bool CanShare => !Busy
                            && !string.IsNullOrWhiteSpace(Path)
                            && Refused is null
                            && Exists(Folder);

    public event EventHandler? Finished;

    [RelayCommand]
    private async Task ShareAsync()
    {
        if (!CanShare) return;

        Busy = true;
        Status = "starting…";

        try
        {
            await _share(Folder, new ShareOptions(Writable, Announce)).ConfigureAwait(true);
            Finished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, EventArgs.Empty);
}
