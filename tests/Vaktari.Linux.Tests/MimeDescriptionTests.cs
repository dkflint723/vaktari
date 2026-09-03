using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// What a file's type is CALLED in the properties window.
///
/// **It printed the mime type itself.** "application/vnd.oasis.opendocument
/// .text" is an identifier for programs, and it was the whole answer to "what
/// is this file" — the one question that row exists for. Dolphin says "ODT
/// document"; Explorer says "OpenDocument Text".
///
/// The description has been sitting in the same database the glob table comes
/// from all along: one XML file per type, beside the globs2 this already reads.
/// </summary>
public sealed class MimeDescriptionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-mime-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly IReadOnlyList<string>? _before = SharedMimeInfo.DescriptionRootsOverride;

    public MimeDescriptionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "image"));
        Directory.CreateDirectory(Path.Combine(_root, "application"));

        SharedMimeInfo.DescriptionRootsOverride = [_root];
    }

    public void Dispose()
    {
        SharedMimeInfo.DescriptionRootsOverride = _before;

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    private void Write(string type, string body)
        => File.WriteAllText(Path.Combine(_root, type + ".xml"), body);

    /// <summary>The whole point: a name a person can read.</summary>
    [Fact]
    public void A_type_is_described_in_words()
    {
        Write("image/png", """
            <?xml version="1.0" encoding="UTF-8"?>
            <mime-type xmlns="http://www.freedesktop.org/standards/shared-mime-info"
                       type="image/png">
              <comment>PNG image</comment>
            </mime-type>
            """);

        Assert.Equal("PNG image", SharedMimeInfo.Describe("image/png"));
    }

    /// <summary>
    /// **The untranslated one, never whichever came first.** These files carry
    /// a comment per locale — dozens of them, all named "comment" and separated
    /// only by an xml:lang attribute — and the translations are listed BEFORE
    /// nothing in particular, so taking the first match hands back whichever
    /// language happens to sort first in that file. A properties window in
    /// Bulgarian, on an English machine, because of element order.
    /// </summary>
    [Fact]
    public void And_in_the_language_the_rest_of_the_window_is_in()
    {
        Write("image/jpeg", """
            <?xml version="1.0" encoding="UTF-8"?>
            <mime-type xmlns="http://www.freedesktop.org/standards/shared-mime-info"
                       type="image/jpeg">
              <comment xml:lang="bg">Изображение — JPEG</comment>
              <comment xml:lang="de">JPEG-Bild</comment>
              <comment>JPEG image</comment>
              <comment xml:lang="fr">image JPEG</comment>
            </mime-type>
            """);

        Assert.Equal("JPEG image", SharedMimeInfo.Describe("image/jpeg"));
    }

    /// <summary>
    /// **Falling back to the type is worse than a description and far better
    /// than nothing.** A machine with no shared-mime-info installed, or a type
    /// too new for the copy it has, still says something true — which is
    /// exactly what this row said before, so the fix cannot make any case
    /// worse than it already was.
    /// </summary>
    [Fact]
    public void A_type_nobody_has_described_is_still_named()
        => Assert.Equal("application/x-invented", SharedMimeInfo.Describe("application/x-invented"));

    /// <summary>And nothing in gives nothing back, rather than a stray label.</summary>
    [Fact]
    public void Nothing_is_described_as_nothing()
        => Assert.Equal("", SharedMimeInfo.Describe(""));

    /// <summary>
    /// A description file that is not valid XML is one fewer description, not a
    /// properties window that will not open. These files come off the machine's
    /// own disk and can be truncated by a failed package install.
    /// </summary>
    [Fact]
    public void A_broken_description_file_falls_back_rather_than_throwing()
    {
        Write("application/zip", "<mime-type><comment>Zip archi");

        Assert.Equal("application/zip", SharedMimeInfo.Describe("application/zip"));
    }

    /// <summary>
    /// A type with no comment at all — the element is optional in the spec —
    /// falls back the same way.
    /// </summary>
    [Fact]
    public void So_does_one_with_no_comment_in_it()
    {
        Write("application/x-quiet", """
            <?xml version="1.0" encoding="UTF-8"?>
            <mime-type type="application/x-quiet"><glob pattern="*.quiet"/></mime-type>
            """);

        Assert.Equal("application/x-quiet", SharedMimeInfo.Describe("application/x-quiet"));
    }

    /// <summary>
    /// **A description is asked for once.** The properties window opens on one
    /// file, but the same lookup is a candidate for the type column, which asks
    /// per row — and this reads a file off disk to answer.
    /// </summary>
    [Fact]
    public void The_answer_is_remembered()
    {
        Write("image/gif", """
            <?xml version="1.0" encoding="UTF-8"?>
            <mime-type type="image/gif"><comment>GIF image</comment></mime-type>
            """);

        Assert.Equal("GIF image", SharedMimeInfo.Describe("image/gif"));

        File.Delete(Path.Combine(_root, "image", "gif.xml"));

        Assert.Equal("GIF image", SharedMimeInfo.Describe("image/gif"));
    }

    /// <summary>
    /// **Lending the description roots must not move the GLOB database**, which
    /// is what this seam did when it was first written — and it broke CI.
    ///
    /// The database is a Lazy: loaded once per process and never again. Point it
    /// at an empty directory in one test and every later test in the assembly
    /// sees a machine with no mime types at all, which is not a failure anybody
    /// can trace back to here.
    ///
    /// Posix, because it asks the desktop's own database for an answer — the
    /// very thing that would go missing.
    /// </summary>
    [PosixFact]
    public void Lending_the_descriptions_leaves_the_type_lookup_alone()
    {
        // The override is already set by the constructor, to a temp directory
        // holding no globs2 whatsoever.
        Assert.NotNull(SharedMimeInfo.DescriptionRootsOverride);

        Assert.Equal("text/plain", SharedMimeInfo.ForPath("/tmp/notes.txt"));
    }

    /// <summary>
    /// The roots are the database's own, in its own precedence: a type
    /// described locally overrides the system's wording for it. A lookup that
    /// consulted a different set of roots than the one that decided the TYPE
    /// could describe a type this machine does not have.
    /// </summary>
    [Fact]
    public void A_local_description_wins_over_the_system_one()
    {
        var system = Path.Combine(_root, "system");
        var local = Path.Combine(_root, "local");

        Directory.CreateDirectory(Path.Combine(system, "text"));
        Directory.CreateDirectory(Path.Combine(local, "text"));

        File.WriteAllText(Path.Combine(system, "text/x-note.xml"),
                          "<mime-type><comment>Note</comment></mime-type>");
        File.WriteAllText(Path.Combine(local, "text/x-note.xml"),
                          "<mime-type><comment>Lab notebook</comment></mime-type>");

        SharedMimeInfo.DescriptionRootsOverride = [system, local];

        Assert.Equal("Lab notebook", SharedMimeInfo.Describe("text/x-note"));
    }
}
