using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// The box Ctrl+Shift+P opens: type part of a command's name, Enter runs
/// the highlighted one. The window that opened it reads <see cref="Chosen"/>
/// once this has closed and runs it THEN — a command that moves the
/// keyboard, into the address bar or a rename, has to find its own window
/// in front rather than this one.
/// </summary>
public partial class PaletteWindow : Window
{
    public PaletteWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);

        Refresh("");

        Query.TextChanged += (_, _) => Refresh(Query.Text ?? "");
        Matches.DoubleTapped += (_, _) => Pick();

        // Tunnelled, so the keys are answered before the box sees them: the
        // box would otherwise keep Up and Down for a caret that has nowhere
        // to go, and the list is what those keys are for here.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);

        Opened += (_, _) => Query.Focus();
    }

    /// <summary>What Enter or a double-click picked, or null when the box
    /// was closed without picking anything.</summary>
    public PaletteEntry? Chosen { get; private set; }

    /// <summary>Re-lists for a query and highlights the first match, so Enter
    /// straight after typing runs the best one.</summary>
    internal void Refresh(string query)
    {
        Matches.ItemsSource = Palette.Match(query);
        Matches.SelectedIndex = Matches.ItemCount > 0 ? 0 : -1;
    }

    /// <summary>Takes the highlighted entry, if any, and closes.</summary>
    internal void Pick()
    {
        Chosen = Matches.SelectedItem as PaletteEntry;
        Close();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Pick();
                return;

            case Key.Escape:
                e.Handled = true;
                Chosen = null;
                Close();
                return;

            case Key.Down:
                e.Handled = true;
                Move(+1);
                return;

            case Key.Up:
                e.Handled = true;
                Move(-1);
                return;
        }
    }

    private void Move(int by)
    {
        if (Matches.ItemCount == 0) return;

        Matches.SelectedIndex = Math.Clamp(Matches.SelectedIndex + by, 0, Matches.ItemCount - 1);
        Matches.ScrollIntoView(Matches.SelectedIndex);
    }
}
