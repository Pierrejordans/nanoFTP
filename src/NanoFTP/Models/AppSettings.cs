namespace NanoFTP.Models;

public sealed class AppSettings
{
    public int ParallelTransfers { get; set; } = 4;

    public Guid? LastConnectionId { get; set; }
}
