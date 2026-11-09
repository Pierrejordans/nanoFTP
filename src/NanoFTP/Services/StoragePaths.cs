namespace NanoFTP.Services;

/// <summary>
/// En mode Debug, les données sont conservées dans un dossier local au projet.
/// Cela évite les restrictions Windows sur LocalApplicationData et garde un
/// environnement de développement autonome et reproductible.
/// </summary>
public static class StoragePaths
{
    private const string DataFolderName = ".nanoftp-data";

    private static readonly Lazy<string> ResolvedDirectory = new(
        ResolveDataDirectory,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string DataDirectory => ResolvedDirectory.Value;

    public static string DownloadsDirectory => Path.Combine(FindProjectRoot(), "NanoFTP-downloads");

    private static string ResolveDataDirectory()
    {
        var explicitDirectory = Environment.GetEnvironmentVariable("NANOFTP_DATA_DIR");
        if (string.IsNullOrWhiteSpace(explicitDirectory))
        {
            explicitDirectory = Environment.GetEnvironmentVariable("FOIREZILLA_DATA_DIR");
        }

        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            var configuredPath = Path.GetFullPath(explicitDirectory);
            Directory.CreateDirectory(configuredPath);
            return configuredPath;
        }

        var root = FindProjectRoot();
        var dataDirectory = Path.Combine(root, DataFolderName);
        Directory.CreateDirectory(dataDirectory);
        MigratePreviousData(dataDirectory);
        return dataDirectory;
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NanoFTP.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static void MigratePreviousData(string targetDirectory)
    {
        // Le dossier courant est prioritaire : ne jamais écraser des données existantes.
        if (File.Exists(Path.Combine(targetDirectory, "connections.json")) ||
            File.Exists(Path.Combine(targetDirectory, "settings.json")))
        {
            return;
        }

        var candidates = GetMigrationSources()
            .Where(Directory.Exists)
            .Select(directory => new
            {
                Directory = directory,
                LastWrite = GetLastWrite(directory)
            })
            .Where(candidate => candidate.LastWrite is not null)
            .OrderByDescending(candidate => candidate.LastWrite)
            .ToList();

        var source = candidates.FirstOrDefault()?.Directory;
        if (source is null)
        {
            return;
        }

        foreach (var fileName in new[] { "connections.json", "secrets.key", "settings.json" })
        {
            try
            {
                var sourcePath = Path.Combine(source, fileName);
                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, Path.Combine(targetDirectory, fileName), overwrite: false);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static DateTime? GetLastWrite(string directory)
    {
        DateTime? latest = null;

        foreach (var fileName in new[] { "connections.json", "settings.json" })
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            var writeTime = File.GetLastWriteTime(path);
            if (latest is null || writeTime > latest)
            {
                latest = writeTime;
            }
        }

        return latest;
    }

    private static IEnumerable<string> GetMigrationSources()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Dossier de secours utilisé par les versions précédentes en mode Debug.
        yield return Path.Combine(AppContext.BaseDirectory, "NanoFTP-data");

        foreach (var root in new[] { localAppData, roamingAppData, userProfile })
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return Path.Combine(root, "NanoFTP");
            }
        }

        // Ancien nom du produit.
        foreach (var root in new[] { localAppData, roamingAppData, userProfile })
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return Path.Combine(root, "FoireZilla");
            }
        }
    }
}
