using Avalonia.Media;
using NanoFTP.Core;
using NanoFTP.Models;

namespace NanoFTP.ViewModels;

public sealed class RemotePaneViewModel : FilePaneViewModel
{
    private static readonly IBrush DisconnectedBrush = new SolidColorBrush(Color.Parse("#71879A"));
    private static readonly IBrush ConnectingBrush = new SolidColorBrush(Color.Parse("#E6A93D"));
    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#1C9A76"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#D95C52"));

    private ConnectionProfile? _profile;
    private ConnectionState _state = ConnectionState.Disconnected;
    private string? _connectionErrorMessage;

    public RemotePaneViewModel()
    {
        Title = "Serveur distant";
        Subtitle = "Aucune connexion sélectionnée";
        CurrentPath = "/";
    }

    public ConnectionProfile? Profile => _profile;

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            NotifyDisplay();
        }
    }

    public bool IsConnected => _state == ConnectionState.Connected;

    public bool IsConnecting => _state == ConnectionState.Connecting;

    public bool IsDisconnected => _state == ConnectionState.Disconnected;

    public bool HasConnectionError => _state == ConnectionState.Error;

    public bool HasProfile => _profile is not null;

    public string? ConnectionErrorMessage
    {
        get => _connectionErrorMessage;
        private set
        {
            if (!SetProperty(ref _connectionErrorMessage, value))
            {
                return;
            }

            OnPropertiesChanged(nameof(HasConnectionErrorText), nameof(ConnectButtonLabel));
        }
    }

    public bool HasConnectionErrorText => !string.IsNullOrWhiteSpace(ConnectionErrorMessage);

    public string StatusLabel => _state switch
    {
        ConnectionState.Connected => "Connecté",
        ConnectionState.Connecting => "Connexion…",
        ConnectionState.Error => "Erreur de connexion",
        _ => "Déconnecté"
    };

    public IBrush StatusBrush => _state switch
    {
        ConnectionState.Connected => ConnectedBrush,
        ConnectionState.Connecting => ConnectingBrush,
        ConnectionState.Error => ErrorBrush,
        _ => DisconnectedBrush
    };

    public string ConnectButtonLabel => HasConnectionError ? "Réessayer" : "Se connecter";

    public string ProtocolLabel => _profile?.ProtocolLabel ?? "FTP";

    public string Endpoint => _profile?.DisplayEndpoint ?? "—";

    public bool CanConnect => _profile is not null && _state is ConnectionState.Disconnected or ConnectionState.Error;

    public bool CanDisconnect => _state == ConnectionState.Connected;

    public bool ShowDisconnectingButton => _state == ConnectionState.Connecting;

    public bool ShowConnectedContent => _state == ConnectionState.Connected;

    public bool ShowDisconnectedState => _profile is not null && _state == ConnectionState.Disconnected;

    public bool ShowNoProfileState => _profile is null;

    public bool ShowConnectionErrorState => _state == ConnectionState.Error;

    public bool ShowConnectingState => _state == ConnectionState.Connecting;

    public bool ShowListingErrorBanner => ShowConnectedContent && HasError && Entries.Count > 0;

    public bool ShowListingErrorState => ShowConnectedContent && HasError && Entries.Count == 0;

    public bool ShowEmptyDirectory => ShowConnectedContent && !IsLoading && !HasError && Entries.Count == 0;

    public void Reset(ConnectionProfile? profile)
    {
        _profile = profile;
        ConnectionErrorMessage = null;
        State = ConnectionState.Disconnected;
        ClearEntries();
        CurrentPath = "/";
        PathInput = "/";
        Title = profile?.Name ?? "Serveur distant";
        Subtitle = profile is null
            ? "Aucune connexion sélectionnée"
            : $"{profile.DisplayEndpoint} · {profile.ProtocolLabel}";
        NotifyDisplay();
    }

    public void BeginConnecting(ConnectionProfile profile)
    {
        _profile = profile;
        ConnectionErrorMessage = null;
        ClearEntries();
        CurrentPath = "/";
        PathInput = "/";
        State = ConnectionState.Connecting;
        Title = profile.Name;
        Subtitle = $"{profile.DisplayEndpoint} · {profile.ProtocolLabel}";
        SetLoading();
        NotifyDisplay();
    }

    public void SetConnected(ConnectionProfile profile, string path)
    {
        _profile = profile;
        ConnectionErrorMessage = null;
        State = ConnectionState.Connected;
        Title = profile.Name;
        Subtitle = $"{profile.DisplayEndpoint} · {profile.ProtocolLabel}";
        CurrentPath = path;
        ClearError();
        NotifyDisplay();
    }

    public void SetDisconnected()
    {
        ConnectionErrorMessage = null;
        State = ConnectionState.Disconnected;
        ClearEntries();
        CurrentPath = "/";
        PathInput = "/";
        NotifyDisplay();
    }

    public void SetConnectionError(string message)
    {
        ConnectionErrorMessage = message;
        State = ConnectionState.Error;
        ClearEntries();
        CurrentPath = "/";
        PathInput = "/";
        NotifyDisplay();
    }

    public void ShowListingLoading()
    {
        ClearError();
        SetLoading();
        NotifyDisplay();
    }

    protected override void NotifyEntryStateChanged()
    {
        base.NotifyEntryStateChanged();
        OnPropertiesChanged(nameof(ShowListingErrorBanner), nameof(ShowListingErrorState), nameof(ShowEmptyDirectory));
    }

    private void NotifyDisplay()
    {
        OnPropertiesChanged(
            nameof(State),
            nameof(IsConnected),
            nameof(IsConnecting),
            nameof(IsDisconnected),
            nameof(HasConnectionError),
            nameof(HasProfile),
            nameof(StatusLabel),
            nameof(StatusBrush),
            nameof(ConnectButtonLabel),
            nameof(ProtocolLabel),
            nameof(Endpoint),
            nameof(CanConnect),
            nameof(CanDisconnect),
            nameof(ShowDisconnectingButton),
            nameof(ShowConnectedContent),
            nameof(ShowDisconnectedState),
            nameof(ShowNoProfileState),
            nameof(ShowConnectionErrorState),
            nameof(ShowConnectingState),
            nameof(ShowListingErrorBanner),
            nameof(ShowListingErrorState),
            nameof(ShowEmptyDirectory));
    }
}
