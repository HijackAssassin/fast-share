using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace FastShare;

/// <summary>
/// Pulls files from the far side as fast as the link allows.
///
/// Two bottlenecks, two strategies:
///
///  - Large files are bandwidth-limited. One TCP stream cannot fill the link, so each file
///    is cut into chunks and pulled over eight sockets via HTTP Range requests, written
///    straight to their final offsets in a preallocated file.
///
///  - Small files are latency-limited. Bandwidth is irrelevant: at ~20 ms round trip, one
///    request per file caps you near 50 files/sec however fast the link is. So small files
///    are grouped and streamed back to back down one connection, eight such streams at once.
///    10,000 files becomes ~40 round trips instead of 10,000.
///
/// Everything lands on a temp name (<see cref="PartFiles"/>) and is moved into place only
/// once complete, then stamped with the source's modified time.
/// </summary>
public sealed class Downloader(IFileSource api)
{
    public TransferStats Stats { get; } = new();

    /// <summary>Lets finished-file log lines carry the rate the UI is currently showing.</summary>
    public Func<double>? CurrentRate { get; set; }

    /// <summary>
    /// Every destination that now holds a finished file, in the order they landed.
    ///
    /// Cancelling is meant to leave the disk as it was, so the caller needs to know what
    /// actually got written - the part files clean themselves up, but a file that completed
    /// before you pressed cancel is a real file sitting in a folder you did not want it in.
    /// </summary>
    public IReadOnlyCollection<string> Written => _written;
    private readonly ConcurrentBag<string> _written = [];

    /// <summary>Bytes a resumed large file did not have to fetch again, for the summary.</summary>
    public long ResumedBytes => Interlocked.Read(ref _resumedBytes);
    private long _resumedBytes;

    /// <summary>How long a connection may go silent before it is treated as dead.</summary>
    public TimeSpan StallLimit { get; init; } = Watchdog.DefaultIdle;

    public async Task RunAsync(IReadOnlyList<PlanItem> items, CancellationToken ct,
        IReadOnlyCollection<string>? emptyFolders = null)
    {
        Stats.TotalFiles = items.Count;
        Stats.TotalBytes = items.Sum(i => i.Size);

        // Create every destination folder in one pass; doing it per file would add a
        // filesystem round trip each time, which matters at thousands of files. Empty source
        // folders are recreated too, so a folder tree arrives with its shape intact.
        foreach (var directory in items.Select(i => Path.GetDirectoryName(i.Local))
                                       .Concat(emptyFolders ?? [])
                                       .Where(d => !string.IsNullOrEmpty(d))
                                       .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(directory!);
        }

        var large = items.Where(i => i.Size >= Tuning.LargeFileThreshold).ToList();
        var small = items.Where(i => i.Size < Tuning.LargeFileThreshold).ToList();

        if (small.Count > 0)
        {
            await Parallel.ForEachAsync(Batching.IntoBundles(small, i => i.Size),
                new ParallelOptions { MaxDegreeOfParallelism = Tuning.Streams, CancellationToken = ct },
                async (group, token) => await DownloadGroupAsync(group, token));
        }

        // Large files split two ways, because "large" spans a huge range and the two ends
        // want opposite handling:
        //
        //  - A genuinely big file (enough chunks to fill all eight sockets on its own) is best
        //    done alone: it already saturates the link, and doing one at a time keeps its
        //    writes sequential on disk.
        //  - A medium file - bigger than the small-file threshold but only a chunk or two -
        //    cannot fill eight sockets by itself. Done one at a time these ran effectively
        //    single-stream, which is exactly the "why is it only 100 Mbit" case: a folder of
        //    20 MB files crawled. So several run at once, sharing one budget of eight sockets
        //    (the shared semaphore below), which fills the link without ever opening more than
        //    eight range requests in total.
        var fill = Tuning.Streams * Tuning.ChunkSize;   // what one file needs to use every socket
        var huge = large.Where(i => i.Size >= fill).ToList();
        var medium = large.Where(i => i.Size < fill).ToList();

        if (medium.Count > 0)
        {
            await Parallel.ForEachAsync(medium,
                new ParallelOptions { MaxDegreeOfParallelism = Tuning.Streams, CancellationToken = ct },
                async (item, token) => await DownloadLargeGuardedAsync(item, token));
        }

        foreach (var item in huge)
        {
            ct.ThrowIfCancellationRequested();
            await DownloadLargeGuardedAsync(item, ct);
        }
    }

