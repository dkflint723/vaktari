using System.Reflection;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A canary on a private shape inside Avalonia.
///
/// Dragging files out of 7-Zip, or out of Explorer's own zip view, carries no
/// paths at all: the contents of an archive are retrieved one item at a time,
/// by index, as a stream. Avalonia's public drop surface offers formats and
/// bytes and cannot express that, so Vaktari.Windows reaches the underlying COM
/// data object through a private field on Avalonia's Windows wrapper.
///
/// That reach is guarded, and losing it costs the feature and nothing else. But
/// a feature that quietly stops working is exactly what this project keeps
/// trying not to ship — so the shape is asserted here, and the day Avalonia
/// changes it the build says so rather than somebody's drag going silent.
///
/// In this assembly rather than beside the code it guards, because this is the
/// one with Avalonia in it.
/// </summary>
public sealed class AvaloniaDropShapeTests
{
    [WindowsFact]
    public void Avalonias_windows_wrapper_still_holds_the_data_object()
    {
        // By assembly name: everything in Avalonia.Win32 that matters here is
        // internal, including the platform type that would otherwise be the
        // obvious handle on it.
        var win32 = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Avalonia.Win32")
            ?? Assembly.Load("Avalonia.Win32");

        var wrapper = win32.GetType("Avalonia.Win32.OleDataObjectToDataTransferWrapper");

        Assert.True(wrapper is not null,
            "Avalonia no longer has OleDataObjectToDataTransferWrapper — dragging out of "
            + "an archive falls back to explaining itself, and VirtualFileDrop needs revisiting.");

        var field = wrapper!.GetField(
            "_oleDataObject", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.True(field is not null,
            "the wrapper no longer holds _oleDataObject — see the note on VirtualFileDrop.Native.");

        // **Deliberately not asserting the framework's ComTypes.IDataObject.**
        // Avalonia holds its OWN declaration of the interface, and this test
        // caught exactly that: a cast between the two returns null in silence,
        // so the first version of VirtualFileDrop would never have fired. What
        // matters is that the field still names a COM interface describing a
        // data object, which the pointer can then be asked for by IID.
        Assert.True(field!.FieldType.IsInterface,
            $"_oleDataObject is now {field.FieldType}, which is not an interface to "
            + "query — see the note on VirtualFileDrop.Retype.");

        Assert.Contains("IDataObject", field.FieldType.Name, StringComparison.Ordinal);

        // **And the pointer is read out of the proxy, not asked for.** What
        // the field holds for a drag from another program is a MicroCom proxy
        // — a plain managed object, so neither ComWrappers nor
        // Marshal.GetIUnknownForObject leads back to the native object. Its
        // NativePointer does, and that is the second name VirtualFileDrop
        // depends on.
        var proxyBase = Type.GetType("MicroCom.Runtime.MicroComProxyBase, MicroCom.Runtime");

        Assert.True(proxyBase is not null,
            "MicroCom.Runtime.MicroComProxyBase is gone — Avalonia's COM layer changed, and "
            + "VirtualFileDrop.Retype no longer finds the pointer behind a drag.");

        Assert.True(field.FieldType.GetInterfaces().Any(i => i.FullName == "MicroCom.Runtime.IUnknown"),
            "the data object is no longer a MicroCom interface — see VirtualFileDrop.Retype.");

        var pointer = proxyBase!.GetProperty("NativePointer", BindingFlags.Instance | BindingFlags.Public);

        Assert.True(pointer?.PropertyType == typeof(IntPtr),
            "MicroComProxyBase no longer has a public IntPtr NativePointer — see VirtualFileDrop.Retype.");
    }
}
