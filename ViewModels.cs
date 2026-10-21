using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace FastShare;

/// <summary>One row in the file list.</summary>
public sealed class ListRow
{
    public required string Name { get; init; }
    public required string RemotePath { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Created { get; init; }

    /// <summary>Segoe MDL2 Assets glyphs, matching Explorer's icon language.</summary>
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE7C3";
    public string SizeText => IsDirectory ? "" : Format.Bytes(Size);
    public string CreatedText => Created.ToString("g");
    public string ModifiedText => Modified.ToString("g");

    public string TypeText
    {
        get
        {
            if (IsDirectory) return "File folder";
            var extension = Path.GetExtension(Name);
            return extension.Length > 1 ? $"{extension[1..].ToUpperInvariant()} File" : "File";
        }
    }
}

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long value)
    {
        double n = value;
        var unit = 0;
        while (n >= 1024 && unit < Units.Length - 1) { n /= 1024; unit++; }
        return unit == 0 ? $"{value:N0} B" : $"{n:0.##} {Units[unit]}";
    }

    /// <summary>
    /// Speeds in megabytes instead of megabits (Settings → Speed shown in). Megabits is the
    /// default because that is what internet plans are sold in ("1 Gig fiber" = 1000 Mbit/s).
    /// </summary>
    public static bool Megabytes { get; set; }

    /// <summary>
    /// A speed in the chosen unit: "560 Mbit/s", or "70 MB/s". Always returns a figure, never
    /// an empty string. An idle transfer reading "0 Mbit/s" is information; a label that blanks
    /// out looks like the readout broke.
    /// </summary>
    public static string Rate(double bytesPerSecond)
    {
        if (Megabytes) return bytesPerSecond < 1 ? "0 MB/s" : $"{Bytes((long)bytesPerSecond)}/s";
        return bytesPerSecond < 1 ? "0 Mbit/s" : Bits(bytesPerSecond * 8);
    }

    /// <summary>Bits per second in decimal units, as ISPs quote them: "940 Mbit/s", "1.2 Gbit/s".</summary>
    public static string Bits(double bitsPerSecond) => bitsPerSecond switch
    {
        >= 1e9 => $"{bitsPerSecond / 1e9:0.##} Gbit/s",
        >= 100e6 => $"{bitsPerSecond / 1e6:0} Mbit/s",
        >= 1e6 => $"{bitsPerSecond / 1e6:0.#} Mbit/s",
        _ => $"{bitsPerSecond / 1e3:0} Kbit/s",
    };

    /// <summary>A time estimate, rounded the way it deserves: "1h 05m", "4m 20s", "35s".</summary>
    public static string Duration(double seconds)
    {
        var total = (long)Math.Ceiling(Math.Max(seconds, 0));
        if (total >= 3600) return $"{total / 3600}h {total % 3600 / 60:00}m";
        if (total >= 60) return $"{total / 60}m {total % 60:00}s";
        return $"{total}s";
    }
}

/// <summary>Whether a saved host is currently sharing.</summary>
public enum ConnectionState { Checking, Online, Offline }

/// <summary>
/// A row on the Connections tab. Wraps a saved host and tracks whether it is answering,
/// so the list can show a red or green light without the user trying to connect first.
/// </summary>
public sealed class ConnectionRow : INotifyPropertyChanged
{
    public ConnectionRow(SavedConnection model) => Model = model;

    public SavedConnection Model { get; }

    public string DisplayName => Model.DisplayName;
    /// <summary>
    /// What the PC runs on rather than its address, which is nobody's business on screen.
    /// Every FastShare so far is Windows, so that is what an older build that never said is.
    /// </summary>
    public string Detail => Model.Os ?? "Windows";

