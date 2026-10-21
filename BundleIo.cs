using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FastShare;

/// <summary>
/// Reads and writes the bundle frame format. Both directions use this, so upload and
/// download can never disagree about the wire format.
/// </summary>
public static class BundleIo
{
    public const int BufferSize = 1 << 20;

    /// <summary>
    /// Reads the next frame header, or null at end of stream.
    ///
    /// With <paramref name="withTime"/> (the v2 upload format, only used when the far side
    /// advertises it) each header also carries the file's modified time, and a length of
    /// <see cref="BundleFormat.Directory"/> marks an empty folder rather than a file.
    /// </summary>
    public static async Task<(string Path, long Length, long ModifiedUnixMs)?> ReadHeaderAsync(
        Stream source, byte[] scratch, CancellationToken ct, bool withTime = false)
    {
        await source.ReadExactlyAsync(scratch.AsMemory(0, 4), ct);
        var pathLength = BinaryPrimitives.ReadInt32LittleEndian(scratch);
        if (pathLength == BundleFormat.EndOfStream) return null;
        if (pathLength is < 0 or > BundleFormat.MaxPathBytes)
            throw new InvalidDataException("Corrupt bundle frame.");

        var pathBuffer = ArrayPool<byte>.Shared.Rent(pathLength);
        string path;
        try
        {
            await source.ReadExactlyAsync(pathBuffer.AsMemory(0, pathLength), ct);
            path = Encoding.UTF8.GetString(pathBuffer, 0, pathLength);
        }
        finally { ArrayPool<byte>.Shared.Return(pathBuffer); }

        await source.ReadExactlyAsync(scratch.AsMemory(0, withTime ? 16 : 8), ct);
        var length = BinaryPrimitives.ReadInt64LittleEndian(scratch);
        var modified = withTime ? BinaryPrimitives.ReadInt64LittleEndian(scratch.AsSpan(8)) : 0;

        if (length < 0 && !(withTime && length == BundleFormat.Directory))
            throw new InvalidDataException("Corrupt bundle frame.");

        return (path, length, modified);
    }

    public static async Task WriteHeaderAsync(
        Stream destination, string path, long length, byte[] scratch, CancellationToken ct,
        long? modifiedUnixMs = null)
    {
        var pathLength = Encoding.UTF8.GetBytes(path, scratch.AsSpan(4));
        BinaryPrimitives.WriteInt32LittleEndian(scratch, pathLength);
        BinaryPrimitives.WriteInt64LittleEndian(scratch.AsSpan(4 + pathLength), length);

        var size = 4 + pathLength + 8;
        if (modifiedUnixMs is { } modified)
        {
            BinaryPrimitives.WriteInt64LittleEndian(scratch.AsSpan(size), modified);
            size += 8;
        }

        await destination.WriteAsync(scratch.AsMemory(0, size), ct);
    }

    /// <summary>Bytes one frame header takes on the wire, for computing exact body lengths.</summary>
    public static long HeaderBytes(string path, bool withTime) =>
        4 + Encoding.UTF8.GetByteCount(path) + 8 + (withTime ? 8 : 0);

    public static async Task WriteEndAsync(Stream destination, byte[] scratch, CancellationToken ct)
    {
        BinaryPrimitives.WriteInt32LittleEndian(scratch, BundleFormat.EndOfStream);
        await destination.WriteAsync(scratch.AsMemory(0, 4), ct);
    }

