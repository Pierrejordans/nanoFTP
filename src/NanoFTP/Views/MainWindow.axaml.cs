using System.Collections;
using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NanoFTP.Models;
using NanoFTP.Services;
using NanoFTP.ViewModels;

namespace NanoFTP.Views;

public partial class MainWindow : Window
{
    private static readonly DataFormat<string> DragFormat =
        DataFormat.CreateStringApplicationFormat("nanoftp-transfer");

    private readonly MainWindowViewModel _viewModel;
    private Point? _dragStartPoint;
    private ListBox? _dragSource;
    private List<FileEntry>? _dragSelectionSnapshot;
    private FileEntry? _localContextEntry;
    private bool _isDragging;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();

        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NanoFTP/Assets/nanoftp.png")));
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        _viewModel.Remote.PropertyChanged += RemoteOnPropertyChanged;
        _viewModel.Local.PropertyChanged += LocalOnPropertyChanged;

        LocalList.AddHandler(
            InputElement.PointerPressedEvent,
            LocalList_PointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        LocalList.AddHandler(
            InputElement.PointerMovedEvent,
            LocalList_PointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        RemoteList.AddHandler(
            InputElement.PointerPressedEvent,
            RemoteList_PointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        RemoteList.AddHandler(
            InputElement.PointerMovedEvent,
            RemoteList_PointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        DragDrop.SetAllowDrop(LocalCard, true);
        DragDrop.SetAllowDrop(RemoteCard, true);
        LocalCard.AddHandler(DragDrop.DragOverEvent, LocalCard_DragOver);
        LocalCard.AddHandler(DragDrop.DragLeaveEvent, LocalCard_DragLeave);
        LocalCard.AddHandler(DragDrop.DropEvent, LocalCard_Drop);
        RemoteCard.AddHandler(DragDrop.DragOverEvent, RemoteCard_DragOver);
        RemoteCard.AddHandler(DragDrop.DragLeaveEvent, RemoteCard_DragLeave);
        RemoteCard.AddHandler(DragDrop.DropEvent, RemoteCard_Drop);

        Opened += OnOpened;
        Closing += OnClosing;
        KeyDown += OnWindowKeyDown;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        FitToWorkingArea();

        try
        {
            await _viewModel.InitializeAsync();
            UpdateTransferActions();
        }
        catch (Exception exception)
        {
            _viewModel.ReportError($"L'application n'a pas pu démarrer correctement. {exception.Message}");
        }
    }

    private void FitToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scale = Math.Max(screen.Scaling, RenderScaling);
        if (scale <= 0)
        {
            scale = 1;
        }
        var workingArea = screen.WorkingArea;
        var maxWidth = workingArea.Width / scale - 24;
        var maxHeight = workingArea.Height / scale - 52;

        var targetWidth = Math.Min(Width, Math.Max(MinWidth, maxWidth));
        var targetHeight = Math.Min(Height, Math.Max(MinHeight, maxHeight));

        if (targetWidth < Width)
        {
            Width = targetWidth;
        }

        if (targetHeight < Height)
        {
            Height = targetHeight;
        }

        var pixelWidth = (int)Math.Round(Width * scale);
        var pixelHeight = (int)Math.Round(Height * scale);
        Position = new PixelPoint(
            workingArea.X + Math.Max(0, (workingArea.Width - pixelWidth) / 2),
            workingArea.Y + Math.Max(0, (workingArea.Height - pixelHeight) / 2));
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;

        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;

        try
        {
            await _viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            CrashLogger.Log(new TimeoutException("La fermeture a dépassé 5 secondes."), "Fermeture de l'application");
        }
        catch (Exception exception)
        {
            CrashLogger.Log(exception, "Fermeture de l'application");
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    public void ReportUnexpectedError(Exception exception)
    {
        _viewModel.ReportError(
            $"Une erreur inattendue est survenue. L'application reste ouverte. {exception.Message}");
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            _ = RefreshAllAsync();
        }
        else if (e.Key == Key.N && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = AddConnectionAsync();
        }
    }

    private async void ConnectionsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: ConnectionItemViewModel connection })
        {
            return;
        }

        try
        {
            await _viewModel.SelectConnectionAsync(connection);
            UpdateTransferActions();
        }
        catch (Exception exception)
        {
            _viewModel.ReportError(exception.Message);
        }
    }