    private ConnectionState _state = ConnectionState.Checking;
    public ConnectionState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Raise(nameof(State));
            Raise(nameof(StatusText));
        }
    }

    private bool _isConnected;
    /// <summary>
    /// True when there is a live session or open connection with this person right now - as
    /// opposed to merely reachable. It reads as "Connected" rather than "Online", because "they
    /// are online" and "we are talking to them" are different things worth telling apart.
    /// </summary>
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            Raise(nameof(IsConnected));
            Raise(nameof(State));       // the dot binds to State; nudge it too
            Raise(nameof(StatusText));
        }
    }

    private bool _isBlocked;
    /// <summary>On our block list: still listed (so a right-click can unblock), marked Blocked.</summary>
    public bool IsBlocked
    {
        get => _isBlocked;
        set { if (_isBlocked == value) return; _isBlocked = value; Raise(nameof(IsBlocked)); Raise(nameof(StatusText)); }
    }

    public string StatusText =>
        _isBlocked ? (_isConnected ? "Connected · Blocked" : "Blocked")
        : _isConnected ? "Connected"
        : _state switch
        {
            ConnectionState.Online => "Online",
            ConnectionState.Offline => "Offline",
            _ => "Checking…",
        };

    /// <summary>Call after a rename so the list picks up the new label.</summary>
    public void RefreshName() => Raise(nameof(DisplayName));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One starred person under the Favorites section. Carries the address so double-clicking can
/// connect straight away, and the peer id so the star can be toggled and matched back.
/// </summary>
public sealed class FavoriteRow : INotifyPropertyChanged
{
    public required string PeerId { get; init; }
    public required string Name { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; }

    /// <summary>Where a double-click connects to, or null if we have no saved address for them.</summary>
    public string? Target => string.IsNullOrWhiteSpace(Host) ? null : Host;

    // Favorites now show the same live status as Recent connections - dot, and Offline / Online /
    // Connected - so the two lists read the same way. State is mirrored from the matching recent
    // row when there is one, or from whether a session is live when the favorite has no address.
    private ConnectionState _state = ConnectionState.Offline;
    public ConnectionState State
    {
        get => _state;
        set { if (_state == value) return; _state = value; Raise(nameof(State)); Raise(nameof(StatusText)); }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            Raise(nameof(IsConnected));
            Raise(nameof(State));
            Raise(nameof(StatusText));
        }
    }

    private bool _isBlocked;
    /// <summary>On our block list: still listed (so a right-click can unblock), marked Blocked.</summary>
    public bool IsBlocked
    {
        get => _isBlocked;
        set { if (_isBlocked == value) return; _isBlocked = value; Raise(nameof(IsBlocked)); Raise(nameof(StatusText)); }
    }

    public string StatusText =>
        _isBlocked ? (_isConnected ? "Connected · Blocked" : "Blocked")
        : _isConnected ? "Connected"
        : _state switch
        {
            ConnectionState.Online => "Online",
            ConnectionState.Offline => "Offline",
            _ => "Checking…",
        };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A sharing FastShare found on this network, listed under "On your network".</summary>
public sealed class LanRow
{
    public required LanDiscovery.LanPeer Peer { get; init; }
    public required string Name { get; init; }
    public bool IsConnected { get; init; }

    /// <summary>What it runs on, like Recent connections; builds before 2.4.24 do not say.</summary>
    public string Detail => Peer.Os ?? "Windows";

    /// <summary>Green when it can be connected to, red when FastShare is open but not sharing.</summary>
    public ConnectionState State => Peer.Sharing || IsConnected ? ConnectionState.Online : ConnectionState.Offline;

    public string StatusText => IsConnected ? "Connected" : Peer.Sharing ? "Online" : "Not sharing";
}

/// <summary>One line in the all-time top ten, ready for binding.</summary>
public sealed class BiggestRow
{
    public required int Rank { get; init; }
    public required TransferRecord Record { get; init; }

    public string FileName => Record.FileName;
    public string SizeText => Format.Bytes(Record.Bytes);

    /// <summary>"downloaded from Dillon" / "sent to Dillon", or just the direction if unknown.</summary>
    public string DirectionText
    {
        get
        {
            var who = string.IsNullOrWhiteSpace(Record.Peer) ? null : Record.Peer;
            return Record.Sent
                ? who is null ? "sent" : $"sent to {who}"
                : who is null ? "downloaded" : $"downloaded from {who}";
        }
    }
}

