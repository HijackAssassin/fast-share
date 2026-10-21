using System.IO;
using System.Windows;

namespace FastShare;

public partial class MainWindow
{
    // =======================================================================
    // Resuming a transfer that did not finish
    // =======================================================================
    //
    // Every transfer with someone is written down when it starts and crossed off when it ends
    // (finished, or cancelled on purpose). One still on the list next time you connect to that
    // person - the PC went off, the connection died for longer than the automatic reconnect
    // waits, FastShare was closed mid-way - is offered back: "Resume?". If you say yes, they are
    // asked too, and it runs only if both agree. Files already there are skipped and big files
    // continue from where they stopped, so only what is missing moves.

    /// <summary>Offered once per run each, so saying "later" does not bring it straight back.</summary>
    private readonly HashSet<string> _resumeOffered = [];

    /// <summary>Set while FastShare closes: the running transfer is paused, not cancelled and undone.</summary>
    private bool _pausingForClose;

    private DateTime _pendingSavedUtc;

    /// <summary>Writes a transfer down as it starts. Returns its id, carried through retries.</summary>
    private string TrackPending(Connection connection, bool download, string where, string description,
        List<ListRow>? rows, List<string>? paths)
    {
        var record = new PendingTransfer
        {
            PeerId = PeerIdOf(connection),
            PeerName = PeerNameOf(connection),
            Download = download,
            Where = where,
            Description = description,
            Rows = rows?.Select(r => new PendingItem(r.Name, r.RemotePath, r.IsDirectory, r.Size, r.Modified)).ToList(),
            Paths = paths,
            StartedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        if (record.PeerId is null) return record.Id;   // nobody to offer it back to

        _config.PendingTransfers.Add(record);
        while (_config.PendingTransfers.Count > 20) _config.PendingTransfers.RemoveAt(0);
        _config.Save();
        return record.Id;
    }

    /// <summary>
    /// How a transfer ended, for the list: kept when it could still usefully be resumed (the
    /// connection went, FastShare closed), crossed off otherwise (done, cancelled, or failed for
    /// a reason another attempt will not fix).
    /// </summary>
    private void SettlePending(string? id, bool keep, TransferStats stats)
    {
        if (id is null) return;
        var record = _config.PendingTransfers.FirstOrDefault(p => p.Id == id);
        if (record is null) return;

        if (keep)
        {
            record.DoneBytes = Math.Max(record.DoneBytes, stats.BytesDone);
            if (stats.TotalBytes > 0) record.TotalBytes = Math.Max(record.TotalBytes, stats.TotalBytes);
            record.UpdatedUtc = DateTime.UtcNow;
        }
        else
        {
            // Done with - and so is any older copy of the same transfer (started again by hand
            // rather than resumed), which would otherwise be offered for something finished.
            _config.PendingTransfers.RemoveAll(p => p.Id == record.Id ||
                (p.PeerId == record.PeerId && p.Download == record.Download && p.Where == record.Where && p.Description == record.Description));
        }
        _config.Save();
    }

    /// <summary>Every 10 seconds, how far the running transfers have got - so a crash loses little.</summary>
    private void SavePendingProgress()
    {
        if (DateTime.UtcNow - _pendingSavedUtc < TimeSpan.FromSeconds(10)) return;
        _pendingSavedUtc = DateTime.UtcNow;

        var changed = false;
        foreach (var (lane, stats) in new[] { (_downLane, _downStats), (_upLane, _upStats) })
        {
            if (lane.PendingId is not { } id || stats is null) continue;
            if (_config.PendingTransfers.FirstOrDefault(p => p.Id == id) is not { } record) continue;
            record.DoneBytes = stats.BytesDone;
            if (stats.TotalBytes > 0) record.TotalBytes = stats.TotalBytes;
            record.UpdatedUtc = DateTime.UtcNow;
            changed = true;
        }
        if (changed) _config.Save();
    }

    /// <summary>
    /// Just connected: anything unfinished with them? Waits a few seconds first, so an automatic
    /// resume after a short drop starts by itself rather than being asked about.
    /// </summary>
    private async Task OfferResumeAsync(Connection connection)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (!_connections.Contains(connection) || PeerIdOf(connection) is not { } peerId) return;

        foreach (var record in _config.PendingTransfers
                     .Where(p => string.Equals(p.PeerId, peerId, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            if (Lanes.Any(l => l.PendingId == record.Id) || _pendingRetries > 0) continue;   // already on its way
            if (!_resumeOffered.Add(record.Id)) continue;

            var name = PeerNameOf(connection);
            var verb = record.Download ? $"downloading {record.Description} from {name}" : $"sending {record.Description} to {name}";
            var left = record.TotalBytes > 0
                ? $"Up to {Format.Bytes(Math.Max(0, record.TotalBytes - record.DoneBytes))} of {Format.Bytes(record.TotalBytes)} left"
                : "Only what is missing moves";
            var ago = Format.Duration((DateTime.UtcNow - record.UpdatedUtc).TotalSeconds);

            var dialog = new ConfirmDialog(this, "Unfinished transfer",
                $"Resume {verb}?",
                $"{left}. It stopped {ago} ago.\n\nFiles already there are skipped and big files carry on from where they stopped. " +
                $"{name} is asked to agree first.",
                "Resume", "Discard");
            dialog.ShowDialog();

            if (!dialog.Answered) continue;   // closed with X: ask again next time
            if (dialog.DialogResult != true)
            {
                _config.PendingTransfers.Remove(record);
                _config.Save();
                ActivityLog.Note(record.Download ? TransferDirection.Download : TransferDirection.Upload,
                    $"Unfinished transfer with {name} discarded");
                continue;
            }

            // They have to agree too.
            var what = record.Download
                ? $"downloading {record.Description} from you ({left.ToLowerInvariant()[0]}{left[1..]})"
                : $"sending you {record.Description} ({left.ToLowerInvariant()[0]}{left[1..]})";
            TransferText.Text = $"Waiting for {name} to agree to resume…";
            var answer = connection.Writer is { } api ? await api.AskResumeAsync(what) : ApiClient.ResumeAnswer.NotSupported;
            TransferText.Text = "";

            if (answer is ApiClient.ResumeAnswer.Declined)
            {
                _config.PendingTransfers.Remove(record);
                _config.Save();
                MessageBox.Show(this, $"{name} did not agree to resume the last transfer.", "Not resumed",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                continue;
            }
            if (answer is ApiClient.ResumeAnswer.NoAnswer)
            {
                MessageBox.Show(this, $"{name} did not answer, so the transfer was not resumed. You will be asked again next time you connect.",
                    "Not resumed", MessageBoxButton.OK, MessageBoxImage.Information);
                _resumeOffered.Remove(record.Id);
                continue;
            }
            if (answer is ApiClient.ResumeAnswer.NotSupported)
                Diagnostics.Log("transfer", $"{name}'s FastShare cannot be asked about resuming (older version); resuming anyway");

            Diagnostics.Log("transfer", $"resuming {verb}");
            await StartResumeAsync(record, connection);
        }
    }

    private async Task StartResumeAsync(PendingTransfer record, Connection connection)
    {
        if (record.Download && record.Rows is { Count: > 0 } rows)
        {
            Directory.CreateDirectory(record.Where);
            var list = rows.Select(r => new ListRow
            {
                Name = r.Name, RemotePath = r.RemotePath, IsDirectory = r.IsDirectory, Size = r.Size, Modified = r.Modified,
            }).ToList();
            await DownloadToAsync(list, record.Where, connection.Id, 1, record.Id,
                (need, free, drive) => AskBothAboutSpaceAsync(connection, record, need, free, drive, weReceive: true));
        }
        else if (!record.Download && record.Paths is { Count: > 0 } paths)
        {
            var still = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            var items = await Task.Run(() => BuildUploadItems(still));
            await SendAsync(items, record.Where, connection.Id, 1, record.Id,
                (need, free, drive) => AskBothAboutSpaceAsync(connection, record, need, free, drive, weReceive: false));
        }
    }

    /// <summary>
    /// A resumed transfer does not fit on the receiving disk any more. Both PCs get the same
    /// choice: Retry (the receiver frees some space in File Explorer first) or Discard. It only
    /// tries again when both press Retry; then the space is measured again, and if it still does
    /// not fit, they are asked again. Either Discard ends it, and the other is told.
    /// Returns true to measure again, false to give up.
    /// </summary>
    private async Task<bool> AskBothAboutSpaceAsync(Connection connection, PendingTransfer record,
        long need, long free, string drive, bool weReceive)
    {
        var name = PeerNameOf(connection);
        var shortfall = Format.Bytes(Math.Max(0, need + (64L << 20) - free));

        var ours = weReceive
            ? $"This PC does not have enough free space for the rest of {record.Description}: it needs {Format.Bytes(need)} and {drive} has {Format.Bytes(free)} free (about {shortfall} short).\n\n" +
              "Free some up in File Explorer, then press Retry. The transfer continues once you and " + name + " have both pressed Retry."
            : $"{name} does not have enough free space for the rest of {record.Description}: it needs {Format.Bytes(need)} and they have {Format.Bytes(free)} free (about {shortfall} short).\n\n" +
              $"{name} can free some up in File Explorer. The transfer continues once you both press Retry.";
        var theirs = weReceive
            ? $"{DisplayNameOf(null, _config.Username ?? Environment.MachineName)} does not have enough free space to receive the rest of {record.Description} from you ({Format.Bytes(need)} needed, {Format.Bytes(free)} free). They are freeing some up. Press Retry to carry on once they have, or Discard to drop the transfer."
            : $"This PC does not have enough free space for the rest of {record.Description}: it needs {Format.Bytes(need)} and has {Format.Bytes(free)} free (about {shortfall} short). Free some up in File Explorer, then press Retry.";

        var api = connection.Writer;
        var id = api is null ? null : await api.AskSpaceAsync(theirs);

        var dialog = new ConfirmDialog(this, "Not enough space", "Not enough free space to resume", ours, "Retry", "Discard");

        // Their Discard closes ours straight away.
        var theyDiscarded = false;
        var watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var polling = false;
        watch.Tick += async (_, _) =>
        {
            if (id is null || polling) return;
            polling = true;
            try
            {
                if (await api!.QuestionStateAsync(id) == "no") { theyDiscarded = true; watch.Stop(); dialog.Close(); }
            }
            finally { polling = false; }
        };
        watch.Start();
        dialog.ShowDialog();
        watch.Stop();

        if (theyDiscarded)
        {
            MessageBox.Show(this, $"{name} chose Discard, so the transfer was dropped.", "Not resumed", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (dialog.DialogResult != true)
        {
            if (id is not null) await api!.WithdrawQuestionAsync(id);
            return false;
        }

        // We pressed Retry; now them (freeing space can take a while).
        if (id is null) return true;
        TransferText.Text = $"Waiting for {name} to press Retry…";
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(30);
        while (DateTime.UtcNow < until)
        {
            switch (await api!.QuestionStateAsync(id))
            {
                case "yes": TransferText.Text = "Checking the space again…"; return true;
                case "no":
                    MessageBox.Show(this, $"{name} chose Discard, so the transfer was dropped.", "Not resumed", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                case "unknown":
                    MessageBox.Show(this, $"Lost touch with {name} while waiting, so the transfer was not resumed. You will be asked again next time you connect.",
                        "Not resumed", MessageBoxButton.OK, MessageBoxImage.Information);
                    throw new TransferDiscardedException($"Not resumed - lost touch with {name}", keep: true);
            }
            await Task.Delay(1000);
        }
        await api!.WithdrawQuestionAsync(id);
        return false;
    }

    /// <summary>The other PC wants to resume a transfer with us: do we agree?</summary>
    private void OnResumeAsked(SessionStore.ResumeAsk ask)
    {
        var name = DisplayNameOf(ask.PeerId, ask.PeerName);
        if (ask.Kind == "space")
        {
            OnSpaceAsked(ask, name);
            return;
        }
        var dialog = new ConfirmDialog(this, "Resume a transfer?",
            $"{name} wants to resume an unfinished transfer",
            $"{name} is {ask.What}.\n\nOnly what is missing moves. Allow it?",
            "Allow", "Decline");
        NotifyIfAway(("Resume a transfer?", $"{name} wants to resume an unfinished transfer."));
        TrayIcon.FlashTaskbar(this);
        var yes = dialog.ShowDialog() == true;
        _server.Sessions.AnswerResume(ask.Id, yes);
        ActivityLog.Note(TransferDirection.Download,
            yes ? $"Agreed to resume the transfer with {name}" : $"Declined to resume the transfer with {name}");
    }

    /// <summary>
    /// The other PC found there is not enough room for a resumed transfer: Retry (once space
    /// has been freed) or Discard. Closes by itself if they discard first.
    /// </summary>
    private void OnSpaceAsked(SessionStore.ResumeAsk ask, string name)
    {
        var dialog = new ConfirmDialog(this, "Not enough space", $"Not enough free space to resume the transfer with {name}",
            ask.What, "Retry", "Discard");
        NotifyIfAway(("Not enough space", $"The transfer with {name} needs more free space."));
        TrayIcon.FlashTaskbar(this);

        var withdrawn = false;
        var watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watch.Tick += (_, _) =>
        {
            if (_server.Sessions.ResumeState(ask.Id) != "withdrawn") return;
            withdrawn = true;
            watch.Stop();
            dialog.Close();
        };
        watch.Start();
        var retry = dialog.ShowDialog() == true;
        watch.Stop();

        if (withdrawn)
        {
            MessageBox.Show(this, $"{name} chose Discard, so the transfer was dropped.", "Not resumed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _server.Sessions.AnswerResume(ask.Id, retry);
        ActivityLog.Note(TransferDirection.Download,
            retry ? $"Pressed Retry on the transfer with {name} (not enough space)" : $"Discarded the transfer with {name} (not enough space)");
    }
}
