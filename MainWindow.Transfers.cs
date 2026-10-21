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
    // Transfer queue
    // =======================================================================

    /// <summary>
    /// Tells the other PC what a transfer is doing before it starts (cap "prepare"): picking a
    /// folder, getting the file list ready, asking about files already there, or out of space.
    /// Messages go one after another so they cannot overtake each other on the way.
    /// </summary>
    private sealed class Preparing(ApiClient? api, bool download)
    {
        private Task _last = Task.CompletedTask;
        private bool _over;

        private void Send(string stage, long need = 0, long free = 0)
        {
            if (api is null) return;
            _last = _last.ContinueWith(_ => api.AnnouncePrepareAsync(download, stage, need, free)).Unwrap();
        }

        public void Say(string stage) { if (!_over) Send(stage); }
        public void NoSpace(long need, long free) { if (_over) return; _over = true; Send("nospace", need, free); }

        /// <summary>Waits for what was said so far, then stops: the transfer is announced as started next.</summary>
        public Task StartedAsync() { _over = true; return _last; }

        /// <summary>It ended before starting (cancelled, nothing to do, failed): "nothing any more".</summary>
        public void Clear() { if (_over) return; _over = true; Send(""); }
    }

    private async Task EnqueueAsync(TransferDirection direction, string description, Func<Task> job,
        Connection? with = null)
    {
        var lane = direction == TransferDirection.Download ? _downLane : _upLane;
        lane.Queue.Add(new QueuedJob { Direction = direction, Description = description, Run = job, With = with });
        RefreshQueueView();
        if (lane.Pumping) return;

        lane.Pumping = true;
        CurrentLane.Value = lane;
        try
        {
            while (lane.Queue.Count > 0)
            {
                var next = lane.Queue[0];
                lane.Queue.RemoveAt(0);
                lane.Current = next;
                next.Running = true;
                RefreshQueueView();

                // One job failing outright must not strand everything queued behind it.
                try { await next.Run(); }
                catch (Exception ex)
                {
                    Diagnostics.Log("transfer", "queued job failed", ex);
                    ActivityLog.Error(ex.Message);
                }
                finally
                {
                    // Ended without starting: the other PC must not keep showing "getting ready".
                    lane.Prep?.Clear();
                    lane.Prep = null;
                    lane.Current = null;
                    RefreshQueueView();
                }
            }
        }
        finally
        {
            lane.Pumping = false;
            UpdateButtons();
        }
    }

    /// <summary>
    /// The connection with this id once it is up again - after a drop it comes back as a new
    /// object with the same id - or null if it has not returned within <paramref name="wait"/>.
    /// </summary>
    private async Task<Connection?> WaitForConnectionAsync(string id, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            if (_connections.FirstOrDefault(c => c.Id == id) is { MissedPings: 0 } back) return back;
            if (DateTime.UtcNow >= until) return null;
            await Task.Delay(2000);
        }
    }

    // =======================================================================
    // Dragging files out to Explorer
    // =======================================================================

    private System.Windows.Point _dragOrigin;
    private bool _dragArmed;
    private DragGhost? _ghost;
    private ShellDrop.Target? _dropTarget;

    /// <summary>
    /// Arms a possible drag. Nothing starts here - the button has to travel a little first,
    /// or every click that twitches would turn into a drag and selection would be unusable.
    /// </summary>
    private void OnFileListMouseDown(object sender, MouseButtonEventArgs e)
    {
        var onRow = IsOverRow(e.OriginalSource as DependencyObject);

        // Only over an actual row: pressing on the header or the empty space below the list
        // must not arm a drag.
        _dragArmed = _source is not null && onRow;
        _dragOrigin = e.GetPosition(null);

        // Clicking the empty space below the rows clears the selection, as Explorer does.
        // The ListView leaves the old selection standing otherwise, which is what made a
        // double-click on nothing act on a file chosen some time ago.
        //
        // Focus matters as much as the selection here: a ListView does not take keyboard focus
        // when you click its empty area, so Ctrl+V went to whatever had focus last and paste
        // appeared to do nothing unless the right-click menu was used instead.
        // The header test walks up from what was hit: the press lands on the header's text or
        // border, never the header itself, so testing OriginalSource alone started a rubber band
        // on every header press - and its mouse capture swallowed the click that sorts.
        if (!onRow && !IsOverHeader(e.OriginalSource as DependencyObject))
        {
            FileList.UnselectAll();
            FileList.Focus();

            // Begin a rubber band from here. Capture so it keeps tracking past the edge.
            _marqueeFrom = e.GetPosition(MarqueeLayer);
            Marquee.Width = 0;
            Marquee.Height = 0;
            FileList.CaptureMouse();
        }
    }

    private static bool IsOverHeader(DependencyObject? hit)
    {
        for (; hit is not null; hit = hit is System.Windows.Media.Visual
                 ? System.Windows.Media.VisualTreeHelper.GetParent(hit)
                 : LogicalTreeHelper.GetParent(hit))
        {
            if (hit is GridViewColumnHeader) return true;
            if (hit is ListViewItem) return false;
        }
        return false;
    }

    private static bool IsOverRow(DependencyObject? hit)
    {
        while (hit is not null)
        {
            if (hit is ListViewItem) return true;
            if (hit is GridViewColumnHeader) return false;
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }
        return false;
    }

    // ---- rubber-band selection ---------------------------------------------

    private System.Windows.Point? _marqueeFrom;

    /// <summary>
    /// Drags a selection box over the empty part of the list, as Explorer does.
    ///
    /// Started only from blank space: beginning one on top of a row would fight the drag-out
    /// gesture, which is the other thing a press-and-move over the list can mean.
    /// </summary>
    private void UpdateMarquee(System.Windows.Point now)
    {
        if (_marqueeFrom is not { } from) return;

        var x = Math.Min(from.X, now.X);
        var y = Math.Min(from.Y, now.Y);
        var width = Math.Abs(now.X - from.X);
        var height = Math.Abs(now.Y - from.Y);

        Canvas.SetLeft(Marquee, x);
        Canvas.SetTop(Marquee, y);
        Marquee.Width = width;
        Marquee.Height = height;
        Marquee.Visibility = Visibility.Visible;

        var box = new Rect(x, y, width, height);

        // Hit-test each row against the box. The list is virtualised, so only what is on
        // screen has a container - which is also all the user can be aiming at.
        for (var i = 0; i < FileList.Items.Count; i++)
        {
            if (FileList.ItemContainerGenerator.ContainerFromIndex(i) is not ListViewItem row) continue;
            if (!row.IsVisible) continue;

            var topLeft = row.TranslatePoint(new System.Windows.Point(0, 0), MarqueeLayer);
            var bounds = new Rect(topLeft, new Size(row.ActualWidth, row.ActualHeight));

            row.IsSelected = box.IntersectsWith(bounds);
        }
    }

    private void EndMarquee()
    {
        _marqueeFrom = null;
        Marquee.Visibility = Visibility.Collapsed;
        if (FileList.IsMouseCaptured) FileList.ReleaseMouseCapture();
        UpdateButtons();
    }

    private void OnFileListMouseMove(object sender, MouseEventArgs e)
    {
        if (_marqueeFrom is not null)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { EndMarquee(); return; }

            UpdateMarquee(e.GetPosition(MarqueeLayer));
            return;
        }

        if (!_dragArmed || _ghost is not null || e.LeftButton != MouseButtonState.Pressed) return;

        var moved = e.GetPosition(null) - _dragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var rows = FileList.SelectedItems.Cast<ListRow>().ToList();
        if (rows.Count == 0) { _dragArmed = false; return; }

        BeginDragOut(rows);
    }

    /// <summary>
    /// Runs the drag by hand rather than through OLE.
    ///
    /// Windows will not let us hand Explorer a file that has not been downloaded yet without
    /// implementing virtual-file drops, so instead the whole gesture stays ours: capture the
    /// mouse, follow it with a label, and when the button is released ask what is underneath.
    /// The trade is that only Explorer and the desktop can be dropped on - which is what these
    /// files are wanted for anyway.
    /// </summary>
    private void BeginDragOut(List<ListRow> rows)
    {
        _dragArmed = false;
        _dropTarget = null;

        var self = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        _ghost = new DragGhost { Owner = this };
        _ghost.Describe(rows.Count, null);
        _ghost.Show();

        // Capture on the list, so moves and the release still reach us once the pointer is
        // over another application's window.
        FileList.CaptureMouse();

        void OnMove(object? s, MouseEventArgs args)
        {
            var (x, y) = ShellDrop.CursorPosition;
            _dropTarget = ShellDrop.Resolve(x, y, self);
            _ghost?.Describe(rows.Count, _dropTarget?.Label);
            _ghost?.MoveTo(x, y);
        }

        void OnUp(object? s, MouseButtonEventArgs args) => EndDragOut(rows);

        void OnLostCapture(object? s, MouseEventArgs args) => Cleanup();

        // Escape abandons the drag, the same as it would in Explorer.
        void OnKey(object? s, KeyEventArgs args)
        {
            if (args.Key != Key.Escape) return;
            _dropTarget = null;
            Cleanup();
        }

        void Cleanup()
        {
            FileList.PreviewMouseMove -= OnMove;
            FileList.PreviewMouseLeftButtonUp -= OnUp;
            FileList.LostMouseCapture -= OnLostCapture;
            PreviewKeyDown -= OnKey;

            if (FileList.IsMouseCaptured) FileList.ReleaseMouseCapture();

            _ghost?.Close();
            _ghost = null;
        }

        void EndDragOut(List<ListRow> dropped)
        {
            var target = _dropTarget;
            Cleanup();

            if (target is null) return;   // released over nothing that takes files

            ActivityLog.Note(TransferDirection.Download,
                $"Dropped {dropped.Count} item(s) into {target.Path}");

            _config.LastDestination = target.Path;
            _config.Save();

            _ = DownloadToAsync(dropped, target.Path);
        }

        FileList.PreviewMouseMove += OnMove;
        FileList.PreviewMouseLeftButtonUp += OnUp;
        FileList.LostMouseCapture += OnLostCapture;
        PreviewKeyDown += OnKey;

        OnMove(null, null!);   // label the target straight away, before the first move
    }

    // =======================================================================
    // Dragging files in from Explorer
    // =======================================================================

    /// <summary>
    /// Files coming the other way need none of the above: Explorer hands over real paths, so
    /// this is ordinary drag-and-drop. Refused unless the host currently grants Send and we
    /// are inside a share rather than at the root, matching the Upload buttons exactly.
    /// </summary>
    private bool CanAcceptDroppedFiles =>
        _currentPath.Length > 0 && (CanSendHere || _browsingLocal);

    private void OnFileListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanAcceptDroppedFiles && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private async void OnFileListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (!CanAcceptDroppedFiles) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        // Off the UI thread: a dropped folder of a hundred thousand files takes seconds to walk.
        TransferText.Text = "Reading the dropped files…";
        var items = await Task.Run(() => BuildUploadItems(paths));
        TransferText.Text = "";
        if (items.Count == 0) return;

        ActivityLog.Note(TransferDirection.Upload,
            $"Dropped {items.Count} file(s) in from Explorer");

        // Your own shares are on this disk, so there is nothing to send: it is a copy. The
        // gesture is the same either way, which is the point - drag it in and it is there.
        if (_browsingLocal) await CopyIntoLocalAsync(items);
        else await SendAsync(items);
    }

    /// <summary>
    /// Copies dropped files into the folder you are standing in, when that folder is one of
    /// your own shares. Runs through the same transfer plumbing as everything else, so it
    /// gets a progress bar, a Session row and a cancel button rather than freezing the
    /// window while Explorer's files are duplicated.
    /// </summary>
    private async Task CopyIntoLocalAsync(List<UploadItem> items)
    {
        if (!new ShareSet(_config.Folders).TryResolve(_currentPath, out var destination) ||
            !Directory.Exists(destination))
        {
            TransferText.Text = "That folder is not one of yours.";
            return;
        }

        static string LocalPath(string root, string relative) =>
            Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        // One disk check per file, so off the UI thread.
        var (existing, identical) = await Task.Run(() =>
        {
            var there = items
                .Where(i => !i.IsEmptyFolder && File.Exists(LocalPath(destination, i.Relative)))
                .ToList();

            // Same file already there (same size and date): nothing to do for it.
            var same = there.Where(i =>
            {
                var file = new FileInfo(LocalPath(destination, i.Relative));
                return PartFiles.LooksIdentical(file.Length, file.LastWriteTimeUtc, i.Size, i.ModifiedUnixMs);
            }).ToHashSet();
            return (there, same);
        });

        var clashing = existing.Where(i => !identical.Contains(i)).ToList();
        items = items.Where(i => !identical.Contains(i)).ToList();
        NoteIdentical(identical.Count, TransferDirection.Upload);

        if (clashing.Count > 0)
        {
            var answer = AskOverwrite(clashing.Select(i => i.Relative).ToList(),
                "in that folder", "copy");

            if (answer == MessageBoxResult.Cancel) { TransferText.Text = "Cancelled."; return; }
            if (answer == MessageBoxResult.No)
                items = items.Where(i => !clashing.Contains(i)).ToList();
        }

        if (items.Count == 0)
        {
            TransferText.Text = identical.Count > 0 ? "Everything is already there." : "Nothing left to copy.";
            return;
        }

        var wasThere = new HashSet<string>(
            clashing.Select(i => LocalPath(destination, i.Relative)),
            StringComparer.OrdinalIgnoreCase);

        var copying = items;
        var written = new List<string>();

        var stats = new TransferStats
        {
            TotalFiles = copying.Count(i => !i.IsEmptyFolder),
            TotalBytes = copying.Where(i => !i.IsEmptyFolder).Sum(i => i.Size),
        };

        await EnqueueAsync(TransferDirection.Upload, $"Copy {copying.Count(i => !i.IsEmptyFolder):N0} file(s) into {Path.GetFileName(destination)}", async () =>
        {
            // Set when the job runs, not when it is queued, so a transfer already running
            // keeps its own file list for History.
            _batch = new BatchInfo
            {
                From = "you",
                To = "You",
                Folders = copying
                    .Where(i => i.Relative.Contains('/'))
                    .Select(i => i.Relative[..i.Relative.IndexOf('/')])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Files = copying.Where(i => !i.IsEmptyFolder).Select(i => i.Relative).ToList(),
            };

            _undoCancel = () =>
            {
                var removed = 0;
                foreach (var path in written)
                {
                    if (wasThere.Contains(path)) continue;
                    try { if (File.Exists(path)) { File.Delete(path); removed++; } } catch { }
                }

                if (removed > 0)
                {
                    ActivityLog.Note(TransferDirection.Upload,
                        $"Cancelled — removed {removed:N0} file(s) already copied");
                }

                return Task.CompletedTask;
            };

            await RunTransferAsync(stats, TransferDirection.Upload, "Copied",
                ct => CopyFilesAsync(copying, destination, written, stats, ct));

            await NavigateAsync(_currentPath, pushHistory: false);
        });
    }

    /// <summary>
    /// A plain buffered copy that reports progress as it goes. Deliberately one file at a
    /// time: this is disk to disk on one machine, where parallelism buys nothing and costs
    /// seek time.
    /// </summary>
    private static async Task CopyFilesAsync(List<UploadItem> items, string destination,
        List<string> written, TransferStats stats, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var target = Path.Combine(destination,
                item.Relative.Replace('/', Path.DirectorySeparatorChar));

            if (item.IsEmptyFolder)
            {
                try { Directory.CreateDirectory(target); } catch (Exception ex) { stats.Fail(item.Relative, ex); }
                continue;
            }

            var folder = Path.GetDirectoryName(target);
            if (folder is not null) Directory.CreateDirectory(folder);

            // Landing on a temp name and moving into place, for the same reason downloads
            // do: a cancel halfway must not leave a truncated file wearing the real name.
            var part = PartFiles.PathFor(target);
            var attempt = stats.NewAttempt();

            try
            {
                await using (var source = new FileStream(item.Local, FileMode.Open, FileAccess.Read,
                                 FileShare.ReadWrite, buffer.Length, useAsync: true))
                await using (var sink = new FileStream(part, FileMode.Create, FileAccess.Write,
                                 FileShare.None, buffer.Length, useAsync: true))
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, ct);
                        if (read <= 0) break;
                        await sink.WriteAsync(buffer.AsMemory(0, read), ct);
                        attempt.AddBytes(read);
                    }
                }

                File.Move(part, target, overwrite: true);
                PartFiles.StampModified(target, item.ModifiedUnixMs);
                written.Add(target);
                attempt.AddFile();
                ActivityLog.File(TransferDirection.Upload, item.Relative, item.Size, 0, "You");
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                attempt.Rollback();
                stats.Fail(item.Relative, ex);
            }
        }
    }

    // =======================================================================
    // Download
    // =======================================================================

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        // Nothing selected: take the folder we are standing in. Right-clicking empty space and
        // finding Download greyed out is a dead end when the obvious meaning is "all of this".
        if (FileList.SelectedItems.Count == 0 && _currentPath.Length > 0 && _source is not null)
        {
            await DownloadCurrentFolderAsync();
            return;
        }

        // Gated on _source, not _api. Browsing a peer's folders over their tunnel sets only
        // _source, so checking _api here meant the Download button did nothing at all for a
        // peer sharing back to us - the one case the tunnel exists for.
        if (_source is null) return;

        var selected = FileList.SelectedItems.Cast<ListRow>().ToList();
        if (selected.Count == 0) return;

        var picking = new Preparing(_active?.Api, download: true);
        picking.Say("location");
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save to" };
        if (Directory.Exists(_config.LastDestination)) dialog.InitialDirectory = _config.LastDestination;
        if (dialog.ShowDialog(this) != true) { picking.Clear(); return; }

        _config.LastDestination = dialog.FolderName;
        _config.Save();

        await DownloadToAsync(selected, dialog.FolderName);
    }

    private async Task DownloadCurrentFolderAsync()
    {
        var picking = new Preparing(_active?.Api, download: true);
        picking.Say("location");
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Save \"{CurrentFolderName()}\" to" };
        if (Directory.Exists(_config.LastDestination)) dialog.InitialDirectory = _config.LastDestination;
        if (dialog.ShowDialog(this) != true) { picking.Clear(); return; }

        _config.LastDestination = dialog.FolderName;
        _config.Save();

        await DownloadToAsync(
            [new ListRow { Name = CurrentFolderName(), RemotePath = _currentPath, IsDirectory = true }],
            dialog.FolderName);
    }

    /// <summary>
    /// Fetches the given rows into a folder. Shared by the Download button, double-click and
    /// dragging out to Explorer, so all three behave identically - same queueing, same
    /// overwrite prompt, same counters.
    /// </summary>
    /// <param name="connectionId">Set by a retry: the PC the original transfer was from, which
    /// may since have dropped and come back as a new connection. Null means the one on screen.</param>
    /// <param name="onNoSpace">When the receiving disk is too full: asked (need, free, drive) whether
    /// to measure again (true, space has been freed) or give up (false). Null: fail with the reason.</param>
    private async Task DownloadToAsync(List<ListRow> selected, string destination,
        string? connectionId = null, int attempt = 1, string? pendingId = null,
        Func<long, long, string, Task<bool>>? onNoSpace = null)
    {
        var connection = connectionId is null ? _active
            : await WaitForConnectionAsync(connectionId, TimeSpan.FromMinutes(2));
        if (connection is null)
        {
            if (connectionId is not null)
                ActivityLog.Error("Couldn't try the download again: that PC is not connected. Reconnect, then use Retry failed files.");
            return;
        }
        if (selected.Count == 0) return;

        // Everything the job needs is pinned now, when it is queued. Read inside the job
        // instead, _source and _api meant whichever connection happened to be on screen when it
        // finally ran - queue a download from a friend, switch to your own shares, and it
        // copied your own files using their paths.
        var source = connection.Source;
        var api = connection.Api;
        var checker = connection.Writer;   // also reaches a PC sharing back, for the optional check
        var peerName = PeerNameOf(connection);

        // Excluded from Defender before the first file lands - the biggest real speed-up for a
        // folder of many small files. With automatic exclusion off, the old one-time offer.
        await EnsureExcludedAsync([destination], downloads: true);

        var what = selected.Count == 1 ? selected[0].Name : $"{selected.Count:N0} items";
        await EnqueueAsync(TransferDirection.Download, $"{what} from {peerName} → {Path.GetFileName(destination.TrimEnd('\\'))}", async () =>
        {
            Lane.ConnectionId = connection.Id;
            Lane.PendingId = pendingId ??= TrackPending(connection, download: true, destination,
                selected.Count == 1 ? $"\"{selected[0].Name}\"" : $"{selected.Count:N0} items", selected, null);
            if (!_connections.Contains(connection))
            {
                ActivityLog.Error($"The connection to {connection.Name} closed before a queued download could start.");
                return;
            }

            var prep = Lane.Prep = new Preparing(api, download: true);
            prep.Say("listing");

            _batch = new BatchInfo { From = peerName, To = "you", Folders = [], Files = [] };
            var downloader = new Downloader(source) { CurrentRate = () => _downMeter.BytesPerSecond };

            // Anything already sitting at a destination is not ours to remove if this gets
            // cancelled, so it is noted before a single byte is written.
            var wasThere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var weCreated = new List<string>();

            _undoCancel = () => UndoDownloadAsync(downloader, wasThere, weCreated);

            await RunTransferAsync(downloader.Stats, TransferDirection.Download, "Downloaded", async ct =>
            {
                TransferText.Text = "Building file list…";
                var (planned, emptyFolders) = await BuildDownloadPlanAsync(source, selected, destination, ct);

                var plan = await ResolveCollisionsAsync(planned, wasThere, () => prep.Say("overwrite"));
                if (plan is null || (plan.Count == 0 && emptyFolders.Count == 0 && planned.Count == 0))
                    throw new OperationCanceledException();

                // Count the distinct folders this will write into, for the session figures.
                var folders = plan.Select(p => Path.GetDirectoryName(p.Local) ?? "")
                    .Concat(emptyFolders).Distinct().ToList();
                foreach (var folder in folders) ActivityLog.Folder(TransferDirection.Download, folder);

                // Folders that do not exist yet are ones this transfer is about to make, and
                // are therefore ours to take away again if it is cancelled.
                weCreated.AddRange(folders.Where(f => f.Length > 0 && !Directory.Exists(f)));

                // Which disk everything is landing on, and which it came off. The source is
                // what the far side reported in its manifest; the destination is this PC's own.
                var destDrive = DriveDetect.Label(DriveDetect.ForPaths(plan.Select(p => p.Local)));
                if (destDrive == "?") destDrive = null;

                // Folders that are part of what was downloaded - "big", "big/textures" - relative
                // to where they landed. Not the folder they landed in: one picture saved into
                // Downloads is a file, not "a download of the Downloads folder".
                var sentFolders = folders
                    .Select(f => Path.GetRelativePath(destination, f).Replace(Path.DirectorySeparatorChar, '/'))
                    .Where(r => r != "." && !r.StartsWith("..", StringComparison.Ordinal))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                _batch = new BatchInfo
                {
                    From = peerName,
                    To = "you",
                    Folders = sentFolders,
                    Files = plan.Select(p => p.Remote).ToList(),
                    SourceDrive = _planSourceDrive,
                    DestDrive = destDrive,
                    Destination = destination,
                };

                // Show the drives on the live row straight away, not only when it finishes.
                if (_activeRow is { } liveRow)
                {
                    liveRow.Entry.SourceDrive = _planSourceDrive;
                    liveRow.Entry.DestDrive = destDrive;
                    liveRow.Refresh();
                }

                // Tell the host how much we are about to pull, so their screen shows the same
                // transfer as one batch with a real percentage rather than a count creeping up.
                // Our destination drive rides along so their peer-driven row shows our end too.
                var totalBytes = plan.Sum(p => p.Size);

                // Room for it here? Checked before anything moves. Identical files are already out
                // of the plan, and what a half-done big file already has on disk is not needed
                // again - counting it made a resume look like it would not fit when it would.
                var already = await Task.Run(() => plan.Sum(p =>
                {
                    try { var part = new FileInfo(PartFiles.PathFor(p.Local)); return part.Exists ? Math.Min(part.Length, p.Size) : 0; }
                    catch { return 0L; }
                }), ct);
                var need = totalBytes - already;
                while (true)
                {
                    DriveInfo disk;
                    try { disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!); _ = disk.AvailableFreeSpace; }
                    catch { break; }   // a drive we cannot measure (network share): carry on

                    var free = DiskSpace.Free(destination);
                    var driveName = disk.Name.TrimEnd('\\');
                    if (need + (64L << 20) <= free) break;
                    if (onNoSpace is null)
                    {
                        prep.NoSpace(need, free);
                        throw new NotEnoughSpaceException(
                            $"Not enough free space on this PC: this transfer needs {Format.Bytes(need)} and {driveName} has {Format.Bytes(free)} free.");
                    }
                    if (!await onNoSpace(need, free, driveName)) throw new TransferDiscardedException("Discarded - not enough free space");
                }

                await prep.StartedAsync();
                if (api is not null) await api.AnnounceAsync(true, totalBytes, plan.Count, true, ct, destDrive);
                var outcome = "failed";
                try
                {
                    await downloader.RunAsync(plan, ct, emptyFolders);

                    if (_config.CheckFiles && checker is not null)
                    {
                        var written = downloader.Written.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        await CheckTransferAsync(checker, downloader.Stats,
                            plan.Where(p => written.Contains(p.Local)).Select(p => (p.Remote, p.Local)).ToList(), ct);
                    }
                    outcome = downloader.Stats.Errors.Count > 0 ? $"partial-{downloader.Stats.Errors.Count}" : "done";
                }
                catch (OperationCanceledException) { outcome = "cancelled"; throw; }
                finally
                {
                    if (api is not null) await api.AnnounceAsync(true, totalBytes, plan.Count, false, CancellationToken.None, result: outcome);
                    if (downloader.ResumedBytes > 0)
                    {
                        ActivityLog.Note(TransferDirection.Download,
                            $"Resumed — {Format.Bytes(downloader.ResumedBytes)} was already here from an earlier attempt");
                    }
                }
            }, next => DownloadToAsync(selected, destination, connection.Id, next, pendingId, onNoSpace), attempt);
        }, connection);
    }

    /// <summary>
    /// Asks before replacing files that already exist.
    ///
    /// This also covers the worst case: downloading a file on top of itself. The transfer
    /// writes to a temp name and moves it into place, so the source can no longer be
    /// truncated while the far side is still reading it - but silently replacing a file the
    /// user did not expect to lose is still the wrong default.
    /// </summary>
    /// <summary>
    /// Files already here that are the same file (same size, same date) are simply skipped -
    /// which is also what makes re-running an interrupted transfer pick up where it stopped
    /// rather than start over. Returns null if the user cancelled.
    /// </summary>
    private async Task<List<PlanItem>?> ResolveCollisionsAsync(List<PlanItem> plan, HashSet<string> wasThere,
        Action? asking = null)
    {
        // One disk check per file - 32,000 of them for a big folder - so off the UI thread.
        var (existing, identical) = await Task.Run(() =>
        {
            var here = plan.Where(p => File.Exists(p.Local)).ToList();
            var same = here.Where(p =>
            {
                var file = new FileInfo(p.Local);
                return PartFiles.LooksIdentical(file.Length, file.LastWriteTimeUtc, p.Size, p.ModifiedUnixMs);
            }).ToHashSet();
            return (here, same);
        });
        foreach (var item in existing) wasThere.Add(item.Local);

        if (existing.Count == 0) return plan;

        NoteIdentical(identical.Count, TransferDirection.Download);
        plan = plan.Where(p => !identical.Contains(p)).ToList();

        var clashing = existing.Where(p => !identical.Contains(p)).ToList();
        if (clashing.Count == 0) return plan;

        asking?.Invoke();
        var answer = AskOverwrite(
            clashing.Select(p => Path.GetFileName(p.Local)).ToList(),
            "in that folder", "download");

        return answer switch
        {
            MessageBoxResult.Yes => plan,
            MessageBoxResult.No => plan.Where(p => !clashing.Contains(p)).ToList(),
            _ => null,
        };
    }

    /// <summary>
    /// The optional check after a transfer: both PCs hash every file that just moved; any that
    /// differ are added to the transfer's failures, so the row and History say so by name.
    /// </summary>
    private async Task CheckTransferAsync(ApiClient api, TransferStats stats,
        List<(string Remote, string Local)> files, CancellationToken ct)
    {
        if (files.Count == 0) return;
        if (!(await api.EnsureCapsAsync(ct)).Contains(Caps.Hash))
        {
            ActivityLog.Note(TransferDirection.Download, "Not checked: the other PC's FastShare is too old to compare files");
            return;
        }

        // The UI tick shows it (bottom bar, the bar beside the speed, and the row) from stats.Check.
        stats.Check = new Verify.CheckProgress(0, files.Count, 0, 0, false);
        List<string> problems;
        try
        {
            problems = await Task.Run(() => Verify.CompareAsync(api, files, p => stats.Check = p, ct), ct);
        }
        finally { stats.Check = null; }
        foreach (var problem in problems) stats.Fail(problem, new IOException("check failed"));
        ActivityLog.Note(TransferDirection.Download, problems.Count == 0
            ? $"Checked {files.Count:N0} file(s): all identical on both PCs"
            : $"Checked {files.Count:N0} file(s): {problems.Count:N0} differ or could not be checked");
    }

    private void NoteIdentical(int count, TransferDirection direction)
    {
        if (count == 0) return;
        ActivityLog.Note(direction, $"Skipped {count:N0} file(s) already there (same size and date)");
    }

    /// <summary>
    /// The one overwrite question, asked the same way wherever files are about to land on
    /// top of files - downloading, uploading, dropping in from Explorer or pasting.
    /// </summary>
    private MessageBoxResult AskOverwrite(IReadOnlyList<string> names, string where, string verb)
    {
        var dialog = new OverwriteDialog(this, names, where, verb);
        dialog.ShowDialog();
        return dialog.Result;
    }

    /// <summary>
    /// Takes back a cancelled download: the files it wrote go, the folders it made go if
    /// they are empty, and anything that was already there is left exactly alone.
    /// </summary>
    private static Task UndoDownloadAsync(Downloader downloader, HashSet<string> wasThere,
        List<string> weCreated)
    {
        var removed = 0;

        foreach (var path in downloader.Written)
        {
            if (wasThere.Contains(path)) continue;
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed++;
            }
            catch { }
        }

        // Deepest first, so a tree of folders this transfer created unwinds completely
        // rather than leaving the parents behind.
        foreach (var folder in weCreated.OrderByDescending(f => f.Length))
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch { }
        }

        if (removed > 0)
        {
            ActivityLog.Note(TransferDirection.Download,
                $"Cancelled — removed {removed:N0} file(s) that had already arrived");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The same for a cancelled upload, asked of the far side. Best effort: it needs Delete
    /// permission over there, and not having it is not a reason to fail the cancel.
    /// </summary>
    private static async Task UndoUploadAsync(ApiClient api, Uploader uploader, HashSet<string> wasThere)
    {
        var paths = uploader.Written.Where(p => !wasThere.Contains(p)).Distinct().ToList();
        if (paths.Count == 0) return;

        try
        {
            var error = await api.DeleteAsync(paths, CancellationToken.None);
            ActivityLog.Note(TransferDirection.Upload, error is null
                ? $"Cancelled — removed {paths.Count:N0} file(s) already sent"
                : $"Cancelled — could not remove what was already sent ({error})");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("transfer", "could not undo a cancelled upload", ex);
        }
    }

    /// <summary>
    /// The source disk of the download being planned, learned from the manifest the far side
    /// sends. Set as a side effect of planning because the plan itself is just paths; read by
    /// the caller to log "SSD → HDD". Null until a folder is listed (a loose-file selection
    /// fetches no manifest, so its source end stays unknown).
    /// </summary>
    private string? _planSourceDrive;

    private async Task<(List<PlanItem> Plan, List<string> EmptyFolders)> BuildDownloadPlanAsync(
        IFileSource source, List<ListRow> rows, string destination, CancellationToken ct)
    {
        var plan = new List<PlanItem>();
        var emptyFolders = new List<string>();
        _planSourceDrive = null;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (!row.IsDirectory)
            {
                var local = SafeLocal(destination, row.Name);
                if (local is not null)
                {
                    plan.Add(new PlanItem(row.RemotePath, local, row.Size,
                        new DateTimeOffset(row.Modified).ToUnixTimeMilliseconds()));
                }
                continue;
            }

            // One request returns the whole subtree, so a deep folder costs a single round
            // trip to plan rather than one per directory level.
            TransferText.Text = $"Listing {row.Name}…";
            var manifest = await source.ManifestAsync(row.RemotePath, ct);
            _planSourceDrive ??= manifest.SourceDrive;

            foreach (var file in manifest.Files)
            {
                var relative = Path.Combine(row.Name, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var local = SafeLocal(destination, relative);
                if (local is not null)
                    plan.Add(new PlanItem($"{row.RemotePath}/{file.Path}", local, file.Size, file.ModifiedUnixMs));
            }

            foreach (var folder in manifest.EmptyDirs ?? [])
            {
                var relative = folder.Length == 0
                    ? row.Name
                    : Path.Combine(row.Name, folder.Replace('/', Path.DirectorySeparatorChar));
                if (SafeLocal(destination, relative) is { } local) emptyFolders.Add(local);
            }
        }

        // Loose files fetch no manifest, so their disk was never learned ("? → NVMe"). Ask:
        // our own shares answer from this PC, anyone else from theirs.
        if (_planSourceDrive is null && plan.Count > 0)
        {
            var first = plan[0].Remote;
            if (source is LocalSource local) _planSourceDrive = local.DriveOf(first);
            else if (_connections.FirstOrDefault(c => ReferenceEquals(c.Source, source))?.Writer is { } api)
                _planSourceDrive = await api.DriveForAsync(first, ct);
        }

        return (plan, emptyFolders);
    }

    /// <summary>
    /// Keeps every write inside the chosen folder. These paths come from the far side, and
    /// one sending "..\..\Windows\System32\..." would otherwise write anywhere this user can.
    /// </summary>
    private static string? SafeLocal(string destination, string relative)
    {
        try
        {
            var root = Path.GetFullPath(destination);
            var full = Path.GetFullPath(Path.Combine(root, LocalName(relative)));

            // Conditional separator, for the same reason ShareSet.IsInside needs one: a
            // destination of "D:\" already ends with it, and appending another matches nothing.
            var prefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Makes a remote path safe to use as a local one, segment by segment.
    ///
    /// Two separate problems. Characters Windows forbids in a filename have to go, since the
    /// far side may be running anything. And a segment like "E:" - which is exactly what a
    /// shared drive is now called - is drive-relative, so Path.Combine would DISCARD the
    /// destination and resolve against that drive instead, quietly writing somewhere else
    /// entirely or failing the containment check and skipping the file.
    /// </summary>
    private static string LocalName(string relative)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var cleaned = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            var segment = new string(part.Where(c => !invalid.Contains(c)).ToArray()).Trim();

            // "." and ".." would walk out of the destination; an emptied segment has no name.
            if (segment.Length == 0 || segment is "." or "..") continue;
            cleaned.Add(segment);
        }

        return cleaned.Count == 0 ? "_" : Path.Combine([.. cleaned]);
    }

    // =======================================================================
    // Upload
    // =======================================================================

    /// <summary>
    /// Opens the picker and sends whatever comes back - files, folders, or a mix. No question
    /// first: which kind you are sending is something the picker can simply accept.
    /// </summary>
    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        if (!CanSendHere || _currentPath.Length == 0) return;

        // One folder highlighted means "put it in there", without having to open it first.
        var into = FileList.SelectedItems.Cast<ListRow>().ToList() is [{ IsDirectory: true } folder]
            ? folder.RemotePath
            : _currentPath;

        var picking = _browsingLocal ? null : new Preparing(_active?.Writer, download: false);
        picking?.Say("picking");
        var picker = new PickerDialog(this, _config.LastUploadFrom);
        if (picker.ShowDialog() != true || picker.Chosen.Count == 0) { picking?.Clear(); return; }

        _config.LastUploadFrom = Path.GetDirectoryName(picker.Chosen[0]);
        _config.Save();

        TransferText.Text = "Reading the chosen files…";
        var chosen = picker.Chosen;
        var items = await Task.Run(() => BuildUploadItems(chosen));
        TransferText.Text = "";
        if (items.Count == 0)
        {
            picking?.Clear();
            TransferText.Text = "Nothing to send.";
            return;
        }

        // On localhost there is no socket to send over - "uploading" into your own share is a
        // copy into the folder you are standing in.
        if (_browsingLocal) await CopyIntoLocalAsync(items);
        else await SendAsync(items, into);
    }

    /// <summary>
    /// Turns a mixed list of local paths into upload items. Shared by the picker and by
    /// dropping files in from Explorer, so both behave identically.
    /// </summary>
    private static List<UploadItem> BuildUploadItems(IEnumerable<string> paths)
    {
        var items = new List<UploadItem>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                // Keep the folder itself, so sending "Photos" makes "Photos" over there
                // rather than scattering its contents into the current folder.
                items.AddRange(EnumerateLocal(path,
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(path))));
                continue;
            }

            FileInfo file;
            try { file = new FileInfo(path); if (!file.Exists) continue; }
            catch { continue; }

            items.Add(ItemFor(file, file.Name));
        }

        return items;
    }

    private static UploadItem ItemFor(FileInfo file, string relative) =>
        new(file.FullName, relative, file.Length, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds());

    private async void OnUploadFilesClick(object sender, RoutedEventArgs e)
    {
        if (!CanSendHere || _currentPath.Length == 0) return;

        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Files to send", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;

        var items = dialog.FileNames
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .Select(file => ItemFor(file, file.Name))
            .ToList();

        await SendAsync(items);
    }

    private async void OnUploadFolderClick(object sender, RoutedEventArgs e)
    {
        if (!CanSendHere || _currentPath.Length == 0) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to send" };
        if (dialog.ShowDialog(this) != true) return;

        var root = dialog.FolderName;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        await SendAsync(EnumerateLocal(root, name));
    }

    private async Task SendAsync(List<UploadItem> items, string? destination = null,
        string? connectionId = null, int attempt = 1, string? pendingId = null,
        Func<long, long, string, Task<bool>>? onNoSpace = null)
    {
        if (items.Count == 0)
        {
            TransferText.Text = "Nothing to send.";
            return;
        }

        var target = destination ?? _currentPath;

        // Pinned now, at queue time, for the same reason as downloads: a queued upload must
        // go to the PC it was aimed at, not whichever one is on screen when its turn comes.
        var chosen = connectionId is null ? _active
            : await WaitForConnectionAsync(connectionId, TimeSpan.FromMinutes(2));
        if (chosen is not { Writer: { } api } connection)
        {
            TransferText.Text = "Not connected to anyone to send to.";
            if (connectionId is not null)
                ActivityLog.Error("Couldn't try the upload again: that PC is not connected. Reconnect, then use Retry failed files.");
            return;
        }
        var source = connection.Source;
        var peerName = PeerNameOf(connection);
        var prep = new Preparing(api, download: false);
        prep.Say("listing");

        // Ask before landing on top of anything over there.
        //
        // Sending a tree needs a recursive manifest of the destination to answer it, but
        // sending a handful of loose files only needs that one folder listed - and a share
        // with 28,000 files in it should not be walked end to end to check four names.
        var nested = items.Exists(i => i.Relative.Contains('/'));
        var alreadyThere = await RemoteFilesAsync(source, target, nested);

        var existing = items.Where(i => !i.IsEmptyFolder && alreadyThere.ContainsKey(i.Relative)).ToList();
        var identical = existing.Where(i =>
        {
            var there = alreadyThere[i.Relative];
            return PartFiles.LooksIdentical(there.Size,
                DateTimeOffset.FromUnixTimeMilliseconds(there.ModifiedUnixMs).UtcDateTime, i.Size, i.ModifiedUnixMs);
        }).ToHashSet();

        var clashing = existing.Where(i => !identical.Contains(i)).ToList();
        items = items.Where(i => !identical.Contains(i)).ToList();
        NoteIdentical(identical.Count, TransferDirection.Upload);

        if (clashing.Count > 0)
        {
            prep.Say("overwrite");
            var answer = AskOverwrite(clashing.Select(i => i.Relative).ToList(),
                "on the other PC", "send");

            if (answer == MessageBoxResult.Cancel)
            {
                prep.Clear();
                TransferText.Text = "Cancelled.";
                return;
            }

            if (answer == MessageBoxResult.No)
                items = items.Where(i => !clashing.Contains(i)).ToList();
        }

        if (items.Count == 0)
        {
            prep.Clear();
            TransferText.Text = identical.Count > 0 ? "Everything is already there." : "Nothing left to send.";
            return;
        }

        // Files that were already there are not ours to delete if this is cancelled.
        var wasThere = new HashSet<string>(
            clashing.Select(i => $"{target.TrimEnd('/')}/{i.Relative}"),
            StringComparer.OrdinalIgnoreCase);

        // Distinct destination folders, so "sent folders/files" means the same thing as the
        // download side rather than counting only the top-level one.
        foreach (var folder in items
                     .Select(i => i.Relative.Contains('/')
                         ? $"{target}/{i.Relative[..i.Relative.LastIndexOf('/')]}"
                         : target)
                     .Distinct())
        {
            ActivityLog.Folder(TransferDirection.Upload, folder);
        }

        // Which disk we are reading from. The destination disk is on the far side, so it is
        // fetched from the host once the transfer starts (below).
        var srcDrive = DriveDetect.Label(DriveDetect.ForPaths(items.Select(i => i.Local)));
        if (srcDrive == "?") srcDrive = null;

        var sending = items;
        var sendFiles = sending.Count(i => !i.IsEmptyFolder);
        var sendBytes = sending.Where(i => !i.IsEmptyFolder).Sum(i => i.Size);

        await EnqueueAsync(TransferDirection.Upload, $"{sendFiles:N0} file(s) to {peerName} ({Format.Bytes(sendBytes)})", async () =>
        {
            Lane.ConnectionId = connection.Id;
            if (pendingId is null)
            {
                // What was picked on this PC - the top-level files and folders - so a resume can
                // look at them afresh rather than keep a list of every file.
                var picked = sending.Select(i =>
                {
                    var rel = i.Relative.Replace('/', Path.DirectorySeparatorChar);
                    var root = i.Local.EndsWith(rel, StringComparison.OrdinalIgnoreCase) ? i.Local[..^rel.Length] : Path.GetDirectoryName(i.Local) + Path.DirectorySeparatorChar;
                    var first = i.Relative.Split('/')[0];
                    return Path.Combine(root, first);
                }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                pendingId = TrackPending(connection, download: false, target,
                    picked.Count == 1 ? $"\"{Path.GetFileName(picked[0])}\"" : $"{picked.Count:N0} items", null, picked);
            }
            Lane.PendingId = pendingId;
            Lane.Prep = prep;
            prep.Say("listing");   // was "queued" if it had to wait
            if (!_connections.Contains(connection))
            {
                ActivityLog.Error($"The connection to {connection.Name} closed before a queued upload could start.");
                return;
            }

            // Set when the job runs, not when it is queued: queuing used to overwrite the file
            // list of the transfer already running, which then saved the wrong one to History.
            _batch = new BatchInfo
            {
                From = "you",
                To = peerName,
                Folders = sending
                    .Where(i => i.Relative.Contains('/'))
                    .Select(i => i.Relative[..i.Relative.IndexOf('/')])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Files = sending.Where(i => !i.IsEmptyFolder).Select(i => i.Relative).ToList(),
                SourceDrive = srcDrive,
            };

            var uploader = new Uploader(api) { CurrentRate = () => _upMeter.BytesPerSecond };
            _undoCancel = () => UndoUploadAsync(api, uploader, wasThere);

            await RunTransferAsync(uploader.Stats, TransferDirection.Upload, "Sent", async ct =>
            {
                // Ask the host what disk our files are landing on, so the row reads "SSD → HDD" -
                // and how much room it has, so a transfer that cannot fit stops now.
                var (dstDrive, free) = await api.DriveInfoForAsync(target, ct);
                while (free is { } room && sendBytes + (64L << 20) > room)
                {
                    if (onNoSpace is null)
                    {
                        prep.NoSpace(sendBytes, room);
                        throw new NotEnoughSpaceException(
                            $"{peerName} does not have enough free space: this transfer needs {Format.Bytes(sendBytes)} and they have {Format.Bytes(room)} free there.");
                    }
                    if (!await onNoSpace(sendBytes, room, "their drive")) throw new TransferDiscardedException("Discarded - not enough free space");
                    (_, free) = await api.DriveInfoForAsync(target, ct);   // measured again after they freed some
                }
                if (_batch is { } b) b.DestDrive = dstDrive;
                if (_activeRow is { } liveRow)
                {
                    liveRow.Entry.SourceDrive = srcDrive;
                    liveRow.Entry.DestDrive = dstDrive;
                    liveRow.Refresh();
                }

                // Announce so the receiving side shows one batch with a real percentage, and
                // learns our source drive for its own peer-driven row.
                await prep.StartedAsync();
                await api.AnnounceAsync(false, sendBytes, sendFiles, true, ct, srcDrive);
                var outcome = "failed";
                try
                {
                    await uploader.RunAsync(target, sending, ct);

                    if (_config.CheckFiles)
                    {
                        var sent = uploader.Written.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        await CheckTransferAsync(api, uploader.Stats, sending
                            .Where(i => !i.IsEmptyFolder && sent.Contains($"{target}/{i.Relative}"))
                            .Select(i => ($"{target}/{i.Relative}", i.Local)).ToList(), ct);
                    }
                    outcome = uploader.Stats.Errors.Count > 0 ? $"partial-{uploader.Stats.Errors.Count}" : "done";
                }
                catch (OperationCanceledException) { outcome = "cancelled"; throw; }
                finally
                {
                    await api.AnnounceAsync(false, sendBytes, sendFiles, false, CancellationToken.None, result: outcome);
                }
            }, next => SendAsync(items, target, connection.Id, next, pendingId, onNoSpace), attempt);

            await NavigateAsync(_currentPath, pushHistory: false);
        }, connection);
    }

    /// <summary>
    /// Every file already under a remote folder, relative to it. Used to spot overwrites
    /// before they happen; an unreachable or missing folder answers "nothing", which lets
    /// the transfer go ahead rather than blocking it over a question we could not ask.
    /// </summary>
    private static async Task<Dictionary<string, (long Size, long ModifiedUnixMs)>> RemoteFilesAsync(
        IFileSource source, string target, bool recursive)
    {
        var known = new Dictionary<string, (long Size, long ModifiedUnixMs)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (recursive)
            {
                var manifest = await source.ManifestAsync(target, CancellationToken.None);
                foreach (var file in manifest.Files) known[file.Path] = (file.Size, file.ModifiedUnixMs);
            }
            else
            {
                foreach (var entry in await source.ListAsync(target, CancellationToken.None))
                {
                    if (!entry.IsDirectory) known[entry.Name] = (entry.Size, entry.ModifiedUnixMs);
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("upload", $"could not list \"{target}\" before sending", ex);
        }

        return known;
    }

    private static List<UploadItem> EnumerateLocal(string root, string prefix)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        string Relative(string path)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative == ".") relative = "";
            return prefix.Length == 0 ? relative : relative.Length == 0 ? prefix : $"{prefix}/{relative}";
        }

        var items = new List<UploadItem>();
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            FileInfo file;
            try { file = new FileInfo(path); if (!file.Exists) continue; }
            catch { continue; }

            items.Add(ItemFor(file, Relative(path)));
        }

        // Folders with nothing in them are sent too, so the tree arrives with its shape.
        var shallow = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        foreach (var folder in Directory.EnumerateDirectories(root, "*", options).Prepend(root))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(folder, "*", shallow).Any())
                    items.Add(new UploadItem(folder, Relative(folder), -1));
            }
            catch { }
        }
        return items;
    }

    // =======================================================================
    // Delete
    // =======================================================================

    /// <summary>
    /// Drops every connection in both directions: the one we opened, and everyone who opened
    /// one to us. Sharing itself keeps running, so people can reconnect.
    /// </summary>
    /// <summary>
    /// Stops browsing everything. Deliberately does NOT end anyone's session or stop sharing:
    /// this button lives on the Browse tab and its job is the browse views. Cutting people off
    /// from our folders is the Share tab's business, where it can be done per person.
    /// </summary>
    private void OnDisconnectAllClick(object sender, RoutedEventArgs e)
    {
        if (_connections.Count == 0) return;

        var names = string.Join(", ", _connections.Select(c => c.Name));

        var confirm = MessageBox.Show(this,
            $"""
             Stop browsing {_connections.Count} connection(s)?

             {names}

             Any transfer in progress will be cancelled. This does not stop sharing your own
             folders, and nobody is disconnected from you.
             """,
            "Close all", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        CancelAll();

        foreach (var connection in _connections.ToList()) CloseConnection(connection, announce: false, leave: true);

        ActivityLog.Note(TransferDirection.Download, "Stopped browsing everything", LogKind.Network);
    }

    private void OnSessionAskShareBackClick(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionStore.Session session) AskForShareBack(session.DisplayName);
    }

    private void OnSessionMenuOpened(object sender, RoutedEventArgs e)
    {
        var session = SessionList.SelectedItem as SessionStore.Session;
        var picked = session is not null;
        SessionMenuConnect.IsEnabled = picked;
        SessionMenuRename.IsEnabled = picked;
        SessionMenuDisconnect.IsEnabled = picked;

        SessionMenuFavorite.IsEnabled = picked;
        SessionMenuFavorite.Header = picked && IsFavorite(session!.PeerId)
            ? "Remove from favorites"
            : "Add to favorites";
    }

    private void OnSessionFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionStore.Session session) ToggleFavorite(session.PeerId);
    }

    /// <summary>
    /// Filters the recent list. Matches the name we call them and the address, so both a
    /// nickname and a half-remembered IP find the right row.
    /// </summary>
    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ReloadRecent();

    private void OnDisconnectSessionClick(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionStore.Session session) return;
        DisconnectPeer(session.PeerId, session.DisplayName);
    }

    /// <summary>
    /// Ends someone's access to our folders. This is the real disconnection - their session
    /// goes, and they are told at once rather than finding out on their next poll.
    /// </summary>
    private void DisconnectPeer(string peerId, string name)
    {
        var confirm = MessageBox.Show(this,
            $"""
             Stop sharing with {name}?

             They lose access to your folders immediately. They can ask to connect again, and
             you would approve it as usual.
             """,
            "Stop sharing", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _server.Sessions.Revoke(peerId, DenyReasons.Removed);
        _server.Tunnels.Drop(peerId);

        // If this was someone browsing us back over a tunnel, that tunnel serves them and
        // must come down too.
        if (string.Equals(peerId, _reversePeerId, StringComparison.OrdinalIgnoreCase))
        {
            _reversePeerId = null;
            _reverseConnection = null;
            SyncShareBack();
        }

        // Their tunnel is gone, so anything of theirs we had open is now dead.
        var browsing = _connections.FirstOrDefault(c => c.Id == "peer:" + peerId);
        if (browsing is not null) CloseConnection(browsing, announce: false);

        ActivityLog.Note(TransferDirection.Upload, $"Stopped sharing with {name}", LogKind.Network);
        UpdateConnectionSummary();
    }

    /// <summary>A delete is running; further presses are ignored until it is done.</summary>
    private bool _deleting;

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        // Checked here as well as on the menu item. The prompt used to appear regardless and
        // the server then refused it, so confirming a deletion did nothing and said nothing.
        if (!CanDeleteHere || _deleting) return;

        var selected = FileList.SelectedItems.Cast<ListRow>().ToList();
        if (selected.Count == 0) return;

        var folders = selected.Count(r => r.IsDirectory);
        var summary = selected.Count == 1
            ? $"\"{selected[0].Name}\""
            : $"{selected.Count} items ({folders} folder(s))";

        var where = _browsingLocal ? "from this PC" : "from the other PC";
        var confirm = MessageBox.Show(this,
            $"Permanently delete {summary} {where}?\n\nThis cannot be undone — it does not go to a Recycle Bin.",
            "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        // A big folder takes a while (a 160 GB one, minutes on a hard drive). Say so at once and
        // keep the window alive: the local delete used to run on the UI thread, which froze it
        // and made it look as if the button had not been pressed.
        _deleting = true;
        DeleteButton.IsEnabled = false;
        TransferText.Text = $"Deleting {summary}…";
        try
        {
            var paths = selected.Select(r => r.RemotePath).ToList();
            var shares = LocalShares;
            var error = _browsingLocal
                ? await Task.Run(() => LocalFs.Delete(shares, paths))
                : _api is { } api ? await api.DeleteAsync(paths, CancellationToken.None)
                : "Not connected.";

            TransferText.Text = error is null ? $"Deleted {summary}." : "Some items could not be deleted.";
            ActivityLog.Note(TransferDirection.Upload, error is null ? $"Deleted {summary}" : $"Delete of {summary} incomplete");
            if (error is not null)
                MessageBox.Show(this, error, "Not everything was deleted", MessageBoxButton.OK, MessageBoxImage.Warning);
            await NavigateAsync(_currentPath, pushHistory: false);
        }
        catch (Exception ex)
        {
            TransferText.Text = "Delete failed.";
            MessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _deleting = false;
            UpdateButtons();
        }
    }

    // =======================================================================
    // Transfer plumbing
    // =======================================================================

    // What the transfer about to run is moving lives on its lane (_batch, in MainWindow.xaml.cs):
    // set by the job just before it starts, read by the summary afterwards.

    /// <summary>How many times a transfer that failed on the network is tried again by itself.</summary>
    private const int AutoAttempts = 3;

    /// <param name="retry">Runs the same transfer again (attempt number given). Files that
    /// already arrived intact are skipped as identical and a big file resumes from its part
    /// file, so a retry only moves what is missing. Null for transfers that can't be repeated.</param>
    private async Task RunTransferAsync(TransferStats stats, TransferDirection direction,
        string verb, Func<CancellationToken, Task> work, Func<int, Task>? retry = null, int attempt = 1)
    {
        _cts = new CancellationTokenSource();

        if (direction == TransferDirection.Download) { _downStats = stats; _downMeter.Reset(); }
        else { _upStats = stats; _upMeter.Reset(); }

        UpdateButtons();

        // The row appears the moment the transfer starts and is updated in place, so the
        // Session tab shows what is happening rather than only what has happened.
        _activeRow = AddTransferRow(new HistoryEntry
        {
            WhenUtc = DateTime.UtcNow,
            Sent = direction == TransferDirection.Upload,
            Peer = _batch?.Counterparty ?? CurrentPeerName(),
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await work(_cts.Token);
            stopwatch.Stop();

            var total = stats.BytesDone;
            var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);

            var summary =
                $"{verb} {stats.FilesDone:N0} files, {Format.Bytes(total)} in {stopwatch.Elapsed:mm\\:ss} " +
                $"(average {Format.Rate(total / seconds)})";
            TransferText.Text = summary;
            NotifyIfAway((stats.Errors.Count > 0 ? "Transfer finished with problems" : "Transfer finished", summary));

            // A one-line record in log.txt (always on, so it rides along in a bug report). This
            // is the line that answers "why was it slow": the average rate next to the disk ends
            // is usually the whole story - a fast link into an HDD reads exactly like this.
            Diagnostics.Log("transfer",
                $"{direction} · {stats.FilesDone:N0} files · {Format.Bytes(total)} · {seconds:0.0}s · " +
                $"avg {Format.Rate(total / seconds)} · drives {_batch?.SourceDrive ?? "?"}→{_batch?.DestDrive ?? "?"} · " +
                $"peer {_batch?.Counterparty ?? CurrentPeerName()}" +
                (stats.Errors.Count > 0 ? $" · {stats.Errors.Count} failed" : ""));

            // The full file list is kept behind the row rather than written out as lines.
            if (_batch is { } batch) _activeRow.Details = batch.Lines();

            // Failures go through the error path, which raises a dialog and keeps them out of
            // the status bar - unless the transfer is about to be tried again by itself: then an
            // "SSL connection could not be established" box for a file that is seconds from
            // arriving anyway is just alarming. They go to the log instead.
            var retrying = WillRetryBySelf(retry, attempt, stats.Errors);
            foreach (var problem in stats.Errors)
            {
                if (retrying) ActivityLog.Note(direction, "Will retry: " + problem);
                else ActivityLog.Error(problem);
            }

            if (stats.Errors.Count > 0)
            {
                _activeRow.Entry.Failed = stats.Errors.ToList();
                AddFailedFiles(_activeRow, stats.Errors);
            }

            _activeRow.Entry.Files = stats.FilesDone;
            _activeRow.Entry.Folders = _batch?.Folders.Count ?? 0;
            _activeRow.Entry.Bytes = total;
            _activeRow.Entry.Seconds = stopwatch.Elapsed.TotalSeconds;
            _activeRow.Entry.FileNames = _batch?.Files.ToList();
            _activeRow.Entry.FolderNames = _batch?.Folders.ToList();
            _activeRow.Entry.SourceDrive = _batch?.SourceDrive;
            _activeRow.Entry.DestDrive = _batch?.DestDrive;
            FinishRow(_activeRow, stats.Errors.Count > 0
                ? $"{stats.Errors.Count:N0} failed - double-click for why"
                : null);

            RecordHistory(_activeRow.Entry);

            if (stats.Errors.Count > 0) OfferRetry(_activeRow, retry, attempt, stats.Errors);

            // Kept only if the connection is what stopped it; a clean finish, or files that
            // failed for their own reasons (in use, no room), are done with.
            SettlePending(Lane.PendingId, keep: stats.Errors.Any(LooksLikeNetwork), stats);
        }
        catch (OperationCanceledException) when (_pausingForClose && Lane.PendingId is not null)
        {
            // FastShare is closing: stop, keep what arrived, and offer it back next time.
            stopwatch.Stop();
            SettlePending(Lane.PendingId, keep: true, stats);
            _activeRow.Entry.Files = stats.FilesDone;
            _activeRow.Entry.Bytes = stats.BytesDone;
            _activeRow.Entry.Seconds = stopwatch.Elapsed.TotalSeconds;
            FinishRow(_activeRow, "Paused - FastShare was closed; resume next time you connect");
            RecordHistory(_activeRow.Entry);
            Diagnostics.Log("transfer", $"paused for close at {Format.Bytes(stats.BytesDone)} of {Format.Bytes(stats.TotalBytes)}");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            TransferText.Text = "Cancelled.";
            SettlePending(Lane.PendingId, keep: false, stats);   // cancelled on purpose: not offered back

            // Put back what was moved. A cancelled transfer that leaves half a folder behind
            // is worse than no transfer: you cannot tell by looking which files arrived, and
            // running it again just writes over the ones that did.
            // Removing what already arrived is one delete per file; on the UI thread that froze
            // the window for as long as it took, which read as "cancel isn't working".
            var undo = _undoCancel;
            if (undo is not null)
            {
                TransferText.Text = "Cancelled — removing what was already copied…";
                try { await Task.Run(undo); }
                catch (Exception ex) { Diagnostics.Log("transfer", "cancel cleanup failed", ex); }
                TransferText.Text = "Cancelled.";
            }

            ActivityLog.Note(direction, Lane.CancelledBy is { } by ? $"Cancelled by {by}" : "Cancelled");

            // Backing out of the overwrite prompt cancels before anything moves. That is a
            // decision not to transfer, not a transfer, so it leaves no row and no history.
            if (stats.FilesDone == 0 && stats.BytesDone == 0)
            {
                _transfers.Remove(_activeRow);
                UpdateTransferEmpty();
            }
            else
            {
                _activeRow.Entry.Files = stats.FilesDone;
                _activeRow.Entry.Folders = _batch?.Folders.Count ?? 0;
                _activeRow.Entry.Bytes = stats.BytesDone;
                _activeRow.Entry.Seconds = stopwatch.Elapsed.TotalSeconds;
                _activeRow.Entry.FileNames = _batch?.Files.ToList();
                _activeRow.Entry.FolderNames = _batch?.Folders.ToList();
                _activeRow.Entry.SourceDrive = _batch?.SourceDrive;
                _activeRow.Entry.DestDrive = _batch?.DestDrive;
                FinishRow(_activeRow, Lane.CancelledBy is { } who ? $"Cancelled by {who}" : "Cancelled");

                RecordHistory(_activeRow.Entry);
            }
        }
        catch (TransferDiscardedException discarded)
        {
            stopwatch.Stop();
            TransferText.Text = discarded.Message + ".";
            FinishRow(_activeRow, discarded.Message);
            SettlePending(Lane.PendingId, keep: discarded.Keep, stats);
            ActivityLog.Note(direction, discarded.Message);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("transfer", "failed", ex);
            TransferText.Text = "Transfer failed.";
            if (WillRetryBySelf(retry, attempt, [ex.Message])) ActivityLog.Note(direction, "Will retry: " + ex.Message);
            else ActivityLog.Error(ex.Message);
            FinishRow(_activeRow, ex is NotEnoughSpaceException ? "Not enough free space" : "Failed");
            OfferRetry(_activeRow, retry, attempt, [ex.Message]);
            SettlePending(Lane.PendingId, keep: ex is not NotEnoughSpaceException && LooksLikeNetwork(ex.Message), stats);
        }
        finally
        {
            Lane.CancelledBy = null;
            Lane.ConnectionId = null;
            Lane.PendingId = null;

            // Cleared so the next transfer cannot inherit this one's file list.
            _batch = null;
            _activeRow = null;
            _undoCancel = null;

            if (direction == TransferDirection.Download) _downStats = null; else _upStats = null;


            _cts?.Dispose();
            CommitLifetime();   // persist totals as we go, not only at exit
            _cts = null;
            UpdateButtons();
        }
    }

    /// <summary>
    /// After a transfer that did not fully succeed: the row gets "Retry failed files", and if
    /// the failures look like the connection dropping (not a full disk or a refused file), the
    /// transfer is tried again by itself - up to <see cref="AutoAttempts"/> times, once the PC
    /// is connected again. Only the missing files move on a retry.
    /// </summary>
    private void OfferRetry(SessionRow row, Func<int, Task>? retry, int attempt, IReadOnlyList<string> errors)
    {
        if (retry is null) return;
        row.Retry = () => retry(1);

        if (!WillRetryBySelf(retry, attempt, errors)) return;

        ActivityLog.Note(TransferDirection.Download,
            $"Connection trouble — trying again by itself in 15 seconds (attempt {attempt + 1} of {AutoAttempts})", LogKind.Network);
        _pendingRetries++;   // "shut down when done" must wait for it
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                row.Retry = null;   // this one is being retried; the new row carries its own
                await retry(attempt + 1);
            }
            finally { _pendingRetries--; }
        });
    }

    private static bool WillRetryBySelf(Func<int, Task>? retry, int attempt, IReadOnlyList<string> errors) =>
        retry is not null && attempt < AutoAttempts && errors.Any(LooksLikeNetwork);

    private static bool LooksLikeNetwork(string error) =>
        new[] { "connection", "transport", "prematurely", "stall", "timed out", "no answer", "reset", "network", "dropped", "host" }
            .Any(word => error.Contains(word, StringComparison.OrdinalIgnoreCase));

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelEverything();

    /// <summary>
    /// Every transfer: ours, and the ones other people are running against us (they are asked
    /// to stop). Used by the status bar's cancel button and its right-click menu.
    /// </summary>
    private void CancelEverything()
    {
        // Said at once, so the click visibly landed even while the transfer winds down.
        if (AnyRunning || TrafficMonitor.Active().Count > 0) TransferText.Text = "Cancelling…";

        // "All" includes what is waiting: cancelling only the running one just started the next.
        foreach (var lane in Lanes)
        {
            foreach (var job in lane.Queue.ToList()) Dequeued(job);
            lane.Queue.Clear();
        }
        RefreshQueueView();
        CancelAll();
        foreach (var flow in TrafficMonitor.Active()) CancelPeerFlow(flow.Key);
    }

    /// <summary>
    /// Stops a transfer someone else is running against us - them sending to us, or them
    /// pulling from us. We cannot stop it from here, so they are asked to (on their next ping,
    /// within a few seconds); the row ends now, marked as cancelled by us.
    /// </summary>
    private void CancelPeerFlow(string key)
    {
        var peerId = TrafficMonitor.PeerOf(key);
        var name = TrafficMonitor.Find(key)?.PeerName ?? peerId;
        _server.Sessions.AskCancel(peerId);
        TrafficMonitor.Stop(key, "Cancelled by you");
        Diagnostics.Log("transfer", $"asked {name} to cancel their transfer");
    }

    /// <summary>The other PC cancelled the transfer we are running with it.</summary>
    private void CancelledByThem(Connection connection)
    {
        foreach (var lane in Lanes.Where(l => l.Cts is not null && l.ConnectionId == connection.Id))
        {
            lane.CancelledBy = connection.Name;
            try { lane.Cts?.Cancel(); } catch { }
            TransferText.Text = $"{connection.Name} cancelled the transfer.";
            Diagnostics.Log("transfer", $"{connection.Name} cancelled the {lane.Direction} transfer");
        }
    }

    /// <summary>Right-click on the status bar.</summary>
    private void OnStatusMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        var busy = AnyRunning || TrafficMonitor.Active().Count > 0;
        var item = new MenuItem { Header = "Cancel all transfers", IsEnabled = busy };
        item.Click += (_, _) => CancelEverything();

        var shutdown = new MenuItem
        {
            Header = "Shut down PC when transfers finish",
            IsCheckable = true,
            IsChecked = ShutdownWhenDone,
            IsEnabled = HasWorkPending || ShutdownWhenDone,
        };
        shutdown.Click += (_, _) => SetShutdownWhenDone(shutdown.IsChecked);

        StatusBar.ContextMenu = new ContextMenu { Items = { item, new Separator(), shutdown } };
    }

    /// <summary>
    /// Whether anything is moving right now: a transfer of ours (or one queued behind it), or one
    /// someone else is driving against us. Used to guard the window close.
    /// </summary>
    /// <summary>For Settings: an update must not swap the exe out from under a transfer.</summary>
    public bool IsTransferring => TransferInProgress();

    private bool TransferInProgress() =>
        AnyRunning || _downStats is not null || _upStats is not null ||
        QueuedCount > 0 || TrafficMonitor.Active().Count > 0;
}
