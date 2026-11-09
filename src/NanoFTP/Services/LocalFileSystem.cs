using System.Diagnostics;
using NanoFTP.Models;

namespace NanoFTP.Services;

public static class LocalFileSystem
{
    public static string HomePath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Directory.Exists(home) ? home : Environment.CurrentDirectory;
        }
    }

    public static string ResolvePath(string path, string currentPath)
    {
        var value = path.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return currentPath;
        }

        if (value == "~")
        {
            return HomePath;
        }

        if (value.StartsWith("~/", StringComparison.Ordinal) ||
            value.StartsWith("~\\", StringComparison.Ordinal))
        {
            value = Path.Combine(HomePath, value[2..]);
        }

        if (!Path.IsPathRooted(value))
        {
            value = Path.Combine(currentPath, value);
        }

        return Path.GetFullPath(value);
    }

    public static Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<FileEntry>>(() =>
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0
            };

            var directory = new DirectoryInfo(path);
            var entries = new List<FileEntry>();

            foreach (var item in directory.EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
                    entries.Add(new FileEntry
                    {
                        Name = item.Name,
                        FullPath = item.FullName,
                        IsDirectory = isDirectory,
                        IsHidden = item.Name.StartsWith('.') ||
                                   (item.Attributes & FileAttributes.Hidden) != 0,
                        Size = isDirectory ? 0 : ((FileInfo)item).Length,
                        Modified = item.LastWriteTime
                    });
                }
                catch (IOException)
                {
                    // Un élément disparu pendant l'énumération ne doit pas bloquer le dossier.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.IsHidden)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    public static bool CanWriteDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probePath = Path.Combine(path, $".nanoftp-write-test-{Guid.NewGuid():N}");
            using (File.Create(probePath))
            {
            }

            File.Delete(probePath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    public static Task<IReadOnlyList<string>> EnumerateFilesRecursiveAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0
            };

            var files = new List<string>();
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(file);
            }

            files.Sort(StringComparer.CurrentCultureIgnoreCase);
            return files;
        }, cancellationToken);
    }

    public static string? ParentOf(string path)
    {
        var parent = Directory.GetParent(path);
        return parent?.FullName;
    }

    public static void OpenInFileManager(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var startInfo = new ProcessStartInfo("open")
            {
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-R");
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
            return;
        }

        var target = isDirectory ? path : ParentOf(path) ?? path;
        var linuxStartInfo = new ProcessStartInfo("xdg-open")
        {
            UseShellExecute = false
        };
        linuxStartInfo.ArgumentList.Add(target);
        Process.Start(linuxStartInfo);
    }

    public static string FriendlyError(Exception exception, string path)
    {
        return exception switch
        {
            UnauthorizedAccessException => $"Accès refusé à « {path} ».",
            DirectoryNotFoundException => "Ce dossier n'existe plus.",
            PathTooLongException => "Le chemin est trop long.",
            IOException => "Impossible de lire ce dossier. Il est peut-être utilisé par un autre programme.",
            _ => $"Impossible d'ouvrir « {path} »."
        };
    }
}
