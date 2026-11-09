using System.Runtime.InteropServices;

namespace NanoFTP.Services;

public static class FatalError
{
    private const uint MessageBoxIconError = 0x00000010;

    public static void Show(Exception exception)
    {
        try
        {
            CrashLogger.Log(exception, "FatalError");
        }
        catch
        {
        }

        var logPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
        var message =
            "NanoFTP n'a pas pu démarrer." + Environment.NewLine + Environment.NewLine +
            exception.Message + Environment.NewLine + Environment.NewLine +
            $"Détails techniques dans : {logPath}";

        if (OperatingSystem.IsWindows())
        {
            MessageBoxW(IntPtr.Zero, message, "NanoFTP", MessageBoxIconError);
        }
        else
        {
            Console.Error.WriteLine(message);
            Console.Error.WriteLine(exception);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