/// <summary>
/// What one transfer moved, for the Session tab's summary line and its details popup.
///
/// Built before the transfer runs and read after it finishes. Transfers are serialised through
/// the queue, so exactly one of these is live at a time.
/// </summary>
public sealed class BatchInfo
{
    public required string From { get; init; }
    public required string To { get; init; }

    /// <summary>The other party, whichever side of the arrow they are on.</summary>
    public string Counterparty => From == "you" ? To : From;

    /// <summary>Where the files ended up ("C:\Users\…\Downloads", or a folder on their PC), for the first line.</summary>
    public string? Destination { get; init; }
    public required List<string> Folders { get; init; }
    public required List<string> Files { get; init; }

    /// <summary>Disk kinds this transfer moved between, source → destination, for the Activity
    /// Log's Drives column. Null on an end that could not be told.</summary>
    public string? SourceDrive { get; set; }
    public string? DestDrive { get; set; }

    /// <summary>"43 files, 4 folders" - plural-correct, and folders omitted when there are none.</summary>
    public string Describe(long filesDone)
    {
        var files = $"{filesDone:N0} file{(filesDone == 1 ? "" : "s")}";
        if (Folders.Count == 0) return files;
        return $"{files}, {Folders.Count:N0} folder{(Folders.Count == 1 ? "" : "s")}";
    }

    /// <summary>The full contents, folders first, for the popup.</summary>
    public List<string> Lines()
    {
        var lines = new List<string>();

        // Where it went is not part of what was sent - it used to appear as a folder you had
        // downloaded ("Downloads") - so it is said on its own line instead.
        if (!string.IsNullOrWhiteSpace(Destination))
        {
            lines.Add($"{(From == "you" ? $"Sent to {To}" : $"From {From}")}, saved in {Destination}");
            lines.Add("");
        }

        if (Folders.Count > 0)
        {
            lines.Add($"Folders ({Folders.Count:N0})");
            lines.AddRange(Folders.Order(StringComparer.OrdinalIgnoreCase).Select(f => "    " + f));
            lines.Add("");
        }

        lines.Add($"Files ({Files.Count:N0})");
        lines.AddRange(Files.Order(StringComparer.OrdinalIgnoreCase).Select(f => "    " + f));
        return lines;
    }
}

/// <summary>
/// How a transfer's direction and counterparty read in the Activity Log and History.
///
/// The wording is from the reader's point of view. What you did is plain - "Uploaded",
/// "Downloaded". What someone else did to you spells out the "you", so a glance tells the two
/// apart: "Uploaded to You" (they pushed files here), "Downloaded From You" (they pulled files
/// off here). And the counterparty is "You" when it is this machine, never "Yourself".
/// </summary>
public static class TransferWording
{
    /// <summary>
    /// How a finished transfer ended, as one word, from its Result text (null = it finished
    /// cleanly). Matches the wording the transfer code writes there.
    /// </summary>
    public static string Outcome(string? problem)
    {
        if (string.IsNullOrWhiteSpace(problem)) return "Completed";
        var p = problem.Trim();
        if (p.StartsWith("Paused", StringComparison.OrdinalIgnoreCase)) return "Paused";
        if (p.StartsWith("Discarded", StringComparison.OrdinalIgnoreCase)) return "Discarded";
        if (p.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)) return "Cancelled";
        if (char.IsDigit(p[0]) && p.Contains(" failed", StringComparison.OrdinalIgnoreCase)) return "Some failed";
        if (p.StartsWith("Not resumed", StringComparison.OrdinalIgnoreCase)) return "Interrupted";
        if (p.StartsWith("Stopped", StringComparison.OrdinalIgnoreCase))
            return p.Contains("Stopped early", StringComparison.OrdinalIgnoreCase) ? "Failed" : "Interrupted";
        return "Failed";
    }

    public static string Direction(bool sent, bool peerDriven, bool live)
    {
        if (peerDriven)
        {
            return sent
                ? (live ? "Downloading From You" : "Downloaded From You")
                : (live ? "Uploading to You" : "Uploaded to You");
        }

        return sent
            ? (live ? "Uploading" : "Uploaded")
            : (live ? "Downloading" : "Downloaded");
    }

    public static string User(string? peer)
    {
        if (string.IsNullOrWhiteSpace(peer)) return "—";
        return peer is "Yourself" or "Yourself (localhost)"
            ? peer.Replace("Yourself", "You")
            : peer!;
    }
}

