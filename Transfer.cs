using System.IO;

namespace FastShare;

/// <summary>
/// One file to move: where it is now, where it goes, how big it is, and when it was last
/// written at the source (Unix ms, 0 if unknown) so the copy can keep the same date.
/// </summary>
public sealed record PlanItem(string Remote, string Local, long Size, long ModifiedUnixMs = 0);

/// <summary>
/// Where the copy loops spend their time: waiting for the network, or for the disk.
///
/// Every loop that moves file bytes alternates between the two - read the socket, write the
/// file (receiving) or read the file, write the socket (sending) - so summing the time each
/// await takes says which one the transfer is actually waiting on. Receiving mostly on the
/// network means the line (or the far side) is the limit; mostly on the disk means this PC is.
/// Read and reset by dev mode's stats line.
/// </summary>
public static class WaitTimer
{
    private static long _recvNet, _recvDisk, _sendDisk, _sendNet;

    public static long Now => System.Diagnostics.Stopwatch.GetTimestamp();

    public static void RecvNet(long since) => Interlocked.Add(ref _recvNet, Now - since);
    public static void RecvDisk(long since) => Interlocked.Add(ref _recvDisk, Now - since);
    public static void SendDisk(long since) => Interlocked.Add(ref _sendDisk, Now - since);
    public static void SendNet(long since) => Interlocked.Add(ref _sendNet, Now - since);

    /// <summary>Seconds of waiting since the last call, summed over every stream, then zeroed.</summary>
    public static (double RecvNet, double RecvDisk, double SendDisk, double SendNet) Take()
    {
        static double S(ref long t) => Interlocked.Exchange(ref t, 0) / (double)System.Diagnostics.Stopwatch.Frequency;
        return (S(ref _recvNet), S(ref _recvDisk), S(ref _sendDisk), S(ref _sendNet));
    }
}

/// <summary>
/// Naming for in-progress files, shared by both directions.
///
/// Writing straight to the destination truncates it the instant the transfer starts. That
/// corrupts a file being copied onto itself and destroys a good copy if the transfer fails
/// halfway. Landing on a temp name and moving into place only on success avoids both.
/// </summary>
public static class PartFiles
{
    public const string Suffix = ".fspart";

    /// <summary>Beside a large part file: which chunks are already on disk, for resuming.</summary>
    public const string NoteSuffix = ".fspart.json";

    /// <summary>
    /// The temp name for a destination. NTFS caps a single name at 255 characters, so a long
    /// name plus the suffix would be impossible to create at all; those fall back to a short
    /// name derived from the full one, still in the same folder so the final move stays a
    /// rename.
    /// </summary>
    public static string PathFor(string destination)
    {
        var name = Path.GetFileName(destination);
        if (name.Length + NoteSuffix.Length <= 255) return destination + Suffix;

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(destination.ToLowerInvariant())))[..24];
        return Path.Combine(Path.GetDirectoryName(destination) ?? "", "~" + hash + Suffix);
    }

    /// <summary>
    /// The same, on a handle that is still open for writing. Setting it by path means opening
    /// the file a second time, which for 100,000 small files is 100,000 extra opens - and each
    /// one can be scanned again by Defender on close.
    /// </summary>
    public static void StampModified(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long modifiedUnixMs)
    {
        if (modifiedUnixMs <= 0) return;
        try { File.SetLastWriteTimeUtc(handle, DateTimeOffset.FromUnixTimeMilliseconds(modifiedUnixMs).UtcDateTime); }
        catch { }
    }

    /// <summary>Stamps a finished file with its source's modified time. Best effort.</summary>
    public static void StampModified(string path, long modifiedUnixMs)
    {
        if (modifiedUnixMs <= 0) return;
        try { File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(modifiedUnixMs).UtcDateTime); }
        catch { /* a date is a nicety; never fail a finished transfer over it */ }
    }

    /// <summary>
    /// Whether a file already on disk is the same one being sent: same size, and modified
    /// within two seconds (FAT and some network shares round times to two seconds).
    /// </summary>
    public static bool LooksIdentical(long localSize, DateTime localModifiedUtc, long size, long modifiedUnixMs) =>
        modifiedUnixMs > 0 && localSize == size &&
        Math.Abs((localModifiedUtc - DateTimeOffset.FromUnixTimeMilliseconds(modifiedUnixMs).UtcDateTime)
            .TotalSeconds) <= 2;
}

