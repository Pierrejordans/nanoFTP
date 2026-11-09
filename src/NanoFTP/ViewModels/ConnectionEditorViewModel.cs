using NanoFTP.Core;
using NanoFTP.Models;
using NanoFTP.Services;

namespace NanoFTP.ViewModels;

public sealed class ConnectionEditorViewModel : ObservableObject
{
    private readonly ConnectionProfile? _original;
    private readonly FtpService _ftpService = new();
    private CancellationTokenSource? _testCancellation;
    private string _name = string.Empty;
    private string _host = string.Empty;
    private string _portText = "21";
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _useTls;
    private bool _allowInvalidCertificate;
    private string _startPath = string.Empty;
    private string? _nameError;
    private string? _hostError;
    private string? _portError;
    private string? _testMessage;
    private bool? _testSucceeded;
    private bool _isTesting;

    public ConnectionEditorViewModel(ConnectionProfile? original)
    {
        _original = original;

        if (original is not null)
        {
            _name = original.Name;
            _host = original.Host;
            _portText = original.Port.ToString();
            _username = original.Username;
            _password = original.Password;
            _useTls = original.UseTls;
            _allowInvalidCertificate = original.AllowInvalidCertificate;
            _startPath = original.StartPath;
        }
    }

    public bool IsNew => _original is null;

    public bool CanDelete => !IsNew;

    public string WindowTitle => IsNew ? "Nouvelle connexion" : "Modifier la connexion";

    public string Heading => IsNew ? "Nouvelle connexion" : "Modifier la connexion";

    public string Description => IsNew
        ? "Ajoutez un serveur FTP à votre espace de travail."
        : "Ajustez les paramètres de ce serveur FTP.";