/// <summary>
/// One row on the Session tab: the same shape as a history row, but alive.
///
/// The Session tab used to be a line per file, which meant a folder of ten thousand small
/// files produced ten thousand rows and answered nothing. This is one row per transfer -
/// who, which way, how much - updated in place while it runs and then left as the record of
/// it, exactly matching the line the History tab will show afterwards.
/// </summary>
public sealed class SessionRow : INotifyPropertyChanged
{
    public required HistoryEntry Entry { get; init; }

    /// <summary>Still moving. Drives the pulsing "in progress" wording rather than a result.</summary>
    public bool Live { get; set; } = true;

    /// <summary>Runs this transfer again (only what is missing moves); null when it can't be retried.</summary>
    public Func<Task>? Retry { get; set; }

    /// <summary>Set while running, because a live row has no finished total to divide by.</summary>
    public double LiveRate { get; set; }

    /// <summary>How much this transfer will move in all, when it is known - what the bar and
    /// the percentage are measured against. Zero means "known only as a count", so the bar
    /// runs indeterminate rather than lying about a proportion.</summary>
    public long Total { get; set; }

    /// <summary>The full contents, kept behind the row and opened by double-clicking it.</summary>
    public IReadOnlyList<string>? Details
    {
        get => _details ?? DetailsProvider?.Invoke();
        set => _details = value;
    }

    private IReadOnlyList<string>? _details;

    /// <summary>Builds the contents on demand while the transfer is still running.</summary>
    public Func<IReadOnlyList<string>>? DetailsProvider { get; set; }

    /// <summary>
    /// Waiting its turn: its place in line ("Queued · position 1"), shown where the progress bar
    /// goes. Zero once it runs (or for a row that never queued).
    /// </summary>
    public int QueuePosition { get; set; }

    /// <summary>
    /// Not started yet because the other PC is still getting it ready: a few words for the
    /// Progress cell ("Getting file list") and the full sentence for Result. Null otherwise.
    /// </summary>
    public string? Preparing { get; set; }
    public string? PreparingDetail { get; set; }

    public bool IsQueued => QueuePosition > 0 || Preparing is not null;

    /// <summary>
    /// Everything has moved and the two PCs are comparing fingerprints (Check files after
    /// every transfer). Shown instead of a "100%" that looks stuck.
    /// </summary>
    public bool Checking { get; set; }

    /// <summary>How far the check has got (0-100), or null while that is not known (older peer).</summary>
    public double? CheckPercent { get; set; }

    /// <summary>The bar shows while the transfer is live (or queued); a finished row shows its outcome word instead.</summary>
    public bool ShowProgress => Live || IsQueued;

    /// <summary>Whether the bar can show a real proportion, or has to run indeterminate.</summary>
    public bool HasPercent => (Live && (Checking ? CheckPercent is not null : Total > 0)) || QueuePosition > 0;

    /// <summary>The share of the transfer moved - or, once it is checking, a fresh bar for the check.</summary>
    public double Percent => IsQueued ? 0
        : Checking ? CheckPercent ?? 0
        : Total > 0 ? Math.Clamp(Entry.Bytes * 100.0 / Total, 0, 100) : 0;

    public string PercentText =>
        Preparing is not null ? Preparing
        : IsQueued ? $"Queued · position {QueuePosition}"
        : !Live ? "" : Checking ? (CheckPercent is { } c ? $"Checking {c:0}%" : "Checking files…")
        : Total > 0 ? $"{Percent:0}%" : "…";

    /// <summary>
    /// One word for how a finished transfer ended, in the Progress column: Completed, Some
    /// failed, Failed, Interrupted, Cancelled, Paused or Discarded. Read from the Result text,
    /// so rows reloaded from history get one too.
    /// </summary>
    public string OutcomeText => Live || IsQueued ? "" : TransferWording.Outcome(Entry.Problem);

