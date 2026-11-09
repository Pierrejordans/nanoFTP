using System.Runtime.InteropServices;
using Avalonia;
using NanoFTP.Services;

namespace NanoFTP;

internal static class Program
{
#if DEBUG
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            CrashLogger.Log(eventArgs.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            CrashLogger.Log(eventArgs.Exception, "TaskScheduler");
            eventArgs.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            FatalError.Show(exception);
            Environment.ExitCode = 1;
        }
    }
#else
    [STAThread]
    public static void Main(string[] args)
    {
        const string message =
            "NanoFTP est réservé au mode Debug.\n\n" +
            "Windows : double-cliquez NanoFTP-debug.bat\n" +
            "macOS : double-cliquez NanoFTP-debug.command\n" +
            "Linux : exécutez ./NanoFTP-debug.sh\n\n" +
            "Ou : dotnet run -c Debug --project src/NanoFTP/NanoFTP.csproj";

        if (OperatingSystem.IsWindows())
        {
            MessageBoxW(IntPtr.Zero, message, "NanoFTP — Debug uniquement", 0x00000040);
        }
        else
        {
            Console.Error.WriteLine(message);
        }

        Environment.ExitCode = 1;
    }
#endif

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
