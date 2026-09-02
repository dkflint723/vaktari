using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The reader the source-scanning tests share.
///
/// **They read the file raw, and the file is not raw.** The repository stores
/// LF; git hands out CRLF on checkout. So a scan for the end of a method —
/// "\n    }\n" — matched on the machine the tests were written on and found
/// nothing on the Windows agent that gates the merge. One test crashed on the
/// -1 outright, which is the only reason this was noticed; the others had a
/// "less than zero" fallback and quietly widened from "inside this method" to
/// "anywhere in the file", passing all the while.
///
/// A test that is weaker on the machine that gates the merge than on the one it
/// was written on is worse than no test, because nothing says so.
/// </summary>
public sealed class RepoSourceTests
{
    [Fact]
    public void Source_arrives_with_one_kind_of_line_ending()
    {
        var source = RepoSource.Ui("MainWindow.axaml.cs");

        Assert.DoesNotContain('\r', source);
        Assert.Contains('\n', source);
    }

    [Fact]
    public void A_method_body_stops_at_its_own_closing_brace()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("ViewModels", "Confirmations.cs"),
            "internal static string Subject(int count, string? only)");

        Assert.Contains("return $\"{count:N0} items\";", body);

        // The next member is NOT in it. Without the brace scan working, the
        // fallback handed back the rest of the file and every "is this inside
        // that method" assertion became "is this anywhere".
        Assert.DoesNotContain("private static string Elide", body);
    }

    /// <summary>
    /// A declaration that has been renamed is a broken test, and answering it
    /// with the whole file turns a broken test into a passing one.
    /// </summary>
    [Fact]
    public void A_method_that_is_not_there_is_an_error_rather_than_everything()
        => Assert.Throws<InvalidOperationException>(
            () => RepoSource.Body(RepoSource.Ui("MainWindow.axaml.cs"),
                                  "private void NoSuchMethodExists()"));
}
