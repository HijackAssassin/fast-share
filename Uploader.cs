using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace FastShare;

/// <summary>
/// One local file (or empty folder) to send, and where it goes relative to the remote folder.
/// A negative <see cref="Size"/> marks an empty folder.
/// </summary>
public sealed record UploadItem(string Local, string Relative, long Size, long ModifiedUnixMs = 0)
{
    public bool IsEmptyFolder => Size < 0;
}

/// <summary>
/// Pushes files to the far side using the same strategies as <see cref="Downloader"/>,
/// mirrored: large files are split into chunks written at explicit offsets over eight
/// sockets, medium files run several at once sharing those eight, and small files are packed
/// into framed bundles so thousands of them cost tens of round trips instead of thousands.
///
/// Against a current host every file lands on a temp name and only replaces what was there
/// once it is complete, so a failed or cancelled upload never leaves a blank or half-written
/// file behind. An older host is spoken to in the original protocol.
/// </summary>
public sealed class Uploader(ApiClient api)
{
    public TransferStats Stats { get; } = new();

    /// <summary>Lets finished-file log lines carry the rate the UI is currently showing.</summary>
    public Func<double>? CurrentRate { get; set; }

    /// <summary>How long a request may go without progress before it is treated as dead.</summary>
    public TimeSpan StallLimit { get; init; } = Watchdog.DefaultIdle;

    /// <summary>
    /// Remote paths that now hold a finished file, so a cancel can ask the far side to
    /// remove what it already accepted rather than leaving half a folder behind.
    /// </summary>
    public IReadOnlyCollection<string> Written => _written;
    private readonly ConcurrentBag<string> _written = [];

    /// <summary>The same eight-socket budget the downloader uses, shared by every large file in flight.</summary>
    private readonly SemaphoreSlim _streamBudget = new(Tuning.Streams);

    private bool _part;
    private bool _v2;

    public async Task RunAsync(string remoteDirectory, IReadOnlyList<UploadItem> items,
        CancellationToken ct)
    {
        var files = items.Where(i => !i.IsEmptyFolder).ToList();
        var folders = items.Where(i => i.IsEmptyFolder).ToList();

        Stats.TotalFiles = files.Count;
        Stats.TotalBytes = files.Sum(i => i.Size);

        var caps = await api.EnsureCapsAsync(ct);
        _part = caps.Contains(Caps.UploadPart);
        _v2 = caps.Contains(Caps.BundleV2);

        var large = files.Where(i => i.Size >= Tuning.LargeFileThreshold).ToList();
        var small = files.Where(i => i.Size < Tuning.LargeFileThreshold).ToList();

        // Empty folders ride in a bundle of their own. An older host cannot take them (its
        // frames have no way to say "folder"), so they are simply not sent there.
        var bundles = Batching.IntoBundles(small, i => i.Size);
        if (_v2 && folders.Count > 0) bundles.Add(folders);

        if (bundles.Count > 0)
        {
            await Parallel.ForEachAsync(bundles,
                new ParallelOptions { MaxDegreeOfParallelism = Tuning.Streams, CancellationToken = ct },
                async (group, token) => await UploadGroupAsync(remoteDirectory, group, token));
        }

        // Large files split like the downloader's: big ones one at a time (each already fills
        // all eight sockets), medium ones several at once sharing the same eight. Uploading
        // medium files one by one was the "folder of 10 MB files crawls" case in this direction.
        var fill = Tuning.Streams * Tuning.ChunkSize;
        var huge = large.Where(i => i.Size >= fill).ToList();
        var medium = large.Where(i => i.Size < fill).ToList();

        if (medium.Count > 0)
        {
            await Parallel.ForEachAsync(medium,
                new ParallelOptions { MaxDegreeOfParallelism = Tuning.Streams, CancellationToken = ct },
                async (item, token) => await UploadLargeGuardedAsync(remoteDirectory, item, token));
        }

        foreach (var item in huge)
        {
            ct.ThrowIfCancellationRequested();
            await UploadLargeGuardedAsync(remoteDirectory, item, ct);
        }
    }

    private static string RemotePath(string remoteDirectory, string relative) =>
        $"{remoteDirectory.TrimEnd('/')}/{relative}";

    // ---- small files ---------------------------------------------------------------------