    /// <summary>Colour for <see cref="OutcomeText"/>: "Good", "Bad" or "" (plain).</summary>
    public string OutcomeTone => OutcomeText switch
    {
        "Completed" => "Good",
        "Failed" or "Interrupted" or "Some failed" => "Bad",
        _ => "",
    };

    public string WhenText => Entry.WhenUtc.ToLocalTime().ToString("HH:mm:ss");

    public string Arrow => TransferWording.Direction(Entry.Sent, Entry.PeerDriven, Live);

    public string Peer => TransferWording.User(Entry.Peer);

    public string ContentsText
    {
        get
        {
            if (IsQueued) return "";
            var files = $"{Entry.Files:N0} file{(Entry.Files == 1 ? "" : "s")}";
            return Entry.Folders <= 1
                ? files
                : $"{files}, {Entry.Folders:N0} folders";
        }
    }

    /// <summary>"12.4 GB of 31.6 GB" while it runs and the total is known; just the size after.</summary>
    public string SizeText => IsQueued ? "" : Live && Total > 0
        ? $"{Format.Bytes(Entry.Bytes)} of {Format.Bytes(Total)}"
        : Format.Bytes(Entry.Bytes);

    public string TookText => IsQueued ? "" : Entry.Seconds >= 60
        ? $"{(int)(Entry.Seconds / 60)}m {(int)(Entry.Seconds % 60)}s"
        : $"{Entry.Seconds:0.0}s";

    // Progress over the last WindowSeconds, for the time-left estimate. The live rate readout
    // swings with every chunk boundary; an estimate built on it jumps by minutes each tick.
    // Twenty seconds of real progress is steady enough to read and still follows a real
    // change in speed (a second transfer starting, a slow disk) within a few seconds.
    private const double WindowSeconds = 20;
    private readonly Queue<(double Seconds, long Bytes)> _samples = new();

    /// <summary>Records where the transfer is now, for <see cref="RemainingText"/>.</summary>
    public void Sample(double seconds, long bytes)
    {
        _samples.Enqueue((seconds, bytes));
        while (_samples.Count > 2 && seconds - _samples.Peek().Seconds > WindowSeconds)
            _samples.Dequeue();
    }

    /// <summary>Estimated seconds left, or null while there is not enough to go on.</summary>
    public double? SecondsLeft
    {
        get
        {
            if (!Live || Total <= 0 || _samples.Count < 2) return null;
            var left = Total - Entry.Bytes;
            if (left <= 0) return 0;

            var (firstAt, firstBytes) = _samples.Peek();
            var span = Entry.Seconds - firstAt;
            var moved = Entry.Bytes - firstBytes;
            if (span < 3 || moved <= 0) return null;   // just started, or stalled

            return left / (moved / span);
        }
    }

    public string RemainingText
    {
        get
        {
            if (!Live || Checking) return "";
            if (SecondsLeft is not { } s) return Total > 0 ? "Estimating…" : "";
            return Format.Duration(s);
        }
    }

    public string RateText =>
        Live
            ? (LiveRate >= 1 ? Format.Rate(LiveRate) : "")
            : Entry.Seconds <= 0 ? "" : Format.Rate(Entry.Bytes / Entry.Seconds);

    public string ResultText =>
        Preparing is not null ? PreparingDetail ?? Preparing
        : IsQueued ? "Waiting in the queue"
        : Live ? "In progress…"
        : string.IsNullOrWhiteSpace(Entry.Problem) ? "Completed"
        : Entry.Problem!;

    /// <summary>
    /// The disks this transfer moved between, "SSD → HDD". A blank string when neither end is
    /// known (so the column simply stays empty rather than showing "? → ?"); a single unknown
    /// end shows as "?" so the known one is still useful.
    /// </summary>
    public string DrivesText
    {
        get
        {
            var source = Entry.SourceDrive;
            var dest = Entry.DestDrive;
            if (string.IsNullOrEmpty(source) && string.IsNullOrEmpty(dest)) return "";
            return $"{(string.IsNullOrEmpty(source) ? "?" : source)} → {(string.IsNullOrEmpty(dest) ? "?" : dest)}";
        }
    }

