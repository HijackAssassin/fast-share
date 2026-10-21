using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FastShare;

public partial class MainWindow
{
    // =======================================================================
    // Activity log and statistics
    // =======================================================================

    /// <summary>Every line ever logged this run; the list on screen is a filtered view of it.</summary>
    private readonly List<LogEntry> _allLog = [];
    private LogKind _logView = LogKind.Transfer;

    private void OnActivityAppended(LogEntry entry)
    {
        // Transfer workers call this from pool threads; hop to the UI thread before touching
        // the bound collection.
        Dispatcher.BeginInvoke(() =>
        {
            _allLog.Add(entry);
            while (_allLog.Count > MaxLogEntries) _allLog.RemoveAt(0);

            // Errors get a dialog rather than a line nobody looks at. They were being written
            // into the status bar, which is the one strip reserved for live transfer numbers -
            // so a failure both went unnoticed and shoved the figures off screen.
            if (entry.IsError)
            {
                ShowError(entry.Text);
                return;
            }

            // Everything non-error goes in the running log. It is the "Events" view now -
            // transfers have their own grid, so filtering this one by kind would simply hide
            // notes like "Cancelled" with nowhere else to show them.
            _log.Add(entry);
            while (_log.Count > MaxLogEntries) _log.RemoveAt(0);
            if (LogList.IsVisible) LogList.ScrollIntoView(entry);
        });
    }

    private bool _errorDialogOpen;
    private readonly List<string> _pendingErrors = [];

