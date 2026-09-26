using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Vaktari.Core.Diagnostics;
using Vaktari.Core.Tests;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// A drag source that does not always answer — which is what a drag out of
/// Explorer is, from the other side of a process boundary.
///
/// **One failed question used to be the whole drag.** Every drag-over asks
/// the source whether it carries an archive's files, and a busy Explorer can
/// answer that with a failure that says nothing about the files. That failure
/// read as "no", the cursor said None, and when it was the last answer before
/// the release Windows took the drag away instead of dropping it — "sometimes
/// it works, sometimes it fails". These hold the reader to asking twice, to
/// telling a failure from a no, and to saying what it refused where the
/// windowed build can be read: the log.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UnreliableDropSourceTests
{
    [WindowsFact]
    public void A_busy_moment_is_asked_again()
    {
        var source = new NativeDropSource(("a.txt", "x"u8.ToArray(), false)) { FailQueries = 1 };

        Serve(source, drag =>
        {
            Assert.True(new VirtualFileDrop().Offers(drag, out var failure),
                "one refused QueryGetData was taken for a drag with no archive files in it");
            Assert.Null(failure);
        });
    }

    /// <summary>
    /// **Only a busy answer is asked again.** E_NOTIMPL is the source's
    /// considered answer, not a moment of it being busy; asking it twice on
    /// every drag-over would only double the calls into the other process.
    /// It is still a failure with a reason.
    /// </summary>
    [WindowsFact]
    public void An_answer_that_is_not_busy_is_not_asked_again()
    {
        var source = new NativeDropSource(("a.txt", "x"u8.ToArray(), false))
        {
            FailQueries = 1,
            QueryFailure = unchecked((int)0x80004001),
        };

        Serve(source, drag =>
        {
            Assert.False(new VirtualFileDrop().Offers(drag, out var failure),
                "E_NOTIMPL was asked again as though the source had been busy");
            Assert.NotNull(failure);
        });
    }

    /// <summary>
    /// **A descriptor is not an archive.** Explorer's data object for an
    /// ordinary file on disk offers FileGroupDescriptorW and FileContents
    /// beside CF_HDROP; a drag with paths is a drag of those paths.
    /// </summary>
    [WindowsFact]
    public void A_source_with_paths_offers_no_archive_files()
    {
        var source = new NativeDropSource(("a.txt", "x"u8.ToArray(), false)) { OffersPaths = true };

        Serve(source, drag =>
        {
            Assert.False(new VirtualFileDrop().Offers(drag, out var failure));
            Assert.Null(failure);
        });
    }

    /// <summary>
    /// **The shell's optimized-move rule has two halves, and the data object
    /// is the second.** The target that moved the files itself sets
    /// CFSTR_PERFORMEDDROPEFFECT to DROPEFFECT_NONE — so a source that reads
    /// the format rather than the drop's effect still leaves its originals —
    /// and CFSTR_LOGICALPERFORMEDDROPEFFECT to DROPEFFECT_MOVE.
    /// </summary>
    [WindowsFact]
    public void A_move_by_the_target_is_written_on_the_data_object()
    {
        var source = new NativeDropSource(("a.txt", "x"u8.ToArray(), false));

        Serve(source, drag => Assert.True(new VirtualFileDrop().MovedByTarget(drag)));

        Assert.Equal(0, source.WasSet["Performed DropEffect"]);
        Assert.Equal(2, source.WasSet["Logical Performed DropEffect"]);
    }

    /// <summary>A source that takes no such formats is simply not told —
    /// no throw, and the memory handed to it freed here.</summary>
    [WindowsFact]
    public void A_source_that_refuses_the_formats_is_left_alone()
    {
        var source = new NativeDropSource(("a.txt", "x"u8.ToArray(), false)) { RefuseSetData = true };

        Serve(source, drag => Assert.False(new VirtualFileDrop().MovedByTarget(drag)));

        Assert.Empty(source.WasSet);
    }

    /// <summary>
    /// A source that fails every time is still a no — but one with a reason,
    /// the HRESULT, so the window can say why the drag was refused.
    /// </summary>
    [WindowsFact]
    public void A_source_that_keeps_failing_says_why()
    {
        var source = new NativeDropSource(("a.txt", "x"u8.ToArray(), false))
        {
            FailQueries = 100,
            QueryFailure = NativeDropSource.EFail,
        };

        Serve(source, drag =>
        {
            Assert.False(new VirtualFileDrop().Offers(drag, out var failure));
            Assert.True(failure is not null && failure.Contains("0x80004005", StringComparison.Ordinal),
                $"a failing source was reported as an honest no: '{failure}'");
        });
    }

    /// <summary>
    /// **And an honest no is not a failure.** DV_E_FORMATETC is how a data
    /// object says it has no such format — every ordinary drag of text says
    /// it — and a reason there would put a line in the log for every drag of
    /// anything that is not an archive.
    /// </summary>
    [WindowsFact]
    public void An_honest_no_is_not_a_failure()
    {
        var source = new NativeDropSource(("a.txt", "x"u8.ToArray(), false))
        {
            FailQueries = 100,
            QueryFailure = NativeDropSource.DvEFormatEtc,
        };

        Serve(source, drag =>
        {
            Assert.False(new VirtualFileDrop().Offers(drag, out var failure));
            Assert.Null(failure);
        });
    }

    /// <summary>
    /// **Never throws, whatever it is handed.** An exception out of the drag
    /// handler is swallowed by Avalonia's COM layer as E_FAIL, and a failed
    /// DragEnter makes Windows stop offering the window the drag at all. Only
    /// four kinds of exception were caught; a null is none of them.
    /// </summary>
    [WindowsFact]
    public void Something_that_throws_is_a_failure_and_not_a_throw()
    {
        Assert.False(new VirtualFileDrop().Offers(null!, out var failure));
        Assert.NotNull(failure);
    }

    /// <summary>
    /// **What the source refused goes to the log.** These lines went to stderr
    /// alone, and the shipped build is a windowed process with none — so the
    /// one account of why an archive gave up nothing was written nowhere.
    /// </summary>
    [WindowsFact]
    public void A_refused_entry_is_written_to_the_log()
    {
        var logs = Path.Combine(Path.GetTempPath(), "vaktari-unreliable-drop-" + Guid.NewGuid().ToString("N")[..8]);
        var name = "refused-" + Guid.NewGuid().ToString("N")[..8] + ".txt";

        var source = new NativeDropSource((name, "x"u8.ToArray(), false)) { RefuseContents = true };

        Log.Configure(logs, includePaths: false);

        try
        {
            Serve(source, drag => Assert.Empty(new VirtualFileDrop().Take(drag)));

            var lines = File.ReadAllLines(Path.Combine(logs, Log.FileName));

            Assert.Contains(lines, l => l.Contains(" warn ", StringComparison.Ordinal)
                                        && l.Contains($"'{name}' refused", StringComparison.Ordinal));
        }
        finally
        {
            Log.Reset();

            try { Directory.Delete(logs, recursive: true); }
            catch (IOException) { /* a temp dir is not worth failing over */ }
        }
    }

    /// <summary>The source behind a native pointer, handed over the way
    /// Avalonia hands a drag over.</summary>
    private static void Serve(NativeDropSource source, Action<object> use)
    {
        var unknown = new StrategyBasedComWrappers()
            .GetOrCreateComInterfaceForObject(source, CreateComInterfaceFlags.None);

        try
        {
            use(new VirtualFileDropTests.AvaloniaShapedWrapper(new VirtualFileDropTests.MicroComShapedProxy(unknown)));
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
