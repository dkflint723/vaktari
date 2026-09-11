using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

/// <summary>
/// The three cards of <see cref="Tour"/>, one at a time. Code-behind rather
/// than a view model, like the shortcut sheet: the cards are a constant and
/// the only state is which one is showing.
/// </summary>
public partial class TourWindow : Window
{
    private int _index;

    public TourWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);

        Show(0);
    }

    /// <summary>The card on screen, counted from zero.</summary>
    internal int Index => _index;

    /// <summary>The next card, or the end of the tour from the last one.</summary>
    internal void Next()
    {
        if (_index + 1 >= Tour.Cards.Count)
        {
            Close();
            return;
        }

        Show(_index + 1);
    }

    internal void Back()
    {
        if (_index > 0) Show(_index - 1);
    }

    private void Show(int index)
    {
        _index = index;

        var card = Tour.Cards[index];

        CardTitle.Text = card.Title;
        CardLead.Text = card.Lead;
        CardLines.ItemsSource = card.Lines;

        Progress.Text = $"{index + 1} of {Tour.Cards.Count} — Escape closes this.";

        // Back has nowhere to go from the first card, and Next is the way
        // out from the last: the same button, saying so.
        BackButton.IsEnabled = index > 0;
        NextButton.Content = index + 1 == Tour.Cards.Count ? "Done" : "Next";
    }

    private void OnBack(object? sender, RoutedEventArgs e) => Back();

    private void OnNext(object? sender, RoutedEventArgs e) => Next();

    /// <summary>Escape closes, as the footer says. In code rather than on an
    /// IsCancel button because there is no Close button: Next is the way out.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }
}
