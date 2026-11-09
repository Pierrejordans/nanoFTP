using Avalonia.Media;
using NanoFTP.Core;
using NanoFTP.Models;

namespace NanoFTP.ViewModels;

public sealed class ConnectionItemViewModel : ObservableObject
{
    private static readonly IBrush DisconnectedBrush = new SolidColorBrush(Color.Parse("#71879A"));
    private static readonly IBrush ConnectingBrush = new SolidColorBrush(Color.Parse("#E6A93D"));
    private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.Parse("#2BC59A"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E06A5F"));

    private ConnectionProfile _profile;
    private ConnectionState _state = ConnectionState.Disconnected;

    public ConnectionItemViewModel(ConnectionProfile profile)
    {
        _profile = profile;
    }

    public ConnectionProfile Profile => _profile;

    public Guid Id => _profile.Id;

    public string Name => _profile.Name;

    public string HostLine => _profile.DisplayEndpoint;

    public string ProtocolLabel => _profile.ProtocolLabel;

    public ConnectionState State => _state;

    public bool IsConnected => _state == ConnectionState.Connected;

    public bool IsConnecting => _state == ConnectionState.Connecting;

    public bool HasError => _state == ConnectionState.Error;

    public string StatusLabel => _state switch
    {
        ConnectionState.Connected => "Connecté",
        ConnectionState.Connecting => "Connexion…",
        ConnectionState.Error => "Erreur",
        _ => "Déconnecté"
    };

    public IBrush StatusBrush => _state switch
    {
        ConnectionState.Connected => ConnectedBrush,
        ConnectionState.Connecting => ConnectingBrush,
        ConnectionState.Error => ErrorBrush,
        _ => DisconnectedBrush
    };

    public string AccessibleStatus => $"{Name}, {StatusLabel}, {HostLine}";

    public override string ToString() => $"{Name}, {StatusLabel}, {HostLine}";

    public void SetProfile(ConnectionProfile profile)
    {
        _profile = profile;
        OnPropertiesChanged(nameof(Profile), nameof(Name), nameof(HostLine), nameof(ProtocolLabel), nameof(AccessibleStatus));
    }

    public void SetState(ConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        OnPropertiesChanged(
            nameof(State),
            nameof(IsConnected),
            nameof(IsConnecting),
            nameof(HasError),
            nameof(StatusLabel),
            nameof(StatusBrush),
            nameof(AccessibleStatus));
    }
}