    /// <summary>
    /// Shows errors in a dialog, coalescing any that arrive while one is already up. A failed
    /// transfer can produce hundreds at once, and a hundred modal boxes is worse than the bug.
    /// </summary>
    private void ShowError(string message)
    {
        // Nobody is at the PC when it is set to shut down by itself, and an error box waiting to
        // be clicked must not be what is left on screen. The errors are still in Events and the
        // Failed view.
        if (ShutdownWhenDone) return;

        _pendingErrors.Add(message);
        if (_errorDialogOpen) return;

        _errorDialogOpen = true;

        // Let the burst land before showing anything, so one dialog covers the lot.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            // The same failure can be reported twice (once as it happens, once in the summary).
            var errors = _pendingErrors.Distinct().ToList();
            _pendingErrors.Clear();

            var shown = string.Join(Environment.NewLine, errors.Take(12).Select(t => "  • " + t));
            if (errors.Count > 12)
                shown += $"{Environment.NewLine}  …and {errors.Count - 12:N0} more";

            var heading = errors.Count == 1 ? "Something went wrong" : $"{errors.Count:N0} problems";
            MessageBox.Show(this, shown, heading, MessageBoxButton.OK, MessageBoxImage.Warning);

            _errorDialogOpen = false;

            // More may have arrived while the dialog was up. Shown as they are - passing the
            // last one back through ShowError queued it a second time, so it appeared twice.
            if (_pendingErrors.Count > 0)
            {
                var more = _pendingErrors.ToList();
                _pendingErrors.Clear();
                foreach (var message in more.Take(more.Count - 1)) _pendingErrors.Add(message);
                ShowError(more[^1]);
            }
        });
    }

    private void OnLogViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogViewPicker is null || TransferLog is null || FailedLog is null) return;

        var view = LogViewPicker.SelectedIndex;
        _logView = view == 1 ? LogKind.Network : LogKind.Transfer;

        TransferLog.Visibility = view == 0 ? Visibility.Visible : Visibility.Collapsed;
        LogList.Visibility = view == 1 ? Visibility.Visible : Visibility.Collapsed;
        FailedLog.Visibility = view == 2 ? Visibility.Visible : Visibility.Collapsed;
        FailedLog.ItemsSource ??= _failedFiles;
        UpdateTransferEmpty();

        RefilterLog();
    }

    /// <summary>One file that did not make it, for the Activity Log's "Failed" view.</summary>
    public sealed record FailedFileRow(DateTime WhenUtc, string Peer, string Direction, string File, string Reason, SessionRow Row)
    {
        public string WhenText => WhenUtc.ToLocalTime().ToString("HH:mm:ss");
    }

    private readonly ObservableCollection<FailedFileRow> _failedFiles = [];

    /// <summary>Lists a transfer's failures in the "Failed" view, newest first.</summary>
    private void AddFailedFiles(SessionRow row, IReadOnlyList<string> errors)
    {
        foreach (var error in errors.Reverse())
        {
            var split = error.LastIndexOf(": ", StringComparison.Ordinal);
            _failedFiles.Insert(0, new FailedFileRow(DateTime.UtcNow, row.Peer,
                TransferWording.Direction(row.Entry.Sent, row.Entry.PeerDriven, false),
                split > 0 ? error[..split] : error, split > 0 ? error[(split + 2)..] : "", row));
        }
        while (_failedFiles.Count > 2000) _failedFiles.RemoveAt(_failedFiles.Count - 1);
    }

    private void OnFailedRowActivated(object sender, MouseButtonEventArgs e)
    {
        if (FailedLog.SelectedItem is FailedFileRow failed) OpenTransferDetails(failed.Row, showFailed: true);
    }

    private void RefilterLog()
    {
        _log.Clear();
        foreach (var entry in _allLog.Where(entry => !entry.IsError)) _log.Add(entry);
    }

    private void UpdateTransferEmpty()
    {
        var view = LogViewPicker?.SelectedIndex ?? 0;
        TransferLogEmpty.Text = view == 2 ? "No files have failed this session." : "Nothing has moved yet this session.";
        TransferLogEmpty.Visibility =
            (view == 0 && _transfers.Count == 0) || (view == 2 && _failedFiles.Count == 0)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>
    /// Starts a row on the Session tab. Newest first, because the thing you just did is the
    /// thing you are looking for.
    /// </summary>
    private SessionRow AddTransferRow(HistoryEntry entry)
    {
        var row = new SessionRow { Entry = entry };
        _transfers.Insert(0, row);
        while (_transfers.Count > 400) _transfers.RemoveAt(_transfers.Count - 1);
        UpdateTransferEmpty();
        return row;
    }

    /// <summary>Rewrites a live row's figures in place.</summary>
    private static void UpdateRow(SessionRow row, long bytes, long files, long folders,
        double seconds, double rate, long total = 0)
    {
        row.Entry.Bytes = bytes;
        row.Entry.Files = files;
        row.Entry.Folders = folders;
        row.Entry.Seconds = seconds;
        row.LiveRate = rate;
        if (total > 0) row.Total = total;
        row.Sample(seconds, bytes);
        row.Refresh();
    }

    /// <summary>
    /// The columns' widths as designed, used as proportions: the list is fitted to the window
    /// so it never needs a sideways scroll bar (they added up to 1,550 px, wider than the
    /// window). The progress column keeps its size; its bar is a fixed width.
    /// </summary>
    private double[]? _transferColumnDesign;

    private void OnTransferLogSizeChanged(object sender, SizeChangedEventArgs e) => FitTransferColumns();

    private void FitTransferColumns()
    {
        if (TransferLog.View is not GridView view || TransferLog.ActualWidth <= 0) return;
        _transferColumnDesign ??= view.Columns.Select(c => double.IsNaN(c.Width) ? 100 : c.Width).ToArray();

        // Room for the vertical scroll bar and the border, so the last column is not clipped.
        var available = TransferLog.ActualWidth - SystemParameters.VerticalScrollBarWidth - 8;
        var fixedIndex = view.Columns.ToList().FindIndex(c => Equals(c.Header, "Progress"));
        var fixedWidth = fixedIndex >= 0 ? _transferColumnDesign[fixedIndex] : 0;
        var flexible = _transferColumnDesign.Where((_, i) => i != fixedIndex).Sum();
        var scale = Math.Max(0.4, (available - fixedWidth) / flexible);

        for (var i = 0; i < view.Columns.Count; i++)
            view.Columns[i].Width = i == fixedIndex ? fixedWidth : Math.Floor(_transferColumnDesign[i] * scale);
    }

    private static void FinishRow(SessionRow row, string? problem)
    {
        row.Live = false;
        row.Entry.Problem = problem;
        row.Refresh();
    }

    /// <summary>Opens the full file list behind a transfer row, when one was kept.</summary>
    private void OnTransferRowActivated(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click that actually landed on a row. Double-clicking the empty area
        // below the list used to open whatever happened to still be selected, which is a
        // startling thing to get from clicking nothing.
        if (!IsOverRow(e.OriginalSource as DependencyObject)) return;
        if (TransferLog.SelectedItem is not SessionRow row || (row.Details is null && row.Entry.Failed is null)) return;

        OpenTransferDetails(row);
    }

    /// <summary>Enter opens the selected transfer's details, like a double-click.</summary>
    private void OnTransferLogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || TransferLog.SelectedItem is not SessionRow row) return;
        e.Handled = true;
        OpenTransferDetails(row);
    }

    /// <summary>Clears the selection when the empty area is clicked, the way a file list does.</summary>
    private void OnTransferLogClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsOverRow(e.OriginalSource as DependencyObject))
            TransferLog.SelectedItem = null;
    }

    private void OpenTransferDetails(SessionRow row, bool showFailed = false)
    {
        if (row.Details is null && row.Entry.Failed is null) return;
        new DetailsDialog(this, $"{row.Arrow} · {row.Peer}", row.Details ?? [], row.Entry.Failed,
            showFailed || (row.Details is null)).ShowDialog();
    }

    /// <summary>Opens the file list behind a History row, the same way the Session tab does.</summary>
    private void OnHistoryRowActivated(object sender, MouseButtonEventArgs e)
    {
        if (!IsOverRow(e.OriginalSource as DependencyObject)) return;
        if (HistoryList.SelectedItem is not HistoryRow row) return;

        if (!row.HasDetails)
        {
            // Older rows keep only their counts, so there is nothing to open.
            TransferText.Text = "The file list for that transfer is no longer kept.";
            return;
        }

        new DetailsDialog(this, $"{row.Arrow} · {row.Peer}", row.Details).ShowDialog();
    }

    private void OnHistoryListClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsOverRow(e.OriginalSource as DependencyObject))
            HistoryList.SelectedItem = null;
    }

    // =======================================================================
    // Traffic the far side is driving
    // =======================================================================

    /// <summary>
    /// Folds what everyone else is pulling or pushing into the same one-row-per-transfer view
    /// our own transfers use, and into the status bar's speed readout.
    ///
    /// There is no end marker to wait for - the far side simply stops asking - so a flow is
    /// closed once it has been quiet for a couple of seconds and written to the history then.
    /// </summary>
    /// <summary>Rows for transfers other people have queued with us, by flow key.</summary>
    private readonly Dictionary<string, SessionRow> _queuedFlowRows = [];

    private void SyncPeerFlows()
    {
        var active = TrafficMonitor.Active();

        _servedInRate = active.Exists(f => f.Direction == TransferDirection.Download)
            ? _servedInMeter.BytesPerSecond : 0;
        _servedOutRate = active.Exists(f => f.Direction == TransferDirection.Upload)
            ? _servedOutMeter.BytesPerSecond : 0;

        // Our end of any peer-driven transfer is one of our shared folders. Resolved once here
        // (the lookup is cached per drive letter), then paired with the peer's own drive to read
        // "SSD → HDD". Null if our shares span drives with no majority or cannot be told.
        var ourDrive = DriveDetect.Label(DriveDetect.ForPaths(_config.Folders));
        if (ourDrive == "?") ourDrive = null;

        foreach (var flow in active)
        {
            if (!_flowRows.TryGetValue(flow.Key, out var row))
            {
                row = AddTransferRow(new HistoryEntry
                {
                    WhenUtc = flow.StartedUtc,
                    Sent = flow.Direction == TransferDirection.Upload,
                    Peer = FlowName(flow.Key, flow.PeerName),
                    PeerDriven = true,   // the far side is pulling from / pushing to us
                });
                _flowRows[flow.Key] = row;

                var key = flow.Key;
                row.DetailsProvider = () => TrafficMonitor.Find(key) is { } full ? DescribeContents(full) : [];

                NotifyIfAway(flow.Direction == TransferDirection.Download
                        ? ("Incoming files", $"{flow.PeerName} is sending you files.")
                        : ("Files being downloaded", $"{flow.PeerName} is downloading from you."));
            }

            row.Checking = flow.Checking;
            row.CheckPercent = flow.Checking && flow.Total > 0 ? Math.Min(99, flow.CheckBytes * 100.0 / flow.Total) : null;
            UpdateRow(row, flow.Bytes, flow.Files, flow.Folders, flow.Seconds,
                flow.Direction == TransferDirection.Upload ? _servedOutRate : _servedInRate,
                flow.Total);

            // Upload (they pull from us): our disk is the source, theirs the destination.
            // Download (they push to us): theirs is the source, ours the destination.
            if (flow.Direction == TransferDirection.Upload)
            {
                row.Entry.SourceDrive = ourDrive;
                row.Entry.DestDrive = flow.PeerDrive;
            }
            else
            {
                row.Entry.SourceDrive = flow.PeerDrive;
                row.Entry.DestDrive = ourDrive;
            }

        }

        // Transfers they have queued with us: a row each, "Queued · position N".
        var queued = TrafficMonitor.Queued();
        foreach (var waiting in queued)
        {
            if (!_queuedFlowRows.TryGetValue(waiting.Key, out var qrow))
            {
                qrow = AddTransferRow(new HistoryEntry
                {
                    WhenUtc = waiting.SinceUtc,
                    Sent = waiting.Direction == TransferDirection.Upload,
                    Peer = FlowName(waiting.Key, waiting.PeerName),
                    PeerDriven = true,
                });
                _queuedFlowRows[waiting.Key] = qrow;
            }
            var (shortText, sentence) = PreparingText(waiting);
            if (qrow.QueuePosition != waiting.Position || qrow.Preparing != shortText)
            {
                qrow.QueuePosition = waiting.Position;
                qrow.Preparing = shortText;
                qrow.PreparingDetail = sentence;
                qrow.Refresh();
            }
        }

        // They gave up before starting because the receiving disk is too full: the row stays,
        // as a failed transfer that says why, instead of just vanishing.
        foreach (var full in TrafficMonitor.DrainNoSpace())
        {
            var name = FlowName(full.Key, full.PeerName);
            var why = full.Direction == TransferDirection.Upload
                ? $"Not enough free space on {name}'s PC: needs {Format.Bytes(full.Need)}, {Format.Bytes(full.Free)} free"
                : $"Not enough free space on this PC for {name}'s files: needs {Format.Bytes(full.Need)}, {Format.Bytes(full.Free)} free";
            if (!_queuedFlowRows.Remove(full.Key, out var failed))
                failed = AddTransferRow(new HistoryEntry
                {
                    WhenUtc = DateTime.UtcNow,
                    Sent = full.Direction == TransferDirection.Upload,
                    Peer = name,
                    PeerDriven = true,
                });
            failed.QueuePosition = 0;
            failed.Preparing = null;
            failed.PreparingDetail = null;
            FinishRow(failed, why);
            ActivityLog.Note(full.Direction, why);
            NotifyIfAway(("Not enough space", why + "."));
        }
        foreach (var gone in _queuedFlowRows.Keys.Where(k => !queued.Exists(q => q.Key == k)).ToList())
        {
            _transfers.Remove(_queuedFlowRows[gone]);
            _queuedFlowRows.Remove(gone);
            UpdateTransferEmpty();
        }

        _peerActivity = active.Count switch
        {
            0 when queued.Count > 0 =>
                PreparingText(queued[0]).Sentence +
                (queued.Count > 1 ? $" (+{queued.Count - 1} more waiting)" : ""),
            0 => null,
            1 => DescribeFlow(active[0], _flowRows.GetValueOrDefault(active[0].Key)),
            _ => $"{active.Count} people transferring — " +
                 (active.All(f => f.Total > 0)
                     ? $"{Format.Bytes(active.Sum(f => f.Bytes))} / {Format.Bytes(active.Sum(f => f.Total))}"
                     : $"{Format.Bytes(active.Sum(f => f.Bytes))} so far"),
        };

        foreach (var flow in TrafficMonitor.Sweep(TimeSpan.FromSeconds(2.5)))
        {
            foreach (var folder in flow.FolderNames) ActivityLog.Folder(flow.Direction, folder);

            if (!_flowRows.Remove(flow.Key, out var row)) continue;

            UpdateRow(row, flow.Bytes, flow.Files, flow.Folders, flow.Seconds, 0);
            row.Details = DescribeContents(flow);
            row.DetailsProvider = null;

            // Ended early: say where it got to and why, rather than "Completed".
            var stopped = flow.Stopped is { } why
                ? (flow.Total > 0 ? $"Stopped at {Format.Bytes(flow.Bytes)} of {Format.Bytes(flow.Total)} - {why}" : $"Stopped - {why}")
                : null;
            FinishRow(row, stopped ?? flow.Problem);

            if (stopped is not null)
            {
                NotifyIfAway(("Transfer stopped", $"{flow.PeerName}: {stopped}."));
            }
            else if (flow.Bytes > 0)
            {
                NotifyIfAway(flow.Direction == TransferDirection.Download
                    ? ("Files received", $"{flow.PeerName} sent you {flow.Files:N0} file(s), {Format.Bytes(flow.Bytes)}.")
                    : ("Download finished", $"{flow.PeerName} downloaded {flow.Files:N0} file(s), {Format.Bytes(flow.Bytes)} from you."));
            }

            // Only worth a history line if something actually moved. A browse that touched
            // one small file is not a transfer anyone wants recorded forever.
            if (flow.Bytes <= 0) continue;

            // Keep the file list on the history row too, so it opens like the Session row.
            row.Entry.FileNames = flow.Names.ToList();
            row.Entry.FolderNames = flow.FolderNames.Where(f => f.Length > 0).ToList();

            if (flow.Direction == TransferDirection.Upload)
            {
                row.Entry.SourceDrive = ourDrive;
                row.Entry.DestDrive = flow.PeerDrive;
            }
            else
            {
                row.Entry.SourceDrive = flow.PeerDrive;
                row.Entry.DestDrive = ourDrive;
            }

            RecordHistory(row.Entry);

            // Log peer-driven transfers too. Only our own transfers were being logged, so a
            // 40 GB pull by the other side left no trace in log.txt at all - which is exactly
            // the "there is no logging anywhere" gap. Same one-line shape as our own transfers.
            var flowSeconds = Math.Max(flow.Seconds, 0.001);
            Diagnostics.Log("transfer",
                $"{flow.Direction} (peer-driven) · {flow.Files:N0} files · {Format.Bytes(flow.Bytes)} · " +
                $"{flowSeconds:0.0}s · avg {Format.Rate(flow.Bytes / flowSeconds)} · " +
                $"drives {row.Entry.SourceDrive ?? "?"}→{row.Entry.DestDrive ?? "?"} · peer {flow.PeerName}" +
                (stopped is null ? "" : $" · INCOMPLETE: {stopped}"));

            // The end of someone else's transfer is as good a moment to bank the all-time
            // figures as the end of one of ours. Only our own transfers used to do it, so a
            // peer could pull 40 GB off this PC and a crash would lose every byte of it from
            // the Statistics tab.
            CommitLifetime();
        }
    }

    /// <summary>
    /// The file list behind a peer's row, so "what are they taking" can actually be answered
    /// rather than left at a count. Names only - the list is capped where it is collected.
    /// </summary>
    private static List<string> DescribeContents(FlowSnapshot flow)
    {
        var lines = new List<string> { $"Files ({flow.Files:N0})" };
        lines.AddRange(flow.Names.Select(n => "    " + n));

        if (flow.Files > flow.Names.Count)
            lines.Add($"    …and {flow.Files - flow.Names.Count:N0} more");

        return lines;
    }

    /// <summary>
    /// What we call the person behind a flow: their nickname here, else the name they reported.
    /// The reported one alone showed "PC3 has queued…" next to a row that said "Dillon 3".
    /// </summary>
    private string FlowName(string key, string reported) => DisplayNameOf(TrafficMonitor.PeerOf(key), reported);

    /// <summary>"6 files left", "1 file left".</summary>
    private static string FilesLeft(int left) => $"{Math.Max(left, 0):N0} file{(left == 1 ? "" : "s")} left";

    /// <summary>
    /// A transfer that has not started on the other PC: a few words for the Progress cell and a
    /// sentence for the Result column and the bottom bar.
    /// </summary>
    private (string? Short, string Sentence) PreparingText(TrafficMonitor.QueuedFlow waiting)
    {
        var name = FlowName(waiting.Key, waiting.PeerName);
        var theyDownload = waiting.Direction == TransferDirection.Upload;
        return waiting.Stage switch
        {
            "location" => ("Choosing a folder", $"{name} is picking where to save it"),
            "picking" => ("Choosing files", $"{name} is choosing what to send you"),
            "listing" => ("Getting file list", theyDownload
                ? $"{name} is getting the file list ready"
                : $"{name} is getting ready to send you files"),
            "overwrite" => ("Checking files", theyDownload
                ? $"{name} is deciding what to do with files they already have"
                : $"{name} is deciding whether to replace files you already have"),
            _ => (null, $"{name} has queued {(theyDownload ? "a download from you" : "files to send you")} · position {waiting.Position}"),
        };
    }

    private string DescribeFlow(FlowSnapshot flow, SessionRow? row)
    {
        var who = FlowName(flow.Key, flow.PeerName);
        var verb = flow.Direction == TransferDirection.Upload
            ? "is downloading from you"
            : "is sending you files";

        // "5 GB / 7 GB" when they announced how much it is, as our own transfers show.
        var size = flow.Total > 0
            ? $"{Format.Bytes(flow.Bytes)} / {Format.Bytes(flow.Total)}"
            : Format.Bytes(flow.Bytes);

        // Everything is here; the "about 0s left" that used to sit there for minutes was them
        // fingerprinting the files.
        if (flow.Checking)
            return $"{who}: all {flow.Files:N0} files, {size} moved · checking the files on both PCs" +
                   (flow.Total > 0 ? $" {Math.Min(99, flow.CheckBytes * 100.0 / flow.Total):0}%" : "") +
                   $" · {Format.Duration((DateTime.UtcNow - flow.StartedUtc).TotalSeconds)} elapsed";

        // Files still to come when they said how many; a count so far when they did not (older build).
        var files = flow.TotalFiles > 0 ? FilesLeft(flow.TotalFiles - flow.Files) : $"{flow.Files:N0} files so far";
        return $"{who} {verb} — {files}, {size}" +
               $" · {Format.Duration((DateTime.UtcNow - flow.StartedUtc).TotalSeconds)} elapsed" +
               (row?.SecondsLeft is { } left ? $" · {Format.Duration(left)} remaining" : "");
    }

    /// <summary>Opens the full file list behind a batched transfer line.</summary>
    private void OnShowBatchClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LogEntry entry || entry.Details is null) return;

        new DetailsDialog(this, entry.Text, entry.Details).ShowDialog();
    }


    // =======================================================================
    // All-time top ten
    // =======================================================================

    /// <summary>
    /// Keeps the biggest-file leaderboard. Runs for every completed file in either direction,
    /// including ones the server hands out, so a peer pulling a huge file off this PC is
    /// recorded as "sent to" them.
    /// </summary>
    private void OnFileCompleted(TransferDirection direction, string name, long size, string? peer)
    {
        // Ignore the small stuff outright: a batch of thousands of tiny files would otherwise
        // churn the list on every single one for no chance of ever placing.
        if (size < 1024 * 1024) return;

        Dispatcher.BeginInvoke(() =>
        {
            var smallest = _config.BiggestTransfers.Count < 10
                ? 0
                : _config.BiggestTransfers.Min(r => r.Bytes);

            if (_config.BiggestTransfers.Count >= 10 && size <= smallest) return;

            _config.BiggestTransfers.Add(new TransferRecord
            {
                FileName = Path.GetFileName(name.Replace('/', Path.DirectorySeparatorChar)),
                Bytes = size,
                Sent = direction == TransferDirection.Upload,
                Peer = peer ?? CurrentPeerName(),
                WhenUtc = DateTime.UtcNow,
            });

            _config.BiggestTransfers = _config.BiggestTransfers
                .OrderByDescending(r => r.Bytes)
                .Take(10)
                .ToList();

            _config.Save();
            RefreshBiggest();
        });
    }

    /// <summary>
    /// Who we are transferring with, when the caller did not say.
    ///
    /// Your own machine is named as such. Browsing your own shares, or connecting over
    /// loopback to test, used to be recorded as coming from "them", which read as though a
    /// stranger had sent you your own files.
    /// </summary>
    private string CurrentPeerName() => PeerNameOf(_active);

    private string PeerNameOf(Connection? connection)
    {
        if (connection?.Kind == ConnectionKind.Local) return "You";
        if (connection?.Source is PeerTunnel peer) return peer.Username;

        if (connection?.Info is not { } info) return "You";

        return IsLoopbackAddress(info.Host)
            ? "You (localhost)"
            : NameFor(info);
    }

    /// <summary>True for an address that is this same PC, however it was typed.</summary>
    private static bool IsLoopbackAddress(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (System.Net.IPAddress.TryParse(host, out var parsed) &&
         System.Net.IPAddress.IsLoopback(parsed));

    // =======================================================================
    // History
    // =======================================================================

    /// <summary>Oldest entries drop off. Enough to be a history, not enough to bloat the config.</summary>
    private const int MaxHistory = 2000;

    /// <summary>How many recent history rows keep their full file list. Older ones drop to
    /// counts only, so a long history cannot bloat config.json without bound.</summary>
    private const int HistoryDetailKept = 100;

    /// <summary>Cap on names stored per history row, so one huge transfer stays reasonable.</summary>
    private const int HistoryNameCap = 2000;

    private void RecordHistory(HistoryEntry entry)
    {
        // Trim the stored names to a ceiling; the counts (Files/Folders) stay exact.
        if (entry.FileNames is { Count: > HistoryNameCap })
            entry.FileNames = entry.FileNames.Take(HistoryNameCap).ToList();
        if (entry.FolderNames is { Count: > HistoryNameCap })
            entry.FolderNames = entry.FolderNames.Take(HistoryNameCap).ToList();

        _config.History.Add(entry);
        while (_config.History.Count > MaxHistory) _config.History.RemoveAt(0);

        // Keep detail lists only on the most recent rows; drop them from older ones so the
        // file stays small however long the history grows.
        for (var i = 0; i < _config.History.Count - HistoryDetailKept; i++)
            HistoryDetails.Delete(_config.History[i]);

        _config.Save();
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        var rows = _config.History
            .OrderByDescending(h => h.WhenUtc)
            .Select(h => new HistoryRow { Entry = h })
            .ToList();

        HistoryList.ItemsSource = rows;
        HistoryEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var bytes = _config.History.Sum(h => h.Bytes);
        HistorySummary.Text = rows.Count == 0
            ? "Nothing recorded yet."
            : $"{rows.Count:N0} transfers · {Format.Bytes(bytes)} moved in total";
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_config.History.Count == 0) return;

        var confirm = MessageBox.Show(this,
            $"""
             Delete all {_config.History.Count:N0} history entries?

             This cannot be undone. All-time totals and the top ten are not affected.
             """,
            "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _config.History.Clear();
        _config.Save();
        RefreshHistory();
    }

    /// <summary>
    /// Resets the all-time figures and the top ten. The in-memory counters for this run are
    /// zeroed too, otherwise the next save would fold them straight back in and it would look
    /// as though nothing had been cleared.
    /// </summary>
    private void OnClearStatsClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            """
            Reset the all-time totals?

            This cannot be undone. The top ten and the History tab are not affected.
            """,
            "Reset totals", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _config.LifetimeDownloadedBytes = 0;
        _config.LifetimeUploadedBytes = 0;
        _config.LifetimeDownloadedFiles = 0;
        _config.LifetimeUploadedFiles = 0;
        _config.LifetimeLargestFileBytes = 0;
        _config.LifetimePeakBytesPerSecond = 0;
        _config.LifetimeRuns = 0;
        _config.FirstRunUtc = DateTime.UtcNow;

        // This run's totals have already been counted into the figures above, so the base has
        // to be re-taken from the current counters rather than left at its old value.
        _baseDownBytes = -ActivityLog.DownloadedBytes;
        _baseUpBytes = -ActivityLog.UploadedBytes;
        _baseDownFiles = -ActivityLog.DownloadedFiles;
        _baseUpFiles = -ActivityLog.UploadedFiles;

        _config.Save();
        UpdateLifetime();
    }

    /// <summary>Clears the leaderboard alone. Separate concern, separate button.</summary>
    private void OnClearBiggestClick(object sender, RoutedEventArgs e)
    {
        if (_config.BiggestTransfers.Count == 0) return;

        var confirm = MessageBox.Show(this,
            """
            Clear the top ten biggest transfers?

            This cannot be undone. The all-time totals and the History tab are not affected.
            """,
            "Reset top 10", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _config.BiggestTransfers.Clear();
        _config.Save();
        RefreshBiggest();
    }

    private void RefreshBiggest()
    {
        var rows = _config.BiggestTransfers
            .OrderByDescending(r => r.Bytes)
            .Select((record, index) => new BiggestRow { Rank = index + 1, Record = record })
            .ToList();

        BiggestList.ItemsSource = rows;
        BiggestEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // =======================================================================
    // All-time statistics
    // =======================================================================

    private void UpdateLifetime()
    {
        // Only worth formatting while the tab is actually on screen.
        if (!LifeDownBytes.IsVisible) return;

        var downBytes = _baseDownBytes + ActivityLog.DownloadedBytes;
        var upBytes = _baseUpBytes + ActivityLog.UploadedBytes;
        var downFiles = _baseDownFiles + ActivityLog.DownloadedFiles;
        var upFiles = _baseUpFiles + ActivityLog.UploadedFiles;

        LifeDownBytes.Text = Format.Bytes(downBytes);
        LifeDownFiles.Text = $"{downFiles:N0} files";
        LifeUpBytes.Text = Format.Bytes(upBytes);
        LifeUpFiles.Text = $"{upFiles:N0} files";
        LifeTotalBytes.Text = Format.Bytes(downBytes + upBytes);
        LifeTotalFiles.Text = $"{downFiles + upFiles:N0} files";

        LifeRuns.Text = $"{_config.LifetimeRuns:N0}";
        LifePeak.Text = Format.Rate(Math.Max(_config.LifetimePeakBytesPerSecond, _peakRate));
        LifeBiggest.Text = Format.Bytes(Math.Max(_config.LifetimeLargestFileBytes,
            ActivityLog.LargestFileBytes));

        LifetimeSince.Text = _config.FirstRunUtc is { } since
            ? $"since {since.ToLocalTime():d MMMM yyyy}"
            : "";

    }

    /// <summary>Folds this run's totals into the persisted ones. Safe to call repeatedly.</summary>
    private void CommitLifetime()
    {
        _config.LifetimeDownloadedBytes = _baseDownBytes + ActivityLog.DownloadedBytes;
        _config.LifetimeUploadedBytes = _baseUpBytes + ActivityLog.UploadedBytes;
        _config.LifetimeDownloadedFiles = _baseDownFiles + ActivityLog.DownloadedFiles;
        _config.LifetimeUploadedFiles = _baseUpFiles + ActivityLog.UploadedFiles;

        _config.LifetimeLargestFileBytes =
            Math.Max(_config.LifetimeLargestFileBytes, ActivityLog.LargestFileBytes);
        _config.LifetimePeakBytesPerSecond =
            Math.Max(_config.LifetimePeakBytesPerSecond, _peakRate);

        _config.Save();
    }

    private void UpdateStats()
    {
        StatDown.Text = Format.Bytes(ActivityLog.DownloadedBytes);
        StatUp.Text = Format.Bytes(ActivityLog.UploadedBytes);
        StatDownItems.Text = $"{ActivityLog.DownloadedFolders:N0} / {ActivityLog.DownloadedFiles:N0}";
        StatUpItems.Text = $"{ActivityLog.UploadedFolders:N0} / {ActivityLog.UploadedFiles:N0}";
        StatErrors.Text = $"{ActivityLog.Errors:N0}";
        StatPeak.Text = Format.Rate(_peakRate);

        // Dashes rather than zeros until sharing starts: a clock that has not been started
        // reading 00:00 looks like a clock that is broken.
        if (ActivityLog.StartedUtc is not { } startedUtc)
        {
            StatUptime.Text = "--:--";
            return;
        }

        var elapsed = DateTime.UtcNow - startedUtc;
        StatUptime.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}
