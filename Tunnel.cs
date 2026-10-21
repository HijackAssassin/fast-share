using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FastShare;

/// <summary>
/// Lets the side that cannot accept connections still share its files.
///
/// Only one end of this pair can forward a port. Once the other end opens a connection to it,
/// though, that socket is bidirectional - NAT happily carries traffic both ways on a
/// connection it has already seen established. What blocks us is HTTP, not the network: HTTP
/// only ever goes client-asks then server-answers, so the host cannot spontaneously ask the
/// client for anything.
///
/// A WebSocket upgrade on the very same forwarded port removes that restriction. After the
/// handshake the connection is just a stream of messages either side may send, so the host can
/// issue requests down a connection the client opened.
///
/// Eight sockets are opened rather than one, and each carries a single request at a time. That
/// is the same shape as the eight parallel HTTPS connections used in the other direction, and
/// for the same reason: one stream cannot fill the link.
/// </summary>
public static class TunnelFrames
{
    public const byte Request = 1;
    public const byte Header = 2;
    public const byte Chunk = 3;
    public const byte End = 4;

    public const int ChunkSize = 256 * 1024;

    /// <summary>
    /// The largest message either end will assemble. Data chunks are 256 KB; this only has
    /// room for the one-message text replies older builds send (a big manifest). Anything
    /// larger is a broken or hostile peer, and refusing it keeps memory bounded.
    /// </summary>
    public const int MaxMessage = 512 * 1024 * 1024;

    /// <summary>Ping/pong on idle tunnel sockets, so a vanished peer is noticed in under a minute.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(30);

    public const string List = "list";
    public const string Manifest = "manifest";
    public const string File = "file";
    public const string Bundle = "bundle";

    /// <summary>
    /// A whole HTTP request carried over the tunnel (2.4.8+): the far side hands it to its own
    /// loopback FastShare server. It is how writes - uploads, deletes, renames, new folders -
    /// reach a peer whose port is shut. Frames: Request (payload = <see cref="HttpHead"/>),
    /// then, if <see cref="HttpHead.Body"/>, Chunk* End. Answer: Header (status, a marker byte,
    /// then the content type as UTF-8), Chunk*, End - always, whatever the status. An older build
    /// refuses the method with a bare 400 Header; the host only sends a body to a peer that has
    /// already answered a bodiless probe, so an old peer's socket is never left out of step.
    /// </summary>
    public const string Http = "http";

    public sealed record HttpHead(string Method, string Path, string? ContentType, bool Body);

