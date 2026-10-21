using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FastShare;

/// <summary>
/// The connecting side's half of the tunnel: opens sockets back to the host and answers the
/// requests that arrive down them.
///
/// This is what lets the end that cannot forward a port still share files. It offers whatever
/// folders are on its own Share tab - the same list, whether it is hosting or visiting - so
/// the rule stays simple: the folders you list are the folders you offer.
/// </summary>
public sealed class TunnelService : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _running;

    public bool IsRunning => _running is { IsCompleted: false };

    /// <summary>Raised when a socket connects or drops, so the UI can show whether it is up.</summary>
    public event Action? Changed;

    private int _connected;
    public int ConnectedSockets => Volatile.Read(ref _connected);

    /// <summary>
    /// What to call the host in the traffic figures. They are pulling files off this PC down
    /// the tunnel exactly as a normal peer would over the listener, so it should read the
    /// same way on the Session tab.
    /// </summary>
    public string PeerLabel { get; set; } = "them";

    /// <summary>
    /// Where writes arriving over the tunnel are handed on: our private loopback server and the
    /// host's reverse-session token, or null while that is not ready (writes are then refused).
    /// </summary>
    public Func<(ConnectionInfo Local, string Token)?>? WriteTarget { get; set; }

    public void Start(ConnectionInfo host, string token, Func<ShareSet> shares)
    {
        if (IsRunning) return;


        _running = Task.WhenAll(Enumerable
            .Range(0, Tuning.Streams)
            .Select(_ => WorkerAsync(host, token, shares, _stopping.Token))
            .ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stopping.CancelAsync(); } catch { }
        if (_running is not null)
        {
            try { await _running; } catch { }
        }
        _stopping.Dispose();
    }

    /// <summary>
    /// One socket, kept alive and reconnected if it drops. Eight of these run together for the
    /// same reason the download engine opens eight connections: one stream cannot fill the link.
    /// </summary>
    private async Task WorkerAsync(ConnectionInfo host, string token, Func<ShareSet> shares,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", "Bearer " + token);

                // Ping the host every so often and give up on a socket that stops answering,
                // so a host that vanished is noticed and reconnected instead of waited on.
                socket.Options.KeepAliveInterval = TunnelFrames.KeepAliveInterval;
                socket.Options.KeepAliveTimeout = TunnelFrames.KeepAliveTimeout;

                // Same pinning rule as the HTTPS side: the certificate must be the one we
                // recorded on first contact.
                socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null &&
                    (string.IsNullOrEmpty(host.Fingerprint) ||
                     Convert.ToHexStringLower(SHA256.HashData(cert.GetRawCertData()))
                         .Equals(host.Fingerprint, StringComparison.OrdinalIgnoreCase));

                await socket.ConnectAsync(new Uri($"wss://{host.Host}:{host.Port}/api/tunnel"), ct);

                Interlocked.Increment(ref _connected);
                Changed?.Invoke();
                Diagnostics.Log("tunnel", $"socket up to {host.Host}:{host.Port} ({ConnectedSockets} of {Tuning.Streams})");

                try { await ServeAsync(socket, shares, host, PeerLabel, WriteTarget, ct); }
                finally
                {
                    Interlocked.Decrement(ref _connected);
                    Changed?.Invoke();
                    Diagnostics.Log("tunnel", $"socket closed ({ConnectedSockets} left)");
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // The host may not be up yet, or may have restarted. Back off and retry.
                //
                // Logged rather than swallowed: this is the one place a share-back can fail
                // silently, and "it didn't work" with no record left nothing to go on. A
                // rejected certificate and a closed port look identical from the outside.
                Diagnostics.Log("tunnel", $"connect to {host.Host}:{host.Port} failed", ex);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task ServeAsync(WebSocket socket, Func<ShareSet> shares,
        ConnectionInfo host, string label, Func<(ConnectionInfo Local, string Token)?>? writes, CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            string method, payload;
            using (var message = await TunnelFrames.ReceiveAsync(socket, ct))
            {
                if (message is null) return;

                try { (method, payload) = TunnelFrames.ParseRequest(message.Span); }
                catch { return; }
            }

            try
            {
                if (method == TunnelFrames.Http) await ProxyAsync(socket, payload, writes?.Invoke(), ct);
                else await HandleAsync(socket, method, payload, shares(), host, label, ct);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Anything unexpected: close the socket rather than leave the host waiting for
                // a body that is never coming. A worker reconnects moments later.
                return;
            }
        }
    }

    private static async Task HandleAsync(WebSocket socket, string method, string payload,
        ShareSet shares, ConnectionInfo host, string label, CancellationToken ct)
    {
        switch (method)
        {
            case TunnelFrames.List:
            {
                var entries = Listing.Children(shares, payload);
                if (entries is null) { await RefuseAsync(socket, 404, ct); return; }
                await SendTextAsync(socket, JsonSerializer.Serialize(entries), ct);
                return;
            }

            case TunnelFrames.Manifest:
            {
                var manifest = Listing.Tree(shares, payload, FolderOp.Download);
                if (manifest is null) { await RefuseAsync(socket, 404, ct); return; }
                await SendTextAsync(socket, JsonSerializer.Serialize(manifest), ct);
                return;
            }

            case TunnelFrames.File:
            {
                var request = JsonSerializer.Deserialize<TunnelFrames.RangeRequest>(payload);
                if (request is null || !shares.TryResolve(request.Path, out var full) || !File.Exists(full))
                {
                    await RefuseAsync(socket, 404, ct);
                    return;
                }
                if (!shares.Allows(FolderOp.Download, full))
                {
                    await RefuseAsync(socket, 403, ct);
                    return;
                }

                Microsoft.Win32.SafeHandles.SafeFileHandle handle;
                try { handle = BundleIo.OpenForRead(full); }
                catch { await RefuseAsync(socket, 404, ct); return; }

                using var _ = handle;
                var whole = RandomAccess.GetLength(handle);

                // A file that is no longer the size the host's manifest listed has changed
                // since; sending a range of it would splice two versions together.
                if (request.Size > 0 && whole != request.Size)
                {
                    await RefuseAsync(socket, 409, ct);
                    return;
                }

                await TunnelFrames.SendAsync(socket, TunnelFrames.BuildHeader(200), ct);
                await SendRangeAsync(socket, handle, request.Offset, request.Length, ct);

                // Counted the same way the listener counts a range request, so a host pulling
                // our folders back down the tunnel shows up in the figures too.
                TrafficMonitor.Add(HostKey(host), label, TransferDirection.Upload,
                    request.Path, whole, request.Length);
                await TunnelFrames.SendAsync(socket, TunnelFrames.BuildEnd(), ct);
                return;
            }

            case TunnelFrames.Bundle:
            {
                var paths = JsonSerializer.Deserialize<List<string>>(payload) ?? [];
                await TunnelFrames.SendAsync(socket, TunnelFrames.BuildHeader(200), ct);
                await SendBundleAsync(socket, shares, paths, HostKey(host), label, ct);
                await TunnelFrames.SendAsync(socket, TunnelFrames.BuildEnd(), ct);
                return;
            }

            default:
                await RefuseAsync(socket, 400, ct);
                return;
        }
    }



    private static Task RefuseAsync(WebSocket socket, int status, CancellationToken ct) =>
        TunnelFrames.SendAsync(socket, TunnelFrames.BuildHeader(status), ct);

    // ---- writes: HTTP requests carried over the tunnel ----------------------

    /// <summary>One client per loopback server, keyed by its port; pinned to its certificate.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HttpClient> LocalClients = new();

    private static HttpClient LocalClient(ConnectionInfo local) =>
        LocalClients.GetOrAdd($"{local.Port}|{local.Fingerprint}", _ => new HttpClient(new SocketsHttpHandler
        {
            // Idle connections are dropped well before Kestrel's own keep-alive timeout, so a
            // request is never sent on a connection the server is closing - that would make the
            // handler retry it, and a body read off the tunnel cannot be sent twice.
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = Tuning.Streams * 2,
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "fastshare",
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && Convert.ToHexStringLower(SHA256.HashData(cert.GetRawCertData()))
                        .Equals(local.Fingerprint, StringComparison.OrdinalIgnoreCase),
            },
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        });

    /// <summary>
    /// Hands a request from the host to our loopback server as their reverse session, and sends
    /// the answer back. The host's own credentials are never used: the token is ours, the one
    /// our User Permissions govern, so the host can do exactly what we allowed and no more.
    /// </summary>
    private static async Task ProxyAsync(WebSocket socket, string payload,
        (ConnectionInfo Local, string Token)? target, CancellationToken ct)
    {
        var head = JsonSerializer.Deserialize<TunnelFrames.HttpHead>(payload);
        if (head is null || !IsForwardable(head.Path))
        {
            if (head?.Body == true) await TunnelBody.DrainAsync(socket, ct);
            await AnswerAsync(socket, 400, null, null, ct);
            return;
        }

        var body = head.Body ? new TunnelBody(socket, ct) : null;
        if (target is not { } local)
        {
            if (body is not null) await body.FinishAsync();
            await AnswerAsync(socket, 503, null, null, ct);
            return;
        }

        using var request = new HttpRequestMessage(new HttpMethod(head.Method),
            $"https://127.0.0.1:{local.Local.Port}{head.Path}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", local.Token);
        if (body is not null)
        {
            request.Content = body;
            if (head.ContentType is { Length: > 0 } type &&
                System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(type, out var parsed))
                body.Headers.ContentType = parsed;
        }

        HttpResponseMessage? response = null;
        try
        {
            try { response = await LocalClient(local.Local).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostics.Log("tunnel", $"write {head.Method} {head.Path.Split('?')[0]} failed locally", ex);
            }

            // Whatever the server did with the body, the rest of it is still on the socket.
            if (body is not null) await body.FinishAsync();

            if (response is null)
            {
                await AnswerAsync(socket, 502, null, null, ct);
                return;
            }

            await using var content = await response.Content.ReadAsStreamAsync(ct);
            await AnswerAsync(socket, (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(), content, ct);
        }
        finally { response?.Dispose(); }
    }

    /// <summary>
    /// Only file operations cross. Above all not /api/session: our loopback server treats a
    /// connection request from this PC as the owner and approves it with every right, so a
    /// host must never be able to reach that through us.
    /// </summary>
    private static readonly string[] Forwardable =
    [
        "/api/ping", "/api/list", "/api/manifest", "/api/drive", "/api/activity", "/api/hash",
        "/api/upload/", "/api/delete", "/api/create", "/api/rename", "/api/transplant",
        "/api/resume/",
    ];

    private static bool IsForwardable(string path)
    {
        var bare = path.Split('?')[0];
        return !bare.Contains("..", StringComparison.Ordinal) &&
               Forwardable.Any(p => p.EndsWith('/') ? bare.StartsWith(p, StringComparison.Ordinal) : bare == p);
    }

    private static async Task AnswerAsync(WebSocket socket, int status, string? type, Stream? content, CancellationToken ct)
    {
        await TunnelFrames.SendAsync(socket, TunnelFrames.BuildHttpHeader(status, type), ct);
        if (content is not null)
        {
            await using var framing = new SocketWriter(socket);
            await content.CopyToAsync(framing, ct);
        }
        await TunnelFrames.SendAsync(socket, TunnelFrames.BuildEnd(), ct);
    }

    /// <summary>
    /// A request body read straight off the tunnel's Chunk frames as the loopback request is
    /// sent. Readable once (HttpClient must never retry it); <see cref="FinishAsync"/> then
    /// discards anything the server did not read, so the socket is back in step.
    /// </summary>
    private sealed class TunnelBody(WebSocket socket, CancellationToken ct) : HttpContent
    {
        private int _started;
        private bool _ended;
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken token)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
                throw new InvalidOperationException("A tunnelled body cannot be sent twice.");
            try
            {
                while (true)
                {
                    using var message = await TunnelFrames.ReceiveAsync(socket, ct)
                                        ?? throw new IOException("The host's connection dropped mid-upload.");
                    if (message.Kind == TunnelFrames.End) { _ended = true; return; }
                    if (message.Kind != TunnelFrames.Chunk) throw new InvalidDataException("Unexpected frame in an upload.");
                    await stream.WriteAsync(message.Memory[1..], token);
                }
            }
            finally { _done.TrySetResult(); }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        public async Task FinishAsync()
        {
            if (Volatile.Read(ref _started) == 1) await _done.Task;
            if (!_ended) await DrainAsync(socket, ct);
            _ended = true;
        }

        public static async Task DrainAsync(WebSocket socket, CancellationToken ct)
        {
            while (true)
            {
                using var message = await TunnelFrames.ReceiveAsync(socket, ct)
                                    ?? throw new IOException("The host's connection dropped mid-upload.");
                if (message.Kind == TunnelFrames.End) return;
            }
        }
    }

    /// <summary>
    /// A text reply (a listing or a manifest), cut into ordinary chunk-sized messages. It used
    /// to go as one message however big, and a manifest of a large tree is hundreds of
    /// megabytes - one allocation that size on each end.
    /// </summary>
    private static async Task SendTextAsync(WebSocket socket, string text, CancellationToken ct)
    {
        await TunnelFrames.SendAsync(socket, TunnelFrames.BuildHeader(200), ct);

        await using (var framing = new SocketWriter(socket))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await framing.WriteAsync(bytes, ct);
        }

        await TunnelFrames.SendAsync(socket, TunnelFrames.BuildEnd(), ct);
    }

    /// <summary>
    /// Reads straight into the message buffer after a one-byte frame type, so each 256 KB
    /// goes out without being copied into a fresh array first.
    /// </summary>
    private static async Task SendRangeAsync(WebSocket socket, Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        long offset, long length, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(TunnelFrames.ChunkSize + 1);
        buffer[0] = TunnelFrames.Chunk;
        try
        {
            long sent = 0;
            while (sent < length)
            {
                var want = (int)Math.Min(TunnelFrames.ChunkSize, length - sent);
                var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(1, want), offset + sent, ct);
                if (read <= 0) throw new IOException("The file got smaller while it was being sent.");

                await TunnelFrames.SendAsync(socket, buffer.AsMemory(0, read + 1), ct);
                sent += read;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>
    /// Many small files in one reply, using the same frame format as the HTTPS side so the
    /// downloader's unpacking code is shared too.
    /// </summary>
    /// <summary>Groups everything one host pulls down the tunnel into a single flow.</summary>
    private static string HostKey(ConnectionInfo host) => $"tunnel:{host.Host}:{host.Port}";

    private static async Task SendBundleAsync(WebSocket socket, ShareSet shares, List<string> paths,
        string peerKey, string label, CancellationToken ct)
    {
        var header = ArrayPool<byte>.Shared.Rent(BundleFormat.MaxPathBytes + 16);
        var buffer = ArrayPool<byte>.Shared.Rent(TunnelFrames.ChunkSize);
        try
        {
            await using var framing = new SocketWriter(socket);

            foreach (var remote in paths)
            {
                if (!shares.TryResolve(remote, out var full) || !shares.Allows(FolderOp.Download, full)) continue;

                // Opened before its header goes out, so a locked or vanished file is left out
                // (the host reports it missing) instead of breaking the stream mid-frame.
                Microsoft.Win32.SafeHandles.SafeFileHandle handle;
                try { handle = BundleIo.OpenForRead(full); }
                catch { continue; }

                using (handle)
                {
                    var length = RandomAccess.GetLength(handle);
                    await BundleIo.WriteHeaderAsync(framing, remote, length, header, ct);
                    await BundleIo.CopyToAsync(handle, framing, length, buffer, null, ct);
                    TrafficMonitor.Add(peerKey, label, TransferDirection.Upload, remote, length, length);
                }
            }

            await BundleIo.WriteEndAsync(framing, header, ct);
            await framing.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Adapts a WebSocket to a Stream so the shared bundle writer can be used unchanged.
    /// Buffers up to a chunk so each socket message carries a useful amount rather than one
    /// per tiny write.
    /// </summary>
    internal sealed class SocketWriter : Stream
    {
        // Byte 0 is the frame type, filled in once; payload accumulates after it, so a full
        // buffer is already a finished message and goes out without another copy.
        private readonly WebSocket _socket;
        private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(TunnelFrames.ChunkSize + 1);
        private readonly int _limit = TunnelFrames.ChunkSize + 1;
        private int _used = 1;

        public SocketWriter(WebSocket socket)
        {
            _socket = socket;
            _buffer[0] = TunnelFrames.Chunk;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken ct = default)
        {
            var offset = 0;
            while (offset < source.Length)
            {
                var take = Math.Min(_limit - _used, source.Length - offset);
                source.Slice(offset, take).CopyTo(_buffer.AsMemory(_used));
                _used += take;
                offset += take;

                if (_used == _limit) await FlushAsync(ct);
            }
        }

        public override async Task FlushAsync(CancellationToken ct)
        {
            if (_used <= 1) return;
            var length = _used;
            _used = 1;
            await TunnelFrames.SendAsync(_socket, _buffer.AsMemory(0, length), ct);
        }

        public override async ValueTask DisposeAsync()
        {
            await FlushAsync(CancellationToken.None);
            ArrayPool<byte>.Shared.Return(_buffer);
            await base.DisposeAsync();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
