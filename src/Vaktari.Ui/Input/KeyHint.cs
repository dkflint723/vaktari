using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Vaktari.Ui.Input;

/// <summary>
/// A command's keys, printed where a control mentions them — and printed
/// again when the keys change.
///
/// **Thirty places in the window's markup spelled a key out**: a menu row's
/// gesture, "Back  (alt+left)" on a button, the Ctrl+F chip in the search
/// box. Each was a literal, so the day a key can be changed every one of them
/// becomes a promise about a key that no longer does anything. The markup now
/// names the COMMAND and says which words the key belongs after; this writes
/// the rest from the keymap in force.
///
/// What it writes depends on what is set:
///   * <c>Command</c> on a MenuItem — the row's gesture, the command's first key;
///   * <c>Tip</c>, <c>Name</c>, <c>Content</c> — those words with the keys
///     after them, into the tooltip, the name a screen reader says, or the
///     content;
///   * <c>Command</c> alone on a TextBlock — the first key as the sheet
///     prints it, for a chip that is a key and nothing else.
///
/// An attached property like PaneScale and SidebarIcon rather than a binding,
/// because there is nothing in a view model to bind to: the keymap is one per
/// application, and the rows it labels live inside templates.
/// </summary>
public static class KeyHint
{
    public static readonly AttachedProperty<string?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Command", typeof(KeyHint));

    public static readonly AttachedProperty<string?> TipProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Tip", typeof(KeyHint));

    public static readonly AttachedProperty<string?> NameProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Name", typeof(KeyHint));

    public static readonly AttachedProperty<string?> ContentProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Content", typeof(KeyHint));

    public static void SetCommand(Control control, string? value) => control.SetValue(CommandProperty, value);
    public static string? GetCommand(Control control) => control.GetValue(CommandProperty);
    public static void SetTip(Control control, string? value) => control.SetValue(TipProperty, value);
    public static string? GetTip(Control control) => control.GetValue(TipProperty);
    public static void SetName(Control control, string? value) => control.SetValue(NameProperty, value);
    public static string? GetName(Control control) => control.GetValue(NameProperty);
    public static void SetContent(Control control, string? value) => control.SetValue(ContentProperty, value);
    public static string? GetContent(Control control) => control.GetValue(ContentProperty);

    /// <summary>
    /// The keymap handler each hinted control holds while it is on screen,
    /// so it comes off the same one it went on. Per control, and only while
    /// attached: a menu's rows are built when it opens and dropped when it
    /// closes, and a static event holding a row that has gone would keep it —
    /// and its whole window — alive for the life of the process.
    /// </summary>
    private static readonly ConditionalWeakTable<Control, Hooks> Hooked = new();

    private sealed class Hooks
    {
        public EventHandler? OnKeymapChanged;
        public EventHandler<VisualTreeAttachmentEventArgs>? OnAttached;
        public EventHandler<VisualTreeAttachmentEventArgs>? OnDetached;
    }

    static KeyHint()
    {
        CommandProperty.Changed.AddClassHandler<Control>((control, _) => Track(control));
        TipProperty.Changed.AddClassHandler<Control>((control, _) => Track(control));
        NameProperty.Changed.AddClassHandler<Control>((control, _) => Track(control));
        ContentProperty.Changed.AddClassHandler<Control>((control, _) => Track(control));
    }

    private static void Track(Control control)
    {
        Refresh(control);

        if (Hooked.TryGetValue(control, out _)) return;

        var hooks = new Hooks();

        hooks.OnKeymapChanged = (_, _) => Refresh(control);
        hooks.OnAttached = (_, _) =>
        {
            Keymap.Changed += hooks.OnKeymapChanged;

            // The keys may have moved while it was off screen.
            Refresh(control);
        };
        hooks.OnDetached = (_, _) => Keymap.Changed -= hooks.OnKeymapChanged;

        control.AttachedToVisualTree += hooks.OnAttached;
        control.DetachedFromVisualTree += hooks.OnDetached;

        // Already on screen: the first attach has been and gone.
        if (TopLevel.GetTopLevel(control) is not null) Keymap.Changed += hooks.OnKeymapChanged;

        Hooked.Add(control, hooks);
    }

    /// <summary>Writes the control's hint from the keymap in force.</summary>
    internal static void Refresh(Control control)
    {
        if (GetCommand(control) is not { Length: > 0 } id) return;

        var keymap = Keymap.Current;
        var keys = keymap.KeysOf(id);

        if (control is MenuItem row) row.InputGesture = keys.Count > 0 ? keys[0] : null;

        var tip = GetTip(control);
        var name = GetName(control);
        var content = GetContent(control);

        if (tip is not null) ToolTip.SetTip(control, keymap.Labelled(tip, id));
        if (name is not null) AutomationProperties.SetName(control, keymap.Labelled(name, id));
        if (content is not null && control is ContentControl holder) holder.Content = keymap.Labelled(content, id);

        if (control is TextBlock chip && tip is null && name is null)
            chip.Text = keys.Count > 0 ? KeyChords.Readable(keys[0]) : "";
    }
}
