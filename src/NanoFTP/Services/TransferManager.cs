using System.Collections.ObjectModel;
using NanoFTP.Core;
using NanoFTP.Models;

namespace NanoFTP.Services;

public sealed class TransferCompletedEventArgs : EventArgs
{
    public TransferCompletedEventArgs(TransferItem item)
    {
        Item = item;
    }

    public TransferItem Item { get; }
}

public sealed class TransferManager : ObservableObject
{
    private readonly FtpService _ftpService;
    private readonly ObservableCollection<TransferItem> _pending = [];
    private readonly ObservableCollection<TransferItem> _completed = [];
    private int _parallelTransfers = 4;
    private int _running;
    private bool _shuttingDown;

    public TransferManager(FtpService ftpService)
    {
        _ftpService = ftpService;
        Pending = new ReadOnlyObservableCollection<TransferItem>(_pending);
        Completed = new ReadOnlyObservableCollection<TransferItem>(_completed);
    }

    public ReadOnlyObservableCollection<TransferItem> Pending { get; }

    public ReadOnlyObservableCollection<TransferItem> Completed { get; }

    public IReadOnlyList<int> ParallelOptions { get; } = [1, 2, 3, 4, 5, 6, 8];

    public int ParallelTransfers
    {
        get => _parallelTransfers;
        set
        {
            var clamped = Math.Clamp(value, 1, 8);
            if (!SetProperty(ref _parallelTransfers, clamped))
            {
                return;
            }

            ParallelismChanged?.Invoke(this, EventArgs.Empty);
            Pump();
        }
    }

    public int PendingCount => _pending.Count;

    public int CompletedCount => _completed.Count;

    public int ActiveCount => _running;

    public int WaitingCount => _pending.Count(item => item.IsQueued);

    public bool HasPending => _pending.Count > 0;

    public bool HasCompleted => _completed.Count > 0;

    public bool ShowPendingEmpty => _pending.Count == 0;

    public bool ShowCompletedEmpty => _completed.Count == 0;

    public string PendingHeader => PendingCount == 0 ? "EN ATTENTE" : $"EN ATTENTE · {PendingCount}";

    public string CompletedHeader => CompletedCount == 0 ? "TRANSFÉRÉS" : $"TRANSFÉRÉS · {CompletedCount}";

    public string QueueSummary
    {
        get
        {
            if (ActiveCount > 0)
            {
                return $"{ActiveCount} en cours · {WaitingCount} en attente";
            }

            return PendingCount == 0 ? "Aucun transfert en attente" : $"{WaitingCount} en attente";
        }
    }

    public event EventHandler<TransferCompletedEventArgs>? TransferFinished;

    public event EventHandler? ParallelismChanged;

    public void Enqueue(IEnumerable<TransferRequest> requests)
    {
        var added = 0;

        foreach (var request in requests)
        {
            _pending.Add(new TransferItem { Request = request });
            added++;
        }

        if (added == 0)
        {
            return;
        }

        NotifyQueueChanged();
        Pump();
    }

    public void Cancel(TransferItem item)
    {
        if (!_pending.Contains(item))
        {
            return;
        }

        if (item.IsQueued)
        {
            item.Cancel();
            MoveToCompleted(item);
            NotifyQueueChanged();
            Pump();
            return;
        }

        item.Cancel();
        OnPropertyChanged(nameof(QueueSummary));
    }

    public void Retry(TransferItem item)
    {
        if (!_completed.Contains(item) || !item.CanRetry)
        {
            return;
        }

        _completed.Remove(item);
        _pending.Add(new TransferItem { Request = item.Request });
        NotifyQueueChanged();
        Pump();
    }

    public void ClearCompleted()
    {
        if (_completed.Count == 0)
        {
            return;
        }

        _completed.Clear();
        NotifyQueueChanged();
    }

    public void CancelAll()
    {
        _shuttingDown = true;

        foreach (var item in _pending.ToList())
        {
            Cancel(item);
        }
    }

    private void Pump()
    {
        if (_shuttingDown)
        {
            return;
        }

        while (_running < ParallelTransfers)
        {
            var next = _pending.FirstOrDefault(item => item.IsQueued);
            if (next is null)
            {
                break;
            }

            _running++;
            _ = RunAsync(next);
        }

        NotifyQueueChanged();
    }

    private async Task RunAsync(TransferItem item)
    {
        item.Start();
        NotifyQueueChanged();

        var progress = new Progress<double>(item.ReportProgress);

        try
        {
            if (item.IsUpload)
            {
                await _ftpService.UploadAsync(
                    item.Request.Profile,
                    item.LocalPath,
                    item.RemotePath,
                    progress,
                    item.Cancellation.Token);
            }
            else
            {
                await _ftpService.DownloadAsync(
                    item.Request.Profile,
                    item.RemotePath,
                    item.LocalPath,
                    progress,
                    item.Cancellation.Token);
            }

            if (item.CancelRequested)
            {
                item.FinishCancelled();
            }
            else
            {
                item.Complete();
            }
        }
        catch (OperationCanceledException)
        {
            item.FinishCancelled();
        }
        catch (Exception exception)
        {
            item.Fail(GetMessage(exception));
        }
        finally
        {
            item.Cancellation.Dispose();
            _running = Math.Max(0, _running - 1);
            MoveToCompleted(item);
            NotifyQueueChanged();
            TransferFinished?.Invoke(this, new TransferCompletedEventArgs(item));
            Pump();
        }
    }

    private void MoveToCompleted(TransferItem item)
    {
        _pending.Remove(item);
        _completed.Insert(0, item);
    }

    private void NotifyQueueChanged()
    {
        OnPropertiesChanged(
            nameof(PendingCount),
            nameof(CompletedCount),
            nameof(ActiveCount),
            nameof(WaitingCount),
            nameof(HasPending),
            nameof(HasCompleted),
            nameof(ShowPendingEmpty),
            nameof(ShowCompletedEmpty),
            nameof(PendingHeader),
            nameof(CompletedHeader),
            nameof(QueueSummary));
    }

    private static string GetMessage(Exception exception)
        => exception is FtpOperationException ftpException
            ? ftpException.Message
            : $"Une erreur inattendue est survenue. {exception.Message}";
}