    public string PrimaryLabel => IsNew ? "Enregistrer et se connecter" : "Enregistrer";

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                NameError = null;
                InvalidateTest();
            }
        }
    }

    public string Host
    {
        get => _host;
        set
        {
            if (SetProperty(ref _host, value))
            {
                HostError = null;
                InvalidateTest();
            }
        }
    }

    public string PortText
    {
        get => _portText;
        set
        {
            if (SetProperty(ref _portText, value))
            {
                PortError = null;
                InvalidateTest();
            }
        }
    }

    public string Username
    {
        get => _username;
        set
        {
            if (SetProperty(ref _username, value))
            {
                InvalidateTest();
            }
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                InvalidateTest();
            }
        }
    }

    public bool UseTls
    {
        get => _useTls;
        set
        {
            if (SetProperty(ref _useTls, value))
            {
                if (!value)
                {
                    AllowInvalidCertificate = false;
                }

                OnPropertyChanged(nameof(AllowInvalidCertificate));
                InvalidateTest();
            }
        }
    }

    public bool AllowInvalidCertificate
    {
        get => _allowInvalidCertificate;
        set
        {
            if (SetProperty(ref _allowInvalidCertificate, value))
            {
                InvalidateTest();
            }
        }
    }

    public string StartPath
    {
        get => _startPath;
        set
        {
            if (SetProperty(ref _startPath, value))
            {
                InvalidateTest();
            }
        }
    }

    public string? NameError
    {
        get => _nameError;
        private set
        {
            if (SetProperty(ref _nameError, value))
            {
                OnPropertyChanged(nameof(HasNameError));
            }
        }
    }

    public string? HostError
    {
        get => _hostError;
        private set
        {
            if (SetProperty(ref _hostError, value))
            {
                OnPropertyChanged(nameof(HasHostError));
            }
        }
    }

    public string? PortError
    {
        get => _portError;
        private set
        {
            if (SetProperty(ref _portError, value))
            {
                OnPropertyChanged(nameof(HasPortError));
            }
        }
    }

    public bool HasNameError => !string.IsNullOrEmpty(NameError);

    public bool HasHostError => !string.IsNullOrEmpty(HostError);

    public bool HasPortError => !string.IsNullOrEmpty(PortError);

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (!SetProperty(ref _isTesting, value))
            {
                return;
            }

            OnPropertiesChanged(nameof(TestButtonLabel), nameof(CanRunTest));
        }
    }

    public string TestButtonLabel => IsTesting ? "Test en cours…" : "Tester";

    public bool CanRunTest => !IsTesting;

    public string? TestMessage
    {
        get => _testMessage;
        private set
        {
            if (!SetProperty(ref _testMessage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasTestMessage));
        }
    }

    public bool HasTestMessage => !string.IsNullOrWhiteSpace(TestMessage);

    public bool? TestSucceeded
    {
        get => _testSucceeded;
        private set
        {
            if (!SetProperty(ref _testSucceeded, value))
            {
                return;
            }

            OnPropertiesChanged(nameof(TestSucceededValue), nameof(TestFailedValue));
        }
    }

    public bool TestSucceededValue => TestSucceeded == true;

    public bool TestFailedValue => TestSucceeded == false;

    public bool Validate()
    {
        var valid = true;

        if (string.IsNullOrWhiteSpace(Name))
        {
            NameError = "Donnez un nom à cette connexion.";
            valid = false;
        }

        if (!ValidateHostAndPort())
        {
            valid = false;
        }

        return valid;
    }

    public ConnectionProfile BuildProfile()
    {
        var port = ParsePort();
        var host = NormalizeHost(Host, ref port, out var uriUsesTls);
        var startPath = string.IsNullOrWhiteSpace(StartPath)
            ? string.Empty
            : RemotePaths.Normalize(StartPath);

        return new ConnectionProfile
        {
            Id = _original?.Id ?? Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(Name) ? host : Name.Trim(),
            Host = host,
            Port = port,
            Username = Username.Trim(),
            Password = Password,
            UseTls = UseTls || uriUsesTls,
            AllowInvalidCertificate = (UseTls || uriUsesTls) && AllowInvalidCertificate,
            StartPath = startPath
        };
    }

    public async Task TestAsync()
    {
        if (IsTesting)
        {
            return;
        }

        if (!ValidateHostAndPort())
        {
            TestSucceeded = false;
            TestMessage = "Renseignez l'adresse et le port du serveur avant de tester.";
            return;
        }

        _testCancellation?.Cancel();
        _testCancellation?.Dispose();
        _testCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        IsTesting = true;
        TestSucceeded = null;
        TestMessage = "Connexion au serveur…";

        try
        {
            var count = await _ftpService.TestAsync(BuildProfile(), _testCancellation.Token);
            TestSucceeded = true;
            TestMessage = $"Connexion réussie · {Formatters.Count(count, "élément visible", "éléments visibles")}.";
        }
        catch (OperationCanceledException)
        {
            TestSucceeded = false;
            TestMessage = "Le test a été interrompu.";
        }
        catch (FtpOperationException exception)
        {
            TestSucceeded = false;
            TestMessage = exception.Message;
        }
        catch (Exception exception)
        {
            TestSucceeded = false;
            TestMessage = $"Le test a échoué. {exception.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    public void CancelTest()
    {
        _testCancellation?.Cancel();
        _testCancellation?.Dispose();
        _testCancellation = null;
    }

    private bool ValidateHostAndPort()
    {
        var valid = true;

        if (string.IsNullOrWhiteSpace(Host))
        {
            HostError = "L'adresse du serveur est requise.";
            valid = false;
        }

        if (!int.TryParse(PortText.Trim(), out var port) || port is < 1 or > 65535)
        {
            PortError = "Utilisez un port entre 1 et 65535.";
            valid = false;
        }

        return valid;
    }

    private int ParsePort()
        => int.TryParse(PortText.Trim(), out var port) && port is >= 1 and <= 65535 ? port : 21;

    private static string NormalizeHost(string rawHost, ref int port, out bool usesTls)
    {
        usesTls = false;
        var host = rawHost.Trim();

        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) &&
            uri.Scheme is "ftp" or "ftps" &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            usesTls = uri.Scheme == "ftps";
            if (!uri.IsDefaultPort)
            {
                port = uri.Port;
            }

            return uri.Host;
        }

        return host.TrimEnd('/');
    }

    private void InvalidateTest()
    {
        if (TestMessage is null && TestSucceeded is null)
        {
            return;
        }

        TestMessage = null;
        TestSucceeded = null;
    }
}

public sealed record ConnectionEditorResult(ConnectionProfile Profile, bool Delete, bool ConnectAfterSave);
