using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// Reading an Exec= line out of a .desktop file.
///
/// **It was split on spaces and the quotes trimmed off each piece.** An
/// application installed in a directory with a space in its name —
/// "/opt/My App/bin/app", which is exactly what a quoted Exec= is FOR — came
/// out as two arguments, neither of which is a program, so "Open with" failed
/// with a message about a file that does not exist.
/// </summary>
public sealed class ExecLineTests
{
    private static List<string> Tokens(string exec) => DesktopEntries.Tokens(exec);

    private static List<string> Expand(string exec, string path)
        => DesktopEntries.SplitExec(exec, path);

    /// <summary>The whole finding, in the shape it actually appears in.</summary>
    [Fact]
    public void A_program_in_a_folder_with_a_space_is_one_argument()
        => Assert.Equal(
            ["/opt/My App/bin/app", "%f"],
            Tokens("\"/opt/My App/bin/app\" %f"));

    [Fact]
    public void An_ordinary_line_is_split_the_way_it_always_was()
        => Assert.Equal(["gimp", "-n", "%U"], Tokens("gimp -n %U"));

    /// <summary>Runs of whitespace separate, they do not create empty
    /// arguments.</summary>
    [Fact]
    public void Extra_spaces_do_not_become_empty_arguments()
        => Assert.Equal(["a", "b"], Tokens("  a   b  "));

    /// <summary>
    /// Inside quotes the spec escapes with a backslash, and names four
    /// characters — the ones a shell would otherwise eat.
    /// </summary>
    [Theory]
    [InlineData(@"""a\""b""", "a\"b")]
    [InlineData(@"""a\b""", @"a\b")]
    [InlineData(@"""a\$b""", "a$b")]
    [InlineData(@"""a\`b""", "a`b")]
    public void A_quoted_argument_unescapes_what_the_spec_says_it_may(
        string exec, string expected)
        => Assert.Equal([expected], Tokens(exec));

    /// <summary>A backslash before anything else is just a backslash.</summary>
    [Fact]
    public void A_backslash_before_something_else_stays_a_backslash()
        => Assert.Equal([@"a\nb"], Tokens(@"""a\nb"""));

    /// <summary>
    /// **"%%" is a literal percent**, and it was dropped as an unknown field
    /// code — so a launcher whose Exec passes "50%% done" lost the sign
    /// entirely rather than passing "50% done".
    /// </summary>
    [Fact]
    public void A_doubled_percent_is_one_percent()
        => Assert.Equal(["say", "50% done", "/tmp/x"], Expand("say \"50%% done\" %f", "/tmp/x"));

    /// <summary>
    /// **And it is folded AFTER the field codes are read, not before.** Folded
    /// first, "%%f" becomes "%f" and is then substituted with the filename —
    /// which is the opposite of what writing it twice asks for.
    ///
    /// The path still arrives at the end: this line declares no real field
    /// code, and an entry that names none is handed the file anyway.
    /// </summary>
    [Fact]
    public void A_literal_percent_f_is_not_the_file()
        => Assert.Equal(["show", "%f", "/tmp/x.txt"], Expand("show %%f", "/tmp/x.txt"));

    /// <summary>The tokenizer itself does quoting and nothing else.</summary>
    [Fact]
    public void The_tokenizer_leaves_the_percents_alone()
        => Assert.Equal(["say", "50%% done"], Tokens("say \"50%% done\""));

    /// <summary>An empty quoted argument is still an argument.</summary>
    [Fact]
    public void An_empty_quoted_argument_survives()
        => Assert.Equal(["cmd", ""], Tokens("cmd \"\""));

    /// <summary>A quote that is never closed takes the rest of the line, which
    /// is better than dropping it.</summary>
    [Fact]
    public void An_unclosed_quote_keeps_what_follows_it()
        => Assert.Equal(["cmd", "the rest"], Tokens("cmd \"the rest"));

    // ---- and the field codes on top of it -----------------------------------

    [Fact]
    public void The_file_goes_where_the_field_code_is()
        => Assert.Equal(
            ["/opt/My App/bin/app", "/tmp/report.txt"],
            Expand("\"/opt/My App/bin/app\" %f", "/tmp/report.txt"));

    [Fact]
    public void A_url_field_code_gets_a_url()
        => Assert.Equal(
            ["app", "file:///tmp/a%20b.txt"],
            Expand("app %U", "/tmp/a b.txt"));

    /// <summary>Codes the spec says to drop are dropped.</summary>
    [Fact]
    public void The_codes_that_carry_nothing_are_left_out()
        => Assert.Equal(["app", "/tmp/x"], Expand("app %i %c %k %f", "/tmp/x"));

    /// <summary>An entry with no field code at all still receives the
    /// file.</summary>
    [Fact]
    public void An_entry_that_names_no_field_code_still_gets_the_file()
        => Assert.Equal(["app", "--flag", "/tmp/x"], Expand("app --flag", "/tmp/x"));
}
