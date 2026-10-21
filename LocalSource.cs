using System.Buffers;
using System.IO;
using System.IO.Pipelines;

namespace FastShare;

/// <summary>
/// Your own shared folders, read straight off the disk.
///
/// Browsing your own shares is not a connection and should not pretend to be one. Going
/// through a loopback listener meant starting a server, doing a TLS handshake and issuing a
/// session just to look at files already on this machine - which then showed up as "someone is
/// connected to you", counted towards the disconnect prompt, and failed outright if anything
/// about the listener went wrong.
///
/// This implements the same <see cref="IFileSource"/> the real connections do, so the browser,
/// the tree and the download engine all work against it unchanged - they cannot tell the
/// difference, and there is nothing on the wire.
/// </summary>
public sealed class LocalSource(Func<ShareSet> shares) : IFileSource
{
    /// <summary>The kind of disk a shared path is on ("NVMe", "SSD", "HDD"), or null if unknown.</summary>
    public string? DriveOf(string remote) =>
        shares().TryResolve(remote, out var full) && DriveDetect.Label(DriveDetect.ForPath(full)) is var label && label != "?"
            ? label
            : null;

    /// <summary>Read afresh each call, so folders added on the Share tab appear immediately.</summary>
    private ShareSet Shares => shares();

    public Task<List<Entry>> ListAsync(string path, CancellationToken ct) =>
        Task.FromResult(Listing.Children(Shares, path)
                        ?? throw new DirectoryNotFoundException($"\"{path}\" is not a shared folder."));

    public Task<Manifest> ManifestAsync(string path, CancellationToken ct) =>
        Task.FromResult(Listing.Tree(Shares, path)
                        ?? throw new DirectoryNotFoundException($"\"{path}\" is not a shared folder."));

    public Task<Stream> OpenRangeAsync(string path, long offset, long length, long fileSize, CancellationToken ct)
    {
        if (!Shares.TryResolve(path, out var full) || !File.Exists(full))
            throw new FileNotFoundException($"\"{path}\" is not in a shared folder.");

        var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (fileSize >= 0 && file.Length != fileSize)
        {
            file.Dispose();
            throw new IOException("The file changed while it was being copied.");
        }

        file.Position = offset;
        return Task.FromResult<Stream>(new Window(file, length));
    }

    /// <summary>
    /// Many small files in one stream, in the same frame format the network uses, so the
    /// downloader's unpacking code is shared rather than special-cased for local reads.
    /// Produced through a pipe so it streams instead of being assembled in memory first.
    /// </summary>
    public Task<Stream> OpenBundleAsync(List<string> paths, CancellationToken ct)
    {
        var resolved = Shares;
        var pipe = new Pipe();

        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            var header = ArrayPool<byte>.Shared.Rent(BundleFormat.MaxPathBytes + 16);
            var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);

            try
            {
                await using var stream = pipe.Writer.AsStream();

                foreach (var remote in paths)
                {
                    if (!resolved.TryResolve(remote, out var full)) continue;

                    // Opened before the header goes out, so a file that is locked or gone is
                    // simply left out - the reader reports it as missing - rather than failing
                    // the whole stream halfway through its frame.
                    Microsoft.Win32.SafeHandles.SafeFileHandle handle;
                    try { handle = BundleIo.OpenForRead(full); }
                    catch { continue; }

                    using (handle)
                    {
                        var length = RandomAccess.GetLength(handle);
                        await BundleIo.WriteHeaderAsync(stream, remote, length, header, ct);
                        await BundleIo.CopyToAsync(handle, stream, length, buffer, null, ct);
                    }
                }

                await BundleIo.WriteEndAsync(stream, header, ct);
                await stream.FlushAsync(ct);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
                ArrayPool<byte>.Shared.Return(buffer);

                // Completing with the fault is what lets the reader see the error rather than
                // a bundle that simply stops halfway with no explanation.
                await pipe.Writer.CompleteAsync(failure);
            }
        }, ct);

        return Task.FromResult(pipe.Reader.AsStream());
    }

    /// <summary>
    /// A fixed-length view onto part of a file. Without it the caller would be handed the
    /// whole remainder of the file and would have to be trusted to stop at the right byte.
    /// </summary>
    private sealed class Window(FileStream file, long length) : Stream
    {
        private readonly long _length = length;
        private long _left = length;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) return 0;
            var read = file.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_left <= 0) return 0;
            var read = await file.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], ct);
            _left -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) file.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await file.DisposeAsync();
            await base.DisposeAsync();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _left; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
