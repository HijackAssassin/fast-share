using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FastShare;

public partial class MainWindow : Window
{
    private readonly AppConfig _config = AppConfig.Load();

    /// <summary>Signs connection requests, proving our PeerId. Null only if the key could not be loaded.</summary>
    private System.Security.Cryptography.ECDsa? _identity;

    /// <summary>
    /// A copy of a list the UI thread owns, safe to read from a server thread. Copying can
    /// collide with an edit in progress; that throws, and simply trying again gets a clean copy.
    /// </summary>
    private static T[] Snapshot<T>(Func<List<T>> list)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return list().ToArray(); }
            catch (Exception) when (attempt < 5) { Thread.Yield(); }
            catch (Exception) { return []; }
        }
    }

    private KnownPeer? KnownPeerOf(string peerId) =>
        Snapshot(() => _config.Known)
            .FirstOrDefault(k => string.Equals(k.PeerId, peerId, StringComparison.OrdinalIgnoreCase));
    private readonly ShareServer _server = new();
    private readonly ObservableCollection<string> _shareFolders = [];
    private readonly ObservableCollection<SidebarRow> _sidebar = [];
    private readonly ObservableCollection<ConnectionRow> _recent = [];
    private readonly ObservableCollection<LogEntry> _log = [];

    /// <summary>
    /// One row per transfer this session, newest first - ours and theirs alike.
    /// </summary>
    private readonly ObservableCollection<SessionRow> _transfers = [];


    /// <summary>Live rows for traffic the far side is driving, keyed by its flow.</summary>
    private readonly Dictionary<string, SessionRow> _flowRows = [];

    /// <summary>
    /// Everything currently open for browsing: our own shares, hosts we connected to, and
    /// peers sharing back. Several at once, switched between in the sidebar.
    /// </summary>
    private readonly ObservableCollection<Connection> _connections = [];

    /// <summary>Which of them the Browse tab is showing.</summary>
    private Connection? _active;

    /// <summary>
    /// One lane per direction: a queue of transfers and the state of the one running. Downloads
    /// and uploads run side by side - they travel opposite ways down the line, so neither slows
    /// the other - while within a lane transfers still take their turn.
    /// </summary>
    private sealed class TransferLane
    {
        public required TransferDirection Direction { get; init; }

        /// <summary>Waiting jobs in the order they will run (the queue panel can reorder them).</summary>
        public List<QueuedJob> Queue { get; } = [];

        /// <summary>The job running now, if any.</summary>
        public QueuedJob? Current;
        public bool Pumping;
        public CancellationTokenSource? Cts;
        public BatchInfo? Batch;
        public SessionRow? ActiveRow;
        public Func<Task>? UndoCancel;

        /// <summary>What the running job has told the other PC it is doing before it starts.</summary>
        public Preparing? Prep;

        /// <summary>The connection the running job is with, so the far side can cancel just it.</summary>
        public string? ConnectionId;

        /// <summary>Set when the other PC cancelled it, for the row's Result.</summary>
        public string? CancelledBy;

        /// <summary>The unfinished-transfer record for the running job, crossed off when it ends.</summary>
        public string? PendingId;
    }

    private readonly TransferLane _downLane = new() { Direction = TransferDirection.Download };
    private readonly TransferLane _upLane = new() { Direction = TransferDirection.Upload };
    private IEnumerable<TransferLane> Lanes => [_downLane, _upLane];

    /// <summary>
    /// The lane the running job belongs to. Carried by the async flow, so everything a queued
    /// job calls (RunTransferAsync, the job's own body) reads and writes its own lane's state
    /// without it being passed through every call.
    /// </summary>
    private static readonly AsyncLocal<TransferLane?> CurrentLane = new();
    private TransferLane Lane => CurrentLane.Value ?? _downLane;

    private CancellationTokenSource? _cts { get => Lane.Cts; set => Lane.Cts = value; }
    private BatchInfo? _batch { get => Lane.Batch; set => Lane.Batch = value; }
    private SessionRow? _activeRow { get => Lane.ActiveRow; set => Lane.ActiveRow = value; }
    private Func<Task>? _undoCancel { get => Lane.UndoCancel; set => Lane.UndoCancel = value; }

    private int QueuedCount => _downLane.Queue.Count + _upLane.Queue.Count;
    private bool AnyRunning => Lanes.Any(l => l.Cts is not null);
    private void CancelAll() { foreach (var lane in Lanes) try { lane.Cts?.Cancel(); } catch { } }

    // The old single-connection fields, now views onto whichever connection is active. Kept as
    // names rather than replaced everywhere, because every one of these reads the same thing it
    // always did - just of the active connection rather than of the only one.
    /// <summary>The active connection's client for changes - direct, or down a peer's tunnel.</summary>
    private ApiClient? _api => _active?.Writer;
    private IFileSource? _source => _active?.Source;
    private ConnectionInfo? _connected => _active?.Info;
    private Permissions? _granted => _active?.Granted;
    private bool _browsingLocal => _active?.Kind == ConnectionKind.Local;

    private Stack<string> _history => _active?.History ?? _noHistory;
    private Stack<string> _forward => _active?.Forward ?? _noHistory;
    private readonly Stack<string> _noHistory = new();

    private string _currentPath
    {
        get => _active?.CurrentPath ?? "";
        set { if (_active is not null) _active.CurrentPath = value; }
    }

    /// <summary>Our own folders served back down a connection we opened.</summary>
    private TunnelService? _tunnel;

    /// <summary>
    /// The session token for the host we are connected to, kept so a share-back can be started
    /// later - when Start Sharing is pressed - rather than only at the moment we connect.
    /// Null on loopback, where sharing back to ourselves would be pointless.
    /// </summary>
    private string? _grantToken;

    /// <summary>
    /// The peer who is browsing us back over a tunnel, once we have approved them, and the
    /// connection that tunnel rides on. The tunnel serves only what their reverse session
    /// permits, read live.
    /// </summary>
    private string? _reversePeerId;
    private Connection? _reverseConnection;

    /// <summary>Reverse-connect requests we are already showing a prompt for, so a second
    /// ping arriving mid-decision does not stack a second identical dialog.</summary>
    private readonly HashSet<string> _reversePrompting = new(StringComparer.OrdinalIgnoreCase);


    // Tracked separately so the status bar can show both directions at once.
    private TransferStats? _downStats;
    private TransferStats? _upStats;
    private readonly RateMeter _downMeter = new();
    private readonly RateMeter _upMeter = new();

    // Traffic somebody else is driving has no TransferStats to sample, so it is metered off
    // the running totals the endpoints keep. Without these, a peer pulling 40 GB off this PC
    // showed 0 B/s in the status bar and nothing at all on the Session tab.
    private readonly RateMeter _servedInMeter = new();
    private readonly RateMeter _servedOutMeter = new();
    private double _servedInRate, _servedOutRate;
    private string? _peerActivity;
    private bool _peerTextShown;

    private double _peakRate;


    private DispatcherTimer? _uiTimer;
    private DispatcherTimer? _pingTimer;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _refreshTimer;
    private bool _refreshing;

    private long _baseDownBytes, _baseUpBytes, _baseDownFiles, _baseUpFiles;

    private List<ListRow> _rows = [];
    private string _sortKey = "Name";
    private bool _sortDescending;

    private const int MaxLogEntries = 2000;

    public MainWindow()
    {
        Theme.Apply(_config.DarkMode);
        Format.Megabytes = _config.SpeedUnit == "MB";
        InitializeComponent();

        // Version comes from the assembly, so the title bar and Settings can never disagree
        // about which build this is.
        Title = $"FastShare {AppVersion.Text}";
        UpdateThemeGlyph();

        foreach (var folder in _config.Folders) _shareFolders.Add(folder);
        ShareList.ItemsSource = _shareFolders;
        ShareSidebar.ItemsSource = _sidebar;
        RecentList.ItemsSource = _recent;
        FavoritesList.ItemsSource = _favorites;
        LogList.ItemsSource = _log;
        TransferLog.ItemsSource = _transfers;
        ApplyViewScale();
        SharePortText.Text = $"Port {_config.Port} — change it in Settings.";

        // The code box deliberately starts empty - a stale code from last session is more
        // often wrong than right, since hosts reissue codes when they restart.
        CodeBox.Text = "";

        // A stable identity of our own, so blocks and remembered permissions survive the
        // other side changing address or reissuing its code.
        if (string.IsNullOrEmpty(_config.PeerId))
        {
            _config.PeerId = PeerIdentity.NewId();
            _config.Save();
        }

        // The key that proves that identity. Existing installs get one on first run of this
        // build; their PeerId is unchanged, so nothing either side remembers is lost.
        try
        {
            if (string.IsNullOrEmpty(_config.IdentityKey))
            {
                _config.IdentityKey = PeerIdentity.NewKey();
                _config.Save();
            }
            _identity = PeerIdentity.LoadKey(_config.IdentityKey);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("identity", "could not load the identity key; connecting without proof", ex);
        }

        _server.Sessions.KnownKey = id => KnownPeerOf(id)?.PublicKey;
        _server.Sessions.AutoApproves = id => KnownPeerOf(id)?.AutoApprove == true;
        _server.Sessions.AutoApproved += pending => Dispatcher.BeginInvoke(() =>
        {
            var who = DisplayNameOf(pending.PeerId, pending.Username);
            RememberInbound(pending.PeerId, pending.Username, pending.RemoteAddress, pending.Os);
            ActivityLog.Note(TransferDirection.Download,
                $"{who} connected (allowed without asking) — may {pending.Suggested.Summary}", LogKind.Network);
            FlashStatus($"{who} connected");
            NotifyIfAway(("Connected", $"{who} connected to you."));
        });

        // Read from server threads while the UI thread may be editing these lists, so each read
        // takes a snapshot (and retries if it lands mid-edit) rather than enumerating live.
        _server.Sessions.IsBlocked = id => Snapshot(() => _config.Blocked)
            .Any(b => string.Equals(b.PeerId, id, StringComparison.OrdinalIgnoreCase));

        _server.Sessions.RememberedPermissions = id => KnownPeerOf(id)?.ToPermissions();

        _server.Sessions.ApprovalNeeded += OnApprovalNeeded;
        _server.Sessions.Changed += OnSessionsChanged;
        _server.Sessions.SessionEnded += session => Dispatcher.BeginInvoke(() => OnSessionEnded(session));
        _server.Sessions.ResumeAsked += ask => Dispatcher.BeginInvoke(() => OnResumeAsked(ask));
        _server.Tunnels.Changed += OnTunnelsChanged;
        _server.SpeedTestReported += OnSpeedTestReported;

        // All-time figures are a persisted base plus whatever this run has done. Keeping the
        // base fixed for the lifetime of the window makes writing them back idempotent, so it
        // can happen as often as we like without double counting.
        _baseDownBytes = _config.LifetimeDownloadedBytes;
        _baseUpBytes = _config.LifetimeUploadedBytes;
        _baseDownFiles = _config.LifetimeDownloadedFiles;
        _baseUpFiles = _config.LifetimeUploadedFiles;

        // A saved PC can never be us. One that says so (a copied settings folder gives two
        // installs one identity) would match - and merge with - everyone; drop the claim.
        foreach (var saved in _config.Recent.Where(r => string.Equals(r.PeerId, _config.PeerId, StringComparison.OrdinalIgnoreCase)))
            saved.PeerId = null;

        // A brand-new install asks for a name once, on its first launch.
        var askForName = _config.FirstRunUtc is null && string.IsNullOrWhiteSpace(_config.Username);
        _config.FirstRunUtc ??= DateTime.UtcNow;
        _config.Save();
        if (askForName) Loaded += (_, _) => Dispatcher.BeginInvoke(AskForName);

        Loaded += (_, _) => ClearBrowseView();
        Loaded += (_, _) => UpdateTrayIcon();
        Loaded += (_, _) => StartLanWatch();
        Loaded += (_, _) => StartDevMode();
        Loaded += (_, _) => StartUpdateChecks();
        // An (empty) menu from the start, or right-click never raises ContextMenuOpening.
        Loaded += (_, _) => TransferLog.ContextMenu = new System.Windows.Controls.ContextMenu();
        Loaded += (_, _) => { RefreshFolderRules(); RefreshBlocked(); };
        System.Windows.Application.Current.SessionEnding += (_, _) => _quitRequested = true;

        ActivityLog.Appended += OnActivityAppended;
        ActivityLog.FileCompleted += OnFileCompleted;

        Reachability.Updated += state => Dispatcher.BeginInvoke(() =>
        {
            ShowPortState(state);
            // A second Updated for the same check only adds the UPnP answer: no second note.
            if (state.Explanation != _portNoted) ActivityLog.Note(TransferDirection.Upload, state.Explanation, LogKind.Network);
            _portNoted = state.Explanation;
        });

        // Debug mode is persisted, so a console left on for troubleshooting comes back with
        // the app rather than having to be re-enabled every launch.
        // Everything worth knowing goes to log.txt whatever this says; the old debug mode only
        // added a console window, so it is gone and any saved "on" is ignored.
        Diagnostics.SetVerbose(false);
        RefreshBiggest();
        RefreshHistory();
        ReloadRecent();

        // One always-running UI timer. Keeping it alive between transfers is what stops the
        // speed readout blinking in and out: an idle meter reads zero rather than vanishing.
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        DiscordPresence.SetEnabled(_config.DiscordPresence);

        Loaded += async (_, _) =>
        {
            await RefreshStatusesAsync();
            // Two seconds: the lights are the main signal that someone came online, and a
            // probe is one tiny request per saved host. A host still being probed is skipped.
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += async (_, _) => await RefreshStatusesAsync();
            _statusTimer.Start();

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _refreshTimer.Tick += async (_, _) =>
            {
                await AutoRefreshAsync();
                await RefreshTreeAsync();
            };
            _refreshTimer.Start();

            // Sessions that stop pinging are gone, whatever happened to them. Twenty seconds
            // against a four-second ping is five missed in a row - not a blip.
            var sweeper = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            sweeper.Tick += (_, _) =>
            {
                _server.Sessions.ExpireStale(TimeSpan.FromSeconds(20));

                // Always refresh, not only when a session expired: this is what notices a peer
                // who left cleanly and announces "<name> disconnected" by name (per-name now,
                // instead of the old bare "N connection(s) timed out").
                UpdateConnectionSummary();
            };
            sweeper.Start();

            // Once, at startup. Whether the port is forwarded is a property of the router,
            // not something that changes minute to minute, and re-probing our own public
            // address on a timer is traffic for no new information.
            await UpdatePortStateAsync();

            // Only after the check, never during it: the probe binds a throwaway listener on
            // the share port, and starting the real one underneath it collides.
            if (_config.WasSharing && !_server.IsRunning) await StartSharingAsync();

            // Shares that are not excluded from Defender yet get excluded now (one UAC prompt,
            // and only if something is missing).
            await EnsureExcludedAsync(_config.Folders);
        };
    }

    // =======================================================================
    // Theme
    // =======================================================================

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        _config.DarkMode = !Theme.IsDark;
        Theme.Apply(_config.DarkMode);
        _config.Save();
        UpdateThemeGlyph();
    }

    // Offer the theme you would switch TO: a sun while dark, a moon while light.
    private void UpdateThemeGlyph() => ThemeGlyph.Text = Theme.IsDark ? "\uE706" : "\uE708";

    /// <summary>
    /// Binds a brush by resource key rather than assigning a fetched brush, so the colour
    /// follows a later theme switch instead of freezing at whatever it was when set.
    /// </summary>
    private void SetState(string text, string brushKey)
    {
        StateText.Text = text;
        StateDot.SetResourceReference(ForegroundProperty, brushKey);
    }

    /// <summary>
    /// First launch: what should people see when you connect to them? Skip keeps the desktop
    /// name (Settings → Your name changes it any time).
    /// </summary>
    private void AskForName()
    {
        var name = PromptDialog.Ask(this, "Welcome to FastShare",
            "Pick a name. It's what people see when you connect to them.",
            Environment.MachineName,
            hint: "Skip to use this PC's name. You can change it any time in Settings.",
            cancelText: "Skip");

        if (string.IsNullOrWhiteSpace(name) || name == Environment.MachineName) return;
        _config.Username = name.Trim();
        _config.Save();
        Diagnostics.Log("setup", "name chosen on first launch");
    }

    // =======================================================================
    // Notification area
    // =======================================================================

    private TrayIcon? _tray;
    private bool _toldAboutTray;

    /// <summary>Notifications shown by the icon and not yet gone.</summary>
    private int _notificationsShowing;

    /// <summary>Takes a notification-only icon away if Windows never says the notification ended.</summary>
    private DispatcherTimer? _trayFallback;

    /// <summary>
    /// The icon lives in the notification area only while "close to tray" is on. With it off,
    /// it is put there just long enough to show a notification (Windows needs an icon to show
    /// one from) and taken away again once that has gone.
    /// </summary>
    private void UpdateTrayIcon()
    {
        if (_config.CloseToTray) CreateTrayIcon();
        else if (_notificationsShowing == 0) RemoveTrayIcon();
    }

    private void CreateTrayIcon()
    {
        if (_tray is not null) return;
        try
        {
            _tray = new TrayIcon(this, $"FastShare {AppVersion.Text}");
            Diagnostics.Log("tray", "notification icon created");
            _tray.Activated += RestoreFromTray;
            _tray.MenuRequested += ShowTrayMenu;
            _tray.NotificationGone += OnNotificationGone;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("tray", "could not create the notification icon", ex);
        }
    }

    private void RemoveTrayIcon()
    {
        _trayFallback?.Stop();
        _notificationsShowing = 0;
        if (_tray is null) return;
        _tray.Dispose();
        _tray = null;
    }

    private void OnNotificationGone()
    {
        if (_notificationsShowing > 0) _notificationsShowing--;
        if (_notificationsShowing == 0 && !_config.CloseToTray) RemoveTrayIcon();
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Open FastShare", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => RestoreFromTray();
        menu.Items.Add(open);
        menu.Items.Add(new Separator());

        var quit = new MenuItem { Header = "Quit FastShare" };
        quit.Click += (_, _) => { RestoreFromTray(); QuitApp(); };
        menu.Items.Add(quit);

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>With "close to tray" on, X hides the window to the notification area.</summary>
    private void HideToTray()
    {
        Hide();
        if (_toldAboutTray || _tray is null) return;
        _toldAboutTray = true;
        _tray.Notify("FastShare is still running",
            _server.IsSharing
                ? "Still sharing. Right-click the icon and choose Quit to close it completely."
                : "Click the icon to bring it back, or right-click it to quit.");
    }

    /// <summary>Set by the tray's Quit, an update, or Windows logging off: close for real.</summary>
    private bool _quitRequested;

    /// <summary>Closes FastShare completely, whatever the close-to-tray setting says.</summary>
    public void QuitApp()
    {
        _quitRequested = true;
        Close();
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>A notification from the tray icon, for things worth interrupting for.</summary>
    private void Notify(string title, string text)
    {
        CreateTrayIcon();
        Diagnostics.Log("notify", $"{title}: {text}{(_tray is null ? " (no tray icon)" : "")}");
        if (_tray is null) return;
        _tray.Notify(title, text);
        _notificationsShowing++;

        if (_config.CloseToTray) return;
        if (_trayFallback is null)
        {
            _trayFallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _trayFallback.Tick += (_, _) => { if (!_config.CloseToTray) RemoveTrayIcon(); };
        }
        _trayFallback.Stop();
        _trayFallback.Start();
    }

    /// <summary>The same, but only when FastShare is not what you are looking at.</summary>
    private void NotifyIfAway((string Title, string Text) message)
    {
        if (IsActive && IsVisible && WindowState != WindowState.Minimized) return;
        Notify(message.Title, message.Text);
    }

    // =======================================================================
    // Closing
    // =======================================================================

    private bool _shutdownDone;
    private bool _shuttingDown;

    /// <summary>
    /// Closing is done in two passes. The first cancels the close and runs the goodbye - which
    /// needs to await network calls - and the second, once that is finished, lets the window go.
    ///
    /// The goodbye used to run inside this handler with Task.WaitAll, on the UI thread. The
    /// calls it waited for needed that same thread to finish, so every close froze for the full
    /// three-second timeout, and some messages never went out at all.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownDone)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_shuttingDown) return;

        if (_config.CloseToTray && _tray is not null && !_quitRequested)
        {
            HideToTray();
            return;
        }
        _quitRequested = false;   // a cancelled quit below must not leave X quitting next time

        // Don't let a transfer be lost to an accidental close. Covers our own transfers (and
        // anything queued behind them) and transfers other people are driving against us.
        if (TransferInProgress())
        {
            var answer = MessageBox.Show(this,
                "A transfer is currently in progress.\n\n" +
                "If you close FastShare now it stops. What already arrived is kept, and next time you " +
                "connect to them you will be asked whether to resume it.\n\n" +
                "Close anyway?",
                "Transfer in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;
        }

        _quitRequested = true;
        _shuttingDown = true;
        _ = ShutDownThenCloseAsync();
    }

    /// <summary>
    /// Tells everyone we are going, in both directions, then closes for real.
    ///
    ///  - Hosts we are connected to get a "leave", so we vanish from their screen at once.
    ///  - Everyone connected to us has their session ended with the reason "closed", which
    ///    their parked watch receives immediately - their screen says we closed FastShare.
    ///  - The tunnel we serve our folders over is shut, so anyone browsing us back sees it go.
    ///
    /// All of it on a short leash: a peer that has already vanished must not hold the window open.
    /// </summary>
    private async Task ShutDownThenCloseAsync()
    {
        IsEnabled = false;
        Title = $"FastShare {AppVersion.Text} — closing…";

        try
        {
            // Our own transfer, if one was running: paused, not undone, so it can be resumed.
            foreach (var lane in Lanes) lane.Queue.Clear();
            _pausingForClose = true;
            CancelAll();

            // Let it clean up (a cancelled upload asks the far side to remove what it had
            // already sent) while the connection it needs is still up.
            for (var waited = 0; AnyRunning && waited < 50; waited++) await Task.Delay(100);

            var goodbyes = _connections
                .Where(c => c.Api is not null)
                .Select(c => Task.Run(() => c.Api!.LeaveAsync("closed")))
                .ToList();

            Diagnostics.Log("close", $"closing: leaving {goodbyes.Count} host(s), ending {_server.Sessions.Active.Count} session(s) with 'closed'");
            _server.Sessions.RevokeAll(DenyReasons.Closed);
            _server.Tunnels.DropAll();

            if (_tunnel is { } tunnel)
            {
                _tunnel = null;
                goodbyes.Add(Task.Run(async () => await tunnel.DisposeAsync()));
            }

            await Task.WhenAny(Task.WhenAll(goodbyes), Task.Delay(TimeSpan.FromSeconds(3)));

            // The ended sessions' watch replies are already on their way; a moment for them to
            // leave before the listener stops.
            await Task.Delay(300);
            try { await _server.StopAsync().WaitAsync(TimeSpan.FromSeconds(4)); } catch { }

            CommitLifetime();
            _config.Save();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("close", "shutdown did not finish cleanly", ex);
        }

        _tray?.Dispose();
        _tray = null;
        DiscordPresence.SetEnabled(false);
        _shutdownDone = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        ActivityLog.Appended -= OnActivityAppended;
        _server.Sessions.Changed -= OnSessionsChanged;
        _server.Tunnels.Changed -= OnTunnelsChanged;
        _uiTimer?.Stop();
        _refreshTimer?.Stop();
        _statusTimer?.Stop();
        _pingTimer?.Stop();
        StopLanResponder();
        CancelAll();

        foreach (var connection in _connections.ToList())
        {
            connection.Watch?.Cancel();
            connection.Api?.Dispose();
        }

        _tray?.Dispose();
        base.OnClosed(e);
    }
}