    /// <summary>
    /// The one budget of parallel range requests, shared across every large file in flight.
    /// Eight sockets is the whole point of the design; this is what keeps that true whether the
    /// eight are carrying one big file's eight chunks or eight medium files' one chunk each.
    /// </summary>
    private readonly SemaphoreSlim _streamBudget = new(Tuning.Streams);

    /// <summary>Asks the other PC for the first byte of a file a batch left out, to learn why.</summary>
    private async Task<string> WhyMissingAsync(PlanItem item, CancellationToken ct)
    {
        const string unknown = "Could not be read on the other PC (in use by a program there, moved, or no permission)";
        if (item.Size <= 0) return unknown;
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(10));
            await using var stream = await api.OpenRangeAsync(item.Remote, 0, 1, item.Size, probe.Token);
            return "Changed on the other PC while it was being sent - Retry failed files";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var text = FailureText.Of(ex);
            return text == ex.Message ? unknown : text;
        }
    }

    private void FailFile(string what, string reason)
    {
        Stats.Fail(what, new IOException(reason));
        ActivityLog.Error($"{what}: {reason}");
    }

    // ---- small files ---------------------------------------------------------------------

    /// <summary>
    /// One bundle's worth of small files, retried file-by-file rather than all-or-nothing.
    ///
    /// Before, one file that vanished or was locked failed the whole bundle of up to 256, and
    /// each retry re-downloaded every file that had already landed. Now a retry asks only for
    /// what is still outstanding, a file that cannot be written here fails on its own, and a
    /// file the far side no longer has is reported rather than quietly counted as done.
    /// </summary>
    private async Task DownloadGroupAsync(List<PlanItem> group, CancellationToken ct)
    {
        var remaining = new Dictionary<string, PlanItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in group) remaining[item.Remote] = item;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadBundleOnceAsync(remaining, ct);

                // The far side reached its end marker without sending these. It skips files
                // that vanished or became unreadable since the manifest was taken.
                // A batch cannot say why it left a file out, so ask about each one directly:
                // one byte of it, which the other PC answers with the reason (in use, gone, not
                // allowed). Capped, so a folder that vanished wholesale costs a few requests.
                var asked = 0;
                foreach (var missing in remaining.Values)
                {
                    var reason = asked++ < 50 ? await WhyMissingAsync(missing, ct)
                        : "Could not be read on the other PC (in use by a program there, moved, or no permission)";
                    FailFile(missing.Remote, reason);
                }
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < Tuning.Attempts && remaining.Count > 0)
            {
                Diagnostics.Log("transfer", $"bundle retry {attempt} ({remaining.Count} file(s) left)", ex);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
            }
            catch (Exception ex)
            {
                foreach (var item in remaining.Values)
                    Stats.Fail(item.Remote, ex);
                ActivityLog.Error($"{remaining.Count} small file(s) failed: {ex.Message}");
                return;
            }
        }
    }

    private async Task DownloadBundleOnceAsync(Dictionary<string, PlanItem> remaining, CancellationToken ct)
    {
        // Map by remote path so a frame can only land at a destination we chose. The far side
        // never gets to influence where a file is written.
        await using var stream = new StallGuardStream(
            await OpenWatchedAsync(token => api.OpenBundleAsync(remaining.Keys.ToList(), token), ct),
            StallLimit);

        var scratch = ArrayPool<byte>.Shared.Rent(BundleFormat.MaxPathBytes + 16);
        var buffer = ArrayPool<byte>.Shared.Rent(BundleIo.BufferSize);
        try
        {
            while (true)
            {
                var frame = await BundleIo.ReadHeaderAsync(stream, scratch, ct);
                if (frame is null) break;
                var (remote, length, _) = frame.Value;

                if (!remaining.TryGetValue(remote, out var item))
                {
                    // Not something we asked for. Skip its bytes so framing stays aligned
                    // rather than writing to a path we never vetted.
                    await BundleIo.SkipAsync(stream, length, buffer, ct);
                    continue;
                }

                // Each file is its own unit of progress: if the stream dies halfway through
                // this one, only this one's bytes are taken back off the counter.
                var progress = Stats.NewAttempt();
                string? problem;
                try
                {
                    problem = await BundleIo.ReceiveFileAsync(item.Local, stream, length,
                        item.ModifiedUnixMs, buffer, progress.AddBytes, ct);
                }
                catch
                {
                    progress.Rollback();
                    throw;
                }

                remaining.Remove(remote);

                if (problem is not null)
                {
                    progress.Rollback();
                    FailFile(Path.GetFileName(item.Local), problem);
                    continue;
                }

                _written.Add(item.Local);
                progress.AddFile();
                ActivityLog.File(TransferDirection.Download, item.Remote, length, CurrentRate?.Invoke() ?? 0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Opening a response can hang too (the far side accepts, then never answers).</summary>
    private async Task<Stream> OpenWatchedAsync(Func<CancellationToken, Task<Stream>> open, CancellationToken ct)
    {
        using var dog = new Watchdog(ct, StallLimit);
        return await dog.RunAsync(open);
    }

    // ---- large files ---------------------------------------------------------------------

    private async Task DownloadLargeGuardedAsync(PlanItem item, CancellationToken ct)
    {
        try { await DownloadLargeAsync(item, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Stats.Fail(item.Remote, ex);
            ActivityLog.Error($"{item.Remote}: {FailureText.Of(ex)}");
        }
    }

    /// <summary>
    /// What a half-finished large file has on disk already, kept beside its part file.
    /// Only trusted if the source still has the same size and date - otherwise the chunks
    /// on disk belong to a different version of the file.
    /// </summary>
    private sealed record ResumeNote(string Remote, long Size, long ModifiedUnixMs, long ChunkSize, List<int> Done);

    private async Task DownloadLargeAsync(PlanItem item, CancellationToken ct)
    {
        var part = PartFiles.PathFor(item.Local);
        var notePath = part + ".json";

        // Replacing a file that another program has open would fail only at the very end,
        // after the whole thing had downloaded. Find that out first.
        EnsureReplaceable(item.Local);

        var chunks = new List<(int Index, long Offset, long Length)>();
        for (long offset = 0, index = 0; offset < item.Size; offset += Tuning.ChunkSize, index++)
            chunks.Add(((int)index, offset, Math.Min(Tuning.ChunkSize, item.Size - offset)));

        var done = new HashSet<int>(ReadResumable(part, notePath, item));
        var resumed = chunks.Where(c => done.Contains(c.Index)).Sum(c => c.Length);
        if (resumed > 0)
        {
            Interlocked.Add(ref _resumedBytes, resumed);
            Stats.ReduceTotalBytes(resumed);
            Diagnostics.Log("transfer", $"resuming {Path.GetFileName(item.Local)}: {resumed:N0} of {item.Size:N0} bytes already here");
        }

        // Each chunk's progress, so a file that ultimately fails takes all of its bytes back
        // off the counter rather than leaving the History figure inflated.
        var attempts = new ConcurrentBag<TransferStats.Attempt>();
        var noteLock = new Lock();
        var sinceNote = 0L;

        try
        {
            // preallocationSize tells the filesystem the final size up front, so the file is
            // laid out in one extent instead of being grown repeatedly as chunks land out of
            // order. A resume opens the existing part file instead, keeping what it holds.
            using (var handle = done.Count > 0
                       ? File.OpenHandle(part, FileMode.Open, FileAccess.Write, FileShare.None, FileOptions.Asynchronous)
                       : File.OpenHandle(part, FileMode.Create, FileAccess.Write,
                           FileShare.None, FileOptions.Asynchronous, preallocationSize: item.Size))
            {
                if (done.Count == 0) RandomAccess.SetLength(handle, item.Size);

                await Parallel.ForEachAsync(chunks.Where(c => !done.Contains(c.Index)),
                    new ParallelOptions { MaxDegreeOfParallelism = Tuning.Streams, CancellationToken = ct },
                    async (chunk, token) =>
                    {
                        var attempt = Stats.NewAttempt();
                        attempts.Add(attempt);
                        await TransferStats.WithRetry(attempt,
                            a => FetchChunkAsync(item, handle, (chunk.Offset, chunk.Length), a, token),
                            Tuning.Attempts, token);

                        // Note the progress every so often, so a dropped connection or a crash
                        // resumes from roughly here rather than from nothing.
                        lock (noteLock)
                        {
                            done.Add(chunk.Index);
                            sinceNote += chunk.Length;
                            if (sinceNote >= 8 * Tuning.ChunkSize)
                            {
                                sinceNote = 0;
                                WriteNote(notePath, item, done);
                            }
                        }
                    });
            }
        }
        catch (OperationCanceledException)
        {
            // Cancel means "leave the disk as it was".
            foreach (var attempt in attempts) attempt.Rollback();
            BundleIo.TryDelete(part);
            BundleIo.TryDelete(notePath);
            throw;
        }
        catch
        {
            // Anything else - the connection, the far side - keeps what arrived so the next
            // attempt at this file carries on from here.
            foreach (var attempt in attempts) attempt.Rollback();
            lock (noteLock)
            {
                if (item.ModifiedUnixMs > 0 && done.Count > 0) WriteNote(notePath, item, done);
                else { BundleIo.TryDelete(part); BundleIo.TryDelete(notePath); }
            }
            throw;
        }

        // Dated before the move, not after: a PC switched off between the two left a complete
        // file with the wrong date, which a resume then took for a different file ("replace?").
        PartFiles.StampModified(part, item.ModifiedUnixMs);

        try
        {
            File.Move(part, item.Local, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The whole file is here; only the swap failed. Keep it so trying again just
            // retries the move instead of the download.
            foreach (var attempt in attempts) attempt.Rollback();
            lock (noteLock) WriteNote(notePath, item, done);
            throw new IOException($"downloaded, but the existing file could not be replaced: {ex.Message}", ex);
        }

        BundleIo.TryDelete(notePath);
        PartFiles.StampModified(item.Local, item.ModifiedUnixMs);   // again, in case the move changed it
        _written.Add(item.Local);

        Stats.NewAttempt().AddFile();
        ActivityLog.File(TransferDirection.Download, item.Remote, item.Size, CurrentRate?.Invoke() ?? 0);
    }

    /// <summary>Chunks a previous attempt left on disk, if they belong to this same file.</summary>
    private static IEnumerable<int> ReadResumable(string part, string notePath, PlanItem item)
    {
        try
        {
            if (item.ModifiedUnixMs <= 0 || !File.Exists(part) || !File.Exists(notePath)) return [];

            var note = JsonSerializer.Deserialize<ResumeNote>(File.ReadAllText(notePath));
            if (note is null || note.Size != item.Size || note.ModifiedUnixMs != item.ModifiedUnixMs ||
                note.ChunkSize != Tuning.ChunkSize || new FileInfo(part).Length != item.Size)
            {
                return [];
            }

            return note.Done;
        }
        catch
        {
            return [];
        }
    }

    private static void WriteNote(string notePath, PlanItem item, HashSet<int> done)
    {
        try
        {
            File.WriteAllText(notePath, JsonSerializer.Serialize(
                new ResumeNote(item.Remote, item.Size, item.ModifiedUnixMs, Tuning.ChunkSize, done.Order().ToList())));
        }
        catch { /* without a note the next attempt simply starts over */ }
    }

    /// <summary>Throws now if a destination that already exists could not be replaced later.</summary>
    private static void EnsureReplaceable(string destination)
    {
        if (!File.Exists(destination)) return;

        try
        {
            if ((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
                throw new IOException("the existing file is read-only");

            using var probe = File.Open(destination, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"the existing file cannot be replaced (is it open in another program?): {ex.Message}", ex);
        }
    }

    private async Task FetchChunkAsync(PlanItem item, Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        (long Offset, long Length) chunk, TransferStats.Attempt attempt, CancellationToken ct)
    {
        // One of the eight shared sockets. Held for the whole chunk so the number of range
        // requests actually in flight never exceeds eight, however many files are running.
        await _streamBudget.WaitAsync(ct);

        var buffer = ArrayPool<byte>.Shared.Rent(BundleIo.BufferSize);
        try
        {
            await using var stream = new StallGuardStream(
                await OpenWatchedAsync(token => api.OpenRangeAsync(item.Remote, chunk.Offset, chunk.Length, item.Size, token), ct),
                StallLimit);

            var position = chunk.Offset;
            var end = chunk.Offset + chunk.Length;
            while (position < end)
            {
                var want = (int)Math.Min(buffer.Length, end - position);
                var started = WaitTimer.Now;
                var read = await stream.ReadAsync(buffer.AsMemory(0, want), ct);
                WaitTimer.RecvNet(started);
                if (read <= 0) throw new EndOfStreamException("Connection closed mid-chunk.");

                // Parallel writes to distinct offsets on one handle are safe without a lock.
                started = WaitTimer.Now;
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), position, ct);
                WaitTimer.RecvDisk(started);
                position += read;
                attempt.AddBytes(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _streamBudget.Release();
        }
    }
}
