using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace FastShare;

/// <summary>A transfer waiting its turn or running, as the queue panel on the Activity Log tab shows it.</summary>
public sealed class QueuedJob : INotifyPropertyChanged
{
    public required TransferDirection Direction { get; init; }
    public required string Description { get; init; }
    public required Func<Task> Run { get; init; }

    /// <summary>Who the transfer is with, when it is with someone (not a local copy).</summary>
    public Connection? With { get; init; }

    /// <summary>Its row on the Activity Log while it waits.</summary>
    public SessionRow? WaitingRow { get; set; }

    /// <summary>The place in line last told to the other PC (0 = nothing told yet).</summary>
    public int AnnouncedPosition { get; set; }

    private bool _running;
    public bool Running
    {
        get => _running;
        set { _running = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label))); }
    }

    public string Label => $"{(Direction == TransferDirection.Download ? "↓" : "↑")}  {Description}" +
                           (Running ? "   — running" : "   — waiting");

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow
{
    // =======================================================================
    // Transfer queue panel (Activity Log tab)
    // =======================================================================
    //
    // Each direction runs one transfer at a time; the rest wait. This lists what is running and
    // what is waiting, and lets a waiting one be moved up or down or dropped, and a running one
    // cancelled on its own - Cancel on the status bar stops everything at once.

    private readonly ObservableCollection<QueuedJob> _queueView = [];

    private void RefreshQueueView()
    {
        QueueList.ItemsSource ??= _queueView;
        _queueView.Clear();
        foreach (var lane in Lanes)
        {
            if (lane.Current is { } current) _queueView.Add(current);
            foreach (var job in lane.Queue) _queueView.Add(job);
        }
        QueuePanel.Visibility = _queueView.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateQueueButtons();
        SyncWaitingRows();
    }

    /// <summary>
    /// Every waiting job gets a row on the Activity Log saying its place in line, where the
    /// progress bar will be once it runs; the row goes when it starts (it gets its own live row)
    /// or is removed. The other PC is told its place too, so their screen says "queued, position
    /// 1" instead of nothing until it starts.
    /// </summary>
    private void SyncWaitingRows()
    {
        foreach (var lane in Lanes)
        {
            // Nothing running in this lane: the job just added starts this instant, so it is not
            // waiting for anything. Announcing it as "queued, position 1" left the other PC
            // showing that for the minutes it then spent getting its file list ready.
            if (!lane.Pumping) continue;

            for (var i = 0; i < lane.Queue.Count; i++)
            {
                var job = lane.Queue[i];
                var position = i + 1;
                if (job.WaitingRow is null)
                {
                    job.WaitingRow = AddTransferRow(new HistoryEntry
                    {
                        WhenUtc = DateTime.UtcNow,
                        Sent = job.Direction == TransferDirection.Upload,
                        Peer = job.With is { } with ? PeerNameOf(with) : "you",
                    });
                }
                if (job.WaitingRow.QueuePosition != position)
                {
                    job.WaitingRow.QueuePosition = position;
                    job.WaitingRow.Refresh();
                }
                if (job.With?.Writer is { } api && job.AnnouncedPosition != position)
                {
                    job.AnnouncedPosition = position;
                    _ = api.AnnounceQueuedAsync(job.Direction == TransferDirection.Download, position);
                }
            }

            // Out of the queue: running now (its live row takes over) or removed.
            if (lane.Current is { WaitingRow: { } started } running)
            {
                _transfers.Remove(started);
                running.WaitingRow = null;
            }
        }
        UpdateTransferEmpty();
    }

    /// <summary>A job taken out of the queue without running: its row goes, and they are told.</summary>
    private void Dequeued(QueuedJob job)
    {
        if (job.WaitingRow is { } row) _transfers.Remove(row);
        job.WaitingRow = null;
        if (job.AnnouncedPosition > 0 && job.With?.Writer is { } api)
            _ = api.AnnounceQueuedAsync(job.Direction == TransferDirection.Download, 0);
        UpdateTransferEmpty();
    }

    private void OnQueueSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateQueueButtons();

    private void UpdateQueueButtons()
    {
        var job = QueueList.SelectedItem as QueuedJob;
        var lane = job is null ? null : LaneOf(job);
        var index = lane is null || job!.Running ? -1 : lane.Queue.IndexOf(job);
        QueueUpButton.IsEnabled = index > 0;
        QueueDownButton.IsEnabled = index >= 0 && index < lane!.Queue.Count - 1;
        QueueCancelButton.IsEnabled = job is not null;
        QueueCancelButton.Content = job?.Running == true ? "Cancel" : "Remove";
    }

    private TransferLane? LaneOf(QueuedJob job) =>
        Lanes.FirstOrDefault(l => ReferenceEquals(l.Current, job) || l.Queue.Contains(job));

    private void Move(int by)
    {
        if (QueueList.SelectedItem is not QueuedJob job || job.Running || LaneOf(job) is not { } lane) return;
        var index = lane.Queue.IndexOf(job);
        var to = index + by;
        if (index < 0 || to < 0 || to >= lane.Queue.Count) return;
        lane.Queue.RemoveAt(index);
        lane.Queue.Insert(to, job);
        RefreshQueueView();
        QueueList.SelectedItem = job;
    }

    /// <summary>Right-click on a finished transfer that did not fully succeed: run it again.</summary>
    private void OnTransferMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        // The ListView always keeps a (possibly empty) menu: with none at all, WPF stops
        // raising this event and the menu could never appear again.
        // Waiting in our queue: take it out.
        if (TransferLog.SelectedItem is SessionRow { IsQueued: true } waiting &&
            Lanes.SelectMany(l => l.Queue.Select(j => (Lane: l, Job: j)))
                 .FirstOrDefault(x => ReferenceEquals(x.Job.WaitingRow, waiting)) is { Job: not null } found)
        {
            var remove = new System.Windows.Controls.MenuItem { Header = "Remove from queue" };
            remove.Click += (_, _) =>
            {
                found.Lane.Queue.Remove(found.Job);
                Dequeued(found.Job);
                ActivityLog.Note(found.Job.Direction, $"Removed from the queue: {found.Job.Description}");
                RefreshQueueView();
                UpdateButtons();
            };
            TransferLog.ContextMenu = new System.Windows.Controls.ContextMenu { Items = { remove } };
            return;
        }

        if (TransferLog.SelectedItem is SessionRow { Live: true } live)
        {
            // Ours: cancel its lane. Theirs (they are sending to us, or pulling from us): ask
            // them to stop. Either side can cancel either way.
            var lane = Lanes.FirstOrDefault(l => ReferenceEquals(l.ActiveRow, live) && l.Cts is not null);
            var flowKey = _flowRows.FirstOrDefault(kv => ReferenceEquals(kv.Value, live)).Key;
            if (lane is null && flowKey is null)
            {
                e.Handled = true;
                return;
            }

            var cancel = new System.Windows.Controls.MenuItem { Header = "Cancel transfer" };
            cancel.Click += (_, _) =>
            {
                TransferText.Text = "Cancelling…";
                if (lane is not null) { try { lane.Cts?.Cancel(); } catch { } }
                else CancelPeerFlow(flowKey!);
            };
            TransferLog.ContextMenu = new System.Windows.Controls.ContextMenu { Items = { cancel } };
            return;
        }

        if (TransferLog.SelectedItem is not SessionRow { Live: false, Retry: { } retry } row)
        {
            e.Handled = true;
            return;
        }

        var item = new System.Windows.Controls.MenuItem { Header = "Retry failed files" };
        item.Click += async (_, _) =>
        {
            row.Retry = null;   // the retry gets its own row
            await retry();
        };
        TransferLog.ContextMenu = new System.Windows.Controls.ContextMenu { Items = { item } };
    }

    private void OnQueueUpClick(object sender, RoutedEventArgs e) => Move(-1);
    private void OnQueueDownClick(object sender, RoutedEventArgs e) => Move(+1);

    private void OnQueueCancelClick(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not QueuedJob job || LaneOf(job) is not { } lane) return;
        if (job.Running)
        {
            TransferText.Text = "Cancelling…";
            try { lane.Cts?.Cancel(); } catch { }
        }
        else
        {
            lane.Queue.Remove(job);
            Dequeued(job);
            ActivityLog.Note(job.Direction, $"Removed from the queue: {job.Description}");
            RefreshQueueView();
            UpdateButtons();
        }
    }
}
