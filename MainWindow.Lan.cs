using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FastShare;

public partial class MainWindow
{
    // =======================================================================
    // On your network (Connections tab)
    // =======================================================================

    private DispatcherTimer? _lanTimer;
    private bool _lanLooking;

    /// <summary>Answers other PCs' "who is here?" (and, while sharing, lookups by code).</summary>
    private IDisposable? _lanResponder;
    private int _lanResponderPort;

    /// <summary>
    /// Runs for as long as FastShare is open, sharing or not, so other PCs list us - red when we
    /// are not sharing. Started only once Windows Firewall lets this copy in (normally from the
    /// first time it shared): listening before then would pop up Windows' own firewall prompt at
    /// launch. Restarted when sharing starts, in case the port changed.
    /// </summary>
    private async Task StartLanResponderAsync(bool firewallChecked = false)
    {
        if (_lanResponder is not null && _lanResponderPort == _config.Port) return;

        if (!firewallChecked)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) ||
                await Task.Run(() => Firewall.Check(exe)) != Firewall.State.Allowed) return;
        }

        _lanResponder?.Dispose();
        _lanResponderPort = _config.Port;
        _lanResponder = LanDiscovery.Respond(_config.Port,
            () => _server.IsSharing ? _server.Code : null,
            () => new LanDiscovery.Self(_config.PeerId,
                string.IsNullOrWhiteSpace(_config.Username) ? Environment.MachineName : _config.Username.Trim(),
                AppVersion.Device));
    }

    private void StopLanResponder()
    {
        _lanResponder?.Dispose();
        _lanResponder = null;
    }

    /// <summary>
    /// Looks once at startup, again whenever the Connections tab is opened, and every five
    /// seconds while it stays open. One small broadcast a round, and nothing at all while you
    /// are on another tab: no listener waiting for announcements, no list going stale.
    /// </summary>
    private void StartLanWatch()
    {
        _lanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _lanTimer.Tick += async (_, _) =>
        {
            if (ReferenceEquals(Tabs.SelectedItem, ConnectionsTab) && IsVisible &&
                WindowState != WindowState.Minimized)
                await RefreshLanAsync();
        };
        _lanTimer.Start();

        Tabs.SelectionChanged += async (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Tabs) && ReferenceEquals(Tabs.SelectedItem, ConnectionsTab))
                await RefreshLanAsync();
        };

        // Coming back to the window (restored, or clicked into) looks straight away rather than
        // showing a list from before it was minimized.
        StateChanged += async (_, _) =>
        {
            if (WindowState != WindowState.Minimized && ReferenceEquals(Tabs.SelectedItem, ConnectionsTab))
                await RefreshLanAsync();
        };
        Activated += async (_, _) =>
        {
            if (ReferenceEquals(Tabs.SelectedItem, ConnectionsTab)) await RefreshLanAsync();
        };

        _ = StartLanResponderAsync();
        _ = RefreshLanAsync();
    }

    private async Task RefreshLanAsync()
    {
        if (_lanLooking) return;
        _lanLooking = true;
        try
        {
            // The usual port, ours, and any port a saved PC on this network uses. A PC sharing
            // on some other port is not asked, which is the price of not scanning.
            var ports = new[] { 5201, _config.Port }
                .Concat(_config.Recent.Where(r => ShortCode.IsPrivate(r.Host)).Select(r => r.Port));
            var found = await LanDiscovery.ListAsync(ports, _config.PeerId, TimeSpan.FromMilliseconds(700));

            var live = WhoIsLive();
            var rows = found
                .Select(p => new LanRow
                {
                    Peer = p,
                    Name = DisplayNameOf(p.PeerId, p.Name),
                    IsConnected = live.Has(p.PeerId, p.Host, p.Port),
                })
                .OrderByDescending(r => r.Peer.Sharing || r.IsConnected)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var selected = (LanList.SelectedItem as LanRow)?.Peer.PeerId;
            LanList.ItemsSource = rows;
            LanList.SelectedItem = rows.FirstOrDefault(r => r.Peer.PeerId == selected);
            LanList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            LanEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LanEmpty.Text = "No other PCs on your network have FastShare open right now.";
        }
        finally { _lanLooking = false; }
    }

    private async void OnLanDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a real row, not the blank space under the list.
        if (e.OriginalSource is not DependencyObject d || ItemsControl.ContainerFromElement(LanList, d) is null) return;
        await ConnectLanAsync();
    }

    private async void OnLanConnectClick(object sender, RoutedEventArgs e) => await ConnectLanAsync();

    private async Task ConnectLanAsync()
    {
        if (LanList.SelectedItem is not LanRow row) return;
        if (!row.Peer.Sharing)
        {
            MessageBox.Show(this,
                $"{row.Name} has FastShare open but is not sharing, so there is nothing to connect to yet. " +
                "Once they turn sharing on, their dot turns green.",
                "Not sharing", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await ConnectToAsync(row.Peer.Code, (row.Peer.Host, row.Peer.Port), peerId: row.Peer.PeerId);
    }
}