    public static byte[] BuildHttpHeader(int status, string? contentType)
    {
        // Byte 5 is a marker, so even an answer with no content type is longer than the bare
        // 5-byte header an older build refuses with - the two must never be confused.
        var type = Encoding.UTF8.GetBytes(contentType ?? "");
        var message = new byte[6 + type.Length];
        message[0] = Header;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(1), status);
        message[5] = 1;
        type.CopyTo(message.AsSpan(6));
        return message;
    }

    /// <summary>
    /// Shared by both ends so the property names cannot disagree. Size is the file size the
    /// manifest listed, so the serving end can refuse a file that has changed since; it is 0
    /// from older builds, which means "don't check".
    /// </summary>
    public sealed record RangeRequest(string Path, long Offset, long Length, long Size = 0);

    public static byte[] BuildRequest(string method, string payload)
    {
        var methodBytes = Encoding.UTF8.GetBytes(method);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        var message = new byte[1 + 4 + methodBytes.Length + 4 + payloadBytes.Length];
        message[0] = Request;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(1), methodBytes.Length);
        methodBytes.CopyTo(message.AsSpan(5));
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(5 + methodBytes.Length), payloadBytes.Length);
        payloadBytes.CopyTo(message.AsSpan(9 + methodBytes.Length));
        return message;
    }

    public static (string Method, string Payload) ParseRequest(ReadOnlySpan<byte> message)
    {
        if (message.Length < 9 || message[0] != Request)
            throw new InvalidDataException("Malformed tunnel request.");

        var methodLength = BinaryPrimitives.ReadInt32LittleEndian(message[1..]);
        if (methodLength < 0 || 9 + methodLength > message.Length)
            throw new InvalidDataException("Malformed tunnel request.");

        var method = Encoding.UTF8.GetString(message.Slice(5, methodLength));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(message[(5 + methodLength)..]);
        if (payloadLength < 0 || 9 + methodLength + payloadLength > message.Length)
            throw new InvalidDataException("Malformed tunnel request.");

        var payload = Encoding.UTF8.GetString(message.Slice(9 + methodLength, payloadLength));
        return (method, payload);
    }

    public static byte[] BuildHeader(int status)
    {
        var message = new byte[5];
        message[0] = Header;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(1), status);
        return message;
    }

    public static byte[] BuildEnd() => [End];

    public static Task SendAsync(WebSocket socket, byte[] message, CancellationToken ct) =>
        socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, ct);

    public static ValueTask SendAsync(WebSocket socket, ReadOnlyMemory<byte> message, CancellationToken ct) =>
        socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, ct);

    /// <summary>
    /// Reads one whole message, reassembling fragments, into a pooled buffer. Null once the
    /// socket closes. The caller must dispose the result to hand the buffer back.
    ///
    /// This used to assemble into a MemoryStream and copy out with ToArray - three large
    /// allocations per 256 KB received, which at 100 MB/s is half a gigabyte a second of
    /// garbage for the collector to chase.
    /// </summary>
    public static async Task<TunnelMessage?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + 1024);
        var used = 0;
        try
        {
            while (true)
            {
                if (used == buffer.Length)
                {
                    if (buffer.Length >= MaxMessage) throw new InvalidDataException("Tunnel message too large.");
                    var bigger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, MaxMessage));
                    buffer.AsSpan(0, used).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }

                ValueWebSocketReceiveResult result;
                try { result = await socket.ReceiveAsync(buffer.AsMemory(used), ct); }
                catch (WebSocketException) { ArrayPool<byte>.Shared.Return(buffer); return null; }
                catch (OperationCanceledException) { ArrayPool<byte>.Shared.Return(buffer); return null; }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    return null;
                }

                used += result.Count;
                if (result.EndOfMessage) return new TunnelMessage(buffer, used);
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }
}

/// <summary>One received tunnel message in a pooled buffer. Dispose to return the buffer.</summary>
public sealed class TunnelMessage(byte[] buffer, int length) : IDisposable
{
    private byte[]? _buffer = buffer;

    public int Length { get; } = length;
    public byte Kind => Length > 0 ? _buffer![0] : (byte)0;
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Length);
    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, Length);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
}

/// <summary>
/// The host's handle on one connected peer's tunnel: a pool of sockets that peer opened, and
/// the ability to ask it for files over them.
///
/// Implements <see cref="IFileSource"/>, so the ordinary eight-stream downloader works against
/// a peer's folders with no changes at all.
/// </summary>
public sealed class PeerTunnel : IFileSource
{
    /// <summary>Sockets one peer may hold open. Their client opens eight; this leaves slack for reconnects.</summary>
    public const int MaxSockets = Tuning.Streams * 2;

    private readonly ConcurrentQueue<Lease> _idle = new();
    private readonly SemaphoreSlim _available = new(0);
    private int _total;

    public required string PeerId { get; init; }
    public required string Username { get; init; }