    /// <summary>Everything is derived, so one blanket notification is the honest thing to send.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One row on the History tab.</summary>
public sealed class HistoryRow
{
    public required HistoryEntry Entry { get; init; }

    public string WhenText => Entry.WhenUtc.ToLocalTime().ToString("d MMM yyyy HH:mm");
    public string Arrow => TransferWording.Direction(Entry.Sent, Entry.PeerDriven, live: false);
    public string Peer => TransferWording.User(Entry.Peer);
    public string SizeText => Format.Bytes(Entry.Bytes);

    public string ContentsText
    {
        get
        {
            var files = $"{Entry.Files:N0} file{(Entry.Files == 1 ? "" : "s")}";
            return Entry.Folders == 0
                ? files
                : $"{files}, {Entry.Folders:N0} folder{(Entry.Folders == 1 ? "" : "s")}";
        }
    }

    public string TookText => Entry.Seconds >= 60
        ? $"{(int)(Entry.Seconds / 60)}m {(int)(Entry.Seconds % 60)}s"
        : $"{Entry.Seconds:0.0}s";

    /// <summary>Average over the whole transfer, which is the honest figure to record.</summary>
    public string RateText => Entry.Seconds <= 0 ? "" : Format.Rate(Entry.Bytes / Entry.Seconds);

    public string ResultText => string.IsNullOrWhiteSpace(Entry.Problem) ? "Completed" : Entry.Problem!;

    /// <summary>True when the file list was kept, so this row is worth opening.</summary>
    public bool HasDetails => Entry.DetailId is not null ||
                              Entry.FileNames is { Count: > 0 } || Entry.FolderNames is { Count: > 0 };

    /// <summary>The folders-then-files listing shown when the row is opened, same as a batch.</summary>
    public IReadOnlyList<string> Details
    {
        get
        {
            var lines = new List<string>();
            var (folders, files) = HistoryDetails.Load(Entry);

            if (folders.Count > 0)
            {
                lines.Add($"Folders ({folders.Count:N0})");
                lines.AddRange(folders.Order(StringComparer.OrdinalIgnoreCase).Select(f => "    " + f));
                lines.Add("");
            }

            lines.Add($"Files ({files.Count:N0})");
            lines.AddRange(files.Order(StringComparer.OrdinalIgnoreCase).Select(f => "    " + f));
            return lines;
        }
    }
}

/// <summary>One shared folder in the left sidebar.</summary>
public sealed class ShareLink
{
    public required string Name { get; init; }
    public required string RemotePath { get; init; }
}

/// <summary>
/// One row in the Browse tab's sidebar: either a connection's root, or one of its shared
/// folders indented beneath it.
///
/// Both kinds live in one list so connections stack naturally - root, its folders, next root -
/// rather than being split across two controls where the same names appeared twice and it was
/// never obvious which folders belonged to whom.
/// </summary>
public sealed class SidebarRow
{
    public required Connection Owner { get; init; }

    /// <summary>Null for the connection's own row; the share name for a folder row.</summary>
    public string? Share { get; init; }

    public bool IsRoot => Share is null;

    public string RemotePath => Share ?? "";

    /// <summary>"Patrick (4 folders)" for a root, the folder name otherwise.</summary>
    public required string Label { get; init; }

    public string Glyph => IsRoot ? Owner.Glyph : "\uE8B7";
    public string GlyphBrush => IsRoot ? "#0078D4" : "#DCA612";
    public string Weight => IsRoot ? "SemiBold" : "Normal";

    /// <summary>The X that closes a connection shows on its own row, not on its folders.</summary>
    public string CloseVisibility => IsRoot ? "Visible" : "Collapsed";

    /// <summary>Folders sit in from their connection, which is what makes the grouping read.</summary>
    public string Indent => IsRoot ? "0,6,0,0" : "18,0,0,0";
}
