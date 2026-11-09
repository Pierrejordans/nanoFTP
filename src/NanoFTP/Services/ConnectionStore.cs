using System.Text.Json;
using System.Text.Json.Serialization;
using NanoFTP.Models;

namespace NanoFTP.Services;

public sealed class ConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SecretProtector _protector;

    public ConnectionStore()
        : this(GetDefaultStorageDirectory())
    {
    }

    public ConnectionStore(string storageDirectory)
    {
        StorageDirectory = storageDirectory;
        _protector = new SecretProtector(Path.Combine(storageDirectory, "secrets.key"));
    }

    public string StorageDirectory { get; }

    private string ConnectionsPath => Path.Combine(StorageDirectory, "connections.json");

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConnectionsPath))
        {
            return [];
        }

        await using var stream = File.OpenRead(ConnectionsPath);
        var document = await JsonSerializer.DeserializeAsync<ConnectionsDocument>(stream, JsonOptions, cancellationToken)
            ?? new ConnectionsDocument();

        return document.Connections
            .Where(connection => !string.IsNullOrWhiteSpace(connection.Host))
            .Select(connection => new ConnectionProfile
            {
                Id = connection.Id == Guid.Empty ? Guid.NewGuid() : connection.Id,
                Name = string.IsNullOrWhiteSpace(connection.Name) ? connection.Host : connection.Name,
                Host = connection.Host,
                Port = connection.Port is > 0 and <= 65535 ? connection.Port : 21,
                Username = connection.Username ?? string.Empty,
                Password = _protector.Unprotect(connection.PasswordCipher),
                UseTls = connection.UseTls,
                AllowInvalidCertificate = connection.AllowInvalidCertificate,
                StartPath = connection.StartPath ?? string.Empty
            })
            .ToList();
    }

    public async Task SaveAsync(IEnumerable<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(StorageDirectory);

        var document = new ConnectionsDocument
        {
            Connections = profiles.Select(profile => new StoredConnection
            {
                Id = profile.Id,
                Name = profile.Name,
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                PasswordCipher = _protector.Protect(profile.Password),
                UseTls = profile.UseTls,
                AllowInvalidCertificate = profile.AllowInvalidCertificate,
                StartPath = profile.StartPath
            }).ToList()
        };

        var temporaryPath = ConnectionsPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, ConnectionsPath, true);
        SecretProtector.TryRestrictPermissions(ConnectionsPath);
    }

    public static string GetDefaultStorageDirectory()
        => StoragePaths.DataDirectory;

    private sealed class ConnectionsDocument
    {
        public int Version { get; set; } = 1;

        public List<StoredConnection> Connections { get; set; } = [];
    }

    private sealed class StoredConnection
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Host { get; set; } = string.Empty;

        public int Port { get; set; } = 21;

        public string? Username { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PasswordCipher { get; set; }

        public bool UseTls { get; set; }

        public bool AllowInvalidCertificate { get; set; }

        public string? StartPath { get; set; }
    }
}
