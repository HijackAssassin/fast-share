using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace FastShare;

/// <summary>What a host reports when asked who it is.</summary>
public sealed record PingResult(string[] Shares, string? MachineName, double Milliseconds,
    Permissions? Granted = null, bool ShareBackRequested = false, string? HostPeerId = null,
    IReadOnlyCollection<string>? Caps = null, bool SpeedTestRequested = false, string? Os = null,
    bool CancelRequested = false);

public sealed class ApiClient : IFileSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;

    public ConnectionInfo Info { get; }

    public ApiClient(ConnectionInfo info)
    {
        Info = info;
        _base = $"https://{info.Host}:{info.Port}";

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Tuning.Streams * 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(30),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),

            // Without this, a wrong or unforwarded port costs ~21 seconds of TCP SYN retries
            // before Windows gives up, which just reads as the app hanging.
            ConnectTimeout = TimeSpan.FromSeconds(6),

            // TCP keepalive, so a connection whose far end silently vanished (sleep, Wi-Fi
            // drop, a router forgetting the flow) errors out in well under a minute instead of
            // leaving an upload or a ping waiting forever. Costs a few bytes every 15 seconds
            // on an idle socket and nothing on a busy one.
            ConnectCallback = ConnectWithKeepAliveAsync,

            // The data is already compressed. Asking for gzip would burn CPU on both ends to
            // make the payload slightly larger.
            AutomaticDecompression = DecompressionMethods.None,

            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "fastshare",

                // The host's certificate is self-signed - it is reached by raw IP and no CA
                // will issue for one - so there is no chain worth walking. Instead we pin it,
                // the way SSH does: the first connection records the fingerprint, and every
                // connection after that must present the same one. A host that suddenly
                // changes certificate is refused rather than silently trusted.
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;

                    var presented = Convert.ToHexStringLower(SHA256.HashData(cert.GetRawCertData()));

                    // First contact trusts the first certificate - and only that one. Every
                    // later socket this client opens (it opens many) must present the same, so
                    // a first session cannot be taken over halfway through by someone else.
                    var expected = string.IsNullOrEmpty(info.Fingerprint) ? ObservedFingerprint : info.Fingerprint;
                    ObservedFingerprint ??= presented;

                    if (string.IsNullOrEmpty(expected)) return true;   // the very first handshake
                    if (presented.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;

                    // Recorded rather than just refused. A changed certificate is usually a
                    // reinstall, not an attack, and the caller needs to be able to tell the
                    // user which of the two this looks like - otherwise the only way through
                    // is to delete the saved connection, which is not an obvious cure for
                    // "it will not connect".
                    FingerprintMismatch = true;
                    return false;
                },
            },
        };

        _http = new HttpClient(handler)
        {
            // Transfers legitimately run for hours; cancellation is driven by the user.
            Timeout = Timeout.InfiniteTimeSpan,

            // Forcing HTTP/1.1 is the other half of the parallelism story. HTTP/2 would
            // multiplex all eight requests onto ONE TCP connection sharing ONE congestion
            // window - exactly the 554 Mbit/s single-stream case we are escaping. HTTP/1.1
            // gives eight real sockets that recover from loss independently.
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        // No credential yet. The session token only exists once the far side has approved us,
        // which is what ConnectAsync negotiates.
    }

    /// <summary>
    /// A client whose requests travel over something other than its own socket - the tunnel a
    /// peer opened to us (<see cref="PeerTunnel.CreateHttpHandler"/>). No handshake: the far
    /// end attaches the credential itself, so the host part of the URL is never used.
    /// </summary>
    public ApiClient(ConnectionInfo info, HttpMessageHandler transport)
    {
        Info = info;
        _base = "https://tunnel";
        _http = new HttpClient(transport, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    private static async ValueTask<Stream> ConnectWithKeepAliveAsync(SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket,
                System.Net.Sockets.SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp,
                System.Net.Sockets.SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp,
                System.Net.Sockets.SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp,
                System.Net.Sockets.SocketOptionName.TcpKeepAliveRetryCount, 4);

            await socket.ConnectAsync(context.DnsEndPoint, ct);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What the host says it supports beyond the original protocol (<see cref="FastShare.Caps"/>),
    /// learned from its ping. Null until the first ping has answered.
    /// </summary>
    public IReadOnlyCollection<string>? HostCaps { get; private set; }

    /// <summary>Pings once if the host's capabilities are not known yet.</summary>
    public async Task<IReadOnlyCollection<string>> EnsureCapsAsync(CancellationToken ct)
    {
        if (HostCaps is null)
        {
            try { await PingAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { HostCaps = []; }   // unreachable right now; assume the original protocol
        }
        return HostCaps ?? [];
    }

    /// <summary>Fingerprint the host actually presented, recorded on the first handshake.</summary>
    public string? ObservedFingerprint { get; private set; }

    /// <summary>
    /// True if the host presented a certificate that did not match the pinned one. Distinct
    /// from any other connection failure, because it is the one the user can actually act on.
    /// </summary>
    public bool FingerprintMismatch { get; private set; }

    /// <summary>The fingerprint we were expecting, for showing alongside the one we got.</summary>
    public string ExpectedFingerprint => Info.Fingerprint;

    /// <summary>What the far side granted us, once approved.</summary>
    public SessionGrant? Grant { get; private set; }

    /// <summary>
    /// Asks to be let in, then waits for the answer.
    ///
    /// The prompt sits on a person's screen, so this polls rather than hanging on one long
    /// request - a socket held open for minutes is the first thing a router or proxy kills.
    /// </summary>
    public async Task<SessionGrant> ConnectAsync(string peerId, string username, string machineName,
        CancellationToken ct, ECDsa? identity = null, string? codeSecret = null)
    {
        var request = new SessionRequest(peerId, username, machineName, Code: codeSecret, Os: AppVersion.Device);

        // Prove the PeerId, if this host can check it: sign its challenge together with the
        // certificate it just presented. An older host has no challenge endpoint and gets the
        // plain request it has always had.
        if (identity is not null)
        {
            try
            {
                using var challenge = await _http.GetAsync($"{_base}/api/session/challenge", ct);
                if (challenge.IsSuccessStatusCode && ObservedFingerprint is { Length: > 0 } fingerprint)
                {
                    using var document = JsonDocument.Parse(await challenge.Content.ReadAsStringAsync(ct));
                    if (document.RootElement.TryGetProperty("nonce", out var n) && n.GetString() is { Length: > 0 } nonce)
                    {
                        request = request with
                        {
                            PublicKey = PeerIdentity.PublicKeyOf(identity),
                            Nonce = nonce,
                            Signature = PeerIdentity.Sign(identity, peerId, nonce, fingerprint),
                        };
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex) when (ex.StatusCode is not null) { /* older host */ }
        }

        using var opening = await _http.PostAsJsonAsync($"{_base}/api/session/request", request, ct);
        opening.EnsureSuccessStatusCode();

        var status = await opening.Content.ReadFromJsonAsync<SessionResponse>(ct)
                     ?? throw new InvalidOperationException("The other PC sent an unreadable reply.");

        while (status.State == SessionStates.Pending)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), ct);

            status = await _http.GetFromJsonAsync<SessionResponse>(
                         $"{_base}/api/session/status?id={Uri.EscapeDataString(status.RequestId ?? "")}", ct)
                     ?? throw new InvalidOperationException("The other PC stopped answering.");
        }

        if (status.State != SessionStates.Approved || status.Grant is null)
        {
            throw new UnauthorizedAccessException(status.Reason switch
            {
                DenyReasons.Blocked =>
                    "Blocked — you cannot connect to this person unless they unblock you.",
                DenyReasons.Kicked =>
                    "You have been kicked. You can reconnect after this person restarts their app.",
                DenyReasons.Busy =>
                    "They already have a connection request waiting from your network. Try again once it has been answered.",
                DenyReasons.CodeChanged =>
                    "That code does not match the PC that answered. Either they have made a new code (ask them for their current one), " +
                    "or another PC in their house answers on that port - each PC that should be reachable from the internet needs its own forwarded port.",
                _ => "The other PC declined the connection. They can allow it from their end and you can try again.",
            });
        }

        Grant = status.Grant;
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Grant.Token);

        return Grant;
    }

    /// <summary>Round-trip check. Returns the share names and the measured latency.</summary>
    /// <summary>The round trip measured on the previous ping, carried back so the host can
    /// show a real figure for us - it cannot time us itself, since we are the one connecting.</summary>
    private double _lastRttMs;

    public async Task<PingResult> PingAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        // Report the previous round trip on this request's query string. The host reads it and
        // shows it in its connected-users list; there is no other way for it to learn our RTT.
        var url = _lastRttMs > 0
            ? $"{_base}/api/ping?rtt={_lastRttMs.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}"
            : $"{_base}/api/ping";

        // ConfigureAwait(false): the stopwatch is meant to time the network. Resuming on the UI
        // thread between the two awaits added however long that thread was busy to the figure.
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        stopwatch.Stop();

        _lastRttMs = stopwatch.Elapsed.TotalMilliseconds;

        using var document = JsonDocument.Parse(body);
        var shares = document.RootElement.TryGetProperty("shares", out var array)
            ? array.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
            : [];

        var machine = document.RootElement.TryGetProperty("machine", out var name) ? name.GetString() : null;
        var os = document.RootElement.TryGetProperty("os", out var osName) && osName.ValueKind == JsonValueKind.String
            ? osName.GetString()
            : null;

        // The host can revoke or widen our rights at any moment, so every ping carries the
        // current set rather than us trusting what we were given when we connected.
        var granted = document.RootElement.TryGetProperty("permissions", out var perms)
                      && perms.ValueKind == JsonValueKind.Object
            ? perms.Deserialize<Permissions>()
            : null;

        var hostPeerId = document.RootElement.TryGetProperty("peerId", out var pid)
                         && pid.ValueKind == JsonValueKind.String
            ? pid.GetString()
            : null;

        var asked = document.RootElement.TryGetProperty("wantsShareBack", out var wants)
                    && wants.ValueKind is JsonValueKind.True;

        var speedTest = document.RootElement.TryGetProperty("wantsSpeedTest", out var speed)
                        && speed.ValueKind is JsonValueKind.True;

        var cancel = document.RootElement.TryGetProperty("cancelTransfers", out var cancelFlag)
                     && cancelFlag.ValueKind is JsonValueKind.True;

        // Absent from older hosts, which is exactly "none of the newer features".
        var caps = document.RootElement.TryGetProperty("caps", out var capList)
                   && capList.ValueKind == JsonValueKind.Array
            ? capList.EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        HostCaps = caps;

        return new PingResult(shares, machine, stopwatch.Elapsed.TotalMilliseconds, granted, asked, hostPeerId, caps,
            speedTest, Os: os, CancelRequested: cancel);
    }

    /// <summary>
    /// Tells the host a transfer is beginning or ending, and how big it is. The host has no
    /// other way to know the total of what we are about to pull or push - it only sees a
    /// stream of range and bundle requests - so this is what lets it show a real percentage
    /// and treat the whole thing as one batch. Best-effort: a failure here must never sink
    /// the transfer itself.
    /// </summary>
    public enum ResumeAnswer { Agreed, Declined, NoAnswer, NotSupported }

    /// <summary>
    /// Puts a "not enough space - Retry or Discard?" question on the other PC's screen and returns
    /// at once with its id (null if their build cannot be asked). Answered via <see cref="QuestionStateAsync"/>.
    /// </summary>
    public async Task<string?> AskSpaceAsync(string what)
    {
        try
        {
            using var caps = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!(await EnsureCapsAsync(caps.Token)).Contains(Caps.SpaceAsk)) return null;
            using var response = await _http.PostAsync($"{_base}/api/resume/ask?kind=space&what={Uri.EscapeDataString(what)}", null);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("id").GetString();
        }
        catch { return null; }
    }

    /// <summary>"pending", "yes", "no", "withdrawn" or "unknown" (also when they cannot be reached).</summary>
    public async Task<string> QuestionStateAsync(string id)
    {
        try
        {
            using var poll = await _http.GetAsync($"{_base}/api/resume/answer?id={id}");
            using var answer = JsonDocument.Parse(await poll.Content.ReadAsStringAsync());
            return answer.RootElement.GetProperty("state").GetString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    public async Task WithdrawQuestionAsync(string id)
    {
        try { using var _ = await _http.PostAsync($"{_base}/api/resume/withdraw?id={id}", null); }
        catch { }
    }

    /// <summary>
    /// Asks the other PC whether they agree to resume an unfinished transfer, and waits up to two
    /// minutes for their answer. Builds before 2.4.29 cannot be asked (NotSupported).
    /// </summary>
    public async Task<ResumeAnswer> AskResumeAsync(string what)
    {
        try
        {
            using var caps = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!(await EnsureCapsAsync(caps.Token)).Contains(Caps.Resume)) return ResumeAnswer.NotSupported;

            using var response = await _http.PostAsync($"{_base}/api/resume/ask?what={Uri.EscapeDataString(what)}", null);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var id = doc.RootElement.GetProperty("id").GetString();

            var until = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(1000);
                using var poll = await _http.GetAsync($"{_base}/api/resume/answer?id={id}");
                using var answer = JsonDocument.Parse(await poll.Content.ReadAsStringAsync());
                switch (answer.RootElement.GetProperty("state").GetString())
                {
                    case "yes": return ResumeAnswer.Agreed;
                    case "no": return ResumeAnswer.Declined;
                    case "unknown": return ResumeAnswer.NoAnswer;
                }
            }
            return ResumeAnswer.NoAnswer;
        }
        catch
        {
            return ResumeAnswer.NoAnswer;
        }
    }

    /// <summary>
    /// Tells the host where a transfer with them stands in our queue (position 1 = next), or
    /// that it left the queue without running (position 0). Only to hosts that understand it;
    /// an older one would read it as a transfer starting.
    /// </summary>
    public async Task AnnounceQueuedAsync(bool downloading, int position)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!(await EnsureCapsAsync(deadline.Token)).Contains(Caps.Queue)) return;
            var action = downloading ? "download" : "upload";
            var url = position > 0
                ? $"{_base}/api/activity?action={action}&phase=queued&position={position}"
                : $"{_base}/api/activity?action={action}&phase=unqueued";
            using var response = await _http.PostAsync(url, null, deadline.Token);
            _ = response.StatusCode;
        }
        catch
        {
            // Best effort: they just will not see their place in line.
        }
    }

    /// <summary>
    /// Tells the host what we are doing before a transfer starts (cap "prepare", 2.4.40):
    /// "location"/"picking" (a dialog is open), "listing", "overwrite" (asking about files that are
    /// already there), "nospace" (with need/free bytes) or "" for "nothing any more".
    /// Best effort, like the queue position.
    /// </summary>
    public async Task AnnouncePrepareAsync(bool downloading, string stage, long need = 0, long free = 0)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!(await EnsureCapsAsync(deadline.Token)).Contains(Caps.Prepare)) return;
            var action = downloading ? "download" : "upload";
            var url = $"{_base}/api/activity?action={action}&phase=prepare&stage={Uri.EscapeDataString(stage)}";
            if (stage == "nospace") url += $"&need={need}&free={free}";
            using var response = await _http.PostAsync(url, null, deadline.Token);
            _ = response.StatusCode;
        }
        catch
        {
            // They just see less detail about what is happening.
        }
    }

    /// <param name="result">At the end: "done", "cancelled" or "failed", so the host does not
    /// read a transfer that stopped halfway as one that arrived.</param>
    public async Task AnnounceAsync(bool downloading, long totalBytes, int totalFiles, bool starting,
        CancellationToken ct, string? localDrive = null, string? result = null)
    {
        try
        {
            var action = downloading ? "download" : "upload";
            var phase = starting ? "start" : "end";
            var url = $"{_base}/api/activity?action={action}&phase={phase}&bytes={totalBytes}&files={totalFiles}";

            // Our own drive for this transfer, so the host's peer-driven row can show our end
            // as well as theirs. Only worth sending when starting, and only when we know it.
            if (starting && !string.IsNullOrWhiteSpace(localDrive))
                url += $"&drive={Uri.EscapeDataString(localDrive)}";
            if (!starting && result is not null) url += $"&result={result}";

            using var response = await _http.PostAsync(url, null, ct);
            _ = response.StatusCode;
        }
        catch
        {
            // The host just will not get a percentage for this one. Not worth failing over.
        }
    }

    /// <summary>
    /// Asks the host what kind of disk a shared path lives on ("HDD"/"SSD"/"NVMe"), so an
    /// upload can log "SSD → HDD". Best-effort: the destination end simply stays unknown if the
    /// host is an older build or cannot tell.
    /// </summary>
    public async Task<string?> DriveForAsync(string path, CancellationToken ct) =>
        (await DriveInfoForAsync(path, ct)).Drive;

    /// <summary>The disk kind and its free bytes (null from builds before 2.4.28, or if unknown).</summary>
    public async Task<(string? Drive, long? Free)> DriveInfoForAsync(string path, CancellationToken ct)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<DriveResult>(
                $"{_base}/api/drive?path={Uri.EscapeDataString(path)}", ct);
            return (result?.Drive, result?.Free);
        }
        catch
        {
            return (null, null);
        }
    }

    private sealed record DriveResult(
        [property: System.Text.Json.Serialization.JsonPropertyName("drive")] string? Drive,
        [property: System.Text.Json.Serialization.JsonPropertyName("free")] long? Free = null);

    public async Task<List<Entry>> ListAsync(string path, CancellationToken ct) =>
        await _http.GetFromJsonAsync<List<Entry>>(
            $"{_base}/api/list?path={Uri.EscapeDataString(path)}", ct) ?? [];

    public async Task<Manifest> ManifestAsync(string path, CancellationToken ct) =>
        await _http.GetFromJsonAsync<Manifest>(
            $"{_base}/api/manifest?path={Uri.EscapeDataString(path)}", ct) ?? new Manifest([], 0);

    /// <summary>
    /// Opens a byte range of one file, for pulling large files as parallel chunks.
    ///
    /// The reply is checked before a byte is trusted: it must be a real partial response for
    /// exactly the range asked, of a file that is still the size the manifest said. A file that
    /// grew or shrank since the listing used to be pulled at the old size and reported as a
    /// success - silently truncated.
    /// </summary>
    public async Task<Stream> OpenRangeAsync(string path, long offset, long length, long fileSize,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_base}/api/file?path={Uri.EscapeDataString(path)}");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();

            var range = response.Content.Headers.ContentRange;
            var whole = response.StatusCode == HttpStatusCode.OK && offset == 0 &&
                        response.Content.Headers.ContentLength == length;

            if (!whole && (response.StatusCode != HttpStatusCode.PartialContent || range is null ||
                           range.From != offset || range.To != offset + length - 1))
            {
                throw new IOException("The other PC answered with the wrong part of the file.");
            }

            var total = range?.Length ?? response.Content.Headers.ContentLength;
            if (fileSize >= 0 && total is { } now && now != fileSize)
                throw new IOException("The file changed on the other PC while it was being copied.");

            return await response.Content.ReadAsStreamAsync(ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Opens a framed stream carrying many small files back to back.</summary>
    public async Task<Stream> OpenBundleAsync(List<string> paths, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/bundle")
        {
            Content = JsonContent.Create(new BundleRequest(paths)),
        };

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>The host's SHA-256 of each path ("" where it could not or may not read it).</summary>
    public async Task<Dictionary<string, string>> HashAsync(List<string> paths, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"{_base}/api/hash", new HashRequest(paths), ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.RootElement.GetProperty("hashes").EnumerateObject())
            result[entry.Name] = entry.Value.GetString() ?? "";
        return result;
    }

    // ---- speed test (only when the host lists Caps.SpeedTest) --------------

    /// <summary>Bytes from the host's memory for about <paramref name="seconds"/>; no disk either end.</summary>
    public async Task<Stream> SpeedDownAsync(int seconds, CancellationToken ct)
    {
        var response = await _http.GetAsync($"{_base}/api/speed/down?seconds={seconds}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Sends the body to be thrown away; returns how many bytes the host says arrived.</summary>
    public async Task<long> SpeedUpAsync(HttpContent content, CancellationToken ct)
    {
        using var response = await _http.PostAsync($"{_base}/api/speed/up", content, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("bytes").GetInt64();
    }

    /// <summary>Best effort: hands our measurement to the host, which usually asked for it.</summary>
    public async Task SendSpeedResultAsync(string text)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await _http.PostAsync($"{_base}/api/speed/result",
                new StringContent(text), deadline.Token);
        }
        catch { /* the result is in our own log either way */ }
    }

    // ---- uploads ----------------------------------------------------------

    public async Task BeginUploadAsync(string remotePath, long size, bool part, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"{_base}/api/upload/begin",
            new BeginUploadRequest(remotePath, size, part), ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task UploadChunkAsync(string remotePath, long offset, bool part, HttpContent content,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"{_base}/api/upload/chunk?path={Uri.EscapeDataString(remotePath)}&offset={offset}" +
            (part ? "&part=1" : ""))
        {
            Content = content,
        };
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Moves a finished upload into place on the host. Only after this is the old file gone.</summary>
    public async Task CommitUploadAsync(string remotePath, long size, long modifiedUnixMs, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"{_base}/api/upload/commit",
            new CommitUploadRequest(remotePath, size, modifiedUnixMs), ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ProblemDetailAsync(response, ct);
            throw new IOException(detail ?? $"The other PC could not finish saving it ({(int)response.StatusCode}).");
        }
    }

    /// <summary>
    /// Dev mode: hands the host our latest log lines. True if kept, false if the host's dev
    /// mode is off, null if it could not be reached this time.
    /// </summary>
    public async Task<bool?> SendDiagnosticsAsync(IReadOnlyList<string> lines)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await _http.PostAsJsonAsync($"{_base}/api/diag", lines, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Forbidden) return false;
            return response.IsSuccessStatusCode ? true : null;
        }
        catch { return null; }
    }

    /// <summary>Best effort: throws away the host's temp file for an upload that failed.</summary>
    public async Task AbortUploadAsync(string remotePath)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await _http.PostAsJsonAsync($"{_base}/api/upload/abort",
                new AbortUploadRequest(remotePath), deadline.Token);
        }
        catch { /* the host sweeps nothing, but a stray temp file is hidden and harmless */ }
    }

    /// <summary>
    /// Sends one bundle of small files. Returns the files the host could not keep (locked,
    /// read-only, refused path) - reported per file on a 207 - so they fail individually
    /// instead of the whole bundle looking fine.
    /// </summary>
    public async Task<IReadOnlyList<string>> UploadBundleAsync(string remoteDirectory, HttpContent content,
        bool v2, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_base}/api/upload/bundle?dir={Uri.EscapeDataString(remoteDirectory)}" + (v2 ? "&v=2" : ""))
        {
            Content = content,
        };
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        if (response.StatusCode != HttpStatusCode.MultiStatus) return [];

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty("errors", out var errors)
                ? errors.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : [];
        }
        catch { return ["Some files could not be saved on the other PC."]; }
    }

    private static async Task<string?> ProblemDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
        }
        catch { return null; }
    }

    public async Task<string?> DeleteAsync(List<string> paths, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"{_base}/api/delete",
            new DeleteRequest(paths), ct);

        return await OutcomeAsync(response, "delete", ct);
    }

    /// <summary>
    /// Turns a reply into either null (everything worked) or a message worth showing.
    ///
    /// 207 Multi-Status is the trap here: it lives in the 2xx range, so IsSuccessStatusCode
    /// reports true and a partial failure - three of ten files undeletable, a folder copied
    /// into itself - would be reported to the user as complete success.
    /// </summary>
    private static async Task<string?> OutcomeAsync(HttpResponseMessage response, string verb,
        CancellationToken ct)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.MultiStatus)
        {
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (document.RootElement.TryGetProperty("errors", out var errors))
                {
                    var listed = errors.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                    if (listed.Count == 1) return listed[0];
                    if (listed.Count > 1)
                        return $"{listed.Count} item(s) failed — {string.Join("; ", listed.Take(3))}";
                }
            }
            catch { /* fall through to the generic wording */ }

            return $"Some items could not be {verb}d.";
        }

        return response.IsSuccessStatusCode ? null : Describe(response.StatusCode, verb);
    }

    /// <summary>
    /// Cheap liveness check for the Connections list. Builds a throwaway client so a dead
    /// host never poisons the pool of a live one, and gives up fast rather than blocking
    /// the status refresh behind one unreachable machine.
    /// </summary>
    public static async Task<bool> IsOnlineAsync(ConnectionInfo info, TimeSpan timeout)
    {
        try
        {
            using var probe = new ApiClient(info);
            using var cts = new CancellationTokenSource(timeout);
            await probe.PingAsync(cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> CreateAsync(string path, bool isDirectory, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"{_base}/api/create",
            new CreateRequest(path, isDirectory), ct);

        return await OutcomeAsync(response, "create", ct);
    }

    public async Task<string?> RenameAsync(string path, string name, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"{_base}/api/rename",
            new RenameRequest(path, name), ct);

        return await OutcomeAsync(response, "rename", ct);
    }

    /// <summary>Copies or moves entries into a folder on the far side.</summary>
    public async Task<string?> TransplantAsync(List<string> paths, string destination, bool move,
        CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"{_base}/api/transplant",
            new TransplantRequest(paths, destination, move), ct);

        return await OutcomeAsync(response, move ? "move" : "copy", ct);
    }

    private static string Describe(System.Net.HttpStatusCode code, string verb) => code switch
    {
        System.Net.HttpStatusCode.Forbidden => $"You do not have permission to {verb} there (the folder, or your access, does not allow it).",
        System.Net.HttpStatusCode.Conflict => "Something with that name already exists.",
        System.Net.HttpStatusCode.BadRequest => $"That {verb} was refused as invalid.",
        _ => $"The other PC refused the {verb} ({(int)code}).",
    };

    /// <summary>
    /// Parks until the host ends our session, then returns. The request carries no payload and
    /// sends nothing until that happens, so this is how a disconnect is noticed at once rather
    /// than whenever the next ping happens to fail.
    ///
    /// Returns true if the host really ended it, false if the wait itself failed - a dropped
    /// connection, a restarted host - which the caller treats the same way but need not report
    /// as a deliberate disconnection.
    /// </summary>
    /// <summary>What a parked watch came back to tell us.</summary>
    public enum WatchResult { Lost, Ended, Changed, HostStopped, HostClosed }

    public async Task<WatchResult> WatchAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/watch");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            // A 410 can mean "the host disconnected just me" or "the host stopped sharing
            // entirely". The header tells the two apart so the far side can show the right
            // message - only a full stop warrants the "X stopped sharing" popup.
            if (response.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                var reason = response.Headers.TryGetValues("X-FastShare-End", out var values)
                    ? values.FirstOrDefault() ?? ""
                    : "";

                return reason.ToLowerInvariant() switch
                {
                    DenyReasons.Shutdown => WatchResult.HostStopped,
                    DenyReasons.Closed => WatchResult.HostClosed,
                    _ => WatchResult.Ended,
                };
            }

            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.ResetContent => WatchResult.Changed,
                _ => WatchResult.Lost,
            };
        }
        catch
        {
            return WatchResult.Lost;
        }
    }

    /// <summary>
    /// Tells the host we are leaving, so they drop us at once instead of waiting for the
    /// session to go stale. Given a short leash: this runs while the window is closing and
    /// must never hold that up.
    /// </summary>
    public async Task LeaveAsync(string why = "closed")
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await _http.PostAsync($"{_base}/api/session/leave?why={why}", null, deadline.Token);
            _ = response.StatusCode;
        }
        catch
        {
            // Already gone, or no time to say so. The host's sweeper handles it either way.
        }
    }

    public void Dispose() => _http.Dispose();
}
