using Avalonia.Controls;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

public partial class ConflictWindow : Window
{
    public ConflictWindow()
    {
        InitializeComponent();
        ClosedWindow.LetGo(this);
        AppIcon.Apply(this);
    }

    public ConflictWindow(ConflictViewModel model) : this()
    {
        DataContext = model;

        model.Closed += (_, _) => Close();

        // **Closing the window is Cancel, not silence.** The operation is
        // waiting on an answer from a background thread; a window dismissed
        // with the X that answered nothing would leave a copy running with no
        // way to finish it. Cancel is also the safe reading of "go away" —
        // nothing is overwritten by a decision that was never made.
        Closing += (_, _) => model.Cancel();
    }
}
