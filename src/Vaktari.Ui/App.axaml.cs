using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Vaktari.Ui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // **A window opened from another one is a PEER, not a child**, so
            // closing the one that happens to have started the application must
            // not take the others with it — which is what OnMainWindowClose
            // would do. The founder still fills MainWindow, because that is
            // what the desktop lifetime treats as the application's own window,
            // and every dialog is opened from the window that asked for it, so
            // a closed founder owns nothing.
            //
            // Stated rather than inherited: with one window the two modes are
            // indistinguishable, so this was never a decision before and is one
            // now. Directory.Build.props puts it the same way — "A framework
            // default is not a decision".
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;

            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
