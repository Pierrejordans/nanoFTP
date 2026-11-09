namespace NanoFTP.Models;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 21;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool UseTls { get; set; }

    public bool AllowInvalidCertificate { get; set; }

    public string StartPath { get; set; } = string.Empty;

    public string DisplayEndpoint
    {
        get
        {
            var host = string.IsNullOrWhiteSpace(Host) ? "serveur inconnu" : Host.Trim();
            return Port == 21 ? host : $"{host}:{Port}";
        }
    }

    public string ProtocolLabel => UseTls ? "FTPS" : "FTP";

    public ConnectionProfile Snapshot() => (ConnectionProfile)MemberwiseClone();
}
