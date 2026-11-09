using NanoFTP.Core;

namespace NanoFTP.Models;

public sealed class FileEntry
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public bool IsDirectory { get; init; }

    public bool IsFile => !IsDirectory;

    public bool IsHidden { get; init; }

    public long Size { get; init; }

    public DateTime? Modified { get; init; }

    public string SizeText => IsDirectory ? "—" : Formatters.FileSize(Size);

    public string ModifiedText => Formatters.Date(Modified);

    public bool CanTransfer => !IsDirectory;

    public string TypeLabel => IsDirectory ? "Dossier" : "Fichier";

    public override string ToString() => Name;
}