    /// <summary>
    /// A local name for this person, set on this machine only. Mirrors the one on Session so
    /// every list that shows a peer can bind to the same thing rather than some using the
    /// nickname and others the raw machine name.
    /// </summary>
    public string? Nickname { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? Username : Nickname!;

    public int SocketCount => Volatile.Read(ref _total);
    public bool IsUsable => SocketCount > 0;

    private sealed class Lease
    {
        public required WebSocket Socket { get; init; }
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Parks a socket the peer opened. The returned task completes when the socket dies, which
    /// is what keeps the ASP.NET request handler alive for as long as the tunnel is up.
    /// </summary>
    public Task AddAsync(WebSocket socket, CancellationToken connectionClosed = default)
    {
        var lease = new Lease { Socket = socket };
        Interlocked.Increment(ref _total);
        _idle.Enqueue(lease);
        _available.Release();

        // An idle socket is not being read, so nothing here would notice the peer going away -
        // the pool would keep offering dead sockets until someone tried to use one. Kestrel
        // already knows the moment the underlying connection drops, so let it tell us.
        if (connectionClosed.CanBeCanceled)
        {
            connectionClosed.Register(() =>
            {
                if (lease.Closed.TrySetResult()) Interlocked.Decrement(ref _total);
            });
        }

        return lease.Closed.Task;
    }

    public void CloseAll()
    {
        while (_idle.TryDequeue(out var lease)) Retire(lease);
    }

    /// <summary>Retires a socket exactly once, however many places notice it has died.</summary>
    private void Retire(Lease lease)
    {
        if (lease.Closed.TrySetResult()) Interlocked.Decrement(ref _total);
    }

    private async Task<Lease> TakeAsync(CancellationToken ct)
    {
        while (true)
        {
            if (SocketCount == 0)
                throw new InvalidOperationException("That peer is not sharing anything back right now.");

            await _available.WaitAsync(ct);
            if (!_idle.TryDequeue(out var lease))
                throw new InvalidOperationException("The connection to that peer dropped.");

            // Skip anything already retired or closed underneath us.
            if (lease.Closed.Task.IsCompleted || lease.Socket.State != WebSocketState.Open)
            {
                Retire(lease);
                continue;
            }

            return lease;
        }
    }

    private void Release(Lease lease, bool healthy)
    {
        if (!healthy || lease.Socket.State != WebSocketState.Open || lease.Closed.Task.IsCompleted)
        {
            Retire(lease);
            return;
        }

        _idle.Enqueue(lease);
        _available.Release();
    }

    private async Task<Stream> RequestAsync(string method, string payload, CancellationToken ct)
    {
        var lease = await TakeAsync(ct);
        var healthy = false;
        try
        {
            await TunnelFrames.SendAsync(lease.Socket, TunnelFrames.BuildRequest(method, payload), ct);

            using var header = await TunnelFrames.ReceiveAsync(lease.Socket, ct);
            if (header is null || header.Length < 5 || header.Kind != TunnelFrames.Header)
                throw new IOException("The peer did not answer properly.");

            var status = BinaryPrimitives.ReadInt32LittleEndian(header.Span[1..]);
            if (status != 200)
            {
                // A refusal is a complete answer - the socket is still in step - so it goes
                // back in the pool. Retiring it cost a three-second reconnect for nothing.
                healthy = true;
                throw new IOException(status == 409
                    ? "The file changed on the other PC while it was being copied."
                    : $"The peer refused that request ({status}).");
            }

            // The stream owns the socket now and hands it back when disposed.
            return new TunnelStream(this, lease);
        }
        catch
        {
            Release(lease, healthy);
            throw;
        }
    }

    private async Task<string> RequestTextAsync(string method, string payload, CancellationToken ct)
    {
        await using var stream = await RequestAsync(method, payload, ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task<List<Entry>> ListAsync(string path, CancellationToken ct) =>
        JsonSerializer.Deserialize<List<Entry>>(await RequestTextAsync(TunnelFrames.List, path, ct)) ?? [];

    public async Task<Manifest> ManifestAsync(string path, CancellationToken ct) =>
        JsonSerializer.Deserialize<Manifest>(await RequestTextAsync(TunnelFrames.Manifest, path, ct))
        ?? new Manifest([], 0);

    public Task<Stream> OpenRangeAsync(string path, long offset, long length, long fileSize, CancellationToken ct) =>
        RequestAsync(TunnelFrames.File,
            JsonSerializer.Serialize(new TunnelFrames.RangeRequest(path, offset, length, Math.Max(0, fileSize))), ct);

    public Task<Stream> OpenBundleAsync(List<string> paths, CancellationToken ct) =>
        RequestAsync(TunnelFrames.Bundle, JsonSerializer.Serialize(paths), ct);

    /// <summary>
    /// An HttpClient handler that sends each request down this tunnel, so an ordinary
    /// <see cref="ApiClient"/> - and so the ordinary Uploader, delete, rename and create - works
    /// against a peer whose port is shut. The peer authenticates it as our reverse session.
    /// </summary>
    public HttpMessageHandler CreateHttpHandler() => new TunnelHttpHandler(this);

    private sealed class TunnelHttpHandler(PeerTunnel owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            var lease = await owner.TakeAsync(ct);
            var healthy = false;
            var sending = Task.CompletedTask;
            try
            {
                var head = new TunnelFrames.HttpHead(request.Method.Method, request.RequestUri!.PathAndQuery,
                    request.Content?.Headers.ContentType?.ToString(), request.Content is not null);
                await TunnelFrames.SendAsync(lease.Socket,
                    TunnelFrames.BuildRequest(TunnelFrames.Http, JsonSerializer.Serialize(head)), ct);

                // The body goes out while we wait for the answer: the far side may answer
                // before reading all of it (a refusal), and then drains the rest.
                if (request.Content is { } content) sending = SendBodyAsync(lease.Socket, content, ct);

                using var header = await TunnelFrames.ReceiveAsync(lease.Socket, ct);
                if (header is null || header.Length < 5 || header.Kind != TunnelFrames.Header)
                    throw new IOException("The peer did not answer properly.");

                var status = BinaryPrimitives.ReadInt32LittleEndian(header.Span[1..]);
                var type = header.Length > 6 ? Encoding.UTF8.GetString(header.Span[6..]) : "";

                // An older peer refuses the method with a bare header and nothing after it.
                if (header.Length == 5)
                {
                    // In step only if we sent no body for it to misread.
                    healthy = request.Content is null;
                    throw new NotSupportedException("That PC's FastShare is too old to accept changes over this connection.");
                }

                // Answers here are small (JSON, or nothing), so the whole body is read before
                // returning. The socket is then clean and goes straight back to the pool;
                // leaving it to whoever disposes the response retired most sockets unread.
                var body = new MemoryStream();
                while (true)
                {
                    using var message = await TunnelFrames.ReceiveAsync(lease.Socket, ct);
                    if (message is null || message.Length == 0)
                        throw new IOException("The peer's connection dropped mid-answer.");
                    if (message.Kind == TunnelFrames.End) break;
                    if (message.Kind != TunnelFrames.Chunk) throw new InvalidDataException("Unexpected frame from the peer.");
                    body.Write(message.Span[1..]);
                }

                await sending;
                healthy = true;

                var response = new HttpResponseMessage((System.Net.HttpStatusCode)status)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(body.ToArray()),
                };
                if (type.Length > 0 &&
                    System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(type, out var parsed))
                    response.Content.Headers.ContentType = parsed;
                return response;
            }
            finally
            {
                if (!healthy) { try { await sending; } catch { } }
                owner.Release(lease, healthy);
            }
        }

        private static async Task SendBodyAsync(WebSocket socket, HttpContent content, CancellationToken ct)
        {
            await using (var writer = new TunnelService.SocketWriter(socket))
            {
                await content.CopyToAsync(writer, ct);
            }
            await TunnelFrames.SendAsync(socket, TunnelFrames.BuildEnd(), ct);
        }
    }

    /// <summary>Reads a response body off the tunnel, returning the socket when finished.</summary>
    private sealed class TunnelStream(PeerTunnel owner, Lease lease) : Stream
    {
        private TunnelMessage? _current;
        private int _offset;
        private bool _ended;
        private bool _faulted;
        private bool _released;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (true)
            {
                if (_current is not null && _offset < _current.Length)
                {
                    var take = Math.Min(buffer.Length, _current.Length - _offset);
                    _current.Memory.Slice(_offset, take).CopyTo(buffer);
                    _offset += take;
                    return take;
                }

                if (_ended) return 0;

                _current?.Dispose();
                _current = null;

                var message = await TunnelFrames.ReceiveAsync(lease.Socket, ct);
                if (message is null || message.Length == 0)
                {
                    message?.Dispose();
                    _faulted = true;
                    throw new IOException("The peer's connection dropped mid-transfer.");
                }

                switch (message.Kind)
                {
                    case TunnelFrames.Chunk:
                        _current = message;
                        _offset = 1;   // past the frame-type byte; the payload is read in place
                        break;

                    case TunnelFrames.End:
                        message.Dispose();
                        _ended = true;
                        return 0;

                    default:
                        message.Dispose();
                        _faulted = true;
                        throw new InvalidDataException("Unexpected frame from the peer.");
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        private void ReleaseOnce(bool healthy)
        {
            if (_released) return;
            _released = true;

            _current?.Dispose();
            _current = null;

            // A body abandoned part-way leaves unread frames queued on that socket, which
            // would desynchronise the next request. Only a cleanly finished body is reusable.
            owner.Release(lease, healthy);
        }

        /// <summary>
        /// Consumes whatever is left of the body so the socket is clean for the next request.
        ///
        /// Readers usually stop before the tunnel's own End frame: a bundle finishes at its
        /// internal end marker, and a range read stops once it has the bytes it asked for. That
        /// leaves one frame queued, which is enough to desynchronise the socket - so almost
        /// every socket was being retired after a single use. Draining is normally one frame;
        /// it is capped so that cancelling a large download cannot sit here reading the rest
        /// of the file.
        /// </summary>
        private async ValueTask<bool> DrainAsync()
        {
            if (_ended) return !_faulted;
            if (_faulted) return false;

            const int maxFrames = 64;
            for (var i = 0; i < maxFrames; i++)
            {
                TunnelMessage? message;
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    message = await TunnelFrames.ReceiveAsync(lease.Socket, deadline.Token);
                }
                catch { return false; }

                using (message)
                {
                    if (message is null || message.Length == 0) return false;
                    if (message.Kind == TunnelFrames.End) return true;
                    if (message.Kind != TunnelFrames.Chunk) return false;
                }
            }

            return false;   // too much left over; cheaper to drop the socket than keep reading
        }

        protected override void Dispose(bool disposing)
        {
            // The synchronous path cannot drain without blocking, so only a body already read
            // to its end is handed back.
            ReleaseOnce(_ended && !_faulted);
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            var healthy = await DrainAsync();
            ReleaseOnce(healthy);
            await base.DisposeAsync();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Every peer currently offering files back to us.</summary>
public sealed class TunnelRegistry
{
    private readonly ConcurrentDictionary<string, PeerTunnel> _tunnels = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public PeerTunnel GetOrCreate(string peerId, string username)
    {
        var tunnel = _tunnels.GetOrAdd(peerId, _ => new PeerTunnel { PeerId = peerId, Username = username });
        Changed?.Invoke();
        return tunnel;
    }

    public PeerTunnel? Find(string peerId) =>
        _tunnels.TryGetValue(peerId, out var tunnel) && tunnel.IsUsable ? tunnel : null;

    public IReadOnlyList<PeerTunnel> Usable => _tunnels.Values.Where(t => t.IsUsable).ToList();

    public void Drop(string peerId)
    {
        if (_tunnels.TryRemove(peerId, out var tunnel)) tunnel.CloseAll();
        Changed?.Invoke();
    }

    public void DropAll()
    {
        foreach (var peerId in _tunnels.Keys.ToList()) Drop(peerId);
    }

    public void Notify() => Changed?.Invoke();
}
