using Avalonia.Controls;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

public partial class ChooseApplicationWindow : Window
{
    public ChooseApplicationWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);

        // **The filter is the whole point of this window**, and it opens on a
        // list of everything installed. Landing the caret anywhere else means
        // reaching for the pointer before typing the name you already knew.
        Opened += (_, _) => FilterBox.Focus();
    }

    public ChooseApplicationWindow(ChooseApplicationViewModel model) : this()
    {
        DataContext = model;

        // Closing is the model's decision, whichever button made it — the same
        // arrangement the conflict prompt uses. Nothing is hooked the other way
        // round: dismissing this window with the X answers "nothing", which is
        // already what Cancel does, so there is no operation left waiting.
        model.Closed += (_, _) => Close();
    }
}