    /// <summary>Opens a file to send. Separate from the copy so a caller can skip a file it cannot open.</summary>
    public static SafeFileHandle OpenForRead(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes of an open file into a stream.
    ///
    /// The declared length is already on the wire, so a file that shrank mid-send cannot be
    /// finished honestly. This used to pad it out with zeros, which kept the framing aligned
    /// and delivered a silently corrupted file. Now it throws: the stream is abandoned, the far
    /// side sees a failure, and the retry picks up the file as it is now.
    /// </summary>
    public static async Task CopyToAsync(SafeFileHandle handle, Stream destination, long length,
        byte[] buffer, Action<int>? onProgress, CancellationToken ct)
    {
        long sent = 0;
        while (sent < length)
        {
            var want = (int)Math.Min(buffer.Length, length - sent);
            var started = WaitTimer.Now;
            var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, want), sent, ct);
            WaitTimer.SendDisk(started);
            if (read <= 0) throw new IOException("The file got smaller while it was being sent.");
            started = WaitTimer.Now;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            WaitTimer.SendNet(started);
            sent += read;
            onProgress?.Invoke(read);
        }
    }

    /// <summary>Opens a path and copies it. For callers that have nothing to do if the open fails.</summary>
    public static async Task CopyFileToAsync(string path, Stream destination, long length, byte[] buffer,
        Action<int>? onProgress, CancellationToken ct)
    {
        using var handle = OpenForRead(path);
        await CopyToAsync(handle, destination, length, buffer, onProgress, ct);
    }

    /// <summary>
    /// Creates (or truncates) a file to receive <paramref name="length"/> bytes.
    ///
    /// Measured: plain synchronous writes without preallocation were no faster for bundles of
    /// small files (about 3,000-3,400 files/s either way), so every file uses the same
    /// overlapped, preallocated handle.
    /// </summary>
    public static SafeFileHandle OpenForWrite(string path, long length) =>
        File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None,
            FileOptions.Asynchronous, preallocationSize: length);

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes from the stream into an open file.
    ///
    /// A failure reading the stream and a failure writing the disk mean different things to
    /// the caller - one is the connection, the other is this machine - so a disk fault is
    /// wrapped in <see cref="LocalWriteException"/> to keep them apart.
    /// </summary>
    public static async Task WriteFromAsync(SafeFileHandle handle, Stream source, long length,
        byte[] buffer, Action<int>? onProgress, CancellationToken ct)
    {
        long written = 0;
        while (written < length)
        {
            var want = (int)Math.Min(buffer.Length, length - written);
            var started = WaitTimer.Now;
            var read = await source.ReadAsync(buffer.AsMemory(0, want), ct);
            WaitTimer.RecvNet(started);
            if (read <= 0) throw new EndOfStreamException("Stream ended mid-file.");

            started = WaitTimer.Now;
            try { await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), written, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LocalWriteException(ex);
            }
            WaitTimer.RecvDisk(started);

            written += read;
            onProgress?.Invoke(read);
        }
    }

    /// <summary>
    /// Receives one framed file without ever leaving the real file blank or half-written.
    ///
    /// A file that is new here is written straight to its name - created with CreateNew, so
    /// "is anything there?" and "claim the name" are one atomic step - and removed again if
    /// the transfer fails. Only when something already exists does it go to a temp name
    /// beside it and replace the original once complete. The temp-and-rename costs an extra
    /// filesystem operation per file; paying it only when there is something to protect kept
    /// small-file uploads at their old speed (measured: ~70 MB/s always-temp, back to ~120).
    ///
    /// Returns null once the file is in place, or the reason it could not be kept. A file that
    /// cannot even be opened here (locked, read-only folder) has its bytes skipped so the
    /// stream stays aligned and the rest of the bundle carries on. Connection failures throw.
    /// </summary>
    public static async Task<string?> ReceiveFileAsync(string destination, Stream source, long length,
        long modifiedUnixMs, byte[] buffer, Action<int>? onProgress, CancellationToken ct)
    {
        string target;
        SafeFileHandle handle;
        // Creating, dating and closing each file is disk time too - for small files, most of it.
        var opening = WaitTimer.Now;
        try
        {
            try
            {
                handle = File.OpenHandle(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    FileOptions.Asynchronous, preallocationSize: length);
                target = destination;
            }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
            {
                target = PartFiles.PathFor(destination);
                handle = OpenForWrite(target, length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await SkipAsync(source, length, buffer, ct);
            return ex.Message;
        }
        WaitTimer.RecvDisk(opening);

        try
        {
            using (handle)
            {
                await WriteFromAsync(handle, source, length, buffer, onProgress, ct);
                var closing = WaitTimer.Now;
                PartFiles.StampModified(handle, modifiedUnixMs);
                handle.Dispose();
                WaitTimer.RecvDisk(closing);
            }
        }
        catch (LocalWriteException ex)
        {
            // The disk refused mid-file (full, most likely). The rest of this file's bytes are
            // still on the wire; throw so the caller abandons the stream rather than guessing.
            TryDelete(target);
            throw new IOException(ex.InnerException?.Message ?? ex.Message, ex);
        }
        catch
        {
            TryDelete(target);
            throw;
        }

        if (!ReferenceEquals(target, destination))
        {
            try
            {
                File.Move(target, destination, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(target);
                return ex.Message;
            }
        }

        return null;
    }

    /// <summary>Discards a frame's payload so framing stays aligned.</summary>
    public static async Task SkipAsync(Stream source, long length, byte[] buffer, CancellationToken ct)
    {
        long skipped = 0;
        while (skipped < length)
        {
            var want = (int)Math.Min(buffer.Length, length - skipped);
            var read = await source.ReadAsync(buffer.AsMemory(0, want), ct);
            if (read <= 0) throw new EndOfStreamException();
            skipped += read;
        }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>This machine's disk refused a write, as opposed to the connection failing.</summary>
public sealed class LocalWriteException(Exception inner) : IOException(inner.Message, inner);
