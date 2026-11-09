using NanoFTP.Core;

namespace NanoFTP.Models;

public enum TransferDirection
{
    Upload,
    Download
}

public enum TransferStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record TransferRequest(
    string FileName,
    string LocalPath,
    string RemotePath,
    long Size,
    TransferDirection Direction,
    ConnectionProfile Profile);

public sealed class TransferItem : ObservableObject
{
    private TransferStatus _status = TransferStatus.Queued;
    private double _progress;
    private string? _error;
    private bool _cancelRequested;
    private DateTime? _startedAt;
    private DateTime? _completedAt;

    public required TransferRequest Request { get; init; }

    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTime QueuedAt { get; init; } = DateTime.Now;

    public string FileName => Request.FileName;

    public string LocalPath => Request.LocalPath;

    public string RemotePath => Request.RemotePath;

    public long Size => Request.Size;

    public TransferDirection Direction => Request.Direction;

    public bool IsUpload => Direction == TransferDirection.Upload;

    public bool IsDownload => Direction == TransferDirection.Download;

    public string DirectionGlyph => IsUpload ? "↑" : "↓";

    public string DirectionLabel => IsUpload ? "Envoi" : "Téléchargement";

    public string TargetPath => IsUpload ? RemotePath : LocalPath;

    public string SizeText => Formatters.FileSize(Size);

    public TransferStatus Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value))
            {
                return;
            }

            OnPropertiesChanged(
                nameof(IsQueued),
                nameof(IsRunning),
                nameof(IsCompleted),
                nameof(IsFailed),
                nameof(IsCancelled),
                nameof(CanCancel),
                nameof(CanRetry),
                nameof(DisplayStatus),
                nameof(StatusDetail));
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (!SetProperty(ref _progress, value))
            {
                return;
            }

            OnPropertiesChanged(nameof(ProgressText), nameof(StatusDetail));
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value))
            {
                return;
            }

            OnPropertiesChanged(nameof(HasError), nameof(StatusDetail));
        }
    }

    public DateTime? StartedAt
    {
        get => _startedAt;
        private set => SetProperty(ref _startedAt, value);
    }

    public DateTime? CompletedAt
    {
        get => _completedAt;
        private set
        {
            if (!SetProperty(ref _completedAt, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CompletedTimeText));
        }
    }

    public bool IsQueued => Status == TransferStatus.Queued;

    public bool IsRunning => Status == TransferStatus.Running;

    public bool IsCompleted => Status == TransferStatus.Completed;

    public bool IsFailed => Status == TransferStatus.Failed;

    public bool IsCancelled => Status == TransferStatus.Cancelled;

    public bool CanCancel => (Status is TransferStatus.Queued or TransferStatus.Running) && !_cancelRequested;

    public bool CanRetry => Status is TransferStatus.Failed or TransferStatus.Cancelled;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool CancelRequested
    {
        get => _cancelRequested;
        private set
        {
            if (!SetProperty(ref _cancelRequested, value))
            {
                return;
            }

            OnPropertiesChanged(nameof(DisplayStatus), nameof(StatusDetail), nameof(CanCancel));
        }
    }

    public string DisplayStatus => Status switch
    {
        TransferStatus.Queued => "En attente",
        TransferStatus.Running => _cancelRequested ? "Annulation…" : "En cours",
        TransferStatus.Completed => "Terminé",
        TransferStatus.Failed => "Échec",
        TransferStatus.Cancelled => "Annulé",
        _ => string.Empty
    };

    public string StatusDetail
    {
        get
        {
            if (IsRunning && !_cancelRequested)
            {
                return $"{DirectionLabel} · {Progress:0} %";
            }

            if (IsFailed)
            {
                return Error ?? "Le transfert a échoué.";
            }

            if (IsCompleted)
            {
                return $"Terminé · {CompletedTimeText}";
            }

            if (IsCancelled)
            {
                return "Transfert annulé";
            }

            if (IsQueued)
            {
                return $"{DirectionLabel} · en file";
            }

            return DirectionLabel;
        }
    }

    public string ProgressText => $"{Progress:0} %";

    public string CompletedTimeText => CompletedAt is null ? string.Empty : Formatters.Time(CompletedAt.Value);

    public CancellationTokenSource Cancellation { get; } = new();

    public override string ToString() => $"{FileName}, {DisplayStatus}";

    public void Start()
    {
        Status = TransferStatus.Running;
        StartedAt = DateTime.Now;
        Progress = 0;
    }

    public void ReportProgress(double progress)
    {
        if (!IsRunning)
        {
            return;
        }

        Progress = Math.Clamp(progress, 0, 100);
    }

    public void Complete()
    {
        Progress = 100;
        CompletedAt = DateTime.Now;
        Status = TransferStatus.Completed;
    }

    public void Fail(string message)
    {
        Error = message;
        CompletedAt = DateTime.Now;
        Status = TransferStatus.Failed;
    }

    public void Cancel()
    {
        if (Status == TransferStatus.Queued)
        {
            FinishCancelled();
            return;
        }

        if (Status != TransferStatus.Running)
        {
            return;
        }

        CancelRequested = true;
        Cancellation.Cancel();
    }

    public void FinishCancelled()
    {
        CompletedAt = DateTime.Now;
        Status = TransferStatus.Cancelled;
        Error = null;
    }
}
