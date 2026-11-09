namespace NanoFTP.Services;

public static class CrashLogger
{
    public static void Log(Exception? exception, string origin)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            WriteLog(StoragePaths.DataDirectory, origin, exception);
        }
        catch
        {
            try
            {
                // Si le stockage utilisateur est inaccessible, on garde une trace
                // à côté de l'exécutable pour faciliter le diagnostic.
                WriteLog(AppContext.BaseDirectory, origin, exception);
            }
            catch
            {
                // Le journal de diagnostic ne doit jamais provoquer une nouvelle erreur.
            }
        }
    }

    private static void WriteLog(string directory, string origin, Exception exception)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "crash.log");
        var message = $"[{DateTimeOffset.Now:O}] {origin}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
        File.AppendAllText(path, message);
    }
}
