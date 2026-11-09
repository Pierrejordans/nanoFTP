using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using NanoFTP.Core;
using NanoFTP.Models;
using NanoFTP.Services;

namespace NanoFTP.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ConnectionStore _connectionStore;
    private readonly AppSettingsStore _settingsStore;
    private readonly FtpService _ftpService;
    private readonly SemaphoreSlim _remoteGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly AppSettings _settings;
    private FtpSession? _remoteSession;
    private CancellationTokenSource? _connectionCancellation;
    private CancellationTokenSource? _refreshCancellation;
    private int _connectionGeneration;
    private int _selectionGeneration;
    private int _remoteNavigationGeneration;
    private ConnectionItemViewModel? _selectedConnection;
    private string? _globalError;
    private string? _globalWarning;
    private bool _initialized;
    private bool _disposed;
    private bool _isPreparingTransfers;

    public MainWindowViewModel()
    {
        _connectionStore = new ConnectionStore();
        _settingsStore = new AppSettingsStore(_connectionStore.StorageDirectory);
        _settings = new AppSettings();
        _ftpService = new FtpService();
        Local = new LocalPaneViewModel();
        Remote = new RemotePaneViewModel();
        Transfers = new TransferManager(_ftpService);

        Connections.CollectionChanged += OnConnectionsChanged;
        Transfers.TransferFinished += OnTransferFinished;
        Transfers.ParallelismChanged += OnParallelismChanged;
    }

    public ObservableCollection<ConnectionItemViewModel> Connections { get; } = [];

    public LocalPaneViewModel Local { get; }

    public RemotePaneViewModel Remote { get; }

    public TransferManager Transfers { get; }

    public ConnectionItemViewModel? SelectedConnection
    {
        get => _selectedConnection;
        private set
        {
            if (!SetProperty(ref _selectedConnection, value))
            {
                return;
            }

            OnPropertiesChanged(nameof(HasSelectedConnection));
        }
    }

    public bool HasSelectedConnection => SelectedConnection is not null;

    public bool HasConnections => Connections.Count > 0;

    public bool ShowNoConnections => Connections.Count == 0;

    public string ConnectionCountText => Formatters.Count(Connections.Count, "connexion", "connexions");

    public string? GlobalError
    {
        get => _globalError;
        private set
        {
            if (!SetProperty(ref _globalError, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasGlobalError));
        }
    }

    public bool HasGlobalError => !string.IsNullOrWhiteSpace(GlobalError);

    public string? GlobalWarning
    {
        get => _globalWarning;
        private set
        {
            if (!SetProperty(ref _globalWarning, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasGlobalWarning));
        }
    }

    public bool HasGlobalWarning => !string.IsNullOrWhiteSpace(GlobalWarning);

    public string WorkspaceCaption
    {
        get
        {
            if (Remote.Profile is null)
            {
                return "Choisissez un serveur dans la barre latérale";
            }

            if (Remote.IsConnecting)
            {
                return $"Connexion à {Remote.Profile.Name}…";
            }

            if (Remote.IsConnected)
            {
                return $"Connecté à {Remote.Profile.Name}";
            }

            if (Remote.HasConnectionError)
            {
                return $"{Remote.Profile.Name} · connexion impossible";
            }

            return $"{Remote.Profile.Name} · prêt à se connecter";
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            var loadedSettings = await _settingsStore.LoadAsync();
            _settings.LastConnectionId = loadedSettings.LastConnectionId;
            _settings.ParallelTransfers = Math.Clamp(loadedSettings.ParallelTransfers, 1, 8);
            Transfers.ParallelTransfers = _settings.ParallelTransfers;
        }
        catch (Exception exception)
        {
            GlobalError = $"Les préférences n'ont pas pu être chargées. {exception.Message}";
        }

        IReadOnlyList<ConnectionProfile> profiles;

        try
        {
            profiles = await _connectionStore.LoadAsync();
        }
        catch (Exception exception)
        {
            profiles = [];
            GlobalError = $"Vos connexions n'ont pas pu être chargées. {exception.Message}";
        }

        foreach (var profile in profiles)
        {
            Connections.Add(new ConnectionItemViewModel(profile));
        }

        var initial = Connections.FirstOrDefault(item => item.Id == _settings.LastConnectionId)
            ?? Connections.FirstOrDefault();

        SelectedConnection = initial;
        Remote.Reset(initial?.Profile);

        await Local.NavigateAsync(Local.CurrentPath == "/" ? Local.HomePath : Local.CurrentPath);
        NotifyRemoteSummary();
    }

    public async Task SelectConnectionAsync(ConnectionItemViewModel? connection)
    {
        if (ReferenceEquals(connection, SelectedConnection))
        {
            return;
        }

        var generation = ++_selectionGeneration;
        await CloseSessionAsync(updateSelectionState: true, bumpGeneration: true);

        if (generation != _selectionGeneration)
        {
            return;
        }

        SelectedConnection = connection;
        Remote.Reset(connection?.Profile);
        _settings.LastConnectionId = connection?.Id;
        await SaveSettingsSafeAsync();
        NotifyRemoteSummary();
    }

    public async Task ConnectSelectedAsync()
    {
        if (SelectedConnection is null ||
            Remote.IsConnecting ||
            Remote.IsConnected ||
            _connectionCancellation is not null)
        {
            return;
        }

        var profile = SelectedConnection.Profile.Snapshot();
        var connection = SelectedConnection;
        var generation = ++_connectionGeneration;
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _connectionCancellation = cancellation;

        connection.SetState(ConnectionState.Connecting);
        Remote.BeginConnecting(profile);
        NotifyRemoteSummary();

        FtpSession? session = null;

        try
        {
            await CloseSessionAsync(updateSelectionState: false, bumpGeneration: false);

            if (generation != _connectionGeneration)
            {
                return;
            }

            session = await _ftpService.ConnectAsync(profile, cancellation.Token);

            if (generation != _connectionGeneration)
            {
                await session.DisposeAsync();
                return;
            }

            _remoteSession = session;
            Remote.SetConnected(profile, session.InitialPath);
            Remote.ShowListingLoading();
            connection.SetState(ConnectionState.Connected);
            NotifyRemoteSummary();

            await _remoteGate.WaitAsync(cancellation.Token);
            try
            {
                var entries = await _ftpService.GetListingAsync(session, session.InitialPath, cancellation.Token);
                if (generation != _connectionGeneration)
                {
                    return;
                }

                Remote.SetEntries(session.InitialPath, entries);
            }
            finally
            {
                _remoteGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == _connectionGeneration)
            {
                connection.SetState(ConnectionState.Disconnected);
                Remote.Reset(profile);
                NotifyRemoteSummary();
            }
        }
        catch (Exception exception)
        {
            if (generation != _connectionGeneration)
            {
                return;
            }

            var message = exception is FtpOperationException ftpException
                ? ftpException.Message
                : $"La connexion a échoué. {exception.Message}";

            await CloseSessionAsync(updateSelectionState: false, bumpGeneration: false);
            connection.SetState(ConnectionState.Error);
            Remote.SetConnectionError(message);
            Remote.ResetPathInput();
            NotifyRemoteSummary();
        }
        finally
        {
            if (generation == _connectionGeneration && ReferenceEquals(_connectionCancellation, cancellation))
            {
                _connectionCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    public async Task DisconnectAsync()
    {
        await CloseSessionAsync(updateSelectionState: true, bumpGeneration: true);

        if (SelectedConnection is not null)
        {
            SelectedConnection.SetState(ConnectionState.Disconnected);
            Remote.Reset(SelectedConnection.Profile);
        }

        NotifyRemoteSummary();
    }

    public async Task NavigateRemoteAsync(string path)
    {
        var session = _remoteSession;
        if (session is null || !Remote.IsConnected)
        {
            Remote.ResetPathInput();
            return;
        }

        var normalized = RemotePaths.Normalize(path);
        var generation = ++_remoteNavigationGeneration;

        Remote.ShowListingLoading();

        await _remoteGate.WaitAsync();

        try
        {
            if (!ReferenceEquals(_remoteSession, session))
            {
                return;
            }

            var entries = await _ftpService.GetListingAsync(session, normalized, CancellationToken.None);
            if (generation != _remoteNavigationGeneration || !ReferenceEquals(_remoteSession, session))
            {
                return;
            }

            Remote.SetEntries(normalized, entries);
        }
        catch (Exception exception)
        {
            if (generation != _remoteNavigationGeneration || !ReferenceEquals(_remoteSession, session))
            {
                return;
            }

            var message = exception is FtpOperationException ftpException
                ? ftpException.Message
                : $"Le dossier distant n'a pas pu être ouvert. {exception.Message}";
            Remote.SetError(message);
            Remote.ResetPathInput();
        }
        finally
        {
            _remoteGate.Release();
        }
    }

    public Task GoRemoteUpAsync()
    {
        var parent = RemotePaths.Parent(Remote.CurrentPath);
        return parent is null ? Task.CompletedTask : NavigateRemoteAsync(parent);
    }

    public Task RefreshRemoteAsync()
        => Remote.IsConnected ? NavigateRemoteAsync(Remote.CurrentPath) : Task.CompletedTask;

    public Task RefreshLocalAsync()
        => Local.RefreshAsync();

    public async Task RefreshAllAsync()
    {
        await Task.WhenAll(
            RefreshLocalAsync(),
            RefreshRemoteAsync());
    }

    public async Task AddConnectionAsync(ConnectionProfile profile, bool connectAfterSave)
    {
        var item = new ConnectionItemViewModel(profile);
        Connections.Add(item);
        await SaveConnectionsAsync();
        await SelectConnectionAsync(item);

        if (connectAfterSave)
        {
            await ConnectSelectedAsync();
        }
    }

    public async Task UpdateConnectionAsync(ConnectionItemViewModel item, ConnectionProfile profile)
    {
        var wasActive = ReferenceEquals(SelectedConnection, item);
        var wasConnected = wasActive && Remote.IsConnected;
        var wasConnecting = wasActive && Remote.IsConnecting;

        if (wasActive)
        {
            await CloseSessionAsync(updateSelectionState: false, bumpGeneration: true);
            item.SetState(ConnectionState.Disconnected);
        }

        item.SetProfile(profile);

        if (wasActive)
        {
            Remote.Reset(profile);
            NotifyRemoteSummary();
        }

        await SaveConnectionsAsync();

        if (wasActive && (wasConnected || wasConnecting))
        {
            await ConnectSelectedAsync();
        }
    }

    public async Task DeleteConnectionAsync(ConnectionItemViewModel item)
    {
        var wasActive = ReferenceEquals(SelectedConnection, item);
        var index = Connections.IndexOf(item);

        if (wasActive)
        {
            await CloseSessionAsync(updateSelectionState: false, bumpGeneration: true);
        }

        Connections.Remove(item);

        if (wasActive)
        {
            var next = Connections.Count == 0
                ? null
                : Connections[Math.Clamp(index, 0, Connections.Count - 1)];
            SelectedConnection = next;
            Remote.Reset(next?.Profile);
            _settings.LastConnectionId = next?.Id;
            NotifyRemoteSummary();
            await SaveSettingsSafeAsync();
        }

        await SaveConnectionsAsync();
    }

    public void DismissGlobalError()
        => GlobalError = null;

    public void DismissGlobalWarning()
        => GlobalWarning = null;

    public void ReportError(string message)
        => GlobalError = message;

    public async Task<int> EnqueueUploadsAsync(IEnumerable<FileEntry> entries)
    {
        if (SelectedConnection is null || Remote.Profile is null || !Remote.IsConnected || _isPreparingTransfers)
        {
            return 0;
        }

        _isPreparingTransfers = true;

        try
        {
            var profile = SelectedConnection.Profile.Snapshot();
            var remoteDirectory = Remote.CurrentPath;
            var requests = new List<TransferRequest>();
            var folderRoots = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    var remoteFolder = RemotePaths.Combine(remoteDirectory, entry.Name);
                    folderRoots.Add(remoteFolder);

                    var files = await LocalFileSystem.EnumerateFilesRecursiveAsync(entry.FullPath);
                    foreach (var localFile in files)
                    {
                        var relative = Path.GetRelativePath(entry.FullPath, localFile)
                            .Replace(Path.DirectorySeparatorChar, '/');

                        AddUploadRequest(requests, profile, remoteFolder, relative, localFile);
                    }
                }
                else
                {
                    AddUploadRequest(requests, profile, remoteDirectory, entry.Name, entry.FullPath);
                }
            }

            if (folderRoots.Count > 0)
            {
                var session = _remoteSession;
                if (session is not null)
                {
                    await _remoteGate.WaitAsync();

                    try
                    {
                        if (ReferenceEquals(_remoteSession, session))
                        {
                            foreach (var folderRoot in folderRoots)
                            {
                                await _ftpService.CreateRemoteDirectoryAsync(
                                    session,
                                    folderRoot,
                                    CancellationToken.None);
                            }
                        }
                    }
                    finally
                    {
                        _remoteGate.Release();
                    }
                }
            }

            Transfers.Enqueue(requests);

            if (folderRoots.Count > 0 && requests.Count == 0)
            {
                _ = NavigateRemoteAsync(Remote.CurrentPath);
            }

            return requests.Count;
        }
        finally
        {
            _isPreparingTransfers = false;
        }
    }

    public async Task<int> EnqueueDownloadsAsync(IEnumerable<FileEntry> entries)
    {
        if (Remote.Profile is null || !Remote.IsConnected || _isPreparingTransfers)
        {
            return 0;
        }

        var session = _remoteSession;
        if (session is null)
        {
            return 0;
        }

        _isPreparingTransfers = true;

        try
        {
            var profile = SelectedConnection?.Profile.Snapshot() ?? Remote.Profile.Snapshot();
            var requestedLocalDirectory = Local.CurrentPath;
            var localDirectory = requestedLocalDirectory;
            var redirected = false;

            if (!LocalFileSystem.CanWriteDirectory(localDirectory))
            {
                localDirectory = StoragePaths.DownloadsDirectory;
                Directory.CreateDirectory(localDirectory);
                redirected = true;
            }

            var requests = new List<TransferRequest>();
            var createdFolders = false;

            await _remoteGate.WaitAsync();

            try
            {
                if (!ReferenceEquals(_remoteSession, session))
                {
                    return 0;
                }

                foreach (var entry in entries)
                {
                    if (!entry.IsDirectory)
                    {
                        requests.Add(new TransferRequest(
                            entry.Name,
                            Path.Combine(localDirectory, entry.Name),
                            entry.FullPath,
                            entry.Size,
                            TransferDirection.Download,
                            profile));
                        continue;
                    }

                    var tree = await _ftpService.GetTreeAsync(session, entry.FullPath, CancellationToken.None);
                    var localBase = Path.Combine(localDirectory, entry.Name);
                    Directory.CreateDirectory(localBase);
                    createdFolders = true;

                    foreach (var child in tree)
                    {
                        var relative = GetRelativeRemotePath(entry.FullPath, child.FullPath);
                        if (relative is null)
                        {
                            continue;
                        }

                        var localPath = Path.Combine(localBase, ToLocalRelativePath(relative));

                        if (child.IsDirectory)
                        {
                            Directory.CreateDirectory(localPath);
                            continue;
                        }

                        requests.Add(new TransferRequest(
                            relative,
                            localPath,
                            child.FullPath,
                            child.Size,
                            TransferDirection.Download,
                            profile));
                    }
                }
            }
            finally
            {
                _remoteGate.Release();
            }

            Transfers.Enqueue(requests);

            if (redirected)
            {
                GlobalWarning =
                    $"Le dossier « {requestedLocalDirectory} » n'est pas accessible en écriture. " +
                    $"Les fichiers ont été téléchargés dans « {localDirectory} ».";
                _ = Local.NavigateAsync(localDirectory);
            }
            else if (createdFolders)
            {
                _ = Local.RefreshAsync();
            }

            return requests.Count;
        }
        finally
        {
            _isPreparingTransfers = false;
        }
    }

    public async Task<int> EnqueueExternalUploadsAsync(IEnumerable<string> localPaths)
    {
        if (SelectedConnection is null || Remote.Profile is null || !Remote.IsConnected || _isPreparingTransfers)
        {
            return 0;
        }

        _isPreparingTransfers = true;

        try
        {
            var profile = SelectedConnection.Profile.Snapshot();
            var remoteDirectory = Remote.CurrentPath;
            var requests = new List<TransferRequest>();
            var folderRoots = new HashSet<string>(StringComparer.Ordinal);

            foreach (var path in localPaths)
            {
                if (File.Exists(path))
                {
                    AddUploadRequest(requests, profile, remoteDirectory, Path.GetFileName(path), path);
                    continue;
                }

                if (!Directory.Exists(path))
                {
                    continue;
                }

                var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    continue;
                }

                var remoteFolder = RemotePaths.Combine(remoteDirectory, folderName);
                folderRoots.Add(remoteFolder);
                var files = await LocalFileSystem.EnumerateFilesRecursiveAsync(path);

                foreach (var localFile in files)
                {
                    var relative = Path.GetRelativePath(path, localFile)
                        .Replace(Path.DirectorySeparatorChar, '/');

                    AddUploadRequest(requests, profile, remoteFolder, relative, localFile);
                }
            }

            if (folderRoots.Count > 0)
            {
                var session = _remoteSession;
                if (session is not null)
                {
                    await _remoteGate.WaitAsync();

                    try
                    {
                        if (ReferenceEquals(_remoteSession, session))
                        {
                            foreach (var folderRoot in folderRoots)
                            {
                                await _ftpService.CreateRemoteDirectoryAsync(
                                    session,
                                    folderRoot,
                                    CancellationToken.None);
                            }
                        }
                    }
                    finally
                    {
                        _remoteGate.Release();
                    }
                }
            }

            Transfers.Enqueue(requests);

            if (folderRoots.Count > 0 && requests.Count == 0)
            {
                _ = NavigateRemoteAsync(Remote.CurrentPath);
            }

            return requests.Count;
        }
        finally
        {
            _isPreparingTransfers = false;
        }
    }

    private static void AddUploadRequest(
        ICollection<TransferRequest> requests,
        ConnectionProfile profile,
        string remoteBase,
        string relativeRemotePath,
        string localPath)
    {
        try
        {
            var file = new FileInfo(localPath);
            if (!file.Exists)
            {
                return;
            }

            requests.Add(new TransferRequest(
                relativeRemotePath,
                file.FullName,
                RemotePaths.Combine(remoteBase, relativeRemotePath),
                file.Length,
                TransferDirection.Upload,
                profile));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string? GetRelativeRemotePath(string root, string path)
    {
        var normalizedRoot = RemotePaths.Normalize(root);
        var normalizedPath = RemotePaths.Normalize(path);
        var prefix = normalizedRoot == "/" ? "/" : normalizedRoot + "/";

        return normalizedPath.Length > prefix.Length &&
               normalizedPath.StartsWith(prefix, StringComparison.Ordinal)
            ? normalizedPath[prefix.Length..]
            : null;
    }

    private static string ToLocalRelativePath(string remoteRelativePath)
        => remoteRelativePath.Replace('/', Path.DirectorySeparatorChar);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        Transfers.CancelAll();

        await CloseSessionAsync(updateSelectionState: false, bumpGeneration: true);
        await SaveSettingsSafeAsync();
        _disposed = true;
        _remoteGate.Dispose();
        _settingsGate.Dispose();
    }

    private async Task CloseSessionAsync(bool updateSelectionState, bool bumpGeneration)
    {
        if (bumpGeneration)
        {
            _connectionGeneration++;
            var cancellation = _connectionCancellation;
            _connectionCancellation = null;
            cancellation?.Cancel();
        }

        var session = _remoteSession;
        _remoteSession = null;

        if (session is not null)
        {
            await _remoteGate.WaitAsync();

            try
            {
                await session.DisposeAsync();
            }
            finally
            {
                _remoteGate.Release();
            }
        }

        if (updateSelectionState)
        {
            SelectedConnection?.SetState(ConnectionState.Disconnected);
        }
    }

    private async Task SaveConnectionsAsync()
    {
        try
        {
            await _connectionStore.SaveAsync(Connections.Select(connection => connection.Profile));
        }
        catch (Exception exception)
        {
            GlobalError = $"Vos connexions n'ont pas pu être enregistrées. {exception.Message}";
        }
    }

    private async Task SaveSettingsSafeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _settingsGate.WaitAsync();

            try
            {
                _settings.ParallelTransfers = Transfers.ParallelTransfers;
                _settings.LastConnectionId = SelectedConnection?.Id;
                await _settingsStore.SaveAsync(_settings);
            }
            finally
            {
                _settingsGate.Release();
            }
        }
        catch (Exception)
        {
            // Les préférences ne sont pas critiques.
        }
    }

    private void OnConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertiesChanged(nameof(HasConnections), nameof(ShowNoConnections), nameof(ConnectionCountText));
    }

    private void OnParallelismChanged(object? sender, EventArgs e)
    {
        _ = SaveSettingsSafeAsync();
    }

    private void OnTransferFinished(object? sender, TransferCompletedEventArgs e)
    {
        if (!e.Item.IsCompleted)
        {
            return;
        }

        var refreshLocal = false;
        var refreshRemote = false;

        if (e.Item.IsUpload &&
            Remote.IsConnected &&
            Remote.Profile is not null &&
            e.Item.Request.Profile.Id == Remote.Profile.Id &&
            RemotePaths.Parent(e.Item.RemotePath) == Remote.CurrentPath)
        {
            refreshRemote = true;
        }

        if (!e.Item.IsUpload &&
            string.Equals(Path.GetDirectoryName(e.Item.LocalPath), Local.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            refreshLocal = true;
        }

        if (refreshLocal || refreshRemote)
        {
            _ = RefreshAfterTransferAsync(refreshLocal, refreshRemote);
        }
    }

    private async Task RefreshAfterTransferAsync(bool refreshLocal, bool refreshRemote)
    {
        var cancellation = new CancellationTokenSource();
        var previous = _refreshCancellation;
        _refreshCancellation = cancellation;
        previous?.Cancel();
        previous?.Dispose();

        try
        {
            await Task.Delay(450, cancellation.Token);

            if (refreshLocal)
            {
                await Local.RefreshAsync(cancellation.Token);
            }

            if (refreshRemote)
            {
                await NavigateRemoteAsync(Remote.CurrentPath);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Le rafraîchissement est un confort : une erreur ne doit pas perturber la file.
        }
    }

    private void NotifyRemoteSummary()
    {
        OnPropertiesChanged(nameof(WorkspaceCaption));

        if (SelectedConnection is not null)
        {
            _settings.LastConnectionId = SelectedConnection.Id;
        }
    }
}
