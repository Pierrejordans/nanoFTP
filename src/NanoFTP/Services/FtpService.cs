using System.Net.Sockets;
using System.Security.Authentication;
using FluentFTP;
using FluentFTP.Exceptions;
using NanoFTP.Models;

namespace NanoFTP.Services;

public sealed class FtpOperationException : Exception
{
    public FtpOperationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class FtpSession : IAsyncDisposable
{
    internal FtpSession(AsyncFtpClient client, ConnectionProfile profile, string initialPath)
    {
        Client = client;
        Profile = profile;
        InitialPath = initialPath;
    }

    internal AsyncFtpClient Client { get; }

    public ConnectionProfile Profile { get; }

    public string InitialPath { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Client.IsConnected)
            {
                await Client.Disconnect(CancellationToken.None);
            }
        }
        catch
        {
            // La session est de toute façon fermée juste après.
        }
        finally
        {
            Client.Dispose();
        }
    }
}

public sealed class FtpService
{
    public async Task<FtpSession> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        var client = CreateClient(profile);

        try
        {
            await client.Connect(cancellationToken);

            var initialPath = string.IsNullOrWhiteSpace(profile.StartPath)
                ? await client.GetWorkingDirectory(cancellationToken)
                : RemotePaths.Normalize(profile.StartPath);

            return new FtpSession(client, profile.Snapshot(), initialPath);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            client.Dispose();
            throw Translate(exception, profile, isConnection: true);
        }
    }

    public async Task<IReadOnlyList<FileEntry>> GetListingAsync(
        FtpSession session,
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalizedPath = RemotePaths.Normalize(path);
            var items = await session.Client.GetListing(normalizedPath, cancellationToken);

            return items
                .Where(item => item.Name is not "." and not "..")
                .Where(item => item.Type is FtpObjectType.File or FtpObjectType.Directory or FtpObjectType.Link)
                .Select(item => new FileEntry
                {
                    Name = item.Name,
                    FullPath = RemotePaths.Normalize(item.FullName),
                    IsDirectory = item.Type == FtpObjectType.Directory,
                    Size = Math.Max(0, item.Size),
                    Modified = item.Modified == default ? null : item.Modified
                })
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Translate(exception, session.Profile, isConnection: false, isListing: true);
        }
    }

    public async Task<IReadOnlyList<FileEntry>> GetTreeAsync(
        FtpSession session,
        string path,
        CancellationToken cancellationToken = default)
    {
        var root = RemotePaths.Normalize(path);
        var pendingDirectories = new Queue<string>();
        var visitedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<FileEntry>();

        pendingDirectories.Enqueue(root);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Dequeue();

            if (!visitedDirectories.Add(directory))
            {
                continue;
            }

            var children = await GetListingAsync(session, directory, cancellationToken);
            foreach (var child in children)
            {
                entries.Add(child);

                if (child.IsDirectory)
                {
                    pendingDirectories.Enqueue(child.FullPath);
                }
            }
        }

        return entries;
    }

    public async Task CreateRemoteDirectoryAsync(
        FtpSession session,
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await session.Client.CreateDirectory(RemotePaths.Normalize(path), true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Translate(exception, session.Profile, isConnection: false);
        }
    }

    public async Task UploadAsync(
        ConnectionProfile profile,
        string localPath,
        string remotePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(profile);

        try
        {
            await client.Connect(cancellationToken);
            var ftpProgress = new Progress<FtpProgress>(value => progress?.Report(value.Progress));
            var status = await client.UploadFile(
                localPath,
                RemotePaths.Normalize(remotePath),
                FtpRemoteExists.Overwrite,
                createRemoteDir: true,
                FtpVerify.None,
                ftpProgress,
                cancellationToken);

            if (status != FtpStatus.Success)
            {
                throw new FtpOperationException("Le serveur n'a pas confirmé l'envoi du fichier.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FtpOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Translate(exception, profile, isConnection: false);
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    public async Task DownloadAsync(
        ConnectionProfile profile,
        string remotePath,
        string localPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(profile);

        try
        {
            var localDirectory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrWhiteSpace(localDirectory))
            {
                Directory.CreateDirectory(localDirectory);
            }

            await client.Connect(cancellationToken);
            var ftpProgress = new Progress<FtpProgress>(value => progress?.Report(value.Progress));
            var status = await client.DownloadFile(
                localPath,
                RemotePaths.Normalize(remotePath),
                FtpLocalExists.Overwrite,
                FtpVerify.None,
                ftpProgress,
                cancellationToken);

            if (status != FtpStatus.Success)
            {
                throw new FtpOperationException("Le téléchargement n'a pas pu être terminé.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FtpOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Translate(exception, profile, isConnection: false);
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    public async Task<int> TestAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        await using var session = await ConnectAsync(profile, cancellationToken);
        var entries = await GetListingAsync(session, session.InitialPath, cancellationToken);
        return entries.Count;
    }

    private static AsyncFtpClient CreateClient(ConnectionProfile profile)
    {
        var isAnonymous = string.IsNullOrWhiteSpace(profile.Username);
        var username = isAnonymous ? "anonymous" : profile.Username.Trim();
        var password = isAnonymous
            ? "anonymous@nanoftp.local"
            : profile.Password;

        var config = new FtpConfig
        {
            EncryptionMode = profile.UseTls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            ValidateAnyCertificate = profile.AllowInvalidCertificate,
            ConnectTimeout = 15_000,
            ReadTimeout = 30_000,
            WriteTimeout = 30_000,
            DataConnectionConnectTimeout = 15_000,
            DataConnectionReadTimeout = 30_000,
            DataConnectionWriteTimeout = 30_000,
            LogToConsole = false,
            RetryAttempts = 0
        };

        return new AsyncFtpClient(profile.Host.Trim(), username, password, profile.Port, config);
    }

    private static async Task DisconnectQuietlyAsync(AsyncFtpClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                await client.Disconnect(CancellationToken.None);
            }
        }
        catch
        {
            // Rien à faire : le client va être libéré.
        }
    }

    private static FtpOperationException Translate(
        Exception exception,
        ConnectionProfile profile,
        bool isConnection,
        bool isListing = false)
    {
        if (exception is FtpOperationException operationException)
        {
            return operationException;
        }

        if (exception is FtpAuthenticationException ||
            (exception is FtpCommandException command && command.CompletionCode == "530"))
        {
            return new FtpOperationException(
                "Identifiants refusés par le serveur. Vérifiez le nom d'utilisateur et le mot de passe.",
                exception);
        }

        if (exception is FtpInvalidCertificateException or AuthenticationException ||
            exception.InnerException is AuthenticationException)
        {
            return new FtpOperationException(
                "La connexion TLS a échoué. Vérifiez le certificat du serveur ou autorisez un certificat non vérifié.",
                exception);
        }

        if (exception is FtpSecurityNotAvailableException)
        {
            return new FtpOperationException(
                "Ce serveur ne propose pas de connexion TLS. Désactivez FTPS pour vous connecter.",
                exception);
        }

        if (exception is SocketException or FtpMissingSocketException or TimeoutException ||
            exception.InnerException is SocketException)
        {
            return new FtpOperationException(
                $"Impossible de joindre {profile.DisplayEndpoint}. Vérifiez l'adresse, le port et le réseau.",
                exception);
        }

        if (isListing)
        {
            return new FtpOperationException(
                $"Le dossier distant n'a pas pu être ouvert. {exception.Message}",
                exception);
        }

        if (exception is FtpCommandException ftpCommand)
        {
            return new FtpOperationException(
                $"Le serveur a refusé l'opération : {ftpCommand.Message}",
                exception);
        }

        return new FtpOperationException(
            isConnection
                ? $"La connexion à {profile.DisplayEndpoint} a échoué. {exception.Message}"
                : $"Le transfert a échoué. {exception.Message}",
            exception);
    }
}

public static class RemotePaths
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var raw = path.Trim().Replace('\\', '/');
        var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();

        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.Pop();
                }

                continue;
            }

            stack.Push(segment);
        }

        return "/" + string.Join("/", stack.Reverse());
    }

    public static string Combine(string directory, string name)
        => Normalize($"{Normalize(directory).TrimEnd('/')}/{name}");

    public static string? Parent(string path)
    {
        var normalized = Normalize(path);
        if (normalized == "/")
        {
            return null;
        }

        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "/" : normalized[..index];
    }
}
