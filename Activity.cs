namespace FastShare;

public enum TransferDirection { Download, Upload }

/// <summary>
/// Which list a line belongs in. The Session tab shows one or the other, because "moved 412
/// files" and "Dillon connected" answer different questions and reading them interleaved
/// means finding neither.
/// </summary>
public enum LogKind { Transfer, Network }

/// <summary>One line in the Activity tab.</summary>
public sealed record LogEntry(
    DateTime At,
    TransferDirection Direction,
    string Text,
    long Size,
    double BytesPerSecond,
    bool IsError = false,
    LogKind Kind = LogKind.Transfer,
    IReadOnlyList<string>? Details = null)
{
    /// <summary>True when there is a full file list worth opening in a popup.</summary>
    public bool HasDetails => Details is { Count: > 0 };

    public string TimeText => At.ToString("HH:mm:ss");
    public string Arrow => IsError ? "!" : Direction == TransferDirection.Download ? "↓" : "↑";
    public string SizeText => Size > 0 ? Format.Bytes(Size) : "";
    public string RateText => BytesPerSecond >= 1 ? Format.Rate(BytesPerSecond) : "";
}

/// <summary>
/// Running log and counters for the session.
///
/// Transfer workers call Append from whatever thread they happen to be on; the UI subscribes
/// and marshals to the dispatcher itself. Static because there is exactly one window and one
/// log, and threading a logger through every layer would be noise.
/// </summary>
public static class ActivityLog
{
    public static event Action<LogEntry>? Appended;

    /// <summary>
    /// When the session clock started, or null while it has not. Sharing is what starts it:
    /// a window sitting open all afternoon doing nothing is not a session, and counting it
    /// made the figure meaningless.
    /// </summary>
    public static DateTime? StartedUtc { get; private set; }

    public static void StartSession() => StartedUtc ??= DateTime.UtcNow;

    private static long _downloadedBytes;
    private static long _uploadedBytes;
    private static int _downloadedFiles;
    private static int _uploadedFiles;
    private static int _errors;

    // Distinct folders touched, kept as sets because the same folder is hit once per file.
    private static readonly HashSet<string> DownloadFolders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UploadFolders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock FolderGate = new();

    public static long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);
    public static long UploadedBytes => Interlocked.Read(ref _uploadedBytes);
    public static int DownloadedFiles => Volatile.Read(ref _downloadedFiles);
    public static int UploadedFiles => Volatile.Read(ref _uploadedFiles);
    public static int Errors => Volatile.Read(ref _errors);

    public static int DownloadedFolders { get { lock (FolderGate) return DownloadFolders.Count; } }
    public static int UploadedFolders { get { lock (FolderGate) return UploadFolders.Count; } }

    /// <summary>
    /// Records a folder a transfer wrote into. Counted separately from files because "412
    /// files" and "412 files across 9 folders" say quite different things about a transfer.
    /// </summary>
    public static void Folder(TransferDirection direction, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (FolderGate)
        {
            (direction == TransferDirection.Download ? DownloadFolders : UploadFolders).Add(path);
        }
    }

    /// <summary>
    /// Raised for every completed file, with who it went to or came from. The window listens
    /// and keeps the all-time top ten; putting it here rather than in the transfer engines
    /// means every path is covered, including files the server hands out.
    /// </summary>
    public static event Action<TransferDirection, string, long, string?>? FileCompleted;

    public static void File(TransferDirection direction, string name, long size, double rate,
        string? peer = null)
    {
        if (direction == TransferDirection.Download)
        {
            Interlocked.Add(ref _downloadedBytes, size);
            Interlocked.Increment(ref _downloadedFiles);
        }
        else
        {
            Interlocked.Add(ref _uploadedBytes, size);
            Interlocked.Increment(ref _uploadedFiles);
        }

        // Track the record here rather than in the UI, so it counts every transfer path.
        long seen;
        while (size > (seen = Interlocked.Read(ref _largestFile)))
        {
            if (Interlocked.CompareExchange(ref _largestFile, size, seen) == seen) break;
        }

        // Deliberately does NOT write a log line. The Session tab shows one row per transfer,
        // the way the History tab does; a line per file turned a folder of ten thousand small
        // files into ten thousand rows nobody could read or scroll.
        FileCompleted?.Invoke(direction, name, size, peer);
    }

    /// <summary>Biggest single file moved this run, for the all-time record.</summary>
    public static long LargestFileBytes => Interlocked.Read(ref _largestFile);
    private static long _largestFile;

    public static void Note(TransferDirection direction, string text,
        LogKind kind = LogKind.Transfer) =>
        Appended?.Invoke(new LogEntry(DateTime.Now, direction, text, 0, 0, false, kind));

    /// <summary>
    /// One line standing in for a whole batch, with the full list kept behind it. Ten thousand
    /// separate lines is not a log anyone reads; "Dillon → you · 412 files, 9 folders" is.
    /// </summary>
    public static void Batch(TransferDirection direction, string text, long size,
        double rate, IReadOnlyList<string> details) =>
        Appended?.Invoke(new LogEntry(DateTime.Now, direction, text, size, rate, false,
            LogKind.Transfer, details));

    public static void Error(string text)
    {
        Interlocked.Increment(ref _errors);
        Appended?.Invoke(new LogEntry(DateTime.Now, TransferDirection.Download, text, 0, 0, IsError: true));
    }
}

/// <summary>
/// Converts a byte counter into a readable rate.
///
/// Kept separate from the counters because a rate is a property of an observer sampling over
/// time, not of the transfer itself. Light exponential smoothing, because the raw per-tick
/// figure jitters far too much to read.
/// </summary>
public sealed class RateMeter
{
    private long _lastBytes;
    private DateTime _lastTick = DateTime.UtcNow;
    private double _rate;

    public double BytesPerSecond => _rate;

    public void Reset()
    {
        _lastBytes = 0;
        _lastTick = DateTime.UtcNow;
        _rate = 0;
    }

    public void Sample(long totalBytes)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastTick).TotalSeconds;
        if (elapsed < 0.05) return;

        var instant = (totalBytes - _lastBytes) / elapsed;
        if (instant < 0) instant = 0;

        _rate = _rate <= 0 ? instant : _rate * 0.6 + instant * 0.4;
        _lastBytes = totalBytes;
        _lastTick = now;
    }
}