/// <summary>
/// Shared progress accounting for both directions.
///
/// Work is retried in self-contained units - a whole bundle or a whole chunk - so a retry
/// re-sends bytes the failed attempt already counted. Each attempt therefore tracks its own
/// delta and rolls back exactly that on failure. Restoring a snapshot of the global counter
/// instead would clobber the seven other workers running concurrently.
/// </summary>
public sealed class TransferStats
{
    private long _bytesDone;
    private int _filesDone;
    private readonly List<string> _errors = [];
    private readonly Lock _errorLock = new();

    private long _totalBytes;
    public long TotalBytes { get => Interlocked.Read(ref _totalBytes); set => Interlocked.Exchange(ref _totalBytes, value); }
    public int TotalFiles { get; set; }

    /// <summary>A resumed file only has to move what it is missing, so the total shrinks by the rest.</summary>
    public void ReduceTotalBytes(long by) => Interlocked.Add(ref _totalBytes, -by);

    public long BytesDone => Interlocked.Read(ref _bytesDone);
    public int FilesDone => Volatile.Read(ref _filesDone);

    /// <summary>
    /// The check after the transfer (Check files), while it runs: null before and after. Shown
    /// as its own fresh bar - every byte has moved by then, so the transfer's own bar sits at
    /// 100% and said nothing about the minutes the check can take.
    /// </summary>
    public Verify.CheckProgress? Check { get => _check; set => _check = value; }
    private volatile Verify.CheckProgress? _check;

    public IReadOnlyList<string> Errors { get { lock (_errorLock) return _errors.ToList(); } }

    public void Fail(string what, Exception ex)
    {
        lock (_errorLock)
        {
            if (_errors.Count < 200) _errors.Add($"{what}: {FailureText.Of(ex)}");
        }
    }

    public Attempt NewAttempt() => new(this);

    public sealed class Attempt(TransferStats owner)
    {
        private long _bytes;
        private int _files;

        public void AddBytes(int count)
        {
            _bytes += count;
            Interlocked.Add(ref owner._bytesDone, count);
        }

        public void AddFile()
        {
            _files++;
            Interlocked.Increment(ref owner._filesDone);
        }

        /// <summary>Takes back one file this attempt counted, e.g. one the far side refused.</summary>
        public void RemoveFile(long bytes)
        {
            _bytes -= bytes;
            _files--;
            Interlocked.Add(ref owner._bytesDone, -bytes);
            Interlocked.Decrement(ref owner._filesDone);
        }

        public void Rollback()
        {
            if (_bytes != 0) Interlocked.Add(ref owner._bytesDone, -_bytes);
            if (_files != 0) Interlocked.Add(ref owner._filesDone, -_files);
            _bytes = 0;
            _files = 0;
        }
    }

    /// <summary>
    /// Retries transient network faults. Bundles and chunks are idempotent - replaying one
    /// rewrites the same bytes - so the only thing to undo is the progress already reported.
    /// </summary>
    public static Task WithRetry(TransferStats stats, Func<Attempt, Task> action,
        int attempts, CancellationToken ct) =>
        WithRetry(stats.NewAttempt(), action, attempts, ct);

