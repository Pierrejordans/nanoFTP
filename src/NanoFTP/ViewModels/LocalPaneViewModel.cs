using NanoFTP.Models;
using NanoFTP.Services;

namespace NanoFTP.ViewModels;

public sealed class LocalPaneViewModel : FilePaneViewModel
{
    private int _navigationVersion;
    private bool _isRenaming;
    private string _renameText = string.Empty;
    private FileEntry? _renamingEntry;
    private bool _isConfirmingDelete;
    private string _deletePrompt = string.Empty;
    private FileEntry? _pendingDeleteEntry;

    public LocalPaneViewModel()
    {
        Title = "Ordinateur";
        Subtitle = $"Machine locale · {Environment.MachineName}";
        CurrentPath = LocalFileSystem.HomePath;
        PathInput = CurrentPath;
    }

    public string HomePath => LocalFileSystem.HomePath;

    public bool IsRenaming
    {
        get => _isRenaming;
        private set => SetProperty(ref _isRenaming, value);
    }

    public string RenameText
    {
        get => _renameText;
        set => SetProperty(ref _renameText, value);
    }

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        private set => SetProperty(ref _isConfirmingDelete, value);
    }

    public string DeletePrompt
    {
        get => _deletePrompt;
        private set => SetProperty(ref _deletePrompt, value);
    }

    public void BeginRename(FileEntry entry)
    {
        CancelDelete();
        _renamingEntry = entry;
        RenameText = entry.Name;
        IsRenaming = true;
        ClearError();
    }

    public void CancelRename()
    {
        _renamingEntry = null;
        IsRenaming = false;
        RenameText = string.Empty;
    }

    public async Task<bool> CommitRenameAsync()
    {
        var entry = _renamingEntry;
        if (entry is null)
        {
            CancelRename();
            return false;
        }

        var newName = RenameText.Trim();
        if (newName.Length == 0 ||
            newName is "." or ".." ||
            newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            SetError("Ce nom de fichier n'est pas valide.");
            return false;
        }

        var parent = Path.GetDirectoryName(entry.FullPath);
        if (parent is null)
        {
            SetError("Le dossier parent est introuvable.");
            return false;
        }

        var targetPath = Path.Combine(parent, newName);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(targetPath, entry.FullPath, comparison))
        {
            CancelRename();
            return true;
        }

        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            SetError($"Un élément nommé « {newName} » existe déjà.");
            return false;
        }

        try
        {
            if (entry.IsDirectory)
            {
                Directory.Move(entry.FullPath, targetPath);
            }
            else
            {
                File.Move(entry.FullPath, targetPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetError(DescribeFileError(exception, "renommer"));
            return false;
        }

        CancelRename();
        await RefreshAsync();
        return true;
    }

    public void BeginDelete(FileEntry entry)
    {
        CancelRename();
        _pendingDeleteEntry = entry;
        DeletePrompt = entry.IsDirectory
            ? $"Supprimer définitivement le dossier « {entry.Name} » et tout son contenu ?"
            : $"Supprimer définitivement « {entry.Name} » ?";
        IsConfirmingDelete = true;
        ClearError();
    }

    public void CancelDelete()
    {
        _pendingDeleteEntry = null;
        IsConfirmingDelete = false;
        DeletePrompt = string.Empty;
    }

    public async Task<bool> ConfirmDeleteAsync()
    {
        var entry = _pendingDeleteEntry;
        if (entry is null)
        {
            CancelDelete();
            return false;
        }

        try
        {
            if (entry.IsDirectory)
            {
                var attributes = File.GetAttributes(entry.FullPath);
                var isLink = (attributes & FileAttributes.ReparsePoint) != 0;

                await Task.Run(() => Directory.Delete(entry.FullPath, recursive: !isLink));
            }
            else
            {
                File.Delete(entry.FullPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetError(DescribeFileError(exception, "supprimer"));
            return false;
        }

        CancelDelete();
        await RefreshAsync();
        return true;
    }

    private static string DescribeFileError(Exception exception, string action)
        => exception switch
        {
            UnauthorizedAccessException => $"Windows refuse l'accès à cet élément : impossible de le {action}.",
            DirectoryNotFoundException => "Cet élément n'existe plus.",
            PathTooLongException => "Le chemin est trop long.",
            IOException => $"Impossible de le {action} : il est peut-être utilisé par un autre programme.",
            _ => $"Impossible de le {action}."
        };

    public async Task NavigateAsync(string path, CancellationToken cancellationToken = default)
    {
        var version = ++_navigationVersion;
        string resolved;

        try
        {
            resolved = LocalFileSystem.ResolvePath(path, CurrentPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SetError("Ce chemin n'est pas valide.");
            ResetPathInput();
            return;
        }

        if (!Directory.Exists(resolved))
        {
            SetError("Ce dossier n'existe pas.");
            ResetPathInput();
            return;
        }

        SetLoading();

        try
        {
            var entries = await LocalFileSystem.ListAsync(resolved, cancellationToken);
            if (version != _navigationVersion)
            {
                return;
            }

            SetEntries(resolved, entries);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (version != _navigationVersion)
            {
                return;
            }

            SetError(LocalFileSystem.FriendlyError(exception, resolved));
            ResetPathInput();
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => NavigateAsync(CurrentPath, cancellationToken);

    public Task GoUpAsync(CancellationToken cancellationToken = default)
    {
        var parent = LocalFileSystem.ParentOf(CurrentPath);
        return parent is null ? Task.CompletedTask : NavigateAsync(parent, cancellationToken);
    }

    public Task GoHomeAsync(CancellationToken cancellationToken = default)
        => NavigateAsync(HomePath, cancellationToken);
}
