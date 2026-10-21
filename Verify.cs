using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace FastShare;

/// <summary>
/// Optional check after a transfer (Settings → "Check files after every transfer"): both PCs
/// compute SHA-256 of every file that just moved and compare. Anything that differs is reported
/// by name. Off by default - it reads every file again on both ends, which on a big transfer is
/// a minute or more - but it is the only proof that what arrived is exactly what was sent.
/// </summary>
public static class Verify
{
    /// <summary>Files per request: keeps each /api/hash call short enough to never look stalled.</summary>
    private const int Batch = 200;

    /// <summary>
    /// Files read at once on one drive. Measured on the maintainer's PC (cold reads, 3,000 mixed
    /// files + 8 large, 2026-10-06): NVMe 1,660 MB/s at 4, 1,800 at 8, nothing more past 8;
    /// SATA SSD flat at 440 MB/s from 2 up; a hard drive 27/38/46 MB/s at 1/4/8 on small files but
    /// 160/133/128 on big ones, so it stays at 4 (the old figure) rather than trade one for the other. A PC without the CPU's
    /// SHA instructions (Dillon's i7-10700K) is held back by the hashing itself, which more
    /// readers spread over more cores.
    /// </summary>
    public static int ReadersFor(DriveKind kind) => kind switch
    {
        DriveKind.Nvme => 8,
        DriveKind.Ssd => 4,
        DriveKind.Hdd => 4,
        _ => 4,
    };

    /// <summary>The most any drive gets - the upper bound for the loops that feed the gates.</summary>
    public const int MaxReaders = 8;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One gate per drive, shared by everything that hashes on this PC (our own side of a check
    /// and the far side's /api/hash requests), so overlapping requests keep the disk busy
    /// without ever reading more files at once than suits it.
    /// </summary>
    private static SemaphoreSlim GateFor(string path)
    {
        string root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; }
        catch { root = ""; }
        return Gates.GetOrAdd(root, r => new SemaphoreSlim(ReadersFor(DriveDetect.ForPath(r.Length > 0 ? r : null))));
    }

    /// <summary><see cref="HashFileAsync"/>, waiting its turn on the file's drive.</summary>
    public static async Task<string> HashGatedAsync(string path, CancellationToken ct, Action<long>? read = null)
    {
        var gate = GateFor(path);
        await gate.WaitAsync(ct);
        try { return await HashFileAsync(path, ct, read); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// How far a check has got. <paramref name="BytesRead"/> of <paramref name="Bytes"/> is our
    /// own side's reading, which moves smoothly even on one big file; <paramref name="FilesChecked"/>
    /// counts files both PCs have finished. <paramref name="WaitingForOther"/>: we are done and
    /// the other PC is still reading.
    /// </summary>
    public sealed record CheckProgress(int FilesChecked, int Files, long BytesRead, long Bytes, bool WaitingForOther)
    {
        /// <summary>0-100. Held under 100 until both PCs have finished every file.</summary>
        public double Percent
        {
            get
            {
                if (FilesChecked >= Files) return 100;
                var part = Bytes > 0 ? BytesRead * 100.0 / Bytes : FilesChecked * 100.0 / Math.Max(1, Files);
                return Math.Clamp(part, 0, 99);
            }
        }
    }

    /// <summary>
    /// Compares local files with the far side's copies. Returns one line per file that differs
    /// or could not be checked; empty means every file matched.
    /// </summary>
    public static async Task<List<string>> CompareAsync(ApiClient api,
        IReadOnlyList<(string Remote, string Local)> files, Action<CheckProgress>? progress, CancellationToken ct)
    {
        // Each side works through everything at its own pace. This used to go a batch at a time
        // in lockstep - neither PC started the next 200 until both had finished these - so the
        // faster one sat idle whenever the other hit a big file or a busy disk, plus a round trip
        // per batch. On Dillon's 125 GB that ran at about 650 MB/s, under half what the disks do.
        var local = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var theirs = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int localDone = 0, remoteDone = 0;
        long bytesRead = 0;
        var bytes = 0L;
        foreach (var (_, path) in files)
        {
            try { bytes += new FileInfo(path).Length; } catch { /* unreadable: reported below */ }
        }

        void Report()
        {
            if (progress is null) return;
            var mine = Volatile.Read(ref localDone);
            var theirs = Volatile.Read(ref remoteDone);
            progress(new CheckProgress(Math.Min(mine, theirs), files.Count, Interlocked.Read(ref bytesRead), bytes,
                mine >= files.Count && theirs < files.Count));
        }
        void Read(long n)
        {
            Interlocked.Add(ref bytesRead, n);
            Report();
        }
        Report();

        // Two requests in flight when the host gates its own readers (it then never reads more
        // at once than its disk likes); one for an older host, which reads four per request.
        var inFlight = (await api.EnsureCapsAsync(ct)).Contains(Caps.HashGate) ? 2 : 1;
        var remote = Parallel.ForEachAsync(files.Chunk(Batch), new ParallelOptions { MaxDegreeOfParallelism = inFlight, CancellationToken = ct },
            async (chunk, token) =>
            {
                foreach (var (path, hash) in await api.HashAsync(chunk.Select(f => f.Remote).ToList(), token))
                    theirs[path] = hash;
                Interlocked.Add(ref remoteDone, chunk.Length);
                Report();
            });

        var ours = Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = MaxReaders, CancellationToken = ct },
            async (file, token) =>
            {
                local[file.Remote] = await HashGatedAsync(file.Local, token, progress is null ? null : Read);
                Interlocked.Increment(ref localDone);
                Report();
            });

        await Task.WhenAll(remote, ours);
        Report();

        var problems = new List<string>();
        foreach (var (path, _) in files)
        {
            var name = Path.GetFileName(path);
            var mine = local.GetValueOrDefault(path, "");
            var other = theirs.GetValueOrDefault(path, "");
            if (mine.Length == 0) problems.Add($"{name}: could not be read here to check it");
            else if (other.Length == 0) problems.Add($"{name}: the other PC could not check it");
            else if (!string.Equals(mine, other, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{name}: contents differ from the other PC's copy");
        }

        return problems;
    }

    /// <summary>
    /// SHA-256 as lower-case hex, or "" if the file cannot be read. With <paramref name="read"/>,
    /// says how many bytes it has read as it goes, so a check of one big file can show progress.
    /// </summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken ct, Action<long>? read = null)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (read is null) return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                int n;
                while ((n = await stream.ReadAsync(buffer.AsMemory(0, 1 << 20), ct)) > 0)
                {
                    sha.AppendData(buffer, 0, n);
                    read(n);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            return Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch { return ""; }
    }
}
