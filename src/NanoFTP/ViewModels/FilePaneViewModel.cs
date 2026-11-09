using System.Collections.ObjectModel;
using NanoFTP.Core;
using NanoFTP.Models;

namespace NanoFTP.ViewModels;

public abstract class FilePaneViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _currentPath = "/";
    private string _pathInput = "/";
    private bool _isLoading;
    private string? _errorMessage;
    private string _selectionText = string.Empty;
    private int _selectionCount;

    protected FilePaneViewModel()
    {
        Entries = new ObservableCollection<FileEntry>();
        Entries.CollectionChanged += (_, _) => NotifyEntryStateChanged();
    }

    public ObservableCollection<FileEntry> Entries { get; }

    public string Title
    {
        get => _title;
        protected set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        protected set => SetProperty(ref _subtitle, value);
    }

    public string CurrentPath
    {
        get => _currentPath;
        protected set
        {
            if (!SetProperty(ref _currentPath, value))
            {
                return;
            }

            PathInput = value;
        }
    }

    public string PathInput
    {
        get => _pathInput;
        set => SetProperty(ref _pathInput, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        protected set
        {
            if (!SetProperty(ref _isLoading, value))
            {
                return;
            }

            NotifyEntryStateChanged();
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set
        {
            if (!SetProperty(ref _errorMessage, value))
            {
                return;
            }

            OnPropertiesChanged(nameof(HasError), nameof(ShowErrorBanner), nameof(ShowFullError), nameof(ShowEmptyPlaceholder));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool ShowErrorBanner => HasError && Entries.Count > 0;

    public bool ShowFullError => HasError && Entries.Count == 0;

    public bool ShowEmptyPlaceholder => !IsLoading && !HasError && Entries.Count == 0;

    public string EntryCountText
    {
        get
        {
            if (Entries.Count == 0)
            {
                return "Aucun élément";
            }

            return Formatters.Count(Entries.Count, "élément", "éléments");
        }
    }

    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }

    public bool HasSelection => _selectionCount > 0;

    public bool HasFilesSelected { get; private set; }

    public void SetEntries(string path, IEnumerable<FileEntry> entries)
    {
        CurrentPath = path;
        ErrorMessage = null;

        Entries.Clear();
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }

        IsLoading = false;
        SetSelection([]);
        NotifyEntryStateChanged();
    }

    public void ClearEntries()
    {
        Entries.Clear();
        ErrorMessage = null;
        IsLoading = false;
        SetSelection([]);
        NotifyEntryStateChanged();
    }

    public void SetLoading()
    {
        ErrorMessage = null;
        IsLoading = true;
    }

    public void SetError(string message)
    {
        IsLoading = false;
        ErrorMessage = message;
    }

    public void ClearError()
    {
        ErrorMessage = null;
    }

    public void ResetPathInput()
    {
        PathInput = CurrentPath;
    }

    public void SetSelection(IReadOnlyCollection<FileEntry> selectedEntries)
    {
        _selectionCount = selectedEntries.Count;
        HasFilesSelected = selectedEntries.Any(entry => entry.CanTransfer);
        SelectionText = _selectionCount switch
        {
            0 => string.Empty,
            1 => selectedEntries.First().Name,
            _ => $"{_selectionCount} sélectionnés"
        };
        OnPropertiesChanged(nameof(HasSelection), nameof(HasFilesSelected), nameof(SelectionText));
    }

    protected virtual void NotifyEntryStateChanged()
    {
        OnPropertiesChanged(
            nameof(EntryCountText),
            nameof(ShowEmptyPlaceholder),
            nameof(ShowErrorBanner),
            nameof(ShowFullError));
    }
}