    private async void ConnectionsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FindAncestorButton(e.Source) is not null)
        {
            return;
        }

        if (_viewModel.SelectedConnection is not { } connection || connection.IsConnected)
        {
            return;
        }

        try
        {
            await _viewModel.ConnectSelectedAsync();
            UpdateTransferActions();
        }
        catch (Exception exception)
        {
            _viewModel.ReportError(exception.Message);
        }
    }

    private async void AddConnection_Click(object? sender, RoutedEventArgs e)
    {
        await AddConnectionAsync();
    }

    private async Task AddConnectionAsync()
    {
        ModalScrim.IsVisible = true;

        try
        {
            var editor = new ConnectionEditorWindow(null);
            var result = await editor.ShowDialog<ConnectionEditorResult?>(this);

            if (result is null || result.Delete)
            {
                return;
            }

            await _viewModel.AddConnectionAsync(result.Profile, result.ConnectAfterSave);
            UpdateTransferActions();
        }
        catch (Exception exception)
        {
            _viewModel.ReportError($"La connexion n'a pas pu être enregistrée. {exception.Message}");
        }
        finally
        {
            ModalScrim.IsVisible = false;
        }
    }

    private async void EditConnection_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is Button { DataContext: ConnectionItemViewModel connection })
        {
            await EditConnectionAsync(connection);
        }
    }

    private async void EditSelectedConnection_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedConnection is { } connection)
        {
            await EditConnectionAsync(connection);
        }
    }

    private async Task EditConnectionAsync(ConnectionItemViewModel connection)
    {
        ModalScrim.IsVisible = true;

        try
        {
            var editor = new ConnectionEditorWindow(connection.Profile);
            var result = await editor.ShowDialog<ConnectionEditorResult?>(this);

            if (result is null)
            {
                return;
            }

            if (result.Delete)
            {
                await _viewModel.DeleteConnectionAsync(connection);
            }
            else
            {
                await _viewModel.UpdateConnectionAsync(connection, result.Profile);
            }

            UpdateTransferActions();
        }
        catch (Exception exception)
        {
            _viewModel.ReportError($"La connexion n'a pas pu être modifiée. {exception.Message}");
        }
        finally
        {
            ModalScrim.IsVisible = false;
        }
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.ConnectSelectedAsync();
            UpdateTransferActions();
        }
        catch (Exception exception)
        {
            _viewModel.ReportError(exception.Message);
        }
    }

    private async void Disconnect_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.DisconnectAsync();
            UpdateTransferActions();
        }
        catch (Exception exception)
        {
            _viewModel.ReportError(exception.Message);
        }
    }

    private async void RefreshAll_Click(object? sender, RoutedEventArgs e)
        => await RefreshAllAsync();

    private async Task RefreshAllAsync()
    {
        try
        {
            await _viewModel.RefreshAllAsync();
            UpdateTransferActions();
        }
        catch (Exception exception)
        {
            _viewModel.ReportError(exception.Message);
        }
    }

    private async void LocalRefresh_Click(object? sender, RoutedEventArgs e)
        => await RunSafeAsync(() => _viewModel.RefreshLocalAsync());

    private async void RemoteRefresh_Click(object? sender, RoutedEventArgs e)
        => await RunSafeAsync(() => _viewModel.RefreshRemoteAsync());

    private async void LocalHome_Click(object? sender, RoutedEventArgs e)
        => await RunSafeAsync(() => _viewModel.Local.GoHomeAsync());

    private async void LocalUp_Click(object? sender, RoutedEventArgs e)
        => await RunSafeAsync(() => _viewModel.Local.GoUpAsync());

    private async void RemoteUp_Click(object? sender, RoutedEventArgs e)
        => await RunSafeAsync(() => _viewModel.GoRemoteUpAsync());

    private async void LocalGo_Click(object? sender, RoutedEventArgs e)
        => await RunSafeAsync(() => _viewModel.Local.NavigateAsync(_viewModel.Local.PathInput));

    private async void RemoteGo_Click(object? sender, RoutedEventArgs e)
        => await RunSafeAsync(() => _viewModel.NavigateRemoteAsync(_viewModel.Remote.PathInput));

    private async void LocalPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await RunSafeAsync(() => _viewModel.Local.NavigateAsync(_viewModel.Local.PathInput));
    }

    private async void RemotePath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await RunSafeAsync(() => _viewModel.NavigateRemoteAsync(_viewModel.Remote.PathInput));
    }

    private async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action();
            UpdateTransferActions();
        }
        catch (Exception exception)
        {
            _viewModel.ReportError(exception.Message);
        }
    }

    private void LocalList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _viewModel.Local.SetSelection(GetSelectedEntries(LocalList));
        UpdateTransferActions();
    }

    private void RemoteList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _viewModel.Remote.SetSelection(GetSelectedEntries(RemoteList));
        UpdateTransferActions();
    }

    private async void UploadSelection_Click(object? sender, RoutedEventArgs e)
    {
        await PrepareTransfersAsync(() => _viewModel.EnqueueUploadsAsync(GetSelectedEntries(LocalList)));
    }

    private async void DownloadSelection_Click(object? sender, RoutedEventArgs e)
    {
        await PrepareTransfersAsync(() => _viewModel.EnqueueDownloadsAsync(GetSelectedEntries(RemoteList)));
    }

    private async Task PrepareTransfersAsync(Func<Task<int>> enqueue)
    {
        try
        {
            await enqueue();
        }
        catch (UnauthorizedAccessException)
        {
            _viewModel.ReportError(
                "Windows refuse l'accès à ce dossier local. Choisissez un autre dossier " +
                "ou vérifiez ses autorisations.");
        }
        catch (Exception exception)
        {
            _viewModel.ReportError(
                exception is FtpOperationException ftpException
                    ? ftpException.Message
                    : $"La préparation des transferts a échoué. {exception.Message}");
        }
        finally
        {
            UpdateTransferActions();
        }
    }

    private async void LocalList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var entry = GetEntryFromSource(e.Source);
        if (entry is null)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            await RunSafeAsync(() => _viewModel.Local.NavigateAsync(entry.FullPath));
            return;
        }

        await PrepareTransfersAsync(() => _viewModel.EnqueueUploadsAsync([entry]));
    }

    private async void RemoteList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var entry = GetEntryFromSource(e.Source);
        if (entry is null)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            await RunSafeAsync(() => _viewModel.NavigateRemoteAsync(entry.FullPath));
            return;
        }

        await PrepareTransfersAsync(() => _viewModel.EnqueueDownloadsAsync([entry]));
    }

    private void LocalList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(LocalList).Properties.IsRightButtonPressed)
        {
            var entry = GetEntryFromSource(e.Source);
            _localContextEntry = entry;

            if (entry is null)
            {
                LocalList.SelectedItems?.Clear();
                return;
            }

            if (LocalList.SelectedItems is { } selected && !selected.Contains(entry))
            {
                selected.Clear();
                selected.Add(entry);
            }

            LocalList.Focus();
            return;
        }

        BeginPotentialDrag(LocalList, e);
    }

    private void LocalList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_localContextEntry is null ||
            LocalList.SelectedItems?.Contains(_localContextEntry) != true)
        {
            _localContextEntry = LocalList.SelectedItem as FileEntry;
        }

        if (_localContextEntry is null)
        {
            e.Handled = true;
        }
    }

    private void LocalContextSelect_Click(object? sender, RoutedEventArgs e)
    {
        if (GetLocalContextEntry() is not { } entry)
        {
            return;
        }

        SelectLocalEntry(entry);
    }

    private void LocalContextRename_Click(object? sender, RoutedEventArgs e)
    {
        if (GetLocalContextEntry() is not { } entry)
        {
            return;
        }

        _viewModel.Local.BeginRename(entry);
        Dispatcher.UIThread.Post(
            () =>
            {
                LocalRenameBox.Focus();
                LocalRenameBox.SelectAll();
            },
            DispatcherPriority.Background);
    }

    private void LocalContextDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (GetLocalContextEntry() is not { } entry)
        {
            return;
        }

        _viewModel.Local.BeginDelete(entry);
    }

    private void LocalContextReveal_Click(object? sender, RoutedEventArgs e)
    {
        if (GetLocalContextEntry() is not { } entry)
        {
            return;
        }

        try
        {
            LocalFileSystem.OpenInFileManager(entry.FullPath, entry.IsDirectory);
        }
        catch (Exception exception)
        {
            _viewModel.ReportError(
                $"Impossible d'ouvrir l'explorateur de fichiers. {exception.Message}");
        }
    }

    private async void LocalRenameConfirm_Click(object? sender, RoutedEventArgs e)
        => await CommitLocalRenameAsync();

    private void LocalRenameCancel_Click(object? sender, RoutedEventArgs e)
        => _viewModel.Local.CancelRename();

    private async void LocalRename_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitLocalRenameAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.Local.CancelRename();
        }
    }

    private async Task CommitLocalRenameAsync()
    {
        try
        {
            if (await _viewModel.Local.CommitRenameAsync())
            {
                _localContextEntry = null;
                UpdateTransferActions();
            }
            else
            {
                LocalRenameBox.Focus();
                LocalRenameBox.SelectAll();
            }
        }
        catch (Exception exception)
        {
            _viewModel.ReportError($"Le renommage a échoué. {exception.Message}");
        }
    }

    private async void LocalDeleteConfirm_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (await _viewModel.Local.ConfirmDeleteAsync())
            {
                _localContextEntry = null;
                UpdateTransferActions();
            }
        }
        catch (Exception exception)
        {
            _viewModel.ReportError($"La suppression a échoué. {exception.Message}");
        }
    }

    private void LocalDeleteCancel_Click(object? sender, RoutedEventArgs e)
        => _viewModel.Local.CancelDelete();

    private FileEntry? GetLocalContextEntry()
        => _localContextEntry ?? LocalList.SelectedItem as FileEntry;

    private void SelectLocalEntry(FileEntry entry)
    {
        if (LocalList.SelectedItems is not { } selected)
        {
            return;
        }

        selected.Clear();
        selected.Add(entry);
        LocalList.Focus();
    }

    private void RemoteList_PointerPressed(object? sender, PointerPressedEventArgs e)
        => BeginPotentialDrag(RemoteList, e);

    private void LocalList_PointerMoved(object? sender, PointerEventArgs e)
        => _ = ContinueDragAsync(LocalList, "local", e);

    private void RemoteList_PointerMoved(object? sender, PointerEventArgs e)
        => _ = ContinueDragAsync(RemoteList, "remote", e);

    private void BeginPotentialDrag(ListBox listBox, PointerPressedEventArgs e)
    {
        if (_isDragging || !e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (FindAncestorButton(e.Source) is not null)
        {
            ClearDragState();
            return;
        }

        var pressedEntry = GetEntryFromSource(e.Source);
        if (pressedEntry is null)
        {
            ClearDragState();
            return;
        }

        // Le clic sur une ligne déjà sélectionnée peut réduire la multi-sélection
        // à cette seule ligne avant que le drag ne démarre. On mémorise donc la
        // sélection complète dès le PointerPressed.
        var selectedBeforeClick = GetSelectedEntries(listBox);

        _dragSelectionSnapshot =
            selectedBeforeClick.Count > 1 &&
            selectedBeforeClick.Any(entry => ReferenceEquals(entry, pressedEntry))
                ? selectedBeforeClick
                : null;

        _dragStartPoint = e.GetPosition(listBox);
        _dragSource = listBox;
    }

    private void ClearDragState()
    {
        _dragStartPoint = null;
        _dragSource = null;
        _dragSelectionSnapshot = null;
    }

    private async Task ContinueDragAsync(ListBox listBox, string source, PointerEventArgs e)
    {
        if (_isDragging || _dragStartPoint is null || !ReferenceEquals(_dragSource, listBox))
        {
            if (!e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed)
            {
                ClearDragState();
            }

            return;
        }

        var point = e.GetPosition(listBox);
        if (Math.Abs(point.X - _dragStartPoint.Value.X) < 6 &&
            Math.Abs(point.Y - _dragStartPoint.Value.Y) < 6)
        {
            return;
        }

        if (!e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed)
        {
            ClearDragState();
            return;
        }

        var selectedForDrag = _dragSelectionSnapshot ?? GetSelectedEntries(listBox);
        var entries = selectedForDrag
            .GroupBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        _dragStartPoint = null;
        _dragSource = null;
        _dragSelectionSnapshot = null;

        if (entries.Count == 0)
        {
            return;
        }

        _isDragging = true;

        try
        {
            var payload = new DragPayload(
                source,
                entries.Select(entry => new DragPayloadFile(entry.Name, entry.FullPath, entry.Size, entry.IsDirectory)).ToList());

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(DragFormat, JsonSerializer.Serialize(payload)));
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Copy);
        }
        catch (Exception)
        {
            // Une annulation de glisser-déposer ne doit jamais interrompre l'application.
        }
        finally
        {
            _isDragging = false;
            HideDropOverlays();

            if (selectedForDrag.Count > 1)
            {
                RestoreSelection(listBox, selectedForDrag);
            }
        }
    }

    private void RestoreSelection(ListBox listBox, IReadOnlyList<FileEntry> entries)
    {
        try
        {
            var selectedItems = listBox.SelectedItems;
            if (selectedItems is null)
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (!selectedItems.Contains(entry))
                {
                    selectedItems.Add(entry);
                }
            }

            if (ReferenceEquals(listBox, LocalList))
            {
                _viewModel.Local.SetSelection(GetSelectedEntries(LocalList));
            }
            else if (ReferenceEquals(listBox, RemoteList))
            {
                _viewModel.Remote.SetSelection(GetSelectedEntries(RemoteList));
            }

            UpdateTransferActions();
        }
        catch (NotSupportedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void LocalCard_DragOver(object? sender, DragEventArgs e)
    {
        var allowed = GetDragPayload(e.DataTransfer) is { } payload &&
                      payload.Source == "remote" &&
                      payload.Files.Count > 0;

        e.DragEffects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        LocalDropOverlay.IsVisible = allowed;
        RemoteDropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private void LocalCard_DragLeave(object? sender, DragEventArgs e)
    {
        LocalDropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private async void LocalCard_Drop(object? sender, DragEventArgs e)
    {
        HideDropOverlays();
        e.Handled = true;

        var payload = GetDragPayload(e.DataTransfer);
        if (payload is null || payload.Source != "remote")
        {
            return;
        }

        var entries = payload.Files
            .Select(file => new FileEntry
            {
                Name = file.Name,
                FullPath = file.FullPath,
                Size = file.Size,
                IsDirectory = file.IsDirectory,
                Modified = null
            })
            .ToList();

        await PrepareTransfersAsync(() => _viewModel.EnqueueDownloadsAsync(entries));
    }

    private void RemoteCard_DragOver(object? sender, DragEventArgs e)
    {
        var canUpload = _viewModel.Remote.IsConnected;
        var internalFiles = GetDragPayload(e.DataTransfer) is { } payload &&
                            payload.Source == "local" &&
                            payload.Files.Count > 0;
        var externalFiles = e.DataTransfer.TryGetFiles()?
            .Any(item => File.Exists(item.Path.LocalPath) || Directory.Exists(item.Path.LocalPath)) == true;

        var allowed = canUpload && (internalFiles || externalFiles);
        e.DragEffects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        RemoteDropOverlay.IsVisible = allowed;
        LocalDropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private void RemoteCard_DragLeave(object? sender, DragEventArgs e)
    {
        RemoteDropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private async void RemoteCard_Drop(object? sender, DragEventArgs e)
    {
        HideDropOverlays();
        e.Handled = true;

        if (!_viewModel.Remote.IsConnected)
        {
            return;
        }

        var payload = GetDragPayload(e.DataTransfer);
        if (payload is { Source: "local" })
        {
            var entries = payload.Files
                .Select(file => new FileEntry
                {
                    Name = file.Name,
                    FullPath = file.FullPath,
                    Size = file.Size,
                    IsDirectory = file.IsDirectory,
                    Modified = null
                })
                .ToList();

            await PrepareTransfersAsync(() => _viewModel.EnqueueUploadsAsync(entries));
            return;
        }

        var externalPaths = e.DataTransfer.TryGetFiles()?
            .Select(item => item.Path.LocalPath)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToList() ?? [];

        if (externalPaths.Count > 0)
        {
            await PrepareTransfersAsync(() => _viewModel.EnqueueExternalUploadsAsync(externalPaths));
        }
    }

    private void HideDropOverlays()
    {
        LocalDropOverlay.IsVisible = false;
        RemoteDropOverlay.IsVisible = false;
    }

    private void CancelTransfer_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TransferItem item })
        {
            _viewModel.Transfers.Cancel(item);
        }
    }

    private void RetryTransfer_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TransferItem item })
        {
            _viewModel.Transfers.Retry(item);
        }
    }

    private void ClearTransfers_Click(object? sender, RoutedEventArgs e)
        => _viewModel.Transfers.ClearCompleted();

    private void DismissError_Click(object? sender, RoutedEventArgs e)
        => _viewModel.DismissGlobalError();

    private void DismissWarning_Click(object? sender, RoutedEventArgs e)
        => _viewModel.DismissGlobalWarning();

    private void RemoteOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemotePaneViewModel.IsConnected) or
            nameof(RemotePaneViewModel.IsConnecting) or
            nameof(RemotePaneViewModel.State) or
            nameof(RemotePaneViewModel.HasFilesSelected) or
            nameof(RemotePaneViewModel.HasSelection))
        {
            UpdateTransferActionsOnUiThread();
        }
    }

    private void LocalOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalPaneViewModel.HasFilesSelected) or
            nameof(LocalPaneViewModel.HasSelection))
        {
            UpdateTransferActionsOnUiThread();
        }
    }

    private void UpdateTransferActionsOnUiThread()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateTransferActions();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateTransferActions);
        }
    }

    private void UpdateTransferActions()
    {
        UploadButton.IsEnabled = _viewModel.Remote.IsConnected && _viewModel.Local.HasSelection;
        DownloadButton.IsEnabled = _viewModel.Remote.IsConnected && _viewModel.Remote.HasSelection;
    }

    private static List<FileEntry> GetSelectedEntries(ListBox listBox)
    {
        if (listBox.SelectedItems is not IList selected)
        {
            return [];
        }

        return selected.OfType<FileEntry>().ToList();
    }

    private static FileEntry? GetEntryFromSource(object? source)
    {
        if (source is not Visual visual)
        {
            return null;
        }

        return visual.FindAncestorOfType<ListBoxItem>()?.DataContext as FileEntry;
    }

    private static Button? FindAncestorButton(object? source)
    {
        if (source is not Visual visual)
        {
            return null;
        }

        return visual.FindAncestorOfType<Button>();
    }

    private static DragPayload? GetDragPayload(IDataTransfer dataTransfer)
    {
        if (!dataTransfer.Contains(DragFormat))
        {
            return null;
        }

        var raw = dataTransfer.TryGetValue(DragFormat);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DragPayload>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record DragPayload(string Source, List<DragPayloadFile> Files);

    private sealed record DragPayloadFile(string Name, string FullPath, long Size, bool IsDirectory);
}