    /// <summary>
    /// The same, with an attempt the caller holds on to - so if the work fails for good, the
    /// caller can take back what the last try had counted.
    /// </summary>
    public static async Task WithRetry(Attempt attempt, Func<Attempt, Task> action,
        int attempts, CancellationToken ct)
    {
        for (var n = 1; ; n++)
        {
            try
            {
                await action(attempt);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (n < attempts)
            {
                attempt.Rollback();
                await Task.Delay(TimeSpan.FromMilliseconds(250 * n), ct);
            }
        }
    }
}

public static class Batching
{
    /// <summary>
    /// Packs small files into bundles. A bundle closes at whichever limit comes first: big
    /// enough to amortise the round trip that dominates small-file transfer, small enough
    /// that progress stays smooth and a failed retry is cheap to redo.
    /// </summary>
    public static List<List<T>> IntoBundles<T>(List<T> files, Func<T, long> sizeOf)
    {
        var bundles = new List<List<T>>();
        var current = new List<T>();
        long bytes = 0;

        foreach (var file in files)
        {
            current.Add(file);
            bytes += sizeOf(file);

            if (bytes >= Tuning.BundleTargetBytes || current.Count >= Tuning.BundleMaxFiles)
            {
                bundles.Add(current);
                current = [];
                bytes = 0;
            }
        }
        if (current.Count > 0) bundles.Add(current);
        return bundles;
    }
}

/// <summary>Tuning shared by both directions.</summary>
public static class Tuning
{
    /// <summary>
    /// Parallel TCP connections. Measured on this exact path (NC to NY): 554 Mbit/s on one
    /// stream versus 909 Mbit/s across eight, because a single stream keeps colliding with
    /// packet loss and halving its congestion window. Eight is where the curve flattened.
    /// </summary>
    public const int Streams = 8;

    /// <summary>At or above this, a file is chunked across streams; below it, bundled.</summary>
    public const long LargeFileThreshold = 4L * 1024 * 1024;

    public const long ChunkSize = 8L * 1024 * 1024;
    public const long BundleTargetBytes = 8L * 1024 * 1024;
    public const int BundleMaxFiles = 256;
    public const int Attempts = 3;
}

/// <summary>The receiving disk cannot hold the transfer; said before anything moves.</summary>
public sealed class NotEnoughSpaceException(string message) : IOException(message);

/// <summary>
/// A resumed transfer that did not fit ends quietly: someone chose Discard (forgotten), or the
/// other PC could not be reached for an answer (<paramref name="keep"/>: offered again next time).
/// </summary>
public sealed class TransferDiscardedException(string message, bool keep = false) : Exception(message)
{
    public bool Keep { get; } = keep;
}

/// <summary>
/// Free bytes on the disk holding <paramref name="path"/>. FASTSHARE_TEST_FREE_FILE names a file
/// holding a number to report instead - for testing "not enough space" on a PC that has plenty.
/// </summary>
public static class DiskSpace
{
    public static long Free(string path)
    {
        if (Environment.GetEnvironmentVariable("FASTSHARE_TEST_FREE_FILE") is { Length: > 0 } fake &&
            long.TryParse(File.Exists(fake) ? File.ReadAllText(fake).Trim() : "", out var bytes))
            return bytes;
        return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
    }
}

/// <summary>
/// Why a file failed, in words that say what to do about it. The raw messages ("The process
/// cannot access the file because it is being used by another process", "Response status code
/// does not indicate success: 423") were accurate but told nobody that Blender had it open.
/// </summary>
public static class FailureText
{
    public const string InUseThere = "In use by a program on the other PC (e.g. open in Blender) - close it there, then Retry failed files";

    public static string Of(Exception ex)
    {
        switch (ex)
        {
            case System.Net.Http.HttpRequestException { StatusCode: { } status }:
                return (int)status switch
                {
                    423 => InUseThere,
                    403 => "Not allowed by the other PC's permissions",
                    404 => "No longer on the other PC (moved or deleted)",
                    409 => "Cancelled by the other PC",
                    507 => "The other PC's disk is full",
                    >= 500 => "The other PC could not read it (in use, or no permission there)",
                    _ => ex.Message,
                };
            case UnauthorizedAccessException:
                return "Windows refused access on this PC (permissions, or the file is read-only)";
            case PathTooLongException:
                return "The path is too long for Windows";
            case IOException io:
                return (io.HResult & 0xFFFF) switch
                {
                    32 or 33 => "In use by a program on this PC (e.g. open in Blender) - close it, then Retry failed files",
                    39 or 112 => "This PC's disk is full",
                    5 => "Windows refused access on this PC (permissions)",
                    _ => io.Message,
                };
            default:
                return ex.Message;
        }
    }
}
