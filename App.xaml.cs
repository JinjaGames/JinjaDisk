using System.Windows;
using System.Windows.Threading;

namespace JinjaDisk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Dialog.Info(Current?.MainWindow, "Beklenmeyen hata", e.Exception.Message);
    }
}