    private async Task UploadGroupAsync(string remoteDirectory, List<UploadItem> group, CancellationToken ct)
    {
        // Files that disappeared or cannot be read here are dropped before anything is sent,
        // so one of them cannot sink the other 255.
        var readable = new List<UploadItem>(group.Count);
        foreach (var item in group)
        {
            if (item.IsEmptyFolder) { readable.Add(item); continue; }

            try
            {
                var info = new FileInfo(item.Local);
                if (!info.Exists) throw new FileNotFoundException("it no longer exists here");
                if (info.Length != item.Size) throw new IOException("it changed while waiting to be sent");
                readable.Add(item);
            }
            catch (Exception ex)
            {
                Stats.Fail(Path.GetFileName(item.Local), ex);
                ActivityLog.Error($"{Path.GetFileName(item.Local)}: {ex.Message}");
            }
        }
        if (readable.Count == 0) return;

        var attempt = Stats.NewAttempt();
        try
        {
            IReadOnlyList<string> refused = [];

            // A fresh content object per attempt: HttpContent can only be serialised once, so a
            // retry must rebuild it.
            await TransferStats.WithRetry(attempt, async a =>
            {
                using var dog = new Watchdog(ct, StallLimit);
                refused = await dog.RunAsync(token =>
                    api.UploadBundleAsync(remoteDirectory, new BundleContent(readable, a, dog, _v2), _v2, token));
            }, Tuning.Attempts, ct);

            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var problem in refused)
            {
                var name = problem.Split(':', 2)[0];
                failed.Add(name);
                Stats.Fail(name, new IOException(problem.Contains(':') ? problem.Split(':', 2)[1].Trim() : problem));
                ActivityLog.Error($"Not saved on the other PC — {problem}");
            }

            // Counted only now the host has confirmed the bundle, so a retried bundle can no
            // longer put the same files in the log (and the all-time stats) twice.
            foreach (var item in readable)
            {
                if (item.IsEmptyFolder) continue;
                if (failed.Contains(item.Relative))
                {
                    attempt.RemoveFile(item.Size);
                    continue;
                }

                _written.Add(RemotePath(remoteDirectory, item.Relative));
                ActivityLog.File(TransferDirection.Upload, item.Relative, item.Size, CurrentRate?.Invoke() ?? 0);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            attempt.Rollback();
            var fileCount = readable.Count(i => !i.IsEmptyFolder);
            Stats.Fail($"{fileCount} small files", ex);
            ActivityLog.Error($"{fileCount} small files failed: {ex.Message}");
        }
    }

    // ---- large files ---------------------------------------------------------------------

