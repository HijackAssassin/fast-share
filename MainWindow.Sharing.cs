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
    // Inbound connections
    // =======================================================================

    private void OnSessionsChanged() => Dispatcher.BeginInvoke(() =>
    {
        UpdateInbound();
        RefreshSessions();
        // A reverse session appearing or disappearing brings the tunnel that serves it up or
        // down; and the tunnel reads permissions live, so an edit needs no restart.
        SyncShareBack();
    });

    /// <summary>
    /// Shows who is connected *to us*. The state dot on the left only ever described our own
    /// outgoing connection, so a peer connecting in left the window reading "Not connected"
    /// with no sign anyone was there.
    /// </summary>
    private void UpdateInbound() => UpdateConnectionSummary();

    /// <summary>One line in the connected-to list.</summary>
    private sealed record Connected(string Name, string How, string Ping, bool Counts);

    /// <summary>
    /// Everything live right now, in the order it opened.
    ///
    /// Someone we are browsing who is also connected to us is ONE person, described once -
    /// they used to appear twice and inflate the count. Our own shares are listed but do not
    /// count towards it: "connected to 2" should mean two other people.
    /// </summary>
    private List<Connected> LiveConnections()
    {
        var people = new List<Connected>();

        foreach (var connection in _connections)
        {
            var local = connection.Kind == ConnectionKind.Local;

            people.Add(new Connected(
                connection.Name,
                // localhost is just this disk, not a connection: no "your own shares" wording
                // and no ping, since there is no round trip to measure.
                local ? "localhost"
                : connection.Kind == ConnectionKind.Peer ? "sharing with you"
                : "you are connected to them",
                local ? "" : PingTextFor(connection),
                !local));
        }

        foreach (var session in _server.Sessions.Active.Where(x => !x.IsLoopback))
        {
            // They measure their own round trip to us and report it on each ping; we cannot
            // time them ourselves, since they are the one who connected in.
            var ping = session.ReportedRttMs is { } ms ? $"{ms:0} ms" : "";

            // Prefer the nickname we gave them over the name they report, keyed on their stable
            // id - otherwise the status bar read "Connected to OS-PC-22" even after renaming them
            // "Dillon", because an inbound session's own DisplayName does not know the nickname.
            var name = DisplayNameOf(session.PeerId, session.DisplayName);

            var already = people.FindIndex(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (already >= 0)
            {
                people[already] = people[already] with
                {
                    How = people[already].How + ", and connected to you",
                    // Fill in a ping if the connection side had none - a peer sharing back has
                    // no ping of its own, but its session does.
                    Ping = people[already].Ping.Length > 0 ? people[already].Ping : ping,
                };
                continue;
            }

            people.Add(new Connected(name, "connected to you", ping, true));
        }

        return people;
    }

    /// <summary>Last round trip measured for this connection, blank until one has been.</summary>
    private string PingTextFor(Connection connection) =>
        PingMsFor(connection) is { } ms ? $"{ms:0} ms" : "";

    /// <summary>
    /// The line's round trip. For someone sharing back, our own ping travels down their tunnel
    /// and through their PC's relay, which read 369 ms on a 1 ms LAN; they time the line itself
    /// on their pings to us, so that figure is used when they have sent one.
    /// </summary>
    private double? PingMsFor(Connection connection)
    {
        if (connection.Kind == ConnectionKind.Peer &&
            _server.Sessions.Active.FirstOrDefault(s => !s.IsLoopback && !s.IsReverse &&
                string.Equals(s.PeerId, connection.Id["peer:".Length..], StringComparison.OrdinalIgnoreCase))
                ?.ReportedRttMs is { } reported)
            return reported;
        return connection.LastPingMs;
    }

    /// <summary>
    /// One line for however many are connected, with the breakdown on hover and - once there
    /// is more than one - a click that opens the full list.
    /// </summary>
    private void UpdateConnectionSummary()
    {
        // Connection changes flow through here, so it is the natural place to keep each Recent
        // and Favorite row's "Connected" state current the instant someone joins or leaves.
        MarkConnectedRowsOnline();
        RefreshFavoriteStates();

        var people = LiveConnections();
        var counted = people.Count(p => p.Counts);

        var summary = counted == 0
            ? (people.Count > 0 ? "localhost" : "No Connections")
            : counted == 1
                ? $"Connected to {people.First(p => p.Counts).Name}"
                : $"Connected to {counted}";

        // localhost stands alone - nothing after it. Everyone else is their name (already by
        // nickname/chosen/desktop priority) followed by their ping, and nothing else.
        var tooltip = people.Count == 0
            ? null
            : string.Join(Environment.NewLine, people.Select(p =>
                p.How == "localhost" ? "localhost"
                : p.Ping.Length > 0 ? $"{p.Name} — {p.Ping}"
                : p.Name));

        // A button only when there is a list worth opening; a plain label otherwise, so it
        // does not look clickable when clicking would show one name you can already read.
        var clickable = counted > 1;

        StateText.Visibility = clickable ? Visibility.Collapsed : Visibility.Visible;
        StateButton.Visibility = clickable ? Visibility.Visible : Visibility.Collapsed;

        StateText.Text = summary;
        StateText.ToolTip = tooltip;
        StateButtonText.Text = summary;
        StateButton.ToolTip = tooltip;

        StateDot.SetResourceReference(ForegroundProperty, people.Count > 0 ? "Good" : "Idle");

    }

    /// <summary>
    /// Someone connected to us has gone, and why: they closed FastShare, they disconnected, or
    /// nothing has come from their PC for 20 seconds (switched off, network gone, crashed). Said
    /// in the Events log, the status bar and log.txt - and any transfer they were in the middle
    /// of ends now, marked with the same reason, instead of sitting open for 90 seconds and then
    /// being written down as received.
    /// </summary>
    private void OnSessionEnded(SessionStore.Session session)
    {
        if (session.IsLoopback) return;
        var name = DisplayNameOf(session.PeerId, session.DisplayName);

        var why = session.EndReason switch
        {
            DenyReasons.PeerClosed => "they closed FastShare",
            DenyReasons.PeerLeft => "they disconnected",
            DenyReasons.Lost => "lost connection (nothing from their PC for 20 seconds: switched off, network down, or FastShare crashed)",
            DenyReasons.Kicked => "you kicked them",
            DenyReasons.Blocked => "you blocked them",
            DenyReasons.Removed => "you stopped sharing with them",
            DenyReasons.Shutdown => "you stopped sharing",
            DenyReasons.Closed => "you closed FastShare",
            _ => "disconnected",
        };

        Diagnostics.Log("session", $"{name}: connection closed ({session.EndReason ?? "none"}) - {why}");
        TrafficMonitor.Interrupt(session.PeerId, $"Connection closed: {ShortReason(session.EndReason)}");

        // Our own stop and close already say so once for everybody.
        if (session.EndReason is DenyReasons.Shutdown or DenyReasons.Closed) return;

        var message = $"{name}: connection closed - {why}";
        ActivityLog.Note(TransferDirection.Download, message, LogKind.Network);
        FlashStatus(session.EndReason == DenyReasons.Lost ? $"Lost the connection to {name}" : $"{name}: connection closed - {why}");
    }

    /// <summary>The same reasons, short enough for a transfer row's Result column.</summary>
    private static string ShortReason(string? reason) => reason switch
    {
        DenyReasons.PeerClosed => "they closed FastShare",
        DenyReasons.PeerLeft => "they disconnected",
        DenyReasons.Lost => "lost connection",
        DenyReasons.Kicked => "kicked",
        DenyReasons.Blocked => "blocked",
        DenyReasons.Removed => "you stopped sharing with them",
        DenyReasons.Shutdown => "you stopped sharing",
        DenyReasons.Closed => "you closed FastShare",
        _ => "disconnected",
    };

    /// <summary>Opens the full list of who is connected, with live ping figures.</summary>
    private void OnConnectedToClick(object sender, RoutedEventArgs e)
    {
        var people = LiveConnections();
        if (people.Count == 0) return;

        var lines = people
            .Select(p => p.How == "localhost"
                ? "localhost"
                : p.Ping.Length > 0 ? $"{p.Name}  —  {p.Ping}" : p.Name)
            .ToList();

        new DetailsDialog(this, $"{people.Count(x => x.Counts)} connected", lines).ShowDialog();
    }

    // =======================================================================
    // Peers sharing back to us
    // =======================================================================

    private void OnTunnelsChanged() => Dispatcher.BeginInvoke(RefreshPeers);

    /// <summary>
    /// Anyone with a live session counts as online, even when their port is shut. A red light
    /// beside someone you are actively talking to reads as a fault rather than as "they cannot
    /// be dialled", which is only ever true of the direction we never use.
    /// </summary>
    private void MarkConnectedRowsOnline()
    {
        var live = WhoIsLive();

        foreach (var row in _recent)
        {
            var connected = live.Has(row.Model.PeerId, row.Model.Host, row.Model.Port);

            row.IsConnected = connected;
            if (connected) row.State = ConnectionState.Online;
        }
    }

    /// <summary>
    /// The stable identities of everyone we have a live session or open connection with, in
    /// either direction. Matched on identity rather than address because a peer sharing back over
    /// a reverse tunnel has no dialable address of its own.
    /// </summary>
    private HashSet<string> ConnectedPeerIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var session in _server.Sessions.Active.Where(s => !s.IsLoopback))
            if (!string.IsNullOrWhiteSpace(session.PeerId)) set.Add(session.PeerId);

        foreach (var connection in _connections.Where(c => c.Kind == ConnectionKind.Peer))
            if (connection.Id.StartsWith("peer:", StringComparison.Ordinal))
                set.Add(connection.Id["peer:".Length..]);

        return set;
    }

    /// <summary>
    /// Brings each Favorite's dot and status into line with the live sessions. Reachability is
    /// borrowed from the matching Recent row (already probed on its own timer) rather than
    /// probed again; a favorite starred from an inbound-only peer, with no Recent row, still
    /// reads "Connected" whenever their session is live.
    /// </summary>
    private void RefreshFavoriteStates()
    {
        if (_favorites.Count == 0) return;

        var live = WhoIsLive();

        foreach (var fav in _favorites)
        {
            var connected = live.Has(fav.PeerId, fav.Host, fav.Port);

            fav.IsConnected = connected;

            var recent = _recent.FirstOrDefault(r =>
                string.Equals(r.Model.PeerId, fav.PeerId, StringComparison.OrdinalIgnoreCase));

            fav.State = connected
                ? ConnectionState.Online
                : recent?.State ?? ConnectionState.Offline;
        }
    }

    private void RefreshPeers()
    {
        var usable = _server.Tunnels.Usable;

        // Drop anything we were browsing whose tunnel has gone. Without this a peer who
        // disconnected stayed listed as open and kept counting towards "connected to N users",
        // because only the Connections tab list was being refreshed, not the open connections.
        foreach (var dead in _connections
                     .Where(c => c.Kind == ConnectionKind.Peer &&
                                 !usable.Any(u => "peer:" + u.PeerId == c.Id))
                     .ToList())
        {
            CloseConnection(dead, announce: false);
            ActivityLog.Note(TransferDirection.Download,
                $"{dead.Name} stopped sharing ({dead.TopLevel?.Count ?? 0} folder(s) withdrawn)",
                LogKind.Network);
        }

        foreach (var peer in usable) peer.Nickname = NicknameOf(peer.PeerId);

        // Someone sharing back appears in the Browse tab straight away. It used to sit in a
        // list on the Connections tab waiting to be double-clicked, so their folders looked
        // like they had not arrived at all.
        foreach (var peer in usable)
        {
            if (_connections.Any(c => c.Id == "peer:" + peer.PeerId)) continue;

            _ = BrowsePeerAsync(peer, switchTo: false);
            ActivityLog.Note(TransferDirection.Download,
                $"{peer.DisplayName} is sharing back", LogKind.Network);
        }

        MarkConnectedRowsOnline();
        UpdateConnectionSummary();
    }

    /// <summary>
    /// Points the Browse tab at a peer's folders instead of our own outbound connection.
    ///
    /// Their files arrive backwards down the connection they opened to us, so this works even
    /// though they cannot accept a connection themselves. Everything downstream - the tree, the
    /// listing, the eight-stream downloader - is unchanged; only the source differs.
    /// </summary>
    /// <summary>
    /// Adds a peer's shared folders to the Browse tab.
    ///
    /// <paramref name="switchTo"/> is false when this happens on its own - somebody starting
    /// to share back should make their folders available, not yank the view away from whatever
    /// the user was looking at.
    /// </summary>
    private async Task BrowsePeerAsync(PeerTunnel peer, bool switchTo = true)
    {
        var connection = new Connection
        {
            Id = "peer:" + peer.PeerId,
            Kind = ConnectionKind.Peer,
            Source = peer,
            Peer = peer,
            Name = peer.DisplayName,
            // Read-only until their FastShare answers down the tunnel with what we may do.
            Granted = new Permissions(Download: true, Send: false, Delete: false),
        };
        await OpenConnectionAsync(connection, switchTo);
        await RefreshPeerWritesAsync(connection);
    }

    private static bool SamePermissions(Permissions a, Permissions? b) =>
        b is not null && a.Download == b.Download && a.Send == b.Send && a.Delete == b.Delete && a.Modify == b.Modify &&
        (a.Shares ?? []).SequenceEqual(b.Shares ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Asks a peer - down their own tunnel - what they let us do, and turns on uploading,
    /// deleting, renaming and new folders to match. Runs when they appear and on every ping
    /// round, so a permission they change reaches our buttons within seconds. Before 2.4.8 the
    /// tunnel carried reads only, so whatever they ticked for us simply never arrived here.
    /// </summary>
    private async Task RefreshPeerWritesAsync(Connection connection)
    {
        if (connection.Peer is not { } peer || !_connections.Contains(connection)) return;

        var writer = connection.WriteApi ?? new ApiClient(
            new ConnectionInfo("tunnel", 0, "", "", peer.DisplayName), peer.CreateHttpHandler());
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var ping = await writer.PingAsync(deadline.Token);

            var granted = ping.Granted ?? new Permissions(Download: true, Send: false, Delete: false);
            var first = connection.WriteApi is null;
            connection.WriteApi = writer;
            connection.LastPingMs = ping.Milliseconds;
            if (ping.CancelRequested) CancelledByThem(connection);
            if (ReferenceEquals(connection, _active)) PingText.Text = PingTextFor(connection);

            // Compared field by field: the folder list is a List, which a record compares by
            // reference, so every ping would otherwise read as a change.
            if (first || !SamePermissions(granted, connection.Granted))
            {
                var before = connection.Granted;
                connection.Granted = granted;
                if (!first)
                    ActivityLog.Note(TransferDirection.Download,
                        $"{connection.Name}: permission change: {DescribePermissions(before, granted)}", LogKind.Network);
                if (ReferenceEquals(connection, _active)) UpdateButtons();
                RebuildSidebar();
                HighlightCurrentShare();
            }
        }
        catch (NotSupportedException)
        {
            // Their build predates writes over the tunnel; it stays download-only.
            if (!ReferenceEquals(writer, connection.WriteApi)) writer.Dispose();
        }
        catch (Exception ex)
        {
            // Not ready yet (their private server is still starting) or a blip; try next round.
            if (!ReferenceEquals(writer, connection.WriteApi)) writer.Dispose();
            Diagnostics.Log("tunnel", $"could not reach {connection.Name}'s FastShare for changes: {ex.Message}");
        }
    }

    // =======================================================================
    // Live control over connected people
    // =======================================================================

    private List<ShareChoice> _peerFolderChoices = [];

    private void RefreshSessions()
    {
        var selected = (SessionList.SelectedItem as SessionStore.Session)?.PeerId;
        var active = _server.Sessions.Active.Where(s => !s.IsLoopback).ToList();

        // Reapply saved nicknames. Sessions are created by the server, which knows nothing
        // about them - they are ours, kept here and never sent anywhere.
        foreach (var session in active) session.Nickname = NicknameOf(session.PeerId);

        // Rebind so name changes redraw (Session has no change notification).
        SessionList.ItemsSource = null;
        SessionList.ItemsSource = active;
        SessionList.SelectedItem = active.FirstOrDefault(s => s.PeerId == selected);

        SessionList.Visibility = active.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SessionEmptyText.Visibility = active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (SessionList.SelectedItem is null) SessionEditor.Visibility = Visibility.Collapsed;
    }

    /// <summary>Set while a right-click selects someone for the menu: their panel stays shut.</summary>
    private bool _selectingForMenu;

    private void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionStore.Session session || _selectingForMenu)
        {
            SessionEditor.Visibility = Visibility.Collapsed;
            return;
        }

        SessionEditor.Visibility = Visibility.Visible;
        SessionEditorName.Text = session.DisplayName;

        // Set the controls without letting their Click handlers write straight back.
        _suppressPeerWrite = true;
        try
        {
            PeerDownload.IsChecked = session.Permissions.Download;
            PeerSend.IsChecked = session.Permissions.Send;
            PeerModify.IsChecked = session.Permissions.Modify;
            PeerDelete.IsChecked = session.Permissions.Delete;
            PeerAutoApprove.IsChecked = KnownPeerOf(session.PeerId)?.AutoApprove == true;

            var granted = session.Permissions.Shares;
            var everything = granted is null || granted.Count == 0;

            _peerFolderChoices = _server.Shares.Names
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(name => new ShareChoice
                {
                    Name = name,
                    IsSelected = everything || granted!.Contains(name, StringComparer.OrdinalIgnoreCase),
                })
                .ToList();

            PeerFolders.ItemsSource = _peerFolderChoices;
        }
        finally { _suppressPeerWrite = false; }
    }

    /// <summary>
    /// Clicking the person who is already selected closes their panel again. A list that can
    /// only ever open something, never close it, leaves the editor stuck open.
    /// </summary>
    private void OnSessionListClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionList.SelectedItem is null) return;

        var clicked = (e.OriginalSource as DependencyObject).FindListBoxItem();
        if (clicked?.DataContext != SessionList.SelectedItem) return;

        e.Handled = true;

        // Selected by a right-click, panel still shut: this left-click opens it.
        if (SessionEditor.Visibility != Visibility.Visible)
        {
            OnSessionSelectionChanged(SessionList, null!);
            return;
        }

        SessionList.SelectedItem = null;
        SessionEditor.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Right-clicking a person selects them for the menu without opening their panel. Opening
    /// it belongs to left-click; doing both meant a right-click shifted the page under you.
    /// </summary>
    private void OnSessionRightDown(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject).FindListBoxItem() is { } item &&
            !ReferenceEquals(SessionList.SelectedItem, item.DataContext))
        {
            _selectingForMenu = true;
            try { SessionList.SelectedItem = item.DataContext; }
            finally { _selectingForMenu = false; }
        }

        e.Handled = true;
    }

    private bool _suppressPeerWrite;

    private void OnPeerAutoApproveChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPeerWrite) return;
        if (SessionList.SelectedItem is not SessionStore.Session session) return;

        var known = _config.Known.FirstOrDefault(k =>
            string.Equals(k.PeerId, session.PeerId, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            known = new KnownPeer { PeerId = session.PeerId, Username = session.Username };
            known.Remember(session.Permissions);
            _config.Known.Add(known);
        }

        known.AutoApprove = PeerAutoApprove.IsChecked == true;
        _config.Save();

        SessionEditorStatus.Text = known.AutoApprove
            ? known.PublicKey is null
                ? $"{session.DisplayName} will be let in without asking once they are on FastShare 2.4 and you have approved them once more."
                : $"{session.DisplayName} will be let in without asking, with the permissions above."
            : $"{session.DisplayName} will be asked about every time again.";
    }

    /// <summary>
    /// Applies a switch or folder change to the live session straight away.
    ///
    /// Nothing is torn down - they stay connected and their tunnel stays up, they simply have
    /// different rights from the next request onwards. Their own screen catches up on its next
    /// ping, which carries the current permissions.
    /// </summary>
    private void OnPeerPermissionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPeerWrite) return;
        if (SessionList.SelectedItem is not SessionStore.Session session) return;

        var chosen = _peerFolderChoices.Where(c => c.IsSelected).Select(c => c.Name).ToList();

        // Null rather than "all of them", so folders shared later are included automatically
        // instead of being silently withheld.
        var shares = chosen.Count == _peerFolderChoices.Count ? null : chosen;

        var permissions = new Permissions(
            PeerDownload.IsChecked == true,
            PeerSend.IsChecked == true,
            PeerDelete.IsChecked == true,
            shares,
            PeerModify.IsChecked == true);

        if (!_server.Sessions.UpdatePermissions(session.PeerId, permissions))
        {
            SessionEditorStatus.Text = "That person is no longer connected.";
            return;
        }

        // Remember it, so the next approval prompt starts from the same answer.
        var known = _config.Known.FirstOrDefault(k =>
            string.Equals(k.PeerId, session.PeerId, StringComparison.OrdinalIgnoreCase));

        if (known is not null)
        {
            known.Download = permissions.Download;
            known.Send = permissions.Send;
            known.Modify = permissions.Modify;
            known.Delete = permissions.Delete;
            known.Shares = permissions.Shares;
            _config.Save();
        }

        SessionEditorStatus.Text =
            $"Updated — {session.Username} may now {permissions.Summary}" +
            (shares is null ? " in every folder." : $" in {shares.Count} folder(s).");

        ActivityLog.Note(TransferDirection.Upload,
            $"{session.Username}: {permissions.Summary}");
    }

    // =======================================================================
    // Whether this PC can be reached from outside
    // =======================================================================

    private bool _checkingPort;

    /// <summary>The port result last written to the Events log, so its UPnP follow-up is not noted twice.</summary>
    private string? _portNoted;

    /// <summary>
    /// Re-tests the share port and writes the answer onto the Activity tab, so it is possible
    /// to tell "nobody is connecting because my port is shut" from "nobody is connecting".
    /// </summary>
    private async Task UpdatePortStateAsync()
    {
        if (_checkingPort) return;
        _checkingPort = true;

        StatPort.Text = "Checking…";
        StatPort.SetResourceReference(ForegroundProperty, "Subtle");
        _portNoted = null;
        UpdateNetStatus();

        // The check binds a throwaway listener on the share port for the length of the probe,
        // so starting the real one at the same moment would collide. A second or two of a
        // spinning button is a cheaper answer than a confusing "port already in use".
        ShareToggleButton.IsEnabled = false;
        ShareOnOffText.Text = "…";
        ShareSpinner.Visibility = Visibility.Visible;
        StartSpinner();

        try
        {
            // Not gated on sharing. Whether the router forwards this port is a fact about the
            // router, and it is far more useful to know it BEFORE trying to host than after.
            await Reachability.CheckAtStartupAsync(_config.PublicHost, _config.Port, _server.IsSharing);
        }
        finally
        {
            _checkingPort = false;
            ShareSpinner.Visibility = Visibility.Collapsed;
            UpdateShareToggle();
            ShareToggleButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Moves sharing to another port straight away, from Settings. The code carries the port,
    /// so this also gives a new code; the caller has already warned about that. Sharing is
    /// restarted on the new port (dropping whoever is connected), the LAN responder follows,
    /// and the port is re-checked. Refused while anything is moving - restarting would kill it.
    /// Returns false (having said why) when nothing changed.
    /// </summary>
    public async Task<bool> ChangePortAsync(int port, Window asker)
    {
        if (TransferInProgress())
        {
            MessageBox.Show(asker,
                "A transfer is running. Changing the port restarts sharing, which would stop it.\n\n" +
                "Change the port once the transfer has finished.",
                "Transfer in progress", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var old = _config.Port;
        var wasSharing = _server.IsRunning;
        Diagnostics.Log("port", $"changing the share port {old} -> {port} (sharing: {wasSharing})");

        if (wasSharing) await StopSharingAsync();

        _config.Port = port;
        _config.Save();
        SharePortText.Text = $"Port {port} — change it in Settings.";

        // Checked with nothing listening, so the probe can bind the new port itself.
        await UpdatePortStateAsync();

        if (wasSharing) await StartSharingAsync();
        else await StartLanResponderAsync();

        ActivityLog.Note(TransferDirection.Upload, $"Sharing port changed from {old} to {port}", LogKind.Network);
        return true;
    }

    /// <summary>Re-tests the port without changing anything (Save port on the same number).</summary>
    public Task RecheckPortAsync() => UpdatePortStateAsync();

    private void StartSpinner()
    {
        var spin = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(1.1),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };

        ShareSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
    }

    private void ShowPortState(Reachability.Result state)
    {
        StatPort.Text = state.Headline;
        StatPort.SetResourceReference(ForegroundProperty, state.Brush);
        StatPort.ToolTip = state.Explanation + Environment.NewLine + state.Detail;
        UpdateNetStatus();
    }

    /// <summary>Windows Firewall's answer for this exe, from the last check (null = not checked yet).</summary>
    private Firewall.State? _firewallState;

    /// <summary>
    /// The four lines under the Sharing switch. Local and remote are separate questions: a shut
    /// port stops PCs on the internet, never PCs in the same house - those are stopped by Windows
    /// Firewall (or by Sharing being off), which is why the local line has its own Fix.
    /// </summary>
    private void UpdateNetStatus()
    {
        static void Show(System.Windows.Controls.TextBlock box, string text, string brush, string? tip = null)
        {
            box.Text = text;
            box.SetResourceReference(ForegroundProperty, brush);
            box.ToolTip = tip;
        }

        var sharing = _server.IsRunning;
        var firewallShut = _firewallState is Firewall.State.Blocked or Firewall.State.Missing;
        var port = _checkingPort ? null : Reachability.Last;

        if (!sharing)
            Show(NetLocalText, "Off - turn Sharing on", "Subtle");
        else if (firewallShut)
            Show(NetLocalText, "No - blocked by Windows Firewall", "Bad",
                "Other PCs cannot connect in, even in the same house. Press Fix to allow FastShare (one Windows prompt).");
        else
            Show(NetLocalText, "Yes", "Good", "PCs on your network can connect with your code or from their \"On your network\" list.");
        FixFirewallButton.Visibility = firewallShut ? Visibility.Visible : Visibility.Collapsed;

        if (port is null)
            Show(NetRemoteText, "Checking…", "Subtle");
        else if (!sharing)
            Show(NetRemoteText, "Off - turn Sharing on", "Subtle");
        else if (firewallShut)
            Show(NetRemoteText, "No - blocked by Windows Firewall", "Bad");
        else if (port.Reachable)
            Show(NetRemoteText, "Yes", "Good", "Anyone with your code can ask to connect from anywhere.");
        else
            Show(NetRemoteText, "No - port closed", "Bad",
                "PCs outside your house cannot connect to you. You can still connect to them, and they can share back over that connection.");

        if (port is null)
            Show(NetPortText, "Checking…", "Subtle");
        else if (port.Reachable)
            Show(NetPortText, port.ViaUpnp ? $"Open (opened by UPnP) - {_config.Port}" : $"Open (forwarded) - {_config.Port}",
                "Good", port.Explanation);
        else
            Show(NetPortText, $"Closed - {_config.Port}", "Bad", port.Explanation + Environment.NewLine + port.Detail);

        if (port is null)
            Show(NetUpnpText, "Checking…", "Subtle");
        else
            switch (port.UpnpResult)
            {
                case null:
                    Show(NetUpnpText, "Checking…", "Subtle");
                    break;
                case Upnp.MapResult.Mapped:
                    Show(NetUpnpText, "Success", "Good", "The router accepted FastShare's request to open the port.");
                    break;
                default:
                    var why = port.UpnpResult == Upnp.MapResult.NoGateway ? "no router answered" : "the router refused";
                    // Not a problem when the port is forwarded by hand - only say "failed" in red
                    // when it is the reason the port is shut.
                    Show(NetUpnpText, port.Reachable ? $"Not available ({why}) - not needed" : $"Failed - {why}",
                        port.Reachable ? "Subtle" : "Bad",
                        port.Reachable ? "Your port is forwarded on the router, so UPnP is not needed." :
                            "Forward the port on your router (TCP " + _config.Port + " to this PC), or turn on UPnP in the router's settings.");
                    break;
            }
    }

    /// <summary>The Fix beside "Local connection: No": asks Windows to let FastShare in again.</summary>
    private async void OnFixFirewallClick(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        FixFirewallButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => Firewall.Allow(exe));
            _firewallState = await Task.Run(() => Firewall.Check(exe));
            Diagnostics.Log("firewall", $"fix pressed: {result}, now {_firewallState}");
            if (_firewallState == Firewall.State.Allowed) await StartLanResponderAsync(firewallChecked: true);
        }
        finally
        {
            FixFirewallButton.IsEnabled = true;
            UpdateNetStatus();
        }
    }

    // =======================================================================
    // Shared folder list
    // =======================================================================

    private void OnShareMenuOpened(object sender, RoutedEventArgs e)
    {
        var picked = ShareList.SelectedItem is string;
        ShareMenuRemove.IsEnabled = picked;
        ShareMenuOpen.IsEnabled = picked;
    }

    private void OnOpenShareInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (ShareList.SelectedItem is not string folder || !Directory.Exists(folder)) return;

        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception ex) { Diagnostics.Log("share", $"could not open {folder}", ex); }
    }

    /// <summary>
    /// One direction's bar: our own transfer's bytes over its total if we are running one,
    /// else the other PC's transfers through us that way (indeterminate while none of them
    /// announced a total), else hidden.
    /// </summary>
    private void ShowBar(ProgressBar bar, TransferStats? ours, TransferDirection direction)
    {
        double? percent = null;
        var moving = false;

        if (ours is not null)
        {
            moving = true;
            // Checking the files: everything has moved, so the bar starts again for the check.
            percent = ours.Check is { } check ? check.Percent
                : ours.TotalBytes > 0 ? ours.BytesDone * 100.0 / ours.TotalBytes : null;
        }
        else
        {
            var flows = TrafficMonitor.Active().Where(f => f.Direction == direction).ToList();
            if (flows.Count > 0)
            {
                moving = true;
                var measured = flows.Where(f => f.Total > 0).ToList();
                if (measured.Count == flows.Count)
                    percent = flows.All(f => f.Checking)
                        ? Math.Min(99, measured.Sum(f => f.CheckBytes) * 100.0 / measured.Sum(f => f.Total))
                        : measured.Sum(f => f.Bytes) * 100.0 / measured.Sum(f => f.Total);
            }
        }

        // Always shown: empty while nothing moves this way.
        if (!moving)
        {
            bar.IsIndeterminate = false;
            bar.Value = 0;
            return;
        }
        bar.IsIndeterminate = percent is null;
        if (percent is { } p) bar.Value = Math.Clamp(p, 0, 100);
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        _downMeter.Sample(_downStats?.BytesDone ?? 0);
        _upMeter.Sample(_upStats?.BytesDone ?? 0);
        _servedInMeter.Sample(TrafficMonitor.TotalBytes(TransferDirection.Download));
        _servedOutMeter.Sample(TrafficMonitor.TotalBytes(TransferDirection.Upload));

        SyncPeerFlows();

        // Both halves of each direction: what we are moving, plus what is being moved
        // through us. The status bar should read the same whoever pressed the button.
        var down = (_downStats is null ? 0 : _downMeter.BytesPerSecond) + _servedInRate;
        var up = (_upStats is null ? 0 : _upMeter.BytesPerSecond) + _servedOutRate;

        DownSpeedText.Text = Format.Rate(down);
        UpSpeedText.Text = Format.Rate(up);

        var combined = down + up;
        if (combined > _peakRate) _peakRate = combined;

        // One bar per direction, each beside its speed: ours if we are moving that way,
        // otherwise whatever the other PC is driving through us.
        ShowBar(DownProgress, _downStats, TransferDirection.Download);
        ShowBar(UpProgress, _upStats, TransferDirection.Upload);

        var active = _downStats ?? _upStats;
        if (active is not null)
        {
            // Named explicitly rather than left as bare numbers: the status bar also carries
            // idle messages, so "Transfer in progress" is what makes it obvious at a glance
            // that something is actually running right now.
            static string Part(string verb, TransferStats s, SessionRow? row) =>
                (s.Check is { } check
                    ? $"{verb} · all {s.FilesDone:N0} files moved · checking files {check.Percent:0}%" +
                      (check.Files > 1 ? $" ({check.FilesChecked:N0} of {check.Files:N0})" : "") +
                      (check.WaitingForOther ? " · waiting for the other PC to finish its check" : "")
                    : $"{verb} · {FilesLeft(s.TotalFiles - s.FilesDone)} · {Format.Bytes(s.BytesDone)} / {Format.Bytes(s.TotalBytes)}") +
                (row is null ? "" : $" · {Format.Duration((DateTime.UtcNow - row.Entry.WhenUtc).TotalSeconds)} elapsed") +
                (s.Check is null && row?.SecondsLeft is { } left ? $" · {Format.Duration(left)} remaining" : "");

            var parts = new List<string>(2);
            if (_downStats is not null) parts.Add(Part("Receiving", _downStats, _downLane.ActiveRow));
            if (_upStats is not null) parts.Add(Part("Sending", _upStats, _upLane.ActiveRow));
            TransferText.Text = "Transfer in progress — " + string.Join("  |  ", parts) +
                                (QueuedCount > 0 ? $"  (+{QueuedCount} queued)" : "");

            // Each lane's live row, kept in step with its own figures - including the total,
            // so its Session-tab row carries the same percentage the status bar shows.
            void Live(TransferLane lane, TransferStats? stats, RateMeter meter)
            {
                if (lane.ActiveRow is not { } row || stats is null) return;
                if (stats.Check is { } check) { row.Checking = true; row.CheckPercent = check.Percent; }
                UpdateRow(row, stats.BytesDone, stats.FilesDone, row.Entry.Folders,
                    (DateTime.UtcNow - row.Entry.WhenUtc).TotalSeconds, meter.BytesPerSecond, stats.TotalBytes);
            }

            Live(_downLane, _downStats, _downMeter);
            Live(_upLane, _upStats, _upMeter);
        }
        else if (_peerActivity is not null)
        {
            // Nothing of ours is running, but somebody else's transfer is. Say whose, and show
            // the same progress bar the downloader sees - a percentage when they announced a
            // total, indeterminate otherwise.
            TransferText.Text = _peerActivity;
            _peerTextShown = true;
        }
        else if (_peerTextShown)
        {
            TransferText.Text = "";
            _peerTextShown = false;
        }

        UpdateStats();
        UpdateLifetime();
        CheckShutdownWhenDone();
        SavePendingProgress();
        UpdateDiscordPresence();
    }

    // =======================================================================
    // Share tab
    // =======================================================================

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder to share",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var added = new List<string>();
        foreach (var folder in dialog.FolderNames)
        {
            if (!_shareFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                _shareFolders.Add(folder);
                added.Add(folder);
            }
        }
        PersistShares();
        _ = EnsureExcludedAsync(added);
    }

    private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (ShareList.SelectedItem is string folder)
        {
            _shareFolders.Remove(folder);
            DropRulesUnder(folder);
            PersistShares();
            _ = UnexcludeAsync(folder);
        }
    }

    private void OnShareSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveFolderButton.IsEnabled = ShareList.SelectedItem is not null;

    /// <summary>
    /// Saves the folder list and, when already sharing, hands it straight to the running
    /// server. The far side picks the change up on its next refresh - no restart, no new
    /// code, and connections stay up.
    /// </summary>
    private void PersistShares()
    {
        _config.Folders = _shareFolders.ToList();
        _config.Save();
        RefreshFolderRules();

        if (!_server.IsRunning) return;

        _server.UpdateShares(_config.Folders, _config.FolderRules);
        ShareStatusText.Text = _server.Shares.Count == 0
            ? $"Online on port {_config.Port} as {Environment.MachineName} — not sharing any folders."
            : $"Sharing {_server.Shares.Count} folder(s) on port {_config.Port} as {Environment.MachineName}.";
        ActivityLog.Note(TransferDirection.Upload,
            $"Shared folders updated ({_server.Shares.Count} folder(s))");
    }

    private async void OnShareToggleClick(object sender, RoutedEventArgs e)
    {
        if (_server.IsRunning) await StopSharingAsync();
        else await StartSharingAsync();
    }

    /// <summary>
    /// The button says what it is - Sharing - and the word beside it says which way round.
    /// A button labelled "Start Sharing" is ambiguous at a glance: it could equally be
    /// describing the state as offering the action.
    /// </summary>
    private void UpdateShareToggle()
    {
        var on = _server.IsRunning;

        ShareOnOffText.Text = on ? "ON" : "OFF";
        ShareOnOffText.Foreground = System.Windows.Media.Brushes.White;

        // A filled green/red switch reads as a state, not an action - which is the point of
        // sitting it next to a plain "Sharing" label rather than writing the state into a
        // button caption.
        ShareToggleButton.Background = on ? ShareOnBrush : ShareOffBrush;
        ShareToggleButton.BorderBrush = ShareToggleButton.Background;
        UpdateNetStatus();
    }

    private static readonly System.Windows.Media.Brush ShareOnBrush = Frozen(0x2E, 0x9E, 0x44);
    private static readonly System.Windows.Media.Brush ShareOffBrush = Frozen(0xC4, 0x34, 0x2F);

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Stops the listener. Asks first if a transfer is running, because stopping kills it -
    /// and a half-finished download disappearing with no warning looks like a crash.
    /// </summary>
    private async Task StopSharingAsync()
    {
        if (AnyRunning)
        {
            var answer = MessageBox.Show(this,
                "A transfer is still running. Stopping sharing will cancel it.\n\n" +
                "Yes — cancel the transfer and stop sharing\nNo — leave it running",
                "Transfer in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;

            CancelAll();
        }

        // A deliberate stop: stay stopped next launch. (Closing the app does not come through
        // here - it stops the server directly - so quitting while sharing still restores it.)
        _config.WasSharing = false;
        _config.Save();

        // Tell everyone connected that this is a deliberate stop, not just them being dropped,
        // so their watch wakes to a "stopped sharing" popup rather than a bare disconnect.
        _server.Sessions.RevokeAll(DenyReasons.Shutdown);

        await _server.StopAsync();

        UpdateShareToggle();
        ShareStatusText.Text = "Not sharing.";
        ShareCodeBox.Text = "Start sharing to generate a code.";
        CopyCodeButton.IsEnabled = false;
        SharePortText.Text = $"Port {_config.Port} — change it in Settings.";
        ActivityLog.Note(TransferDirection.Upload, "Stopped sharing", LogKind.Network);

        // Stopping means stopping in both directions, including down a connection we
        // opened - otherwise "Not sharing" would still be serving files to someone.
        SyncShareBack();
        UpdateInbound();
        RefreshSessions();
    }

    /// <summary>Starts the listener. Returns false if it could not start, having said why.</summary>
    private async Task<bool> StartSharingAsync()
    {
        // Sharing no folders is allowed. Being reachable and offering nothing are two separate
        // things: you may want to be online so someone can connect and share back to you, and
        // refusing to start until a folder is picked makes that impossible.
        var port = _config.Port;

        ShareToggleButton.IsEnabled = false;
        ShareStatusText.Text = "Starting…";

        try
        {
            _config.Folders = _shareFolders.ToList();
            _config.Port = port;
            _config.Save();

            // Before listening, so Windows never raises its own firewall prompt on top.
            await EnsureFirewallAsync();

            await _server.StartAsync(_config);
            await StartLanResponderAsync(firewallChecked: true);

            // Remember that sharing is on, so it comes back by itself next launch.
            _config.WasSharing = true;
            _config.Save();


            ShareCodeBox.Text = _server.Code ?? "";
            CopyCodeButton.IsEnabled = true;
            UpdateShareToggle();
            ShareStatusText.Text = _server.Shares.Count == 0
                ? $"Online on port {port} as {Environment.MachineName} — not sharing any folders."
                : $"Sharing {_server.Shares.Count} folder(s) on port {port} as {Environment.MachineName}.";

            // Folders stay editable while live; Settings' Save port restarts sharing to rebind.
            SharePortText.Text = $"Port {port} — change it in Settings.";

            // The session clock measures time spent actually sharing, so it starts here - and
            // so does the run counter. Opening the window, or browsing your own folders over
            // localhost, is not a session.
            if (ActivityLog.StartedUtc is null)
            {
                _config.LifetimeRuns++;
                _config.Save();
            }

            ActivityLog.StartSession();
            ActivityLog.Note(TransferDirection.Upload,
                $"Started sharing (port {port}, {_server.Shares.Count} folder(s))", LogKind.Network);

            // If a connection is already open, this is the moment it starts being shared down.
            SyncShareBack();
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("share", $"start failed on port {port}", ex);
            MessageBox.Show(this,
                PortReserved(ex)
                    ? $"Windows has reserved port {port} for its own use, so FastShare cannot share on it right now. " +
                      "This happens on PCs with Hyper-V, WSL or Docker: Windows sets aside random blocks of ports, and " +
                      "sometimes one lands on this port.\n\n" +
                      "To keep the port for FastShare for good, open PowerShell as administrator and run these three lines:\n\n" +
                      "    net stop winnat\n" +
                      $"    netsh int ipv4 add excludedportrange protocol=tcp startport={port} numberofports=1 store=persistent\n" +
                      "    net start winnat\n\n" +
                      "Then turn Sharing on again. Or pick another port in Settings (your code changes with it)."
                    : $"{ex.Message}\n\nIf the port is already in use, pick another one or close whatever is holding it.",
                "Could not start sharing", MessageBoxButton.OK, MessageBoxImage.Error);
            ShareStatusText.Text = "Not sharing.";
            return false;
        }
        finally
        {
            ShareToggleButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// "An attempt was made to access a socket in a way forbidden by its access permissions":
    /// the port sits in a block Windows has reserved (Hyper-V/WSL/Docker NAT, `netsh int ipv4 show
    /// excludedportrange protocol=tcp`). Seen on Patrick's PC 2026-10-11 with 5201 inside 5141-5240.
    /// </summary>
    private static bool PortReserved(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AccessDenied })
                return true;
        return false;
    }

    private static bool IsLoopbackKeyword(string text) =>
        text.Trim() is var t &&
        (t.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         t.Equals("local", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Lets other PCs through Windows Firewall to this copy of FastShare, once per exe path:
    /// the rule is keyed on the path, so a copy run from a new folder is a new program to the
    /// firewall. Asked once per path whatever the answer, so saying no at the UAC prompt sticks.
    /// </summary>
    private async Task EnsureFirewallAsync()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        var state = await Task.Run(() => Firewall.Check(exe));
        _firewallState = state;
        Diagnostics.Log("firewall", $"{exe}: {state}");
        if (state is Firewall.State.Allowed or Firewall.State.Unknown) return;
        if (_config.FirewallAsked.Contains(exe, StringComparer.OrdinalIgnoreCase)) return;

        ShareStatusText.Text = "Allowing FastShare through Windows Firewall - confirm the prompt…";
        var result = await Task.Run(() => Firewall.Allow(exe));
        _config.FirewallAsked.Add(exe);
        _config.Save();
        _firewallState = await Task.Run(() => Firewall.Check(exe));

        if (result == DefenderTune.AddResult.Cancelled)
            ActivityLog.Note(TransferDirection.Upload,
                "Windows Firewall was not changed, so other PCs may not be able to connect to you", LogKind.Network);
    }

    /// <summary>
    /// Replaces the code, so one that got passed around stops working. The address part stays
    /// (it is where this PC is); the secret on the end is what changes, and a request carrying
    /// an old one is refused before it ever reaches the screen - from anyone, including people
    /// approved before. Once they use the new code, their saved entry for this PC is updated.
    /// </summary>
    private void OnNewCodeClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Are you sure you want to change your code?\n\n" +
            "Anyone who has connected to you before will no longer be able to connect to you " +
            "until you give them your new code. People connected right now stay connected.",
            "Change your code?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _config.CodeSecret = ShortCode.NewSecret();
        _config.Save();
        _server.Recode(_config);
        Diagnostics.Log("share", "new connection code made");

        if (_server.IsSharing && _server.Code is { } code)
        {
            ShareCodeBox.Text = code;
            ShareStatusText.Text = "New code made. Give this one out from now on.";
        }
        else
        {
            ShareStatusText.Text = "New code made. It appears here when you start sharing.";
        }
    }

    private void OnCopyCodeClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_server.Code)) return;
        try
        {
            Clipboard.SetText(_server.Code);
            ShareStatusText.Text = "Code copied to clipboard.";
        }
        catch
        {
            // Another process can hold the clipboard open; the box is selectable either way.
            ShareStatusText.Text = "Could not copy — select the code and press Ctrl+C.";
        }
    }

    // =======================================================================
    // Incoming connections
    // =======================================================================

    /// <summary>
    /// Someone is asking to connect. Raised from a Kestrel thread while their request waits,
    /// so hop to the UI thread and put the decision in front of the user.
    /// </summary>
    private void OnApprovalNeeded(SessionStore.PendingApproval pending)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _approvalQueue.Enqueue(pending);
            if (!_approvalShowing) ShowQueuedApprovals();
        });
    }

    /// <summary>Requests waiting for their prompt. Shown one after another, never stacked.</summary>
    private readonly Queue<SessionStore.PendingApproval> _approvalQueue = new();
    private bool _approvalShowing;

    /// <summary>
    /// Works through waiting requests one prompt at a time. Each used to open its own modal
    /// dialog inside the previous one, so a burst of requests stacked windows on top of each
    /// other - under the cursor, inviting a stray click on Accept.
    /// </summary>
    private void ShowQueuedApprovals()
    {
        _approvalShowing = true;
        try
        {
            while (_approvalQueue.TryDequeue(out var pending))
            {
                // Gone stale while it waited its turn (answered, timed out, or given up on).
                if (!_server.Sessions.IsPending(pending.RequestId)) continue;
                DecideApproval(pending);
            }
        }
        finally { _approvalShowing = false; }
    }

    private void DecideApproval(SessionStore.PendingApproval pending)
    {
        {
            var who = DisplayNameOf(pending.PeerId, pending.Username);
            if (!IsActive || !IsVisible || WindowState == WindowState.Minimized)
            {
                TrayIcon.FlashTaskbar(this);
                Notify("Connection request", $"{who} wants to connect to you.");
            }

            var dialog = new ApprovalDialog(pending, NicknameOf(pending.PeerId),
                () => _server.Sessions.IsPending(pending.RequestId))
            {
                AutoApproveSuggested = pending.Identity != IdentityCheck.Mismatch && KnownPeerOf(pending.PeerId)?.AutoApprove == true,
            };
            if (IsVisible) dialog.Owner = this;
            dialog.ShowDialog();

            if (dialog.Withdrawn)
            {
                ActivityLog.Note(TransferDirection.Download,
                    $"{who} stopped waiting before the request was answered", LogKind.Network);
                return;
            }

            switch (dialog.Decision)
            {
                case ApprovalDecision.Accept:
                    if (!_server.Sessions.Approve(pending.RequestId, dialog.Granted))
                    {
                        ActivityLog.Note(TransferDirection.Download,
                            $"{who}'s request had already expired — they need to connect again", LogKind.Network);
                        break;
                    }
                    RememberPeer(pending, dialog.Granted, dialog.AutoApprove);
                    RememberInbound(pending.PeerId, pending.Username, pending.RemoteAddress, pending.Os);
                    ActivityLog.Note(TransferDirection.Download,
                        $"{DisplayNameOf(pending.PeerId, pending.Username)} connected — " +
                        $"may {dialog.Granted.Summary}", LogKind.Network);
                    break;

                case ApprovalDecision.Block:
                    _config.Blocked.Add(new BlockedPeer
                    {
                        PeerId = pending.PeerId,
                        Username = pending.Username,
                        BlockedUtc = DateTime.UtcNow,
                    });
                    _config.Known.RemoveAll(k => k.PeerId == pending.PeerId);
                    _config.Save();

                    _server.Sessions.Deny(pending.RequestId, rememberForThisRun: true, DenyReasons.Blocked);
                    _server.Sessions.Revoke(pending.PeerId);
                    ActivityLog.Note(TransferDirection.Download, $"Blocked {pending.Username}");
                    RefreshBlocked();
                    break;

                default:
                    // A kick lasts until restart, so they cannot simply keep asking.
                    _server.Sessions.Deny(pending.RequestId, rememberForThisRun: true, DenyReasons.Kicked);
                    ActivityLog.Note(TransferDirection.Download, $"Kicked {pending.Username}");
                    break;
            }
        }
    }

    /// <summary>
    /// Straight after letting someone in, offers to ask for their folders too.
    ///
    /// This is the natural moment: they are connected, and the request can only be sent while
    /// they are. Finding the option later means knowing it exists and where it lives.
    /// </summary>
    private void OfferToAskForShareBack(SessionStore.PendingApproval pending)
    {
        var who = DisplayNameOf(pending.PeerId, pending.Username);

        var ask = new ConfirmDialog(this,
            $"{who} can now see the folders you share",
            "Ask them to share back?",
            $"Would you like to ask {who} to share their folders with you as well? " +
            "They will get a prompt on their screen and can say no.",
            "Ask them", "Not now");

        if (ask.ShowDialog() != true) return;

        _server.Sessions.AskForShareBack(pending.PeerId);
        ActivityLog.Note(TransferDirection.Upload,
            $"Asked {who} to share their folders back", LogKind.Network);
    }

    /// <summary>Stores the answer so the same peer is not re-prompted every reconnect.</summary>
    private void RememberPeer(SessionStore.PendingApproval pending, Permissions granted, bool autoApprove = false)
    {
        var known = _config.Known.FirstOrDefault(k =>
            string.Equals(k.PeerId, pending.PeerId, StringComparison.OrdinalIgnoreCase));

        if (known is null)
        {
            known = new KnownPeer { PeerId = pending.PeerId };
            _config.Known.Add(known);
        }

        known.Username = pending.Username;
        known.Remember(granted);

        // Remember the key they proved. A request that proved nothing (an older build) never
        // replaces one on record; one that proved a different key only does because the user
        // was warned and let them in anyway.
        if (pending.PublicKey is { } key) known.PublicKey = key;
        if (pending.Identity != IdentityCheck.Mismatch) known.AutoApprove = autoApprove;
        _config.Save();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings(startUpdate: false);

    private void OpenSettings(bool startUpdate)
    {
        var settings = new SettingsWindow(this, _config);
        if (startUpdate) settings.Loaded += (_, _) => settings.StartUpdate();
        var saved = settings.ShowDialog() == true;
        if (saved) StartDevMode();
        if (saved) RefreshBlocked();
        if (saved) UpdateTrayIcon();
        if (saved) DiscordPresence.SetEnabled(_config.DiscordPresence);

        // Anyone blocked while connected loses their session immediately.
        if (saved) foreach (var peer in _config.Blocked) _server.Sessions.Revoke(peer.PeerId, DenyReasons.Blocked);


    }
}
