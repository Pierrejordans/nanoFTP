using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NanoFTP.Services;
using NanoFTP.Views;

namespace NanoFTP;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            CrashLogger.Log(eventArgs.Exception, "UIThread");

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow: MainWindow mainWindow
                })
            {
                mainWindow.ReportUnexpectedError(eventArgs.Exception);
            }

            eventArgs.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                desktop.MainWindow = new MainWindow();
            }
            catch (Exception exception)
            {
                CrashLogger.Log(exception, "Initialisation de la fenêtre");
                FatalError.Show(exception);
                desktop.Shutdown(1);
                return;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
