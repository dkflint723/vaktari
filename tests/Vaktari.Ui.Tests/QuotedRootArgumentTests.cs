using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A drive root handed over by a command line that mangled it.
///
/// **"C:\\" arrives as C:".** A quoted root ends in \\", which reads as an
/// escaped quote; Vaktari registered itself for drives that way until it
/// stopped, and a registration made then still hands over C:". A Windows path
/// cannot hold a quote, so a trailing one is put back as the backslash it was.
/// </summary>
public sealed class QuotedRootArgumentTests
{
    [WindowsFact]
    public void A_root_that_lost_its_backslash_to_a_quote_gets_it_back()
    {
        Assert.Equal(@"C:\", Program.Repaired("C:\""));
    }

    [Fact]
    public void An_ordinary_path_is_left_as_it_came()
        => Assert.Equal(@"C:\Users\me\Documents", Program.Repaired(@"C:\Users\me\Documents"));
}