    private async Task UploadLargeGuardedAsync(string remoteDirectory, UploadItem item, CancellationToken ct)
    {
        try { await UploadLargeAsync(remoteDirectory, item, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Stats.Fail(Path.GetFileName(item.Local), ex);
            ActivityLog.Error($"{Path.GetFileName(item.Local)}: {ex.Message}");
        }
    }

    private async Task UploadLargeAsync(string remoteDirectory, UploadItem item, CancellationToken ct)
    {
        var remotePath = RemotePath(remoteDirectory, item.Relative);

        // Create and preallocate first so parallel chunk writes cannot race on creating the
        // file, and so the filesystem lays it out in one extent.
        using (var dog = new Watchdog(ct, StallLimit))
            await dog.RunAsync(token => api.BeginUploadAsync(remotePath, item.Size, _part, token));

        var chunks = new List<(long Offset, long Length)>();
        for (long offset = 0; offset < item.Size; offset += Tuning.ChunkSize)
            chunks.Add((offset, Math.Min(Tuning.ChunkSize, item.Size - offset)));

        var attempts = new ConcurrentBag<TransferStats.Attempt>();
        try
        {
            await Parallel.ForEachAsync(chunks,
                new ParallelOptions { MaxDegreeOfParallelism = Tuning.Streams, CancellationToken = ct },
                async (chunk, token) =>
                {
                    var attempt = Stats.NewAttempt();
                    attempts.Add(attempt);
                    await TransferStats.WithRetry(attempt, a => SendChunkAsync(remotePath, item, chunk, a, token),
                        Tuning.Attempts, token);
                });

            if (_part)
            {
                using var dog = new Watchdog(ct, StallLimit);
                await dog.RunAsync(token => api.CommitUploadAsync(remotePath, item.Size, item.ModifiedUnixMs, token));
            }
        }
        catch
        {
            // Nothing of this file counts, and the host's temp file goes - the real file there
            // (if any) was never touched.
            foreach (var attempt in attempts) attempt.Rollback();
            if (_part) await api.AbortUploadAsync(remotePath);
            throw;
        }

        Stats.NewAttempt().AddFile();
        _written.Add(remotePath);
        ActivityLog.File(TransferDirection.Upload, item.Relative, item.Size, CurrentRate?.Invoke() ?? 0);
    }

    private async Task SendChunkAsync(string remotePath, UploadItem item, (long Offset, long Length) chunk,
        TransferStats.Attempt attempt, CancellationToken ct)
    {
        await _streamBudget.WaitAsync(ct);
        try
        {
            using var dog = new Watchdog(ct, StallLimit);
            await dog.RunAsync(token => api.UploadChunkAsync(remotePath, chunk.Offset, _part,
                new FileRangeContent(item.Local, chunk.Offset, chunk.Length, attempt, dog), token));
        }
        finally
        {
            _streamBudget.Release();
        }
    }

    /// <summary>Streams a byte range of a local file as a request body.</summary>
    private sealed class FileRangeContent : HttpContent
    {
        private readonly string _path;
        private readonly long _offset;
        private readonly long _length;
        private readonly TransferStats.Attempt _attempt;
        private readonly Watchdog _dog;

        public FileRangeContent(string path, long offset, long length, TransferStats.Attempt attempt, Watchdog dog)
        {
            _path = path;
            _offset = offset;
            _length = length;
            _attempt = attempt;
            _dog = dog;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;   // exact length avoids chunked encoding overhead
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken ct)
        {
            using var handle = BundleIo.OpenForRead(_path);

            var buffer = ArrayPool<byte>.Shared.Rent(BundleIo.BufferSize);
            try
            {
                long sent = 0;
                while (sent < _length)
                {
                    var want = (int)Math.Min(buffer.Length, _length - sent);
                    var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, want),
                        _offset + sent, ct);

                    // Content-Length is already committed. A file that shrank under us cannot
                    // be sent honestly, so fail the request rather than pad it with zeros.
                    if (read <= 0) throw new IOException("The file got smaller while it was being sent.");

                    await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                    sent += read;
                    _attempt.AddBytes(read);
                    _dog.Pet();
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
    }

    /// <summary>Streams many small files back to back as one framed request body.</summary>
    private sealed class BundleContent : HttpContent
    {
        private readonly List<UploadItem> _items;
        private readonly TransferStats.Attempt _attempt;
        private readonly Watchdog _dog;
        private readonly bool _v2;
        private readonly long _length;

        public BundleContent(List<UploadItem> items, TransferStats.Attempt attempt, Watchdog dog, bool v2)
        {
            _items = items;
            _attempt = attempt;
            _dog = dog;
            _v2 = v2;

            // Frame overhead is a few bytes plus the path, with no padding, so the exact body
            // size is known up front and the request needs no chunked encoding.
            _length = 4;
            foreach (var item in items)
                _length += BundleIo.HeaderBytes(item.Relative, v2) + Math.Max(0, item.Size);

            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken ct)
        {
            var header = ArrayPool<byte>.Shared.Rent(BundleFormat.MaxPathBytes + 24);
            var buffer = ArrayPool<byte>.Shared.Rent(BundleIo.BufferSize);
            try
            {
                foreach (var item in _items)
                {
                    if (item.IsEmptyFolder)
                    {
                        await BundleIo.WriteHeaderAsync(stream, item.Relative, BundleFormat.Directory, header, ct, 0);
                        continue;
                    }

                    // The size was checked when the bundle was planned. Opened here, it is
                    // sent at that size: a file that shrinks mid-send fails the bundle (and it
                    // is retried) rather than arriving padded with zeros.
                    using var handle = BundleIo.OpenForRead(item.Local);
                    await BundleIo.WriteHeaderAsync(stream, item.Relative, item.Size, header, ct,
                        _v2 ? item.ModifiedUnixMs : null);
                    await BundleIo.CopyToAsync(handle, stream, item.Size, buffer, count =>
                    {
                        _attempt.AddBytes(count);
                        _dog.Pet();
                    }, ct);
                    _attempt.AddFile();
                    _dog.Pet();
                }
                await BundleIo.WriteEndAsync(stream, header, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
