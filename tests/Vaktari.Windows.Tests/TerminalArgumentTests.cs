using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The folder, as each terminal is handed it.
///
/// **A ; in a folder's name started a second command in Windows Terminal**,
/// which splits its arguments at every ; not preceded by a backslash, quotes
/// or no quotes. Escaped for Windows Terminal, and for nothing else.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TerminalArgumentTests
{
    private static readonly TerminalOption Wt = new("windows-terminal", "Windows Terminal", "wt.exe", ["-d", "{dir}"]);
    private static readonly TerminalOption Alacritty = new("alacritty", "Alacritty", "alacritty.exe", ["--working-directory", "{dir}"]);

    [WindowsFact]
    public void Windows_terminal_is_given_the_semicolon_escaped()
        => Assert.Equal(["-d", @"C:\x\a\;calc"], WindowsLauncher.ArgumentsFor(Wt, @"C:\x\a;calc"));

    /// <summary>A folder whose name starts with ; — the backslash before it
    /// already stops the split, and the escape still comes back off whole.</summary>
    [WindowsFact]
    public void A_name_starting_with_a_semicolon_keeps_its_separator()
        => Assert.Equal(["-d", @"C:\x\\;b"], WindowsLauncher.ArgumentsFor(Wt, @"C:\x\;b"));

    /// <summary>The chain tried when nothing was detected escapes it too.</summary>
    [WindowsFact]
    public void The_undetected_fallback_escapes_it_too()
        => Assert.Equal(["-d", @"C:\x\a\;calc"],
            WindowsLauncher.Fallbacks(@"C:\x\a;calc").Single(f => f.Program == "wt.exe").Arguments);

    [WindowsFact]
    public void Another_terminal_is_given_the_folder_as_it_is()
        => Assert.Equal(["--working-directory", @"C:\x\a;calc"], WindowsLauncher.ArgumentsFor(Alacritty, @"C:\x\a;calc"));
}
