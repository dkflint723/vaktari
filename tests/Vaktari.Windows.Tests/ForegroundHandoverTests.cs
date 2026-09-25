using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The grant a launch makes so the running copy may come forward.
///
/// Whether Windows then honours it depends on who has the foreground, which a
/// test host does not — so false is a fine answer here. What this holds is
/// that the call reaches user32 at all: a misnamed entry point would throw on
/// every handover instead, and Program would never learn why.
/// </summary>
[SupportedOSPlatform("windows")]
public class ForegroundHandoverTests
{
    [WindowsFact]
    public void The_grant_reaches_the_system()
    {
        var ex = Record.Exception(() => ForegroundHandover.Allow());

        Assert.Null(ex);
    }
}
