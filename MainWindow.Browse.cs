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
    // How big the file list draws itself
    // =======================================================================

    /// <summary>
    /// The zoom steps, as multipliers on the normal row. Deliberately a short ladder rather
    /// than free scaling: a wheel notch should land somewhere sensible, not on 1.07.
    /// </summary>
    private static readonly double[] ViewScales = [0.8, 1.0, 1.25, 1.6, 2.0, 2.5, 3.2];

    /// <summary>
    /// Ctrl+scroll resizes the list, the way Explorer and every browser do. Handled at
    /// preview so the ListView does not scroll the rows as well as resizing them.
    /// </summary>
    private void OnFileListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        e.Handled = true;

        var step = _config.ViewScale + (e.Delta > 0 ? 1 : -1);
        step = Math.Clamp(step, 0, ViewScales.Length - 1);
        if (step == _config.ViewScale) return;

        _config.ViewScale = step;
        _config.Save();
        ApplyViewScale();

        FlashStatus(step == 0 ? "Details view" : $"Tiles {ViewScales[step] * 100:0}%");
    }

    private DispatcherTimer? _flashTimer;
    private string _flashText = "";

    /// <summary>
    /// A status-bar note that clears itself after a moment rather than sitting there until
    /// something else happens to overwrite it. Used for the view-size readout, which is only
    /// worth showing for the second or two after you change it.
    /// </summary>
    private void FlashStatus(string text)
    {
        TransferText.Text = text;
        _flashText = text;

        if (_flashTimer is null)
        {
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _flashTimer.Tick += (_, _) =>
            {
                _flashTimer!.Stop();
                // Only clear if nothing more important has taken the strip in the meantime.
                if (TransferText.Text == _flashText) TransferText.Text = "";
            };
        }

        _flashTimer.Stop();
        _flashTimer.Start();
    }

    /// <summary>
    /// Pushes the chosen size into the resources the row template binds to, so glyph, text
    /// and row height all grow together instead of the text outgrowing its row.
    /// </summary>
    private void ApplyViewScale()
    {
        var step = Math.Clamp(_config.ViewScale, 0, ViewScales.Length - 1);
        var factor = ViewScales[step];

        // The smallest size is the details list, with sortable column headers. Every larger
        // size is the tile grid: big icons that are easy to hit, no columns to squeeze, and
        // the sort headers gone - but the sort order they set still stands, because the rows
        // are ordered before they are bound whatever the view.
        var details = step == 0;

        if (details)
        {
            // Details stays at its normal readable size - it is the "small" end of the range,
            // and shrinking it further helped nobody. Growing happens by switching to tiles.
            Resources["RowFontSize"] = 13.0;
            Resources["RowGlyphSize"] = 15.0;
            Resources["RowHeight"] = 24.0;

            FileList.ItemTemplate = null;
            FileList.ItemContainerStyle = (Style)Resources["DetailsItemStyle"];
            FileList.ItemsPanel = (ItemsPanelTemplate)Resources["DetailsPanel"];
            FileList.View = DetailsView;

            for (var i = 0; i < DetailsView.Columns.Count && i < ColumnWidths.Length; i++)
                DetailsView.Columns[i].Width = ColumnWidths[i];

            // Then stretch Name to fill whatever the pane leaves, so nothing runs off the edge.
            FitColumns();
        }
        else
        {
            // Tiles scale from the same ladder, but from index 1 up - so the first tile step
            // is a sensible size rather than a giant jump off the details row. The tile grows
            // wider than the text does, and the name box grows to ~4 lines, so a long name
            // fills the space instead of being cut off after a few letters.
            var tile = factor;
            var font = 12.0 * tile;
            Resources["TileWidth"] = 92.0 * tile;
            Resources["TileGlyphSize"] = 34.0 * tile;
            Resources["TileFontSize"] = font;
            Resources["TileNameHeight"] = Math.Ceiling(font * 1.35 * 4);   // room for four lines

            FileList.View = null;   // a null View makes the ListView honour ItemTemplate/Panel
            FileList.ItemContainerStyle = (Style)Resources["TileItemStyle"];
            FileList.ItemsPanel = (ItemsPanelTemplate)Resources["TilePanel"];
            FileList.ItemTemplate = (DataTemplate)Resources["TileTemplate"];
        }
    }

    /// <summary>Column widths at 100%, in the order they appear. Scaled by the zoom.</summary>
    private static readonly double[] ColumnWidths = [360, 140, 140, 120, 100];

    /// <summary>Re-fit the details columns whenever the pane resizes, so nothing spills off.</summary>
    private void OnFileListSizeChanged(object sender, SizeChangedEventArgs e) => FitColumns();

    /// <summary>
    /// Widens (or narrows) the Name column to soak up exactly the space the other four leave,
    /// so the details list always fills its pane and never needs a sideways scrollbar. The
    /// fixed columns total 500 at 100%; everything past them belongs to Name.
    /// </summary>
    private void FitColumns()
    {
        if (FileList.View is not GridView || NameColumn is null) return;

        // ActualWidth minus the four fixed columns and a little for the vertical scrollbar and
        // the row's own margins. Never below a readable minimum.
        const double others = 140 + 140 + 120 + 100;
        var available = FileList.ActualWidth - others - 34;
        NameColumn.Width = Math.Max(180, available);
    }

    // =======================================================================
    // Browsing
    // =======================================================================

    private async Task NavigateAsync(string path, bool pushHistory = true)
    {
        if (_source is null) return;

        try
        {
            var entries = await _source!.ListAsync(path, CancellationToken.None);

            if (pushHistory)
            {
                _history.Push(_currentPath);
                // Navigating somewhere new abandons the forward trail, as a browser does.
                _forward.Clear();
            }
            _currentPath = path;

            _rows = ToRows(entries, path);
            ApplySort();
            UpdateEmptyHint();
            HighlightCurrentShare();

            RebuildCrumbs();
            BackButton.IsEnabled = _history.Count > 0;
            ForwardButton.IsEnabled = _forward.Count > 0;
            UpButton.IsEnabled = path.Length > 0;
            UpdateButtons();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("browse", $"list \"{path}\" failed", ex);
            TransferText.Text = $"Could not open folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Explains an empty list rather than leaving a blank panel. "They are sharing nothing" and
    /// "this did not load" look identical otherwise, and only one of them is worth worrying
    /// about.
    /// </summary>
    private void UpdateEmptyHint()
    {
        if (_rows.Count > 0 || _source is null)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyHint.Text = _currentPath.Length == 0
            ? "They are online but are not sharing any folders right now.\n\n" +
              "If they add one it will appear here within a few seconds."
            : "This folder is empty.";

        EmptyHint.Visibility = Visibility.Visible;
    }

    private static List<ListRow> ToRows(List<Entry> entries, string path) =>
        entries.Select(entry => new ListRow
        {
            Name = entry.Name,
            RemotePath = Join(path, entry.Name),
            IsDirectory = entry.IsDirectory,
            Size = entry.Size,
            Modified = DateTimeOffset.FromUnixTimeMilliseconds(entry.ModifiedUnixMs).LocalDateTime,
            Created = DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedUnixMs).LocalDateTime,
        }).ToList();

    /// <summary>
    /// Re-lists the folder on screen so files the other side adds appear without
    /// reconnecting. Only names, sizes and timestamps cross the wire, so this is one small
    /// request every few seconds - nothing reads file contents, and it costs far less than
    /// the transfer it runs alongside.
    /// </summary>
    private async Task AutoRefreshAsync()
    {
        if (_source is null || _refreshing) return;

        _refreshing = true;
        try
        {
            var path = _currentPath;
            var entries = await _source!.ListAsync(path, CancellationToken.None);

            // The user may have navigated while the request was in flight.
            if (path != _currentPath) return;

            var fresh = ToRows(entries, path);

            // Rebuilding every five seconds would fight the user's selection and scroll
            // position, so only touch the UI when something actually changed.
            if (Unchanged(fresh, _rows)) return;

            var selected = FileList.SelectedItems.Cast<ListRow>()
                .Select(r => r.RemotePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _rows = fresh;
            ApplySort();

            if (selected.Count > 0)
            {
                foreach (var row in FileList.Items.Cast<ListRow>()
                             .Where(r => selected.Contains(r.RemotePath)))
                {
                    FileList.SelectedItems.Add(row);
                }
            }
        }
        catch
        {
            // A failed refresh is not worth interrupting the user; the ping timer already
            // reports a dropped connection.
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// Keeps the sidebar in step with folders the other side adds or removes while we are
    /// connected. Only the top level is synced on a timer - deeper nodes refresh when the
    /// user expands them, which is both cheaper and less disruptive than rebuilding a tree
    /// somebody is mid-way through navigating.
    /// </summary>
    /// <summary>
    /// Keeps the sidebar's list of shares current. The far side can add or remove folders
    /// while we are connected, so this runs on the refresh timer as well as after connecting.
    /// </summary>
    private async Task RefreshTreeAsync()
    {
        if (_connections.Count == 0)
        {
            _sidebar.Clear();
            return;
        }

        // Each connection is asked for its own top level every time (this runs on the 5 s
        // refresh timer). It used to be fetched once and cached for good, so a folder the other
        // side shared after we connected never reached the sidebar - only clicking their name,
        // which lists afresh, showed it. The cache still makes switching instant; the sidebar is
        // only rebuilt when something actually changed, so it does not flicker or lose its place.
        var changed = _sidebar.Count == 0;
        foreach (var connection in _connections.ToList())
        {
            try
            {
                var entries = await connection.Source.ListAsync("", CancellationToken.None);
                var folders = entries
                    .Where(entry => entry.IsDirectory)
                    .Select(entry => entry.Name)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (connection.TopLevel is null || !connection.TopLevel.SequenceEqual(folders))
                {
                    connection.TopLevel = folders;
                    changed = true;
                }
            }
            catch
            {
                // Leave whatever we had; the ping and the watch report real failures.
                if (connection.TopLevel is null) { connection.TopLevel = []; changed = true; }
            }
        }

        if (_sidebar.Count(r => r.Share is null) != _connections.Count) changed = true;
        if (!changed) return;

        RebuildSidebar();
        HighlightCurrentShare();
    }

    private void RebuildSidebar()
    {
        _sidebar.Clear();

        foreach (var connection in _connections)
        {
            var folders = connection.TopLevel ?? [];

            _sidebar.Add(new SidebarRow
            {
                Owner = connection,
                // A PC sharing back reaches us over the link it opened to us, and that link only
                // carries reads. Said on the row, because their permissions screen can show
                // everything ticked and it otherwise looks like the ticks never arrived.
                Label = connection.Name +
                        (connection.Kind == ConnectionKind.Peer && connection.WriteApi is null ? " · download only" : ""),
            });

            foreach (var folder in folders)
                _sidebar.Add(new SidebarRow { Owner = connection, Share = folder, Label = folder });
        }
    }

    /// <summary>
    /// Selects whichever share the current folder lives under, so the sidebar tracks where you
    /// are even when you got there through the file list. Done without firing navigation.
    /// </summary>
    private void HighlightCurrentShare()
    {
        var top = _currentPath.Length == 0 ? "" : _currentPath.Split('/')[0];

        _syncingSidebar = true;
        try
        {
            ShareSidebar.SelectedItem = _sidebar.FirstOrDefault(r =>
                ReferenceEquals(r.Owner, _active) &&
                (top.Length == 0 ? r.IsRoot : r.Share?.Equals(top, StringComparison.OrdinalIgnoreCase) == true));
        }
        finally { _syncingSidebar = false; }
    }

    private static bool Unchanged(List<ListRow> a, List<ListRow> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal)) return false;
            if (a[i].Size != b[i].Size) return false;
            if (a[i].Modified != b[i].Modified) return false;
        }
        return true;
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string key }) return;

        // Dates start newest first and sizes biggest first, as Explorer does; names and types A-Z.
        if (_sortKey == key) _sortDescending = !_sortDescending;
        else { _sortKey = key; _sortDescending = key is "Created" or "Modified" or "Size"; }

        ApplySort();
    }

    /// <summary>Puts ▲/▼ on the header being sorted by, so a click visibly did something.</summary>
    private void ShowSortArrows()
    {
        foreach (var column in DetailsView.Columns)
        {
            if (column.Header is not GridViewColumnHeader { Tag: string key } header) continue;
            var label = header.Content as string ?? "";
            var arrow = label.IndexOfAny(['▲', '▼']);
            if (arrow > 0) label = label[..(arrow - 1)];
            header.Content = key == _sortKey ? $"{label} {(_sortDescending ? '▼' : '▲')}" : label;
        }
    }

    private void ApplySort()
    {
        ShowSortArrows();

        IEnumerable<ListRow> ordered = _sortKey switch
        {
            "Created" => _rows.OrderBy(r => r.Created),
            "Modified" => _rows.OrderBy(r => r.Modified),
            "Size" => _rows.OrderBy(r => r.Size),
            "Type" => _rows.OrderBy(r => r.TypeText, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => _rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        };

        var sorted = ordered.ToList();
        if (_sortDescending) sorted.Reverse();

        // Folders stay above files whichever column is sorted, the way Explorer does it.
        // OrderByDescending is stable, so the ordering just chosen survives inside each group.
        FileList.ItemsSource = sorted.OrderByDescending(r => r.IsDirectory).ToList();
    }

    private static string Join(string parent, string name) =>
        parent.Length == 0 ? name : $"{parent}/{name}";

    /// <summary>Suppresses the navigation a programmatic selection change would otherwise cause.</summary>
    private bool _syncingSidebar;

    private async void OnSidebarShareSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSidebar) return;
        if (ShareSidebar.SelectedItem is not SidebarRow row) return;

        // A different connection's row means switch to it first; otherwise we would navigate
        // one connection to a path that belongs to another.
        if (!ReferenceEquals(row.Owner, _active)) await SwitchToAsync(row.Owner);

        if (row.RemotePath == _currentPath) return;

        await NavigateAsync(row.RemotePath);

        // Picking a folder on the left means "I am working in here now", so the file list
        // takes focus with nothing selected. Without this, Ctrl+V after clicking the sidebar
        // went to the sidebar, which does nothing with it.
        FileList.UnselectAll();
        FileList.Focus();
    }

    /// <summary>
    /// Whether we can ask this person to offer their folders back.
    ///
    /// Only for a live inbound session. We cannot open anything towards them - that is the
    /// whole reason the tunnel exists - so the request rides on their next ping, and only a
    /// session gives us somewhere to put it.
    /// </summary>
    private bool CanAskForShareBack(Connection connection, out string who)
    {
        who = connection.Name;

        // Already sharing back, or ourselves: nothing to ask for.
        if (connection.Kind is ConnectionKind.Peer or ConnectionKind.Local) return false;

        var session = _server.Sessions.Active.FirstOrDefault(sess =>
            !sess.IsLoopback && sess.DisplayName.Equals(connection.Name, StringComparison.OrdinalIgnoreCase));

        if (session is null) return false;

        who = session.DisplayName;
        return true;
    }

    /// <summary>Sends the request and says so. They still have to agree.</summary>
    private void AskForShareBack(string name)
    {
        var session = _server.Sessions.Active.FirstOrDefault(s =>
            !s.IsLoopback && s.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (session is null || !_server.Sessions.AskForShareBack(session.PeerId))
        {
            MessageBox.Show(this, $"{name} is not connected any more.", "Cannot ask",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ActivityLog.Note(TransferDirection.Upload, $"Asked {name} to share their folders back",
            LogKind.Network);

        MessageBox.Show(this,
            $"""
             {name} has been asked to share their folders back.

             It will appear on their screen within a few seconds. They have to agree — you
             cannot start sharing on someone else's PC.
             """,
            "Asked to share back", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Right-clicking a share opens its menu and nothing else. It used to select the row too,
    /// which navigated - so a right-click moved you somewhere before you had chosen anything.
    /// </summary>
    private void OnSidebarRightDown(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject).FindListBoxItem() is { } item)
        {
            _syncingSidebar = true;
            try { ShareSidebar.SelectedItem = item.DataContext; }
            finally { _syncingSidebar = false; }
        }

        e.Handled = true;
    }

    private async void OnSidebarRootClick(object sender, RoutedEventArgs e)
    {
        if (_source is null || _currentPath.Length == 0) return;
        await NavigateAsync("");
    }

    private void OnSidebarMenuOpened(object sender, RoutedEventArgs e)
    {
        var row = ShareSidebar.SelectedItem as SidebarRow;
        var folder = row is { IsRoot: false } ? row : null;
        var owner = row?.Owner;

        // Offered for folders only. "Switch to this connection" duplicated what clicking
        // the row already does, and read as though it were a different thing.
        var openable = row is { IsRoot: false };
        SidebarMenuOpen.Visibility = openable ? Visibility.Visible : Visibility.Collapsed;
        SidebarMenuOpen.IsEnabled = openable;
        SidebarMenuOpen.Header = "Open";

        SidebarMenuDownload.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        SidebarMenuDownload.Header = folder is null
            ? "Download this folder"
            : $"Download \"{folder.Label}\"";

        // A person's row can be given our own name for them (not our own shares, not folders).
        SidebarMenuRename.Visibility = row is { IsRoot: true } && owner?.Kind != ConnectionKind.Local
            ? Visibility.Visible : Visibility.Collapsed;

        // Disconnecting belongs to the connection, so it is offered on its root row only -
        // not on every folder underneath it.
        SidebarMenuClose.Visibility = row is { IsRoot: true } ? Visibility.Visible : Visibility.Collapsed;

        // Reject and Block act on a person, so they are offered on a connection's own row and
        // never on a folder - and never on our own shares, where there is nobody to refuse.
        var person = row is { IsRoot: true } && row.Owner.Kind != ConnectionKind.Local;
        SidebarMenuSeparator.Visibility = person ? Visibility.Visible : Visibility.Collapsed;
        SidebarMenuReject.Visibility = person ? Visibility.Visible : Visibility.Collapsed;
        SidebarMenuBlock.Visibility = person ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSidebarDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (ShareSidebar.SelectedItem is SidebarRow { IsRoot: true } row) CloseConnection(row.Owner);
    }

    /// <summary>The X on a connection's sidebar row.</summary>
    private void OnSidebarRowCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SidebarRow { IsRoot: true } row }) CloseConnection(row.Owner);
        e.Handled = true;
    }

    /// <summary>
    /// Refuses this peer for the rest of the run. They can try again after a restart, which is
    /// the difference between this and Block.
    /// </summary>
    private void OnSidebarRejectClick(object sender, RoutedEventArgs e)
    {
        if (ShareSidebar.SelectedItem is not SidebarRow { IsRoot: true } row) return;
        if (PeerIdOf(row.Owner) is not { } peerId) return;

        var confirm = MessageBox.Show(this,
            $"""
             Kick {row.Owner.Name}?

             They lose access now and cannot reconnect until you restart FastShare.
             """,
            "Kick", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _server.Sessions.RejectPeer(peerId);
        _server.Tunnels.Drop(peerId);
        CloseConnection(row.Owner, announce: false);

        ActivityLog.Note(TransferDirection.Upload, $"Kicked {row.Owner.Name}", LogKind.Network);
        UpdateConnectionSummary();
    }

    /// <summary>Refuses them permanently. Undone only from Settings.</summary>
    private void OnSidebarBlockClick(object sender, RoutedEventArgs e)
    {
        if (ShareSidebar.SelectedItem is not SidebarRow { IsRoot: true } row) return;
        if (PeerIdOf(row.Owner) is not { } peerId) return;

        var confirm = MessageBox.Show(this,
            $"""
             Block {row.Owner.Name}?

             They lose access now and can never reconnect until you unblock them (Connections tab, under Favorites).
             """,
            "Block", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        if (!_config.Blocked.Any(b => string.Equals(b.PeerId, peerId, StringComparison.OrdinalIgnoreCase)))
        {
            _config.Blocked.Add(new BlockedPeer
            {
                PeerId = peerId,
                Username = row.Owner.Name,
                BlockedUtc = DateTime.UtcNow,
            });
            _config.Save();
        }

        _server.Sessions.Revoke(peerId, DenyReasons.Blocked);
        _server.Tunnels.Drop(peerId);
        CloseConnection(row.Owner, announce: false);

        ActivityLog.Note(TransferDirection.Upload, $"Blocked {row.Owner.Name}", LogKind.Network);
        UpdateConnectionSummary();
        RefreshBlocked();
    }

    /// <summary>
    /// Gives someone in the sidebar our own name for them. Stored against their identity
    /// (the same nickname the Connections tab's Rename sets), so every list shows it; an empty
    /// name goes back to the name they report.
    /// </summary>
    private void OnSidebarRenameClick(object sender, RoutedEventArgs e)
    {
        if (ShareSidebar.SelectedItem is not SidebarRow { IsRoot: true } row || row.Owner.Kind == ConnectionKind.Local) return;
        var connection = row.Owner;

        var typed = PromptDialog.Ask(this, "Rename", $"Name for {connection.Name}:", connection.Name);
        if (typed is null) return;
        var name = typed.Trim();

        if (PeerIdOf(connection) is { } peerId)
        {
            var reported = connection.Peer?.Username ?? connection.Info?.MachineName ?? connection.Name;
            SetNickname(peerId, reported, name.Length == 0 ? null : name);
            if (name.Length == 0) connection.Name = reported;
        }
        else if (connection.Info is { } info &&
                 _config.Recent.FirstOrDefault(r => r.Port == info.Port &&
                     r.Host.Equals(info.Host, StringComparison.OrdinalIgnoreCase)) is { } saved)
        {
            // An old saved entry with no identity yet: name the entry itself.
            saved.Name = name.Length == 0 ? null : name;
            _config.Save();
            ReloadRecent();
            connection.Name = saved.Name ?? info.DisplayName;
        }

        RebuildSidebar();
        HighlightCurrentShare();
        RebuildCrumbs();
        UpdateConnectionSummary();
    }

    /// <summary>The peer identity behind a connection, when there is one.</summary>
    private string? PeerIdOf(Connection connection)
    {
        if (connection.Kind == ConnectionKind.Peer) return connection.Id["peer:".Length..];
        if (connection.Kind == ConnectionKind.Local || connection.Info is null) return null;

        return _server.Sessions.Active.FirstOrDefault(sess =>
                   !sess.IsLoopback &&
                   sess.RemoteAddress.Equals(connection.Info.Host, StringComparison.OrdinalIgnoreCase))
                   ?.PeerId
               ?? _config.Recent.FirstOrDefault(r =>
                   r.Host.Equals(connection.Info.Host, StringComparison.OrdinalIgnoreCase))?.PeerId;
    }

    // ---- Permissions tab menu ----

    private async void OnSessionConnectClick(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionStore.Session session) return;

        // They are already connected to us, so there is nothing to dial. What "connect" can
        // usefully mean here is "let me see your files too".
        AskForShareBack(session.DisplayName);
        await Task.CompletedTask;
    }

    private void OnSessionRenameClick(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionStore.Session session) return;

        var name = PromptDialog.Ask(this, "Rename",
            $"Name for {session.Username}:", session.DisplayName);

        if (name is null) return;
        SetNickname(session.PeerId, session.Username, name);
    }

    private void OnSidebarAskShareBackClick(object sender, RoutedEventArgs e)
    {
        if (ShareSidebar.SelectedItem is SidebarRow { IsRoot: true } row) AskForShareBack(row.Owner.Name);
    }

    private async void OnSidebarOpenClick(object sender, RoutedEventArgs e)
    {
        if (ShareSidebar.SelectedItem is not SidebarRow row) return;

        if (!ReferenceEquals(row.Owner, _active)) await SwitchToAsync(row.Owner);
        await NavigateAsync(row.RemotePath);
    }

    /// <summary>Downloads a whole share straight from the sidebar, without opening it first.</summary>
    private async void OnSidebarDownloadClick(object sender, RoutedEventArgs e)
    {
        if (ShareSidebar.SelectedItem is not SidebarRow { IsRoot: false } share) return;
        if (!ReferenceEquals(share.Owner, _active)) await SwitchToAsync(share.Owner);

        var picking = new Preparing(_active?.Api, download: true);
        picking.Say("location");
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Save \"{share.Label}\" to" };
        if (Directory.Exists(_config.LastDestination)) dialog.InitialDirectory = _config.LastDestination;
        if (dialog.ShowDialog(this) != true) { picking.Clear(); return; }

        _config.LastDestination = dialog.FolderName;
        _config.Save();

        await DownloadToAsync(
            [new ListRow { Name = share.Label, RemotePath = share.RemotePath, IsDirectory = true }],
            dialog.FolderName);
    }

    private void OnFileListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeFrom is not null) EndMarquee();
    }

    private async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only act on an actual row. Double-clicking the empty space below the list used to
        // fall through to whatever happened to still be selected and pop up the Save-to
        // dialog, which is a startling thing to get from clicking nothing.
        if (!IsOverRow(e.OriginalSource as DependencyObject)) return;
        if (FileList.SelectedItem is not ListRow row) return;

        // A folder opens; a file downloads. Double-clicking a file to open it is the one
        // thing Explorer does that we cannot, so the next most useful meaning is "fetch it".
        if (row.IsDirectory) await NavigateAsync(row.RemotePath);
        else OnDownloadClick(sender, e);
    }


    private async void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;
        _forward.Push(_currentPath);
        await NavigateAsync(_history.Pop(), pushHistory: false);
    }

    private async void OnUpClick(object sender, RoutedEventArgs e)
    {
        if (_currentPath.Length == 0) return;
        var slash = _currentPath.LastIndexOf('/');
        await NavigateAsync(slash < 0 ? "" : _currentPath[..slash]);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var connected = _source is not null;
        var selected = FileList.SelectedItems.Count > 0;

        // The virtual root lists shares rather than a real folder, so nothing can be written
        // into it.
        var insideShare = _currentPath.Length > 0;

        // Deliberately not gated on whether a transfer is running - extra requests join the
        // queue instead of greying the button out mid-work.
        DownloadButton.IsEnabled = connected && selected;
        UploadButton.IsEnabled = CanSendHere && insideShare;
        DeleteButton.IsEnabled = CanDeleteHere && selected && !_deleting;

        // Disconnect is about connections, not about what is selected, so it stays available
        // whenever there is anything to drop.
        DisconnectButton.IsEnabled = connected ||
                                     _server.Sessions.Active.Any(x => !x.IsLoopback) ||
                                     _server.Tunnels.Usable.Count > 0;

        CancelButton.Visibility = AnyRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    // =======================================================================
    // Breadcrumbs
    // =======================================================================

    /// <summary>One clickable level in the address bar.</summary>
    public sealed record Crumb(string Name, string Path, bool Last)
    {
        public Visibility ChevronVisibility => Last ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RebuildCrumbs()
    {
        var crumbs = new List<Crumb>();

        var rootName = _active?.Name ?? "All shares";

        var segments = _currentPath.Length == 0
            ? []
            : _currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        crumbs.Add(new Crumb(rootName, "", segments.Length == 0));

        var walked = "";
        for (var i = 0; i < segments.Length; i++)
        {
            walked = walked.Length == 0 ? segments[i] : $"{walked}/{segments[i]}";
            crumbs.Add(new Crumb(segments[i], walked, i == segments.Length - 1));
        }

        Crumbs.ItemsSource = crumbs;
        _addressText = BuildAddressText(rootName, segments);

        // Keep the deepest part visible. Left-aligned, a long path scrolls its tail off the
        // right-hand edge and you lose sight of where you actually are - which is the one part
        // that matters. Deferred to Loaded priority so the new crumbs have been measured.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CrumbScroller.ScrollToRightEnd());
    }

    /// <summary>The address as text, for copying.</summary>
    private string _addressText = "";

    private static string BuildAddressText(string rootName, string[] segments) =>
        segments.Length == 0 ? rootName : $"{rootName}/{string.Join("/", segments)}";

    /// <summary>
    /// Copies the whole path, even the parts above what this person can browse. It is a label,
    /// not access - and being able to paste "E:/Videos/2024" into a message is the whole point.
    /// </summary>
    private void OnCopyAddressClick(object sender, RoutedEventArgs e) => CopyToClipboard(_addressText);

    private void OnCopyFolderNameClick(object sender, RoutedEventArgs e) =>
        CopyToClipboard(_currentPath.Length == 0
            ? _addressText
            : _currentPath[(_currentPath.LastIndexOf('/') + 1)..]);

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Clipboard.SetText(text);
            TransferText.Text = $"Copied: {text}";
        }
        catch (Exception ex)
        {
            // Another application can hold the clipboard open; worth saying so rather than
            // leaving the user thinking it copied.
            Diagnostics.Log("clipboard", "copy failed", ex);
            TransferText.Text = "Could not copy — another program is using the clipboard.";
        }
    }

    private async void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && path != _currentPath)
            await NavigateAsync(path);
    }

    private async void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (_forward.Count == 0) return;

        var next = _forward.Pop();
        _history.Push(_currentPath);
        await NavigateAsync(next, pushHistory: false);
    }

    // =======================================================================
    // Context menu, keyboard, and remote clipboard
    // =======================================================================

    /// <summary>Paths copied or cut on the far side, waiting to be pasted somewhere else there.</summary>
    private List<string> _clipboard = [];
    private bool _clipboardIsCut;

    // ---- what the far side currently lets us do -----------------------------
    //
    // Defaulting to false when permissions are not known yet, not true. These gate destructive
    // actions, and an optimistic default is how Delete came to be offered to someone who did
    // not have it - the prompt appeared, they confirmed, and the server quietly refused with a
    // 403 so nothing happened and nothing explained why.

    /// <summary>Only our own outbound connections can be written to at all.</summary>
    private bool CanWriteHere => _active?.CanWrite == true;

    private bool CanSendHere => CanWriteHere && _granted?.Send == true;
    private bool CanModifyHere => CanWriteHere && _granted?.Modify == true;
    private bool CanDeleteHere => CanWriteHere && _granted?.Delete == true;

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var rows = FileList.SelectedItems.Cast<ListRow>().ToList();
        var selected = rows.Count;
        var insideShare = _currentPath.Length > 0;

        // Nothing selected means the menu acts on where you are, so Download offers the folder
        // you are standing in rather than being greyed out for no obvious reason.
        MenuDownload.IsEnabled = _source is not null && (_granted?.Download ?? true) &&
                                 (selected > 0 || insideShare);
        MenuDownload.Header = selected > 0 ? "Download" : $"Download \"{CurrentFolderName()}\"";

        MenuUpload.IsEnabled = CanSendHere && insideShare;
        MenuUpload.Header = rows is [{ IsDirectory: true } folder]
            ? $"Upload into \"{folder.Name}\""
            : "Upload here";

        ShowArchiveMenu(rows);

        // Rearranging what is there: all gated on Modify.
        MenuNewFolder.IsEnabled = CanModifyHere && insideShare;
        MenuNewFile.IsEnabled = CanModifyHere && insideShare;
        MenuCopy.IsEnabled = CanModifyHere && selected > 0;
        MenuCut.IsEnabled = CanModifyHere && selected > 0;
        MenuPaste.IsEnabled = CanModifyHere && insideShare && _clipboard.Count > 0;
        MenuRename.IsEnabled = CanModifyHere && selected == 1;

        MenuDelete.IsEnabled = CanDeleteHere && selected > 0;
    }

    /// <summary>The folder currently open, for menu labels.</summary>
    private string CurrentFolderName() =>
        _currentPath.Length == 0 ? _active?.Name ?? "this folder"
        : _currentPath[(_currentPath.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Archive entries appear only for our own files and only when WinRAR is installed.
    ///
    /// Restricted to local browsing on purpose: compressing something on a connection would
    /// mean asking the other machine to run a program on our say-so, which is a far bigger
    /// thing to allow than reading a file.
    /// </summary>
    private void ShowArchiveMenu(List<ListRow> rows)
    {
        // Never at the share root: the rows there are whole shares, and the archive would be
        // written to their PARENT folder - outside everything you share, where it promptly
        // vanishes from view. Only offered once you are inside a share.
        var local = _active?.Kind == ConnectionKind.Local && _currentPath.Length > 0;
        var compress = local && Archives.CanCompress && rows.Count > 0;
        var extract = local && Archives.CanExtract &&
                      rows.Count > 0 && rows.All(r => !r.IsDirectory && Archives.IsArchive(r.Name));

        MenuCompress.Visibility = compress ? Visibility.Visible : Visibility.Collapsed;
        MenuExtract.Visibility = extract ? Visibility.Visible : Visibility.Collapsed;
        MenuArchiveSeparator.Visibility = compress || extract
            ? Visibility.Visible
            : Visibility.Collapsed;

        MenuCompress.Header = rows.Count == 1
            ? $"Compress \"{rows[0].Name}\" to .rar"
            : $"Compress {rows.Count} items to .rar";
    }

    private async void OnCompressClick(object sender, RoutedEventArgs e)
    {
        var rows = FileList.SelectedItems.Cast<ListRow>().ToList();
        if (rows.Count == 0 || !Archives.CanCompress) return;
        if (!LocalPathsFor(rows, out var paths, out var folder)) return;

        var suggested = rows.Count == 1
            ? Path.GetFileNameWithoutExtension(rows[0].Name)
            : Path.GetFileName(folder);

        var name = PromptDialog.Ask(this, "Compress", "Name for the archive:", suggested + ".rar");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) name += ".rar";

        var archive = Path.Combine(folder, name);
        if (File.Exists(archive) && MessageBox.Show(this,
                $"\"{name}\" already exists. Replace it?", "Compress",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunArchiveAsync($"Compressing to {name}",
            ct => Archives.CompressAsync(archive, paths, folder,
                new Progress<string>(text => TransferText.Text = text), ct));
    }

    private async void OnExtractClick(object sender, RoutedEventArgs e)
    {
        var rows = FileList.SelectedItems.Cast<ListRow>().ToList();
        if (rows.Count == 0 || !Archives.CanExtract) return;
        if (!LocalPathsFor(rows, out var paths, out var folder)) return;

        await RunArchiveAsync($"Extracting {rows.Count} archive(s)", async ct =>
        {
            foreach (var archive in paths)
            {
                var error = await Archives.ExtractAsync(archive, folder,
                    new Progress<string>(text => TransferText.Text = text), ct);

                if (error is not null) return error;
            }
            return null;
        });
    }

    /// <summary>
    /// Turns the selected rows into real paths on this disk. Only meaningful while browsing our
    /// own shares, which is the only case the archive menu is offered in.
    /// </summary>
    private bool LocalPathsFor(List<ListRow> rows, out List<string> paths, out string folder)
    {
        paths = [];
        folder = "";

        var shares = new ShareSet(_config.Folders);

        foreach (var row in rows)
        {
            if (!shares.TryResolve(row.RemotePath, out var full)) return false;
            paths.Add(full);
        }

        folder = Path.GetDirectoryName(paths[0]) ?? "";
        return paths.Count > 0 && Directory.Exists(folder);
    }

    private async Task RunArchiveAsync(string what, Func<CancellationToken, Task<string?>> work)
    {
        TransferText.Text = what + "…";

        try
        {
            var error = await work(CancellationToken.None);

            if (error is not null)
            {
                ActivityLog.Error(error);
                TransferText.Text = "Archive failed.";
                return;
            }

            TransferText.Text = what + " — done.";
            ActivityLog.Note(TransferDirection.Upload, what + " — done.");
            await NavigateAsync(_currentPath, pushHistory: false);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("archive", what, ex);
            ActivityLog.Error(ex.Message);
        }
    }

    private async void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Delete when FileList.SelectedItems.Count > 0:
                e.Handled = true;
                OnDeleteClick(sender, e);
                break;

            case Key.F2 when FileList.SelectedItems.Count == 1:
                e.Handled = true;
                await RenameSelectedAsync();
                break;

            case Key.C when control:
                e.Handled = true;
                CopySelection(cut: false);
                break;

            case Key.X when control:
                e.Handled = true;
                CopySelection(cut: true);
                break;

            case Key.V when control:
                e.Handled = true;
                await PasteAsync();
                break;

            case Key.F5:
                e.Handled = true;
                await NavigateAsync(_currentPath, pushHistory: false);
                break;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopySelection(cut: false);
    private void OnCutClick(object sender, RoutedEventArgs e) => CopySelection(cut: true);
    private async void OnPasteClick(object sender, RoutedEventArgs e) => await PasteAsync();
    private async void OnRenameItemClick(object sender, RoutedEventArgs e) => await RenameSelectedAsync();

    /// <summary>
    /// Copy and cut act entirely on the far side: nothing is fetched here and put back. The
    /// paste asks the other PC to copy or move the files itself, which is both instant and
    /// avoids a pointless round trip through this machine.
    /// </summary>
    private void CopySelection(bool cut)
    {
        var selected = FileList.SelectedItems.Cast<ListRow>().Select(r => r.RemotePath).ToList();
        if (selected.Count == 0) return;

        _clipboard = selected;
        _clipboardIsCut = cut;

        TransferText.Text = $"{(cut ? "Cut" : "Copied")} {selected.Count} item(s) — paste into another folder.";
    }

    /// <summary>Our own shared folders as a set, for write operations over localhost.</summary>
    private ShareSet LocalShares => new(_config.Folders);

    private async Task PasteAsync()
    {
        if (!CanModifyHere) return;
        if (_clipboard.Count == 0 || _currentPath.Length == 0) return;

        var target = _currentPath;
        var moving = _clipboardIsCut;

        // The far side overwrites without asking, so the question has to be asked here. The
        // listing on screen is the destination folder, which is all it takes to spot a clash.
        var names = _rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pasting = _clipboard;

        var clashing = pasting
            .Select(p => p[(p.LastIndexOf('/') + 1)..])
            .Where(names.Contains)
            .ToList();

        if (clashing.Count > 0)
        {
            var answer = AskOverwrite(clashing, "in that folder", moving ? "move" : "copy");
            if (answer == MessageBoxResult.Cancel) return;

            if (answer == MessageBoxResult.No)
            {
                pasting = pasting
                    .Where(p => !names.Contains(p[(p.LastIndexOf('/') + 1)..]))
                    .ToList();
            }

            if (pasting.Count == 0)
            {
                TransferText.Text = "Nothing left to paste.";
                return;
            }
        }

        try
        {
            // Copying a big folder is disk work; off the UI thread.
            var shares = LocalShares;
            if (_browsingLocal) TransferText.Text = moving ? "Moving…" : "Copying…";
            var error = _browsingLocal
                ? await Task.Run(() => LocalFs.Transplant(shares, pasting, target, moving))
                : _api is { } api
                    ? await api.TransplantAsync(pasting, target, moving, CancellationToken.None)
                    : "Not connected.";
            if (error is not null)
            {
                MessageBox.Show(this, error, moving ? "Move failed" : "Copy failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                TransferText.Text = $"{(moving ? "Moved" : "Copied")} {pasting.Count} item(s).";
                ActivityLog.Note(TransferDirection.Upload,
                    $"{(moving ? "Moved" : "Copied")} {pasting.Count} item(s) into {target}");

                // A cut is consumed by its paste, the way Explorer behaves.
                if (moving) _clipboard = [];
            }

            await NavigateAsync(_currentPath, pushHistory: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Paste failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RenameSelectedAsync()
    {
        if (!CanModifyHere) return;
        if (FileList.SelectedItems.Count != 1) return;
        if (FileList.SelectedItem is not ListRow row) return;

        var name = PromptDialog.Ask(this, "Rename", $"New name for \"{row.Name}\":", row.Name);
        if (name is null || name == row.Name) return;

        var error = _browsingLocal
            ? LocalFs.Rename(LocalShares, row.RemotePath, name)
            : _api is { } api ? await api.RenameAsync(row.RemotePath, name, CancellationToken.None)
            : "Not connected.";
        if (error is not null)
        {
            MessageBox.Show(this, error, "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ActivityLog.Note(TransferDirection.Upload, $"Renamed {row.Name} to {name}");
        await NavigateAsync(_currentPath, pushHistory: false);
    }

    private async void OnNewFolderClick(object sender, RoutedEventArgs e) => await CreateAsync(true);
    private async void OnNewFileClick(object sender, RoutedEventArgs e) => await CreateAsync(false);

    private async Task CreateAsync(bool folder)
    {
        if (!CanModifyHere || _currentPath.Length == 0) return;

        var name = PromptDialog.Ask(this,
            folder ? "New folder" : "New file",
            folder ? "Name for the new folder:" : "Name for the new file:",
            folder ? "New folder" : "New file.txt");

        if (name is null) return;

        var error = _browsingLocal
            ? LocalFs.Create(LocalShares, $"{_currentPath}/{name}", folder)
            : _api is { } api ? await api.CreateAsync($"{_currentPath}/{name}", folder, CancellationToken.None)
            : "Not connected.";
        if (error is not null)
        {
            MessageBox.Show(this, error, "Could not create", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ActivityLog.Note(TransferDirection.Upload, $"Created {name}");
        await NavigateAsync(_currentPath, pushHistory: false);
    }
}
