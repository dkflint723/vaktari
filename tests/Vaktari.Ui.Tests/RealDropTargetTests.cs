using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A file dragged out of a zip, through the drop target Avalonia itself
/// registers with Windows, onto a real (headless) MainWindow.
///
/// **Everything else in the drop tests stops short of Avalonia's Windows
/// backend.** The window's handlers are raised by hand, and the archive reader
/// is handed a wrapper shaped like Avalonia's rather than Avalonia's own. So
/// nothing checked the three things that decide a real drop: what
/// OleDropTarget and DragDropDevice make of the answers the handlers give,
/// that the data object Avalonia wraps is still readable while the drop
/// handler runs, and which effect Avalonia hands back to the source.
///
/// **This drives the real one.** Avalonia.Win32's OleDropTarget is built for
/// the headless window — its own ITopLevelImpl, its own input root, the
/// shared DragDropDevice — and called through its native IDropTarget vtable,
/// which is exactly how OLE's drag loop calls it. What it is handed is the
/// data object Explorer's zip view makes (zipfldr, asked through
/// BHID_DataObject for an item inside the zip), made on an STA thread of its
/// own and marshalled to the window's thread, so every question the drop asks
/// crosses an apartment through the standard proxy, as a real drag's crosses
/// a process.
///
/// What it cannot stage is OLE's drag loop itself — which of these calls
/// Windows makes, and when; that part is read from the loop and written down
/// in MainWindow.DragDrop.cs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealDropTargetTests : OwnedViewModels
{
    private readonly List<string> _made = [];

    public override void Dispose()
    {
        foreach (var dir in _made)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }

        base.Dispose();
    }

    /// <summary>
    /// The maintainer's gesture, end to end: enter, move, drop. The cursor says
    /// Copy on the way — the zip offers no paths, so that yes can only have come
    /// from the archive reader through Avalonia's own wrapper — the drop
    /// reports Copy back to the source (it used to report Copy and Move, which
    /// is what Explorer allowed), and the file's bytes arrive.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_file_dragged_out_of_a_zip_through_Avalonia_s_own_drop_target_lands_as_a_copy()
    {
        var bytes = Enumerable.Range(0, 150_000).Select(i => (byte)(i * 17 + 3)).ToArray();

        var zip = Path.Combine(Folder("zip"), "archive.zip");

        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("top.bin").Open())
            entry.Write(bytes);

        var into = Folder("into");

        using var made = new ManualResetEventSlim();
        using var done = new ManualResetEvent(false);

        var carried = IntPtr.Zero;
        Exception? making = null;

        // The zip view's object lives on an STA thread of its own, as the
        // shell's objects do, and waits there — a managed wait on an STA
        // pumps, which is what lets the calls from the window's thread land.
        var maker = new Thread(() =>
        {
            var data = IntPtr.Zero;

            try
            {
                data = ZipViewDataObject(zip + @"\top.bin");

                Check(CoMarshalInterThreadInterfaceInStream(ref DataObjectId, data, out carried),
                    "marshalling the zip view's data object");
            }
            catch (Exception e)
            {
                making = e;
            }
            finally
            {
                made.Set();
            }

            try
            {
                done.WaitOne(TimeSpan.FromSeconds(60));
            }
            finally
            {
                if (data != IntPtr.Zero) Marshal.Release(data);
            }
        });

        maker.SetApartmentState(ApartmentState.STA);
        maker.Start();

        try
        {
            Assert.True(made.Wait(TimeSpan.FromSeconds(60)), "the zip view's data object was never made");

            if (making is not null) throw new InvalidOperationException("making the zip view's data object failed", making);

            // The window's thread takes part in COM as a multithreaded
            // apartment unless something already said otherwise; either way
            // the object arrives as a proxy into the maker's apartment.
            _ = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);

            Check(CoGetInterfaceAndReleaseStream(carried, ref DataObjectId, out var proxy), "unmarshalling it");

            UseSearch(PaneViewModel.Search);

            var window = new MainWindow { Width = 1200, Height = 1000 };
            var target = IntPtr.Zero;
            IDisposable? oleTarget = null;

            try
            {
                window.Show();
                Pump();

                var pane = Own((ShellViewModel)window.DataContext!).ActiveTab!;

                await pane.NavigateAsync(into);
                Pump();

                Assert.Equal(into, pane.CurrentPath);

                (oleTarget, target) = AvaloniaDropTarget(window);

                var listing = window.GetVisualDescendants().OfType<ListBox>()
                    .First(l => ReferenceEquals(l.DataContext, pane) && l.IsEffectivelyVisible);

                var centre = listing.TranslatePoint(new Point(listing.Bounds.Width / 2, listing.Bounds.Height / 2), window)!.Value;
                var screen = window.PointToScreen(centre);
                var at = new Pointl { X = screen.X, Y = screen.Y };

                const int allowed = DropEffectCopy | DropEffectMove;

                var effect = allowed;
                Check(Slot<EnterOrDrop>(target, 3)(target, proxy, MkLButton, at, ref effect), "DragEnter");
                Assert.Equal(DropEffectCopy, effect);

                effect = allowed;
                Check(Slot<Over>(target, 4)(target, MkLButton, at, ref effect), "DragOver");
                Assert.Equal(DropEffectCopy, effect);

                effect = allowed;
                Check(Slot<EnterOrDrop>(target, 6)(target, proxy, 0, at, ref effect), "Drop");

                Assert.True(effect == DropEffectCopy,
                    $"the drop reported 0x{effect:X} back to the source; a zip's files are copied, and a Move is "
                    + "the source's cue to delete them");

                var landed = Path.Combine(into, "top.bin");

                for (var i = 0; i < 500 && !File.Exists(landed); i++)
                {
                    await Task.Delay(10);
                    Pump();
                }

                Assert.True(File.Exists(landed), $"nothing arrived; the pane says '{pane.Status}'");

                for (var i = 0; i < 30; i++)
                {
                    await Task.Delay(10);
                    Pump();
                }

                Assert.Equal(bytes, File.ReadAllBytes(landed));
            }
            finally
            {
                if (target != IntPtr.Zero) Marshal.Release(target);
                oleTarget?.Dispose();
                Marshal.Release(proxy);
                window.Close();
            }
        }
        finally
        {
            done.Set();
            maker.Join();
        }
    }

    // ---- Avalonia's side --------------------------------------------------------

    /// <summary>
    /// Avalonia.Win32's OleDropTarget for this window, and its native
    /// IDropTarget — what WindowImpl hands RegisterDragDrop on a real
    /// desktop, built from the headless window's own pieces. Reached by name,
    /// because every type on the way is internal; a rename is a failing test
    /// here and a message that says which.
    /// </summary>
    private static (IDisposable Target, IntPtr Native) AvaloniaDropTarget(MainWindow window)
    {
        var win32 = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Avalonia.Win32.dll"));

        var type = win32.GetType("Avalonia.Win32.OleDropTarget")
                   ?? throw new InvalidOperationException("Avalonia.Win32.OleDropTarget is gone");
        var face = win32.GetType("Avalonia.Win32.Win32Com.IDropTarget")
                   ?? throw new InvalidOperationException("Avalonia.Win32.Win32Com.IDropTarget is gone");

        var root = typeof(TopLevel).GetProperty("InputRoot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                       ?.GetValue(window)
                   ?? throw new InvalidOperationException("TopLevel.InputRoot is gone");

        var device = typeof(DragDrop).Assembly.GetType("Avalonia.Input.DragDropDevice")
                         ?.GetField("Instance", BindingFlags.Static | BindingFlags.Public)
                         ?.GetValue(null)
                     ?? throw new InvalidOperationException("Avalonia.Input.DragDropDevice.Instance is gone");

        var target = Activator.CreateInstance(
            type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [window.PlatformImpl, root, device], null)!;

        var runtime = face.GetInterfaces().First(i => i.FullName == "MicroCom.Runtime.IUnknown").Assembly
                          .GetType("MicroCom.Runtime.MicroComRuntime")!;

        var native = (IntPtr)runtime.GetMethods()
            .First(m => m.Name == "GetNativeIntPtr" && m.IsGenericMethodDefinition)
            .MakeGenericMethod(face)
            .Invoke(null, [target, true])!;

        return ((IDisposable)target, native);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnterOrDrop(IntPtr self, IntPtr data, int keys, Pointl at, ref int effect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Over(IntPtr self, int keys, Pointl at, ref int effect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int BindToHandler(IntPtr self, IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);

    /// <summary>A method of a native object, by its place in the vtable.</summary>
    private static T Slot<T>(IntPtr native, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(native), slot * IntPtr.Size));

    [StructLayout(LayoutKind.Sequential)]
    private struct Pointl
    {
        public int X;
        public int Y;
    }

    // ---- the shell's side -------------------------------------------------------

    /// <summary>
    /// The data object Explorer's zip view makes for one item inside a zip: the
    /// item parsed through the zip folder, and asked for BHID_DataObject —
    /// which is the folder's GetUIObjectOf, the call its view makes when a drag
    /// starts. The caller owns the reference.
    /// </summary>
    private static IntPtr ZipViewDataObject(string inside)
    {
        Check(SHCreateItemFromParsingName(inside, IntPtr.Zero, ref ShellItemId, out var item),
            "parsing a path inside the zip");

        try
        {
            Check(Slot<BindToHandler>(item, 3)(item, IntPtr.Zero, ref DataObjectHandler, ref DataObjectId, out var data),
                "asking the zip's item for its data object");

            return data;
        }
        finally
        {
            Marshal.Release(item);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) Assert.Fail($"{what} failed: 0x{hr:X8}");
    }

    private string Folder(string what)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vaktari-real-drop-{what}-{Guid.NewGuid():N}"[..40]);

        Directory.CreateDirectory(dir);
        _made.Add(dir);

        return dir;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private const int MkLButton = 0x1;
    private const int DropEffectCopy = 0x1;
    private const int DropEffectMove = 0x2;
    private const uint CoinitMultithreaded = 0x0;

    private static Guid DataObjectId = new("0000010e-0000-0000-C000-000000000046");
    private static Guid ShellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static Guid DataObjectHandler = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IntPtr item);

    [DllImport("ole32.dll")]
    private static extern int CoMarshalInterThreadInterfaceInStream(ref Guid iid, IntPtr unknown, out IntPtr stream);

    [DllImport("ole32.dll")]
    private static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, ref Guid iid, out IntPtr unknown);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint model);
}
