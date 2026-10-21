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
    // Connections tab
    // =======================================================================

    private void ReloadRecent()
    {
        var selected = (RecentList.SelectedItem as ConnectionRow)?.Model;

        // Rebuilt rows keep the light they had, rather than going back to "Checking" until the
        // next probe answers.
        var known = _recent.GroupBy(RowKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().State, StringComparer.OrdinalIgnoreCase);

        var filter = SearchBox?.Text?.Trim() ?? "";

        _recent.Clear();
        foreach (var saved in _config.Recent
                     .Where(r => Matches(r, filter))
                     .OrderByDescending(r => r.LastConnectedUtc))
        {
            var row = new ConnectionRow(saved) { IsBlocked = IsBlockedPeer(saved.PeerId) };
            if (known.TryGetValue(RowKey(row), out var state)) row.State = state;
            _recent.Add(row);
        }

        if (selected is not null)
            RecentList.SelectedItem = _recent.FirstOrDefault(r => r.Model.SameHostAs(selected));

        MarkConnectedRowsOnline();
        RebuildFavorites();
    }

    // =======================================================================
    // Favorites
    // =======================================================================

    private readonly ObservableCollection<FavoriteRow> _favorites = [];

    private bool IsFavorite(string? peerId) =>
        peerId is { Length: > 0 } &&
        _config.Favorites.Any(f => string.Equals(f, peerId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds or removes a favorite, then refreshes the Favorites list so the change shows at once.</summary>
    private void ToggleFavorite(string? peerId)
    {
        if (peerId is not { Length: > 0 }) return;

        if (IsFavorite(peerId))
            _config.Favorites.RemoveAll(f => string.Equals(f, peerId, StringComparison.OrdinalIgnoreCase));
        else
            _config.Favorites.Add(peerId);

        _config.Save();
        RebuildFavorites();
    }

    /// <summary>
    /// Rebuilds the starred shortlist. A name and address come from wherever we know the peer -
    /// their saved connection first (so double-click can dial), else the nickname/name we have.
    /// </summary>
    private void RebuildFavorites()
    {
        _favorites.Clear();

        foreach (var peerId in _config.Favorites)
        {
            var saved = _config.Recent.FirstOrDefault(r =>
                string.Equals(r.PeerId, peerId, StringComparison.OrdinalIgnoreCase));
            var known = _config.Known.FirstOrDefault(k =>
                string.Equals(k.PeerId, peerId, StringComparison.OrdinalIgnoreCase));

            var name = NicknameOf(peerId)
                       ?? (saved?.DisplayName is { Length: > 0 } sn ? sn : null)
                       ?? (known?.Username is { Length: > 0 } kn ? kn : null)
                       ?? "Unknown";

            _favorites.Add(new FavoriteRow
            {
                PeerId = peerId,
                Name = name,
                Host = saved?.Host,
                Port = saved?.Port ?? 0,
                IsBlocked = IsBlockedPeer(peerId),
            });
        }

        FavoritesList.Visibility = _favorites.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        FavoritesEmpty.Visibility = _favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRecentFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is ConnectionRow row) ToggleFavorite(row.Model.PeerId);
    }

    private async void OnFavoriteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoritesList.SelectedItem is FavoriteRow fav) await ConnectFavoriteAsync(fav);
    }

    private async void OnFavoriteConnectClick(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is FavoriteRow fav) await ConnectFavoriteAsync(fav);
    }

    private async Task ConnectFavoriteAsync(FavoriteRow fav)
    {
        // We can dial someone we have a saved address for. Someone starred only from an
        // inbound session has no code here, so connecting is their job (they dial in).
        if (fav.Target is { } host)
        {
            if (!ConfirmConnectBlocked(fav.PeerId, fav.Name)) return;
            await ConnectToAsync(host, blockChecked: true);
            return;
        }

        MessageBox.Show(this,
            $"There is no saved code for {fav.Name}, so you cannot dial them from here. " +
            "They can connect to you, or paste their code into the box.",
            "No saved code", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnFavoriteRemoveClick(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is FavoriteRow fav) ToggleFavorite(fav.PeerId);
    }

    private void OnFavoriteMenuOpened(object sender, RoutedEventArgs e)
    {
        var picked = FavoritesList.SelectedItem is FavoriteRow;
        FavoriteMenuConnect.IsEnabled = picked;
        FavoriteMenuRemove.IsEnabled = picked;
        FavoriteMenuBlock.IsEnabled = picked;
        FavoriteMenuBlock.Header = FavoritesList.SelectedItem is FavoriteRow { IsBlocked: true } ? "Unblock" : "Block";
    }

    private void OnFavoriteBlockClick(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is not FavoriteRow fav) return;
        if (fav.IsBlocked) UnblockPeer(fav.PeerId, fav.Name);
        else BlockFromList(fav.PeerId, fav.Name);
    }

    private void OnRecentBlockClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not ConnectionRow { Model.PeerId: { Length: > 0 } peerId } row) return;
        var name = NicknameOf(peerId) ?? row.DisplayName;
        if (row.IsBlocked) UnblockPeer(peerId, name);
        else BlockFromList(peerId, name);
    }

    private static bool Matches(SavedConnection saved, string filter) =>
        filter.Length == 0 ||
        saved.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        saved.Host.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks every saved host whether it is sharing right now. Probes run concurrently so one
    /// unreachable machine cannot hold up the rest; each gives up after a few seconds.
    /// </summary>
    private Task RefreshStatusesAsync() => ProbeRecentAsync();

    /// <summary>
    /// Hosts with a probe still out. Each host is probed on its own: one round for everyone
    /// used to wait for the slowest (an offline PC takes the full timeout), so someone coming
    /// online could take ten seconds to turn green.
    /// </summary>
    private readonly HashSet<string> _probingHosts = new(StringComparer.OrdinalIgnoreCase);

    private static string ProbeKey(ConnectionRow row) => $"{row.Model.Host}:{row.Model.Port}";

    /// <summary>One row's own key: its identity, else its address. Two PCs in one house share an address.</summary>
    private static string RowKey(ConnectionRow row) =>
        string.IsNullOrWhiteSpace(row.Model.PeerId) ? ProbeKey(row) : row.Model.PeerId!;

    /// <summary>
    /// Sets the light on every current row for this host (rows are rebuilt while a probe is out).
    /// A probe answer covers the address; a live connection only lights the row it belongs to.
    /// </summary>
    private void SetRowState(string key, bool reachable)
    {
        var live = WhoIsLive();
        foreach (var row in _recent.Where(r => string.Equals(ProbeKey(r), key, StringComparison.OrdinalIgnoreCase)))
            row.State = reachable || live.Has(row.Model.PeerId, row.Model.Host, row.Model.Port)
                ? ConnectionState.Online
                : ConnectionState.Offline;
    }

    private async Task ProbeRecentAsync()
    {
        var rows = _recent
            .GroupBy(ProbeKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(row => !_probingHosts.Contains(ProbeKey(row)))
            .ToList();
        if (rows.Count == 0) return;

        // Anyone connected to us right now is online whether or not we can reach them back.
        // A friend who cannot forward a port never answers our probe, so without this they
        // showed Offline in the very moment they were downloading from us. (SetRowState
        // checks that per row, by identity.)

        // Rows keep their last answer while the new one is fetched. Resetting every row to
        // "Checking" each round made the list flicker, and left a row that never answers
        // stuck on "Checking" nearly all the time.
        await Task.WhenAll(rows.Select(async row =>
        {
            var key = ProbeKey(row);

            // Someone who only ever connected in to us cannot be reached back - their port is
            // shut - so probing them only ever times out. They are online exactly when they
            // are connected.
            if (row.Model.InboundOnly)
            {
                SetRowState(key, reachable: false);
                return;
            }

            var online = false;
            _probingHosts.Add(key);
            try
            {
                // Probe the saved address directly rather than re-parsing the code; the
                // address is what we actually stored and it works for both code formats.
                var info = new ConnectionInfo(row.Model.Host, row.Model.Port, "",
                    row.Model.PinnedFingerprint ?? "", row.Model.MachineName);

                online = await ApiClient.IsOnlineAsync(info, TimeSpan.FromSeconds(3));
            }
            catch
            {
                // A host we cannot reach counts as offline rather than crashing the refresh.
            }
            finally
            {
                _probingHosts.Remove(key);
            }

            // SetRowState re-reads the live set: a session may have arrived during the probe.
            SetRowState(key, online);
        }));

        // Mark who is actually connected (vs merely reachable) so their row reads "Connected",
        // and let Favorites borrow the new lights.
        MarkConnectedRowsOnline();
        RefreshFavoriteStates();
    }

    /// <summary>
    /// Who is connected right now, in either direction, for the Connected / Online lights.
    ///
    /// Matched on identity wherever there is one. Matching on address alone lit up every PC in
    /// a house the moment one of them connected: Dillon's main and alt share one internet
    /// address, so his alt being open showed both as connected.
    /// </summary>
    private sealed class LiveSet
    {
        public readonly HashSet<string> PeerIds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>host:port of our open outbound connections - one PC answers per forwarded port.</summary>
        public readonly HashSet<string> Endpoints = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Addresses of inbound sessions, only for saved rows that carry no identity.</summary>
        public readonly HashSet<string> SessionAddresses = new(StringComparer.OrdinalIgnoreCase);

        public bool Has(string? peerId, string? host, int port)
        {
            if (!string.IsNullOrWhiteSpace(peerId) && PeerIds.Contains(peerId)) return true;
            if (string.IsNullOrWhiteSpace(host)) return false;
            if (Endpoints.Contains($"{host}:{port}")) return true;
            return string.IsNullOrWhiteSpace(peerId) && SessionAddresses.Contains(host);
        }
    }

    private LiveSet WhoIsLive()
    {
        var live = new LiveSet();
        foreach (var id in ConnectedPeerIds()) live.PeerIds.Add(id);

        foreach (var session in _server.Sessions.Active.Where(s => !s.IsLoopback))
            if (!string.IsNullOrWhiteSpace(session.RemoteAddress)) live.SessionAddresses.Add(session.RemoteAddress);

        foreach (var connection in _connections)
            if (connection.Info is { Host: { Length: > 0 } host } info) live.Endpoints.Add($"{host}:{info.Port}");

        return live;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try { await RefreshStatusesAsync(); }
        finally { RefreshButton.IsEnabled = true; }
    }

    private void OnRecentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = RecentList.SelectedItem as ConnectionRow;
        var has = row is not null;

        ForgetButton.IsEnabled = has;
    }

    private async void OnRecentDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a real row. Double-clicking the blank area below the list used to act on
        // whoever happened to still be selected and start connecting to them.
        if (!IsOverListRow(e)) return;

        if (RecentList.SelectedItem is ConnectionRow row && ConfirmConnectBlocked(row.Model.PeerId, row.DisplayName))
            await ConnectToAsync(row.Model.Code, blockChecked: true);
    }

    /// <summary>Clicking the empty area of the list clears its selection, as Explorer does.</summary>
    private void OnRecentMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsOverListRow(e)) RecentList.UnselectAll();
    }

    private static bool IsOverListRow(MouseButtonEventArgs e) =>
        (e.OriginalSource as DependencyObject).FindListBoxItem() is not null;

    private void OnRecentRightDown(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject).FindListBoxItem() is { } item)
            RecentList.SelectedItem = item.DataContext;

        e.Handled = true;
    }

    private void OnRecentMenuOpened(object sender, RoutedEventArgs e)
    {
        var row = RecentList.SelectedItem as ConnectionRow;
        var picked = row is not null;
        RecentMenuConnect.IsEnabled = picked;
        RecentMenuRename.IsEnabled = picked;
        RecentMenuForget.IsEnabled = picked;

        var favable = row?.Model.PeerId is { Length: > 0 };
        RecentMenuFavorite.IsEnabled = favable;
        // Blocking is by identity, so a row from before identities (no PeerId) cannot be blocked.
        RecentMenuBlock.IsEnabled = favable;
        RecentMenuBlock.Header = row?.IsBlocked == true ? "Unblock" : "Block";
        RecentMenuFavorite.Header = favable && IsFavorite(row!.Model.PeerId)
            ? "Remove from favorites"
            : "Add to favorites";
    }

    private async void OnRecentConnectClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is ConnectionRow row && ConfirmConnectBlocked(row.Model.PeerId, row.DisplayName))
            await ConnectToAsync(row.Model.Code, blockChecked: true);
    }

    /// <summary>
    /// Keeps the Share tab's own list in step with the sharing state, and nothing more. The
    /// "Updated" line is written only by an actual edit.
    /// </summary>
    private void OnSessionsChangedQuiet() => Dispatcher.BeginInvoke(UpdateConnectionSummary);

    /// <summary>
    /// Renames from the list itself rather than only through the box below it, and applies the
    /// name everywhere at once - a nickname is one name for one person, not a per-screen label.
    /// </summary>
    private void OnRecentRenameClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not ConnectionRow row) return;

        var name = PromptDialog.Ask(this, "Rename", $"Name for {row.DisplayName}:", row.DisplayName);
        if (name is null) return;

        SaveRename(name);
    }


    private void SaveRename(string typed)
    {
        if (RecentList.SelectedItem is not ConnectionRow row) return;

        var name = typed.Trim();
        // Clearing the box falls back to the host's own desktop name.
        row.Model.Name = string.IsNullOrWhiteSpace(name) ? null : name;
        row.RefreshName();

        // Store it against their identity too, so the Share tab, the sidebar and the status
        // bar all show the same name rather than only this list.
        if (row.Model.PeerId is { } peerId)
        {
            var known = _config.Known.FirstOrDefault(k =>
                string.Equals(k.PeerId, peerId, StringComparison.OrdinalIgnoreCase));

            if (known is null)
            {
                known = new KnownPeer { PeerId = peerId, Username = row.Model.MachineName ?? "" };
                _config.Known.Add(known);
            }

            known.Nickname = row.Model.Name;
        }

        _config.Save();
        ApplyNicknames();
        RefreshSessions();
        UpdateConnectionSummary();

        if (_connected is not null && row.Model.Port == _connected.Port &&
            row.Model.Host.Equals(_connected.Host, StringComparison.OrdinalIgnoreCase))
        {
            RebuildTreeRoot(row.DisplayName);
            UpdateConnectionSummary();
        }
    }

    private void OnForgetClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not ConnectionRow row) return;

        var confirm = MessageBox.Show(this,
            $"Remove \"{row.DisplayName}\" from the list?\n\nThis only forgets the saved code here — it changes nothing on the other PC.",
            "Forget connection", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _config.Recent.RemoveAll(r => r.SameHostAs(row.Model));
        _config.Save();
        ReloadRecent();
    }

    // =======================================================================
    // Connecting
    // =======================================================================

    private void OnCodeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnConnectClick(sender, e);
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        // An empty box means "show me my own shares". There is nothing else it could
        // usefully mean, and it saves typing the word every time.
        var text = CodeBox.Text.Trim();
        await ConnectToAsync(text.Length == 0 ? "localhost" : text);
    }

    /// <param name="lanHint">Where the PC is on this network, already known (the "On your network"
    /// list), so the lookup broadcast is skipped.</param>
    private async Task ConnectToAsync(string codeOrKeyword, (string Host, int Port)? lanHint = null,
        bool blockChecked = false, string? peerId = null)
    {
        // Someone we blocked, recognised by the code or address we saved for them (or by id, from
        // the LAN list): ask first. Callers that already asked pass blockChecked.
        if (!blockChecked)
        {
            var text = codeOrKeyword.Trim();
            var saved = _config.Recent.FirstOrDefault(r => r.PeerId is { Length: > 0 } &&
                (string.Equals(r.PeerId, peerId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(r.Code, text, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals($"{r.Host}:{r.Port}", text, StringComparison.OrdinalIgnoreCase)));
            var id = peerId ?? saved?.PeerId;
            if (!ConfirmConnectBlocked(id, NicknameOf(id ?? "") ?? saved?.DisplayName ?? "this user")) return;
        }

        // "localhost" is not a connection at all. It reads the shared folders straight off
        // this disk - no listener, no handshake, no session, nothing on the wire. Going
        // through a loopback socket meant your own PC appeared in the connected-people list
        // and counted towards every prompt that asks how many people are connected.
        if (IsLoopbackKeyword(codeOrKeyword))
        {
            await BrowseOwnSharesAsync();
            return;
        }

        ConnectionInfo info;
        try
        {
            info = ResolveTarget(codeOrKeyword);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot connect",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Found sharing on this network, so it can be dialled directly (see the share-back below).
        var onThisNetwork = false;

        // A code, rather than an address: is that PC right here on our own network? Then go
        // straight to it. Dialling its public address from inside the same house goes out to
        // the router and back (slow, and refused by many routers) - which is why typing the
        // code between two PCs at home used to fail and a local address had to be typed.
        if (ShortCode.AddressPart(codeOrKeyword) is { } addressPart)
        {
            ShortCode.TryParse(codeOrKeyword, out _, out _, out var codeSecret);
            var answers = lanHint is { } hint
                ? [hint]
                : await LanDiscovery.FindAllAsync(addressPart, codeSecret, info.Port, TimeSpan.FromMilliseconds(800));

            // Two PCs behind one router on the same port share the address part of a code; with no
            // secret to tell them apart, ask rather than guess (New Code on one of them fixes it).
            (string Host, int Port)? local = answers.Count switch
            {
                0 => null,
                1 => answers[0],
                _ => MessageBox.Show(this,
                         $"{answers.Count} PCs on your network answer to that code:\n\n" +
                         string.Join("\n", answers.Select(a => $"  {a.Host}")) +
                         $"\n\nConnect to {answers[0].Host}? Choose No for {answers[1].Host}.\n\n" +
                         "To stop this, press New Code on one of them so their codes differ.",
                         "Same code on two PCs", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
                     ? answers[0] : answers[1],
            };
            if (local is { } found)
            {
                Diagnostics.Log("connect", $"{addressPart} is on this network at {found.Host}:{found.Port}");
                onThisNetwork = true;
                var saved = _config.Recent.FirstOrDefault(r =>
                    r.Port == found.Port && r.Host.Equals(found.Host, StringComparison.OrdinalIgnoreCase));
                // Same PC, same certificate: a pin learned over the internet holds here too.
                info = info with
                {
                    Host = found.Host,
                    Port = found.Port,
                    Fingerprint = saved?.PinnedFingerprint ?? info.Fingerprint,
                    MachineName = saved?.MachineName ?? info.MachineName,
                };
            }
        }

        // Already browsing this host? Just switch to it - no second handshake, no second
        // request landing on their screen. This is "connect again just takes me back to their
        // folders I already have open".
        if (_connections.FirstOrDefault(c =>
                c.Kind == ConnectionKind.Outbound && c.Info is { } i &&
                i.Host.Equals(info.Host, StringComparison.OrdinalIgnoreCase) && i.Port == info.Port)
            is { } openConnection)
        {
            CodeBox.Text = "";
            await SwitchToAsync(openConnection);
            return;
        }

        // A local address typed by hand, of a PC already connected to us: dial it directly if it
        // answers (it is sharing), rather than asking it to come back through its tunnel.
        if (!onThisNetwork && ShortCode.IsPrivate(info.Host) && InboundSessionAt(info.Host) is not null)
            onThisNetwork = await ApiClient.IsOnlineAsync(info, TimeSpan.FromSeconds(1.5));

        if (InboundSessionAt(info.Host) is { } sharingBack)
        {
            // Already sharing back to us: just show them, whichever way we would have dialled.
            if (_connections.FirstOrDefault(c => c.Kind == ConnectionKind.Peer &&
                    (c.Id == "peer:" + sharingBack.PeerId ||
                     c.Name.Equals(sharingBack.DisplayName, StringComparison.OrdinalIgnoreCase))) is { } shown)
            {
                CodeBox.Text = "";
                await SwitchToAsync(shown);
                return;
            }
        }

        // The code belongs to someone connected to US (they dialled in because their own port
        // is shut). We cannot dial them back over the internet - but the socket they opened is
        // two-way, so send the connection request down that instead. They get the same request
        // popup we got from them, choose what we may see, and once they accept their folders
        // appear here and we appear in their permissions.
        //
        // Not when they answered on this network: their port is reachable, and a direct
        // connection skips the tunnel's framing and the relay through their PC.
        if (!onThisNetwork && InboundSessionAt(info.Host) is { } present)
        {
            CodeBox.Text = "";

            // Show the same "Connecting…" popup a normal dial gets, rather than a silent status
            // line. It sends the request down their existing connection and waits for them to
            // accept - which is when their folders appear here and we appear in their permissions.
            var peerName = DisplayNameOf(present.PeerId, present.DisplayName);
            var shareBack = new ConnectDialog(this, peerName,
                ct => WaitForShareBackAsync(present.PeerId, peerName, ct));

            if (shareBack.ShowDialog() == true)
                Tabs.SelectedItem = BrowseTab;
            return;
        }

        // The dialog owns the attempt, so a failure offers Retry without the user having to
        // find and re-paste the code. Named by our priority - a nickname we set, else their
        // machine name - rather than the bare desktop name from the code.
        // A code with our own internet address is a PC in this house that did not answer the local
        // lookup. Dialling the public address then goes out to the router and back, which only
        // works with the port open (and a router that allows it) - so each attempt, Retry
        // included, asks the local network again first, and a failure says what to check.
        var sameHouse = !onThisNetwork && !string.IsNullOrEmpty(_config.PublicHost) &&
                        info.Host.Equals(_config.PublicHost, StringComparison.OrdinalIgnoreCase) &&
                        ShortCode.AddressPart(codeOrKeyword) is not null;
        var dialog = new ConnectDialog(this, NameFor(info),
            ct => sameHouse ? AttemptSameHouseAsync(info, codeOrKeyword, ct) : AttemptAsync(info, codeOrKeyword, ct),
            local: ShortCode.IsPrivate(info.Host));

        if (dialog.ShowDialog() == true)
        {
            CodeBox.Text = "";
            Tabs.SelectedItem = BrowseTab;
        }
    }

    /// <summary>
    /// Connects by code to a PC behind our own router: looks for it on the local network (again),
    /// then falls back to the public address (works only through an open port and a router that
    /// lets the house dial itself). A failure says what to look at on the other PC.
    /// </summary>
    private async Task AttemptSameHouseAsync(ConnectionInfo info, string code, CancellationToken ct)
    {
        ShortCode.TryParse(code, out _, out _, out var secret);
        var answers = await LanDiscovery.FindAllAsync(ShortCode.AddressPart(code)!, secret, info.Port,
            TimeSpan.FromMilliseconds(1500));
        if (answers.Count > 0)
        {
            var found = answers[0];
            Diagnostics.Log("connect", $"same-house code found on this network at {found.Host}:{found.Port} on a later try");
            var saved = _config.Recent.FirstOrDefault(r =>
                r.Port == found.Port && r.Host.Equals(found.Host, StringComparison.OrdinalIgnoreCase));
            await AttemptAsync(info with
            {
                Host = found.Host,
                Port = found.Port,
                Fingerprint = saved?.PinnedFingerprint ?? info.Fingerprint,
                MachineName = saved?.MachineName ?? info.MachineName,
            }, code, ct);
            return;
        }

        try
        {
            await AttemptAsync(info, code, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Diagnostics.Log("connect", "same-house code: no local answer, and the public address failed", ex);
            throw new InvalidOperationException(
                "That code is for a PC on your own network (it has the same internet address as you), " +
                "but it did not answer on the local network.\n\n" +
                "On that PC: Sharing must be ON, and the Share tab should say \"Local connection: Yes\" - " +
                "if it says Windows Firewall blocks FastShare, press Fix there.\n\n" +
                "Or pick it from \"On your network\" on the Connections tab, or type its local address " +
                "(like 192.168.1.20:5201).", ex);
        }
    }

    /// <summary>
    /// Sends a share-back request down a connected peer's own connection and waits for them to
    /// accept. Backs the "Connecting…" popup so asking someone who is already connected to us
    /// looks and feels like any other connect, rather than a silent status line. Their acceptance
    /// opens a reverse tunnel, which arrives here as a peer connection.
    /// </summary>
    private async Task WaitForShareBackAsync(string peerId, string name, CancellationToken ct)
    {
        // Already sharing back (they accepted a moment ago): nothing to wait for.
        if (_connections.Any(c => c.Kind == ConnectionKind.Peer && c.Id == "peer:" + peerId))
            return;

        // Their link back to us is still up - we only closed the view of it (Close All
        // Connections does that). Accepting again would change nothing on the wire, so no
        // "tunnel opened" event would ever come and this dialog used to wait forever. Reopen it.
        if (await ReopenPeerAsync(peerId)) return;

        if (!_server.Sessions.AskForShareBack(peerId))
            throw new InvalidOperationException(
                "Couldn't reach them to ask — they may have just disconnected. If they are still " +
                "connected, their folders appear here on their own once they share back.");

        ActivityLog.Note(TransferDirection.Download,
            $"Asked to connect to {name} over their connection", LogKind.Network);

        // Poll for the reverse tunnel opening. The UI timers keep running under the modal, so
        // the peer connection is added the moment their acceptance lands.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_connections.Any(c => c.Kind == ConnectionKind.Peer && c.Id == "peer:" + peerId))
                return;
            if (await ReopenPeerAsync(peerId)) return;
            await Task.Delay(300, ct);
        }

        throw new InvalidOperationException(
            $"{name} hasn't accepted yet. The request is on their screen — they can still accept " +
            "it, and their folders will appear here the moment they do.");
    }

    /// <summary>Opens the Browse view of a peer whose tunnel to us is live; false if there is none.</summary>
    private async Task<bool> ReopenPeerAsync(string peerId)
    {
        var tunnel = _server.Tunnels.Usable.FirstOrDefault(t =>
            string.Equals(t.PeerId, peerId, StringComparison.OrdinalIgnoreCase));
        if (tunnel is null) return false;

        if (!_connections.Any(c => c.Id == "peer:" + peerId))
            await BrowsePeerAsync(tunnel, switchTo: true);
        return true;
    }

    /// <summary>
    /// Points the Browse tab at our own shared folders. Instant, because there is nothing to
    /// negotiate - it is the same ShareSet the server would serve, read directly.
    /// </summary>
    private async Task BrowseOwnSharesAsync()
    {
        if (_config.Folders.Count == 0)
        {
            MessageBox.Show(this, "Add at least one folder on the Share tab first.",
                "Nothing to browse", MessageBoxButton.OK, MessageBoxImage.Information);
            Tabs.SelectedItem = ShareTab;
            return;
        }

        // Already open: just switch to it rather than rebuilding, so the folder you were in
        // is still the folder you are in.
        if (_connections.FirstOrDefault(c => c.Kind == ConnectionKind.Local) is { } already)
        {
            await SwitchToAsync(already);
            CodeBox.Text = "";
            return;
        }

        await OpenConnectionAsync(new Connection
        {
            Id = "local",
            Kind = ConnectionKind.Local,
            Source = new LocalSource(() => new ShareSet(_config.Folders)),

            // Your own disk, so everything is permitted - the writes just go to local files
            // instead of over a socket.
            Granted = Permissions.All,

            // Called localhost because that is what was typed to get here. It is not really a
            // connection, but it behaves like one everywhere it is shown, and inventing a
            // second vocabulary for it would help nobody.
            Name = "localhost",
        });

        CodeBox.Text = "";
        ActivityLog.Note(TransferDirection.Download, "Browsing your own shared folders", LogKind.Network);
    }

    /// <summary>A live inbound session coming from this address, if there is one.</summary>
    private SessionStore.Session? InboundSessionAt(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        var known = _config.Recent.FirstOrDefault(r =>
            r.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && r.PeerId is not null);

        return _server.Sessions.Active.FirstOrDefault(session =>
            !session.IsLoopback &&
            (string.Equals(session.PeerId, known?.PeerId, StringComparison.OrdinalIgnoreCase) ||
             session.RemoteAddress.Equals(host, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>First and last few characters of a fingerprint - enough to compare by eye.</summary>
    private static string Short(string? fingerprint) =>
        string.IsNullOrEmpty(fingerprint) ? "(none)"
        : fingerprint.Length <= 20 ? fingerprint
        : $"{fingerprint[..10]}…{fingerprint[^6..]}";

    /// <summary>Clears the recorded certificate for a host, so the next contact re-pins.</summary>
    private void ForgetPinnedFingerprint(ConnectionInfo info)
    {
        var saved = _config.Recent.FirstOrDefault(r =>
            r.Port == info.Port && r.Host.Equals(info.Host, StringComparison.OrdinalIgnoreCase));

        if (saved?.PinnedFingerprint is null) return;

        saved.PinnedFingerprint = null;
        _config.Save();
    }

    private ConnectionInfo ResolveTarget(string text)
    {
        // "localhost" browses our own shares over loopback - a self-test that needs no
        // second machine and never puts the code on the wire.
        if (text.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            if (!_server.IsRunning || _server.Info is null)
                throw new InvalidOperationException(
                    "Start sharing on the Share tab first, then connect to localhost.");

            return _server.Info with { Host = "127.0.0.1" };
        }

        // Long codes issued by earlier builds still work, so saved entries keep connecting.
        if (text.StartsWith("FS1-", StringComparison.OrdinalIgnoreCase))
            return AppConfig.ParseCode(text);

        if (!ShortCode.TryParse(text, out var host, out var port))
            throw new FormatException(
                "That is not a FastShare code. Paste the ten-character code, or an address.");

        // Reuse the certificate we pinned last time we reached this host. Empty means first
        // contact, which the client treats as trust-on-first-use.
        var known = _config.Recent.FirstOrDefault(r =>
            r.Port == port && r.Host.Equals(host, StringComparison.OrdinalIgnoreCase));

        return new ConnectionInfo(host, port, "", known?.PinnedFingerprint ?? "",
            known?.MachineName);
    }

    /// <summary>Hosts being reconnected to right now, so a second loss can't start a second loop.</summary>
    private readonly HashSet<string> _reconnecting = [];

    /// <summary>
    /// After a connection is lost (not closed by the user), tries to connect again every 10 s
    /// for 3 minutes, quietly - no dialog, and the Browse tab is not switched. A host that lets
    /// us in without asking takes us straight back; otherwise its approval prompt appears again.
    /// </summary>
    private async Task ReconnectAsync(ConnectionInfo info, string name)
    {
        var id = $"out:{info.Host}:{info.Port}";
        if (!_reconnecting.Add(id)) return;
        try
        {
            var code = _config.Recent.FirstOrDefault(r => r.Port == info.Port &&
                           r.Host.Equals(info.Host, StringComparison.OrdinalIgnoreCase))?.Code
                       ?? $"{info.Host}:{info.Port}";
            var until = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            for (var tries = 1; DateTime.UtcNow < until; tries++)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                if (_connections.Any(c => c.Id == id)) return;   // the user reconnected meanwhile

                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await AttemptAsync(info, code, deadline.Token, switchTo: false);
                    ActivityLog.Note(TransferDirection.Download, $"Reconnected to {name} by itself", LogKind.Network);
                    FlashStatus($"Reconnected to {name}");
                    return;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("connect", $"reconnect to {name} attempt {tries} failed: {ex.Message}");
                }
            }
            ActivityLog.Note(TransferDirection.Download,
                $"Gave up reconnecting to {name} after 3 minutes — connect again from the Connections tab", LogKind.Network);
        }
        finally { _reconnecting.Remove(id); }
    }

    private async Task AttemptAsync(ConnectionInfo info, string rawCode, CancellationToken ct, bool switchTo = true)
    {
        var api = new ApiClient(info);
        PingResult ping;
        try
        {
            // Reachability first, on a short leash, so a wrong address fails in seconds
            // rather than looking like a slow approval.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            ping = await api.PingAsync(deadline.Token);

            // Then ask to be let in. This one has no deadline of its own: a person has to
            // read the prompt and decide, and rushing them would be the wrong failure.
            // Chosen name and desktop name sent separately, so the host can prefer whichever
            // its own priority calls for rather than being handed one blended string.
            ShortCode.TryParse(rawCode, out _, out _, out var codeSecret);
            await api.ConnectAsync(_config.PeerId, _config.Username ?? "",
                Environment.MachineName, ct, _identity, codeSecret);

            // Ping again now that we are authorised - folder names are only returned to an
            // approved session.
            ping = await api.PingAsync(ct);
        }
        catch (Exception ex)
        {
            var changed = api.FingerprintMismatch;
            var expected = api.ExpectedFingerprint;
            var presented = api.ObservedFingerprint;
            api.Dispose();

            if (!changed) throw;

            Diagnostics.Log("connect",
                $"{info.Host}:{info.Port} certificate changed — expected {Short(expected)}, got {Short(presented)}", ex);

            // A changed certificate almost always means they reinstalled FastShare, but it is
            // also exactly what impersonation looks like, so it is the user's call rather than
            // ours. Until this existed the connection simply failed and the only way through
            // was to press Forget - which quietly does the same thing, with no question asked.
            var answer = MessageBox.Show(this,
                $"The identity of {info.DisplayName} has changed.\n\n" +
                $"Expected: {Short(expected)}\n" +
                $"Got:      {Short(presented)}\n\n" +
                "This is normal if they reinstalled FastShare or reset their settings.\n" +
                "It would also look like this if someone were impersonating them.\n\n" +
                "Trust the new identity and connect?",
                "Identity changed", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) throw;

            // Retry once as if this were first contact; success re-pins what they presented.
            ForgetPinnedFingerprint(info);
            await AttemptAsync(info with { Fingerprint = "" }, rawCode, ct);
            return;
        }

        // Carries the certificate actually seen, not the empty "first contact" value: the tunnel
        // opened back over this connection pins against it, and with an empty value it would
        // have accepted any certificate on every one of its reconnects.
        var resolved = info with
        {
            MachineName = ping.MachineName ?? info.MachineName,
            Fingerprint = string.IsNullOrEmpty(info.Fingerprint) ? api.ObservedFingerprint ?? "" : info.Fingerprint,
        };
        var isLoopback = info.Host is "127.0.0.1";
        // The host says what its code is now. If they made a new one since we saved theirs, the
        // entry (and so the Favorite that points at it) follows, rather than keeping a code
        // that would turn away the next person we give it to.
        var currentCode = api.Grant?.Code is { Length: > 0 } fresh ? fresh : rawCode;
        if (!string.Equals(currentCode, rawCode, StringComparison.OrdinalIgnoreCase))
            Diagnostics.Log("connect", $"{resolved.DisplayName} has a new code; saved entry updated");
        if (!isLoopback) Remember(currentCode, resolved, api.ObservedFingerprint, ping.HostPeerId, ping.Os);

        // Connected to the maintainer: wherever they are now is where updates come from. Only
        // their certificate counts as proof - anyone can claim their PeerId.
        if (!isLoopback && _config.PeerId != AppConfig.MaintainerPeerId &&
            string.Equals(api.ObservedFingerprint, AppConfig.MaintainerFingerprint, StringComparison.OrdinalIgnoreCase) &&
            (_config.UpdateHost != info.Host || _config.UpdatePort != info.Port))
        {
            Diagnostics.Log("update", $"update source follows the maintainer to {info.Host}:{info.Port}");
            _config.UpdateHost = info.Host;
            _config.UpdatePort = info.Port;
            _config.Save();
        }

        var label = isLoopback ? "localhost" : NameFor(resolved);

        // A single shared folder means the top level would be a list of exactly one item,
        // which is a pointless click. Land inside it instead.
        var landing = ping.Shares.Length == 1 ? ping.Shares[0] : "";

        await OpenConnectionAsync(new Connection
        {
            Id = $"out:{info.Host}:{info.Port}",
            Kind = ConnectionKind.Outbound,
            Source = api,
            Api = api,
            Info = resolved,
            Granted = ping.Granted ?? api.Grant?.Permissions,
            VisibleShares = ping.Shares,
            CurrentPath = landing,
            Name = label,
        }, switchTo);

        PingText.Text = $"{ping.Milliseconds:0} ms";
        ActivityLog.Note(TransferDirection.Download,
            $"{label} connected — sharing {ping.Shares.Length} folder(s) with you", LogKind.Network);

        // Connection facts for the log, so a "why won't it connect / why is it slow" report has
        // the round-trip and the path type in it. A high ping to your own public IP is the
        // classic NAT-hairpin tell the handoff warns about (~48 ms where localhost is ~1 ms).
        Diagnostics.Log("connect",
            $"{label} at {info.Host}:{info.Port} · direct HTTPS/1.1 · {ping.Milliseconds:0} ms RTT · " +
            $"{ping.Shares.Length} folder(s)");

        StartPingTimer();

        // Remember what a share-back would need, but do not start one here. Whether we offer
        // our folders is decided by the Start Sharing button, not by connecting.
        _grantToken = isLoopback ? null : api.Grant?.Token;
        SyncShareBack();
    }

    /// <summary>
    /// Brings the share-back tunnel into line with the Start Sharing button.
    ///
    /// Connecting to someone used to start this on its own, which meant opening a connection
    /// quietly handed them every folder on the Share tab. Now one rule covers both directions:
    /// Start Sharing is what offers your folders, whether people reach you by connecting in or
    /// down a connection you opened. Not sharing means not sharing.
    ///
    /// Called on connect, on disconnect, and whenever the button is pressed, so the tunnel can
    /// come up and go down while a connection stays live.
    /// </summary>
    private void SyncShareBack()
    {
        // The tunnel exists only while a reverse peer has an approved session with us, and it
        // rides the connection they came in on - using the token that connection holds, not
        // any "sharing" of our own. Read from the connection itself so several connections
        // cannot get their tunnels crossed.
        var conn = _reverseConnection;
        var token = conn?.Api?.Grant?.Token;

        var wanted = conn?.Info is not null && token is not null &&
                     _reversePeerId is not null &&
                     _server.Sessions.ReversePermissions(_reversePeerId) is not null;

        if (!wanted)
        {
            if (_tunnel is null) return;

            var stopping = _tunnel;
            _tunnel = null;
            _ = stopping.DisposeAsync().AsTask();
            ActivityLog.Note(TransferDirection.Upload, "Stopped serving folders back", LogKind.Network);
            return;
        }

        if (_tunnel is not null) return;   // already up

        var peerId = _reversePeerId!;
        _tunnel = new TunnelService
        {
            PeerLabel = conn!.Name,
            // Their uploads, deletes and renames come down the tunnel as ordinary requests and
            // are answered by our private loopback server, as their reverse session.
            WriteTarget = () => TunnelWriteTarget(peerId),
        };
        _ = EnsureTunnelHostAsync();
        _tunnel.Start(conn.Info!, token!, () => ReverseSharesFor(peerId));

        ActivityLog.Note(TransferDirection.Upload,
            $"Serving folders to {conn.Name} over their connection", LogKind.Network);
    }

    // ---- the private server that answers writes arriving over our tunnel ----

    private volatile ShareServer? _tunnelHost;
    private Task? _tunnelHostStarting;

    private Task EnsureTunnelHostAsync() => _tunnelHostStarting ??= StartTunnelHostAsync();

    private async Task StartTunnelHostAsync()
    {
        // The same shares and folder rules as the Share tab, read live; the reverse session's
        // own permissions and folder list narrow them per request, as they do for anyone.
        var host = ShareServer.ForTunnel(_server.Sessions, () => new ShareSet(_config.Folders, _config.FolderRules));
        try
        {
            await host.StartAsync(_config, loopbackOnly: true);
            _tunnelHost = host;
            Diagnostics.Log("tunnel", $"changes from the host are handled on 127.0.0.1:{host.Info?.Port}");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("tunnel", "could not start the server for changes over the tunnel", ex);
            _tunnelHostStarting = null;   // the next share-back tries again
        }
    }

    /// <summary>Our loopback server and the host's reverse-session token; null if either is missing.</summary>
    private (ConnectionInfo Local, string Token)? TunnelWriteTarget(string peerId)
    {
        if (_tunnelHost?.Info is not { } local) return null;
        var session = _server.Sessions.Active.FirstOrDefault(s =>
            s.IsReverse && string.Equals(s.PeerId, peerId, StringComparison.OrdinalIgnoreCase));
        return session is null ? null : (local, session.Token);
    }

    /// <summary>Whatever we call this peer, else the name they report, else the address.</summary>
    private string NameFor(ConnectionInfo info)
    {
        var saved = _config.Recent.FirstOrDefault(r =>
            r.Port == info.Port && r.Host.Equals(info.Host, StringComparison.OrdinalIgnoreCase));

        return !string.IsNullOrWhiteSpace(saved?.Name) ? saved!.Name! : info.DisplayName;
    }

    /// <summary>Renames the active connection, e.g. after renaming a saved host.</summary>
    private void RebuildTreeRoot(string label)
    {
        if (_active is null) return;

        _active.Name = label;
        RebuildSidebar();
    }

    /// <summary>
    /// Records a host we reached. Keyed on address rather than code, because a host issues a
    /// new code every time its certificate or token changes - that is the same machine, not
    /// a new entry.
    /// </summary>
    private void Remember(string code, ConnectionInfo info, string? fingerprint, string? peerId = null,
        string? os = null)
    {
        // The entry for this PC: same address, and the same identity when we know it. A
        // different PC answering at an address someone else used (two PCs in one house take
        // turns behind the same router) gets its own entry instead of taking over theirs.
        var existing = _config.Recent.FirstOrDefault(r =>
            r.Port == info.Port && r.Host.Equals(info.Host, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(peerId) || r.PeerId is null ||
             string.Equals(r.PeerId, peerId, StringComparison.OrdinalIgnoreCase)));

        if (existing is null)
        {
            existing = new SavedConnection { Host = info.Host, Port = info.Port };
            _config.Recent.Add(existing);
        }

        existing.Code = code;
        existing.MachineName = info.MachineName;
        if (!string.IsNullOrWhiteSpace(os)) existing.Os = os;
        if (!string.IsNullOrWhiteSpace(peerId)) existing.PeerId ??= peerId;

        // The same PC can be saved twice - once by its local address, once by its code over the
        // internet. A new code belongs to the device, so every entry for it takes the new one.
        if (!string.IsNullOrWhiteSpace(existing.PeerId) && ShortCode.AddressPart(code) is not null)
        {
            foreach (var other in _config.Recent.Where(r => !ReferenceEquals(r, existing) &&
                         string.Equals(r.PeerId, existing.PeerId, StringComparison.OrdinalIgnoreCase) &&
                         ShortCode.AddressPart(r.Code) == ShortCode.AddressPart(code)))
                other.Code = code;
        }
        existing.LastConnectedUtc = DateTime.UtcNow;

        // Pin on first contact only. Overwriting it later would defeat the point - a host
        // presenting a new certificate should be refused, not silently re-trusted.
        existing.PinnedFingerprint ??= fingerprint;

        // We just dialled them, so they can be reached - whatever an inbound visit said.
        existing.InboundOnly = false;
        _config.Save();

        ReloadRecent();
        foreach (var row in _recent.Where(r => r.Model.SameHostAs(existing)))
            row.State = ConnectionState.Online;
    }

    /// <summary>
    /// Records anyone we have had a connection with, whichever way round it went.
    ///
    /// Only hosts we dialled out to used to appear, so the people most likely to come back -
    /// the ones who connect IN because they cannot host - were the ones missing from the list.
    /// Their address is stored so their code can be recognised later, even though dialling it
    /// will not reach them.
    /// </summary>
    private void RememberInbound(string peerId, string name, string? address, string? os = null)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        if (string.Equals(peerId, _config.PeerId, StringComparison.OrdinalIgnoreCase))
            Diagnostics.Log("identity", $"{name} at {address} uses this PC's own identity - their FastShare settings were probably copied from here");

        // One entry per PC, found by who they are - not by address. Everyone in a house comes
        // from the same internet address, so matching on it merged Dillon's second PC into
        // his first: one row, the other PC's name, the first one's identity.
        var existing = _config.Recent.FirstOrDefault(r =>
                           string.Equals(r.PeerId, peerId, StringComparison.OrdinalIgnoreCase))
                       ?? _config.Recent.FirstOrDefault(r => r.PeerId is null &&
                           r.Host.Equals(address, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new SavedConnection { Host = address, Port = ShortCode.DefaultPort };
            _config.Recent.Add(existing);
        }

        existing.MachineName = name;
        existing.LastConnectedUtc = DateTime.UtcNow;
        existing.PeerId ??= peerId;
        if (!string.IsNullOrWhiteSpace(os)) existing.Os = os;

        // They reached us, so we know nothing about whether they can be reached. Marked so the
        // list can say so instead of showing a red light that looks like a fault - unless we
        // have dialled this host ourselves before, which proves it can be.
        if (existing.PinnedFingerprint is null) existing.InboundOnly = true;

        if (string.IsNullOrEmpty(existing.Code)) existing.Code = ShortCode.Build(address, ShortCode.DefaultPort);

        _config.Save();
        ReloadRecent();
    }

    private void StartPingTimer()
    {
        _pingTimer?.Stop();
        _pingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _pingTimer.Tick += async (_, _) => await PingAllAsync();
        _pingTimer.Start();
    }

    /// <summary>
    /// Pings every outbound connection. Each is independent - one host going away must not
    /// disturb the others, which is exactly what a single shared ping timer used to do.
    /// </summary>
    private async Task PingAllAsync()
    {
        // A tick that lands while the last round is still waiting on a slow host is skipped.
        // Overlapping rounds could also satisfy the "two pings must agree" check in one go.
        if (_pinging) return;
        _pinging = true;
        try
        {
            // Side by side, so one unreachable host cannot delay everyone else's by six seconds.
            await Task.WhenAll(_connections.Where(c => c.Api is not null).ToList().Select(RefreshOneAsync)
                .Concat(_connections.Where(c => c.Kind == ConnectionKind.Peer).ToList().Select(RefreshPeerWritesAsync)));
        }
        finally { _pinging = false; }
    }

    private bool _pinging;

    /// <summary>Failed pings in a row before a connection is given up on (~15 seconds at one every four).</summary>
    private const int PingsBeforeGivingUp = 4;

    /// <summary>
    /// Re-reads one connection's permissions and folder list. Driven by the ping sweep and,
    /// when the far side pushes a change, immediately.
    /// </summary>
    private async Task RefreshOneAsync(Connection connection)
    {
        if (connection.Api is null) return;

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var ping = await connection.Api.PingAsync(deadline.Token);

            if (connection.MissedPings > 0)
            {
                connection.MissedPings = 0;
                ActivityLog.Note(TransferDirection.Download, $"Reconnected to {connection.Name}", LogKind.Network);
            }

            connection.LastPingMs = ping.Milliseconds;
            if (ReferenceEquals(connection, _active)) PingText.Text = $"{ping.Milliseconds:0} ms";

            if (ping.Granted is not null && ping.Granted != connection.Granted)
            {
                var before = connection.Granted;
                connection.Granted = ping.Granted;
                if (ReferenceEquals(connection, _active)) UpdateButtons();

                ActivityLog.Note(TransferDirection.Download,
                    $"{connection.Name}: permission change: {DescribePermissions(before, ping.Granted)}",
                    LogKind.Network);
            }

            await ApplyShareListAsync(connection, ping.Shares);

            // They want to connect to us over the socket we opened - the only way in, since
            // our port is shut. Delivered once, on the ping, because they cannot reach us any
            // other way. It arrives as a normal connection request we approve.
            if (ping.ShareBackRequested && ping.HostPeerId is { Length: > 0 } hostPeerId)
                await ApproveReverseConnectAsync(connection, hostPeerId);

            // The host asked us to measure the line to them; only we can open connections to it.
            if (ping.SpeedTestRequested) _ = RunSpeedTestAsync(connection, reportBack: true);

            // They cancelled the transfer we are running with them.
            if (ping.CancelRequested) CancelledByThem(connection);
        }
        catch
        {
            // The watch usually gets there first; this is the fallback for a host that
            // vanished without its socket closing cleanly. One miss is usually a blip, so the
            // connection - and anything transferring over it - is kept for a few more tries.
            if (!_connections.Contains(connection)) return;

            connection.MissedPings++;
            if (connection.MissedPings < PingsBeforeGivingUp)
            {
                if (ReferenceEquals(connection, _active)) PingText.Text = "reconnecting…";
                return;
            }

            ActivityLog.Note(TransferDirection.Download,
                $"Lost the connection to {connection.Name}", LogKind.Network);
            Diagnostics.Log("connect", $"{connection.Name}: connection closed - lost connection (no answer to {PingsBeforeGivingUp} pings)");
            FlashStatus($"Lost the connection to {connection.Name}");

            CloseConnection(connection, announce: false);

            // Not closed on purpose, so try to get it back: a transfer that died with it resumes
            // by itself once the same connection is up again (see OfferRetry).
            if (connection.Kind == ConnectionKind.Outbound && connection.Info is { } info)
                _ = ReconnectAsync(info, connection.Name);
        }
    }

    /// <summary>
    /// Spells out a permission change as enabled/disabled per right, rather than just listing
    /// what is now allowed - "download, upload" does not say which of them just changed.
    /// </summary>
    private static string DescribePermissions(Permissions? before, Permissions now)
    {
        var parts = new List<string>(4);

        void Note(string name, bool had, bool has)
        {
            if (before is null || had != has) parts.Add($"{name} {(has ? "enabled" : "disabled")}");
        }

        Note("download", before?.Download ?? false, now.Download);
        Note("upload", before?.Send ?? false, now.Send);
        Note("modify", before?.Modify ?? false, now.Modify);
        Note("delete", before?.Delete ?? false, now.Delete);

        return parts.Count == 0 ? "no change" : string.Join(", ", parts);
    }

    /// <summary>
    /// Reacts to the host changing which folders we are allowed to see.
    ///
    /// Every ping already carried this list and it was being thrown away, so unticking a folder
    /// on the host left it sitting on the peer's screen. Clicking it then returned 404 and the
    /// five-second auto-refresh produced a steady trickle of "Could not open folder" - the
    /// folder really was gone, the screen just had not been told.
    /// </summary>
    /// <summary>
    /// Shows the other side's request that we share our folders back, and acts on the answer.
    ///
    /// Always a question, never automatic: someone asking for access to your files is not the
    /// same as you granting it, however convenient that would be.
    /// </summary>
    /// <summary>
    /// Someone we are connected to wants to connect back to us over the socket we opened. It
    /// is a normal connection request - the same approval prompt anyone dialling in gets -
    /// except the folders we grant travel down the existing tunnel rather than our (shut)
    /// port. On approval they appear in our permissions and can browse exactly what we allow.
    /// </summary>
    private async Task ApproveReverseConnectAsync(Connection connection, string hostPeerId)
    {
        // Guard against a second ping arriving while the prompt is still open.
        if (!_reversePrompting.Add(hostPeerId)) return;

        try
        {
            var who = DisplayNameOf(hostPeerId, connection.Name);

            var pending = new SessionStore.PendingApproval
            {
                RequestId = "reverse",
                PeerId = hostPeerId,
                Username = connection.Name,
                RemoteAddress = connection.Info?.Host ?? "over your connection",
                Suggested = RememberedFor(hostPeerId) ?? Permissions.ReadOnly,
                AvailableShares = _config.Folders
                    .Select(f => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(f)))
                    .ToList(),
            };

            // Allowed without asking: this request comes from the host we dialled, whose
            // certificate is pinned, so the pin is what proves it is them.
            var known = KnownPeerOf(hostPeerId);
            var pinned = _config.Recent.Any(r =>
                string.Equals(r.PeerId, hostPeerId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(r.PinnedFingerprint) &&
                string.Equals(r.PinnedFingerprint, connection.Info?.Fingerprint, StringComparison.OrdinalIgnoreCase));

            Permissions granted;
            var autoApprove = false;
            if (known is { AutoApprove: true } && pinned)
            {
                granted = known.ToPermissions();
                autoApprove = true;
                Diagnostics.Log("identity", $"reverse connect from {who}: allowed without asking (pinned host)");
            }
            else
            {
                var dialog = new ApprovalDialog(pending, NicknameOf(hostPeerId))
                {
                    AutoApproveSuggested = known?.AutoApprove == true,
                };
                TrayIcon.FlashTaskbar(this);
                NotifyIfAway(("Connection request", $"{who} wants to connect to you."));
                dialog.ShowDialog();

                if (dialog.Decision != ApprovalDecision.Accept)
                {
                    ActivityLog.Note(TransferDirection.Upload,
                        $"Declined {who}'s request to connect", LogKind.Network);
                    return;
                }
                granted = dialog.Granted;
                autoApprove = dialog.AutoApprove;
            }

            // Record them as a real, editable session so they show up in Permissions, and
            // remember the answer for next time - just like a normal inbound connection.
            _server.Sessions.CreateReverse(hostPeerId, connection.Name, granted);
            RememberReverse(hostPeerId, connection.Name, granted);
            if (KnownPeerOf(hostPeerId) is { } saved)
            {
                saved.AutoApprove = autoApprove;
                _config.Save();
            }

            _reversePeerId = hostPeerId;
            _reverseConnection = connection;

            // Bring the tunnel up on this connection so they can browse what we granted.
            SyncShareBack();
            RefreshSessions();

            ActivityLog.Note(TransferDirection.Upload,
                $"{who} connected to you — may see {granted.Shares?.Count.ToString() ?? "all"} folder(s)" +
                (autoApprove ? " (allowed without asking)" : ""),
                LogKind.Network);
        }
        finally
        {
            _reversePrompting.Remove(hostPeerId);
        }
    }

    /// <summary>The permissions we granted this peer before, if any, to pre-select the prompt.</summary>
    private Permissions? RememberedFor(string peerId) =>
        _config.Known.FirstOrDefault(k =>
            string.Equals(k.PeerId, peerId, StringComparison.OrdinalIgnoreCase))?.ToPermissions();

    /// <summary>Stores a reverse-connect answer against the peer so it is offered again next time.</summary>
    private void RememberReverse(string peerId, string username, Permissions granted)
    {
        var known = _config.Known.FirstOrDefault(k =>
            string.Equals(k.PeerId, peerId, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            known = new KnownPeer { PeerId = peerId };
            _config.Known.Add(known);
        }
        known.Username = username;
        known.Remember(granted);
        _config.Save();
    }

    /// <summary>Our own shared folders, restricted to what a reverse peer is allowed to see.</summary>
    private ShareSet ReverseSharesFor(string? peerId)
    {
        if (peerId is null) return new ShareSet([]);

        var perms = _server.Sessions.ReversePermissions(peerId);
        // No session, or download taken away: serve nothing at all.
        if (perms is null || !perms.Download) return new ShareSet([]);

        return new ShareSet(_config.Folders, _config.FolderRules).Restrict(perms.Shares);
    }

    private async Task ApplyShareListAsync(Connection connection, string[] shares)
    {
        if (connection.VisibleShares.SequenceEqual(shares))
        {
            connection.PendingShares = null;
            return;
        }

        // Wait for a second ping to say the same thing before believing it. A single reply
        // that briefly omitted a folder was enough to announce it had been withdrawn and throw
        // the user out of it - which is what produced "E: is no longer shared with you" when
        // nothing had been unshared.
        if (connection.PendingShares is null || !connection.PendingShares.SequenceEqual(shares))
        {
            connection.PendingShares = shares;
            Diagnostics.Log("shares",
                $"{connection.Name} sent a different list, waiting for confirmation: " +
                $"had [{string.Join(", ", connection.VisibleShares)}] got [{string.Join(", ", shares)}]");
            return;
        }

        connection.PendingShares = null;
        Diagnostics.Log("shares",
            $"{connection.Name} confirmed: [{string.Join(", ", shares)}]");

        var lost = connection.VisibleShares.Except(shares, StringComparer.OrdinalIgnoreCase).ToList();
        var gained = shares.Except(connection.VisibleShares, StringComparer.OrdinalIgnoreCase).ToList();
        connection.VisibleShares = shares;

        if (lost.Count > 0)
            ActivityLog.Note(TransferDirection.Download,
                $"{connection.Name} no longer shares: {string.Join(", ", lost)}");
        if (gained.Count > 0)
            ActivityLog.Note(TransferDirection.Download,
                $"{connection.Name} now shares: {string.Join(", ", gained)}");

        // If we are standing inside a folder that just went away, step back to the top rather
        // than leaving a dead listing on screen for the user to click and get an error from.
        var current = connection.CurrentPath.Split('/')[0];
        if (current.Length > 0 && !shares.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            connection.History.Clear();
            connection.Forward.Clear();
            connection.CurrentPath = "";

            if (ReferenceEquals(connection, _active))
                TransferText.Text = $"\"{current}\" is no longer shared with you.";
        }

        if (!ReferenceEquals(connection, _active)) return;

        await RefreshTreeAsync();
        await NavigateAsync(connection.CurrentPath, pushHistory: false);
    }

    // =======================================================================
    // Open connections
    // =======================================================================

    /// <summary>
    /// Adds a connection (or replaces one to the same place) and switches to it.
    /// </summary>
    private async Task OpenConnectionAsync(Connection connection, bool switchTo = true)
    {
        var existing = _connections.FirstOrDefault(c => c.Id == connection.Id);
        if (existing is not null) CloseConnection(existing, announce: false);

        // Own shares first, then everyone else in the order they arrived - which is the order
        // the status bar's tooltip lists them in.
        if (connection.Kind == ConnectionKind.Local) _connections.Insert(0, connection);
        else _connections.Add(connection);

        StartWatching(connection);

        if (switchTo) await SwitchToAsync(connection);
        else await RefreshTreeAsync();

        ApplyNicknames();
        UpdateConnectionSummary();

        // Anything unfinished with them from before? (Offered a few seconds later.)
        if (connection.Kind != ConnectionKind.Local) _ = OfferResumeAsync(connection);
    }

    /// <summary>Points the Browse tab at one of the open connections, restoring where it was.</summary>
    private async Task SwitchToAsync(Connection connection)
    {
        _active = connection;
        PingText.Text = connection.Kind == ConnectionKind.Local ? "" : PingTextFor(connection);

        await RefreshTreeAsync();
        await NavigateAsync(connection.CurrentPath, pushHistory: false);

        Tabs.SelectedItem = BrowseTab;
        UpdateButtons();
    }

    /// <summary>
    /// Stops browsing one connection.
    ///
    /// For a peer this ends OUR view of THEIR files and nothing else - they keep their session
    /// and whatever we share with them. Cutting that off as well was wrong: "stop looking at
    /// their files" and "stop letting them see mine" are separate decisions that happen to
    /// travel over the same socket.
    /// </summary>
    private void CloseConnection(Connection connection, bool announce = true, bool? leave = null)
    {
        connection.Watch?.Cancel();
        connection.Watch = null;

        // Say goodbye when we choose to disconnect, so their side reads "disconnected" at once
        // instead of "lost the connection" twenty seconds later. Not when the connection is
        // already dead (announce: false) - there is nobody to tell.
        if ((leave ?? announce) && connection.Kind == ConnectionKind.Outbound && connection.Api is { } leaving)
            _ = Task.Run(async () => { await leaving.LeaveAsync("left"); leaving.Dispose(); });
        else if (connection.Kind == ConnectionKind.Outbound) connection.Api?.Dispose();
        connection.WriteApi?.Dispose();

        // If this connection was carrying a tunnel we serve back over, that tunnel goes with
        // it - and the peer's reverse session with us has nowhere left to run.
        if (ReferenceEquals(connection, _reverseConnection))
        {
            if (_reversePeerId is { } rp) _server.Sessions.Revoke(rp);
            _reversePeerId = null;
            _reverseConnection = null;
            SyncShareBack();
        }

        _connections.Remove(connection);

        if (announce)
        {
            ActivityLog.Note(TransferDirection.Download,
                connection.Kind == ConnectionKind.Local
                    ? "Closed your own shares"
                    : $"Stopped browsing {connection.Name}", LogKind.Network);
        }

        if (ReferenceEquals(_active, connection)) _active = null;

        RefreshConnectionList();
        UpdateConnectionSummary();

        // Fall back to whatever else is open rather than leaving an empty window.
        if (_active is null && _connections.Count > 0)
        {
            _ = SwitchToAsync(_connections[0]);
            return;
        }

        if (_active is null) ClearBrowseView();
    }

    /// <summary>Empties the Browse tab. Nothing is open, so nothing should be on screen.</summary>
    private void ClearBrowseView()
    {
        _sidebar.Clear();
        _rows = [];
        FileList.ItemsSource = null;
        Crumbs.ItemsSource = null;

        EmptyHint.Text = "Nothing open. Connect to someone on the Connections tab, " +
                         "or press Connect with an empty box to browse your own shares.";
        EmptyHint.Visibility = Visibility.Visible;

        PingText.Text = "";
        BackButton.IsEnabled = false;
        ForwardButton.IsEnabled = false;
        UpButton.IsEnabled = false;

        UpdateButtons();
    }

    /// <summary>
    /// Holds a request open on the far side that completes the instant our session ends, so a
    /// disconnect is noticed immediately instead of on the next ping.
    /// </summary>
    private void StartWatching(Connection connection)
    {
        if (connection.Api is null) return;

        var cancel = new CancellationTokenSource();
        connection.Watch = cancel;

        var api = connection.Api;
        _ = Task.Run(async () =>
        {
            // Re-parked after every answer, so one channel carries both "your session ended"
            // and "what you are allowed to do just changed".
            while (!cancel.IsCancellationRequested)
            {
                var result = await api.WatchAsync(cancel.Token);
                if (cancel.IsCancellationRequested) return;

                // The watch itself dropped - which is not the host saying anything. Park it
                // again shortly; if the host really has gone, the pings will say so.
                if (result == ApiClient.WatchResult.Lost)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(2), cancel.Token); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                if (result == ApiClient.WatchResult.Changed)
                {
                    // Permissions or the folder list moved. Re-read them now rather than
                    // waiting up to four seconds and offering things that would be refused.
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        if (_connections.Contains(connection)) await RefreshOneAsync(connection);
                    });
                    continue;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_connections.Contains(connection)) return;

                    var stopped = result == ApiClient.WatchResult.HostStopped;

                    var message = result switch
                    {
                        ApiClient.WatchResult.HostStopped => $"{connection.Name} stopped sharing",
                        ApiClient.WatchResult.HostClosed => $"{connection.Name} closed FastShare",
                        _ => $"{connection.Name} disconnected you",
                    };
                    ActivityLog.Note(TransferDirection.Download, message, LogKind.Network);
                    Diagnostics.Log("connect", $"watch on {connection.Name} ended: {result}");
                    FlashStatus(message);

                    var name = connection.Name;
                    CloseConnection(connection, announce: false);

                    // A deliberate stop gets a popup: the folders vanishing on their own looks
                    // like a crash otherwise, and they may want to know it was the other end's
                    // choice rather than a fault at either end.
                    if (stopped)
                    {
                        MessageBox.Show(this,
                            $"{name} has stopped sharing.\n\nTheir folders are no longer available. " +
                            "You can reconnect once they start sharing again.",
                            "Sharing stopped", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                });
                return;
            }
        });
    }

    private void RefreshConnectionList() => _ = RefreshTreeAsync();

    /// <summary>
    /// Pushes saved nicknames onto everything that shows a name, so renaming somebody renames
    /// them in the sidebar, on the Connections tab, on the Share tab and in the status bar at
    /// the same moment rather than in whichever one happens to redraw first.
    /// </summary>
    private void ApplyNicknames()
    {
        foreach (var connection in _connections)
        {
            var nickname = NicknameFor(connection);
            if (nickname is not null) connection.Name = nickname;
        }

        foreach (var row in _recent)
        {
            var saved = _config.Known.FirstOrDefault(k =>
                k.PeerId is not null &&
                string.Equals(k.PeerId, row.Model.PeerId, StringComparison.OrdinalIgnoreCase));

            if (saved?.Nickname is { Length: > 0 } name && row.Model.Name != name)
            {
                row.Model.Name = name;
                row.RefreshName();
            }
        }

        RebuildSidebar();
        // The address bar starts with the person's name; without this it kept the old one
        // until you moved to another folder.
        RebuildCrumbs();
    }

    /// <summary>
    /// The one place a nickname is looked up, keyed on the peer's stable identity.
    ///
    /// There used to be two stores - a name on the saved connection and a nickname on the
    /// known peer - and different screens read different ones, which is why renaming somebody
    /// changed their name in some places and not others. This is now the only reader, and
    /// KnownPeer.Nickname the only store.
    /// </summary>
    private string? NicknameOf(string? peerId) =>
        string.IsNullOrWhiteSpace(peerId)
            ? null
            : _config.Known.FirstOrDefault(k =>
                string.Equals(k.PeerId, peerId, StringComparison.OrdinalIgnoreCase))
                ?.Nickname is { Length: > 0 } name ? name : null;

    /// <summary>What to call someone, preferring our name for them over theirs.</summary>
    private string DisplayNameOf(string? peerId, string reported) => NicknameOf(peerId) ?? reported;

    /// <summary>Records a nickname against an identity and refreshes every screen showing it.</summary>
    private void SetNickname(string peerId, string reported, string? nickname)
    {
        var known = _config.Known.FirstOrDefault(k =>
            string.Equals(k.PeerId, peerId, StringComparison.OrdinalIgnoreCase));

        if (known is null)
        {
            known = new KnownPeer { PeerId = peerId, Username = reported };
            _config.Known.Add(known);
        }

        known.Nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();

        // Mirror onto the saved connection so the Connections list agrees without having to
        // know about KnownPeer at all.
        foreach (var saved in _config.Recent.Where(r =>
                     string.Equals(r.PeerId, peerId, StringComparison.OrdinalIgnoreCase)))
        {
            saved.Name = known.Nickname;
        }

        _config.Save();
        ApplyNicknames();
        RefreshSessions();
        RefreshPeers();
        UpdateConnectionSummary();
        ReloadRecent();
    }

    /// <summary>The nickname saved for whoever is on the other end of a connection.</summary>
    private string? NicknameFor(Connection connection)
    {
        if (connection.Kind == ConnectionKind.Local) return null;

        var peerId = connection.Kind == ConnectionKind.Peer
            ? connection.Id["peer:".Length..]
            : _config.Recent.FirstOrDefault(r =>
                connection.Info is not null &&
                r.Host.Equals(connection.Info.Host, StringComparison.OrdinalIgnoreCase))?.PeerId;

        if (peerId is null) return null;

        return _config.Known.FirstOrDefault(k =>
            string.Equals(k.PeerId, peerId, StringComparison.OrdinalIgnoreCase))?.Nickname;
    }

    /// <summary>
    /// Closes whatever is being browsed. Kept under the old name because a lot of code calls
    /// it when a connection turns out to be dead.
    /// </summary>
    private void Disconnect(string reason)
    {
        if (_active is { } connection) CloseConnection(connection, announce: false);
        else ClearBrowseView();

        _grantToken = null;
        SyncShareBack();

        if (_connections.Count == 0)
        {
            _pingTimer?.Stop();
            _pingTimer = null;
        }

        ActivityLog.Note(TransferDirection.Download, reason, LogKind.Network);
        UpdateConnectionSummary();
        UpdateButtons();

        // Reflect it on the Connections tab straight away rather than waiting for the sweep.
        _ = RefreshStatusesAsync();
    }
}
