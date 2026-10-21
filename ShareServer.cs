using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FastShare;

/// <summary>
/// Hosts the shared folders. Started and stopped from the Share tab.
/// </summary>
public sealed class ShareServer
{
    private WebApplication? _app;

    public bool IsRunning => _app is not null;
    // Volatile and read per request, so folders can be added or removed while sharing is live.
    private volatile ShareSet _shares = new([]);
    private string _releasePath = "";
    public ShareSet Shares => _shares;
    public ConnectionInfo? Info { get; private set; }

    /// <summary>The ten-character code to hand out, e.g. <c>7K2MQ4XB9T</c>.</summary>
    public string? Code { get; private set; }

    /// <summary>Who is connected and what they may do. The UI subscribes for approval prompts.</summary>
    public SessionStore Sessions { get; }

    /// <summary>
    /// False for the private loopback server that carries a host's writes arriving over our
    /// tunnel (<see cref="ForTunnel"/>). It borrows the main server's session list, so it must
    /// not end those sessions when it stops or claim the list's share callback as its own.
    /// </summary>
    public bool OwnsSessions { get; }

    /// <summary>Where the shares come from when they are not this server's own (the tunnel host).</summary>
    public Func<ShareSet>? SharesSource { get; init; }

    /// <summary>Tunnels peers have opened back to us, so we can browse their folders too.</summary>
    public TunnelRegistry Tunnels { get; } = new();

    /// <summary>A peer's speed-test figures arrived (raised off the UI thread).</summary>
    public event Action<string>? SpeedTestReported;

    /// <summary>Dev mode on this PC: keep the diagnostics connected peers send.</summary>
    public volatile bool DiagnosticsEnabled;

    public ShareServer() : this(new SessionStore(), owns: true) { }

    private ShareServer(SessionStore sessions, bool owns)
    {
        Sessions = sessions;
        OwnsSessions = owns;
        if (!owns) return;

        // A tunnel is opened on the strength of a session. When that session ends, so does the
        // tunnel - a kicked or blocked peer used to keep their sockets parked here regardless.
        // (A reverse session is the other way round: it rides on OUR tunnel to them.)
        Sessions.SessionEnded += session =>
        {
            if (!session.IsReverse) Tunnels.Drop(session.PeerId);
        };
    }

    /// <summary>
    /// True when the listener is bound to loopback only - started quietly so this PC can
    /// browse its own shares, without being open to anyone else and without the Share tab
    /// claiming to be sharing.
    /// </summary>
    public bool IsLoopbackOnly { get; private set; }

    /// <summary>True only when actually offering folders to the outside world.</summary>
    public bool IsSharing => _app is not null && !IsLoopbackOnly;

    public async Task StartAsync(AppConfig config, bool loopbackOnly = false)
    {
        if (_app is not null) return;
        IsLoopbackOnly = loopbackOnly;

        if (string.IsNullOrEmpty(config.Token)) config.Token = AppConfig.NewToken();

        var certPassword = config.CertPassword;
        var certificate = CertStore.LoadOrCreate(AppConfig.CertPath, ref certPassword);
        config.CertPassword = certPassword;
        // Detected every start, not once: a new IP from the ISP would otherwise go into every
        // code from then on. A failed detection keeps the last known address.
        if (!loopbackOnly) config.PublicHost = await DetectPublicHostAsync() ?? config.PublicHost;
        config.Save();

        var shares = new ShareSet(config.Folders, config.FolderRules);
        var tokenBytes = Encoding.UTF8.GetBytes(config.Token);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();       // per-request logging is pure overhead here
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AllowSynchronousIO = false;
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = null;      // uploads stream; no fixed ceiling
            kestrel.Limits.MinRequestBodyDataRate = null;  // a slow client should not be killed
            // Loopback mode binds 127.0.0.1 on an ephemeral port: nothing outside this PC can
            // reach it, and it cannot collide with the real share port if that is in use.
            kestrel.Listen(loopbackOnly ? IPAddress.Loopback : IPAddress.Any,
                loopbackOnly ? 0 : config.Port, listen =>
            {
                // HTTP/1.1 only, deliberately. Over TLS, Kestrel would otherwise advertise
                // HTTP/2 via ALPN, and HTTP/2 multiplexes every request onto a SINGLE TCP
                // connection with a single congestion window - collapsing eight parallel
                // streams back to one. That is the 554 Mbit/s case instead of 909.
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificate);
            });
        });

        var app = builder.Build();

        // Tell the update server (if it is open) that this is taking the port, then give it a
        // moment to step aside - it checks once a second.
        if (!loopbackOnly)
        {
            _sharingMarker ??= new Mutex(initiallyOwned: false, Updater.SharingMutexName);
            for (var waited = 0; waited < 40 && !PortIsFree(config.Port); waited++) await Task.Delay(250);
        }

        // Needed before the tunnel endpoint can accept an upgrade.
        app.UseWebSockets();

        // Authentication and authorisation in one place.
        //
        // Codes are short and guessable now, so possession of one grants nothing: every
        // request outside the handshake needs a session token issued by an explicit approval.
        // Permissions are checked here rather than inside each handler so a new endpoint
        // cannot accidentally ship without a gate.
        app.Use(async (context, next) =>
        {
            // Lower-cased before any comparison. Routing matches endpoints case-insensitively,
            // so an ordinal check against the raw path let "/API/delete" skip the permission
            // gate below entirely and still reach the delete handler.
            var path = (context.Request.Path.Value ?? "").ToLowerInvariant();

            // The handshake is the door - it has to be reachable without a session. So is
            // ping, which the far side uses to show whether this host is online at all.
            // Updates are served without a session: a friend on a fresh copy may not have an
            // approved session yet, and the build is not secret - it is the app itself. The
            // endpoint only ever hands out the fixed release folder, read-only.
            if (path.StartsWith("/api/session", StringComparison.Ordinal) ||
                path.StartsWith("/api/ping", StringComparison.Ordinal) ||
                path.StartsWith("/api/update", StringComparison.Ordinal))
            {
                // Nobody here has been approved yet, so there is no reason to accept more than
                // a small JSON body. Uploads from approved peers stay unlimited (below).
                if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()
                        is { IsReadOnly: false } limit)
                {
                    limit.MaxRequestBodySize = 64 * 1024;
                }

                await next();
                return;
            }

            var header = context.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            var token = header.StartsWith(prefix, StringComparison.Ordinal)
                ? header[prefix.Length..]
                : "";

            var session = Sessions.Lookup(token);
            if (session is null)
            {
                context.Response.StatusCode = 401;
                return;
            }

            // Watching for your own session ending needs no permission; it reveals nothing.
            if (path.StartsWith("/api/watch", StringComparison.Ordinal))
            {
                context.Items["session"] = session;
                await next();
                return;
            }

            var granted = session.Permissions;
            var allowed =
                path.StartsWith("/api/file", StringComparison.Ordinal) ||
                path.StartsWith("/api/bundle", StringComparison.Ordinal) ||
                path.StartsWith("/api/hash", StringComparison.Ordinal) ? granted.Download
                : path.StartsWith("/api/upload", StringComparison.Ordinal) ? granted.Send
                : path.StartsWith("/api/delete", StringComparison.Ordinal) ? granted.Delete
                // Rearranging is its own permission. It used to ride on Send, which meant
                // letting someone add files also let them rename and move everything already
                // there - two quite different amounts of trust.
                : path.StartsWith("/api/create", StringComparison.Ordinal)
                  || path.StartsWith("/api/rename", StringComparison.Ordinal)
                  || path.StartsWith("/api/transplant", StringComparison.Ordinal) ? granted.Modify
                : true;   // browsing is implied by having been approved at all

            if (!allowed)
            {
                context.Response.StatusCode = 403;
                return;
            }

            // We cancelled their transfer and they have not stopped by themselves (an older
            // build): turn the data away for a little while. Aborting an upload still works, so
            // they can clean up what they had started.
            if (Sessions.RefusesTransfers(session.PeerId) &&
                (path.StartsWith("/api/file", StringComparison.Ordinal) ||
                 path.StartsWith("/api/bundle", StringComparison.Ordinal) ||
                 path.StartsWith("/api/upload/begin", StringComparison.Ordinal) ||
                 path.StartsWith("/api/upload/bundle", StringComparison.Ordinal) ||
                 path.StartsWith("/api/upload/commit", StringComparison.Ordinal)))
            {
                context.Response.StatusCode = 409;
                context.Response.Headers["X-FastShare-Cancelled"] = "1";
                return;
            }

            context.Items["session"] = session;
            await next();
        });

        if (OwnsSessions)
            Sessions.AvailableShares = () =>
                _shares.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        _releasePath = config.ReleasePath;
        MapUpdateEndpoints(app, () => _releasePath);

        MapTunnelEndpoint(app, Tunnels);
        MapDiagnosticsEndpoint(app, () => DiagnosticsEnabled);
        SpeedTest.MapEndpoints(app, text =>
        {
            Diagnostics.Log("speedtest", text);
            SpeedTestReported?.Invoke(text);
        });
        MapSessionEndpoints(app, Sessions, CurrentShares, config.PeerId, CertStore.Fingerprint(certificate));
        MapEndpoints(app, CurrentShares);

        // Publish the share set BEFORE the listener opens; otherwise a request arriving in
        // that window would be answered from an empty set.
        _shares = shares;

        await app.StartAsync();

        _app = app;

        if (loopbackOnly)
        {
            // Kestrel picked the port, so read back what it actually bound. No code is issued:
            // there is nothing here for anyone else to connect to.
            Info = new ConnectionInfo("127.0.0.1", BoundPort(app) ?? config.Port, config.Token,
                CertStore.Fingerprint(certificate), Environment.MachineName);
            Code = null;
            return;
        }

        var host = string.IsNullOrWhiteSpace(config.PublicHostOverride)
            ? config.PublicHost ?? "0.0.0.0"
            : config.PublicHostOverride.Trim();

        Info = new ConnectionInfo(host, config.Port, config.Token,
            CertStore.Fingerprint(certificate), Environment.MachineName);
        Code = ShortCode.Build(host, config.Port, config.CodeSecret);

        if (OwnsSessions)
        {
            var settings = config;
            Sessions.CodeSecret = () => settings.CodeSecret;
            Sessions.CurrentCode = () => Code;
        }
    }


    private Mutex? _sharingMarker;

    /// <summary>Makes a new code (after "New code"), without restarting or dropping anyone.</summary>
    public void Recode(AppConfig config)
    {
        if (Info is null || IsLoopbackOnly) return;
        Code = ShortCode.Build(Info.Host, Info.Port, config.CodeSecret);
    }

    private static bool PortIsFree(int port)
    {
        try
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Any, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch { return false; }
    }

    /// <summary>The port Kestrel actually bound, needed when we asked it to choose one.</summary>
    private static int? BoundPort(WebApplication app)
    {
        // A feature of the server, not a registered service: asking the container for it always
        // returned null, so loopback mode reported the configured port instead of the one bound.
        var server = app.Services.GetService(typeof(Microsoft.AspNetCore.Hosting.Server.IServer))
            as Microsoft.AspNetCore.Hosting.Server.IServer;
        var addresses = server?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();

        var url = addresses?.Addresses.FirstOrDefault();
        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Port : null;
    }

    /// <summary>Swaps the shared folders without dropping connections or reissuing the code.</summary>
    public void UpdateShares(IEnumerable<string> folders, IEnumerable<FolderRule>? rules = null)
    {
        _shares = new ShareSet(folders, rules);

        // Adding or removing a shared folder changes what every peer can see, so nudge them
        // all rather than letting each discover it on its own schedule.
        foreach (var session in Sessions.Active) session.NotifyChanged();
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        Info = null;
        Code = null;
        _shares = new ShareSet([]);
        IsLoopbackOnly = false;
        _sharingMarker?.Dispose();
        _sharingMarker = null;

        // End every session and drop every tunnel. Without this the UI went on reporting
        // "X is connected to you" after sharing had stopped, because the session list was
        // never cleared - the listener was gone but the records outlived it.
        if (OwnsSessions)
        {
            Sessions.RevokeAll();
            Tunnels.DropAll();
        }

        try { await app.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
        await app.DisposeAsync();
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// Accepts the sockets a peer opens back to us. The request handler parks here for as long
    /// as the socket lives - that is what keeps the connection available for us to send
    /// requests down.
    /// </summary>
    private static void MapTunnelEndpoint(WebApplication app, TunnelRegistry tunnels)
    {
        app.Map("/api/tunnel", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            if (context.Items["session"] is not SessionStore.Session session)
            {
                context.Response.StatusCode = 401;
                return;
            }

            var tunnel = tunnels.GetOrCreate(session.PeerId, session.Username);

            // Their client opens eight. Refuse beyond a generous margin, so a misbehaving peer
            // cannot park an unbounded number of sockets here.
            if (tunnel.SocketCount >= PeerTunnel.MaxSockets)
            {
                context.Response.StatusCode = 429;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                KeepAliveInterval = TunnelFrames.KeepAliveInterval,
                KeepAliveTimeout = TunnelFrames.KeepAliveTimeout,
            });

            await tunnel.AddAsync(socket, context.RequestAborted);
            tunnels.Notify();
        });
    }

    /// <summary>
    /// Dev mode: an approved peer's log lines, kept in diag\&lt;name&gt;-&lt;id&gt;.log beside our own
    /// log.txt. Refused (403) unless dev mode is on here too. Needs an approved session like
    /// every other endpoint; each file rolls over at 50 MB.
    /// </summary>
    private static readonly Lock DiagGate = new();

    private static void MapDiagnosticsEndpoint(WebApplication app, Func<bool> enabled)
    {
        app.MapPost("/api/diag", (HttpContext context, List<string> lines) =>
        {
            if (!enabled()) return Results.StatusCode(403);
            if (context.Items["session"] is not SessionStore.Session session) return Results.Unauthorized();

            try
            {
                var safe = new string(session.DisplayName.Where(char.IsLetterOrDigit).ToArray());
                var dir = Path.Combine(AppConfig.Dir, "diag");
                var file = Path.Combine(dir, $"{(safe.Length == 0 ? "peer" : safe)}-{session.PeerId[..Math.Min(8, session.PeerId.Length)]}.log");

                lock (DiagGate)
                {
                    Directory.CreateDirectory(dir);
                    if (File.Exists(file) && new FileInfo(file).Length > 50L * 1024 * 1024)
                        File.Move(file, file + ".old", overwrite: true);
                    File.AppendAllLines(file, lines);
                }
                return Results.NoContent();
            }
            catch (Exception ex) { return Problem(ex); }
        });
    }

    /// <summary>The handshake, plus the one probe that works without a session.</summary>
    /// <summary>
    /// Serves the latest build to anyone who presses Update. Only the maintainer's PC has a
    /// release folder, so everywhere else these answer "nothing to offer" and the feature is
    /// simply inert. Read-only, and bounded to that one folder.
    /// </summary>
    private static void MapUpdateEndpoints(WebApplication app, Func<string> releasePath)
    {
        // Offered only while FastShareUpdateServer.exe is open on this PC - otherwise there is
        // nothing here, exactly as if no update existed.
        app.MapGet("/api/update/info", () =>
        {
            if (!Updater.UpdatesOffered) return Results.NotFound();
            var build = Updater.LocalBuild(releasePath());
            return build is null ? Results.NotFound() : Results.Json(build);
        });

        app.MapGet("/api/update/package", (HttpContext context) =>
        {
            var path = releasePath();
            if (!Updater.UpdatesOffered || Updater.LocalBuild(path) is null) return Results.NotFound();

            try
            {
                var zip = Updater.BuildPackage(path);
                return Results.File(zip, "application/zip", "FastShare-update.zip");
            }
            catch
            {
                return Results.Problem("The update package could not be built.");
            }
        });
    }

    private static void MapSessionEndpoints(WebApplication app, SessionStore sessions, Func<ShareSet> shares,
        string hostPeerId, string hostFingerprint)
    {
        // Something for a connecting peer to sign, proving the PeerId they are about to claim.
        app.MapGet("/api/session/challenge", () => Results.Json(new { nonce = sessions.IssueChallenge() }));

        app.MapGet("/api/ping", (HttpContext context) =>
        {
            var session = sessions.Lookup(BearerOf(context));

            // They timed their own round trip to us and report it here. We cannot measure it
            // ourselves - they are the one who connected in - so this is the only figure we
            // ever get for the latency to a connected peer.
            if (session is not null &&
                double.TryParse(context.Request.Query["rtt"], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var rtt) &&
                rtt is >= 0 and < 600000)
            {
                session.ReportedRttMs = rtt;
            }

            return Results.Json(new
            {
                ok = true,
                // Shown by the far side instead of a bare IP address.
                machine = Environment.MachineName,

                // "Windows 11" and the like (2.4.23), shown instead of an address.
                os = AppVersion.Device,

                // Our stable identity, so a peer we are connected to can key a reverse
                // connection (and its nickname) on who we are rather than on an address.
                peerId = hostPeerId,

                // Folder names go only to peers who have actually been approved. An
                // unauthenticated probe learns that someone is here and nothing else, which
                // is all the online/offline light needs.
                // What they are currently allowed to do. Sent on every ping so a change made
                // on the host takes effect on their screen within seconds, not on next connect.
                permissions = session?.Permissions,

                shares = session is null
                    ? Array.Empty<string>()
                    : shares().Restrict(session.Permissions.Shares).Names
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),

                // Set when the host has asked this peer to share their folders back. Carried
                // on the ping because the host has no way to push to them - they connected in,
                // so nothing of ours can reach them unprompted.
                wantsShareBack = session is not null && sessions.WantsShareBack(session.PeerId),
                wantsSpeedTest = session is not null && sessions.WantsSpeedTest(session.PeerId),

                // We cancelled the transfer they are running against us (2.4.25).
                cancelTransfers = session is not null && sessions.WantsCancel(session.PeerId),

                // What this build can do beyond the original protocol. Older clients ignore it;
                // newer ones only use a feature listed here, so mixed versions keep working.
                caps = new[] { Caps.UploadPart, Caps.BundleV2, Caps.Diagnostics, Caps.SpeedTest, Caps.Hash, Caps.Queue, Caps.Resume, Caps.SpaceAsk, Caps.HashGate, Caps.Prepare },
            });
        });

        // Parked until this session ends, then answered. The far side keeps one of these open
        // at all times, which is how a disconnect reaches them immediately rather than on
        // their next poll. Costs one idle connection per peer and no traffic at all.
        app.MapGet("/api/watch", async (HttpContext context) =>
        {
            if (context.Items["session"] is not SessionStore.Session session)
            {
                context.Response.StatusCode = 401;
                return;
            }

            using var either = CancellationTokenSource.CreateLinkedTokenSource(
                session.Ended.Token, session.Refreshed.Token, context.RequestAborted);

            try { await Task.Delay(Timeout.Infinite, either.Token); }
            catch (OperationCanceledException) { }

            // Nothing to say if the CLIENT gave up - there is nobody left to tell, and writing
            // a status to an aborted request would throw.
            if (context.RequestAborted.IsCancellationRequested) return;

            // 410 Gone: the session is over. 205 Reset Content: still here, but what you are
            // allowed to do has changed, so re-read it now.
            if (session.Ended.IsCancellationRequested)
            {
                // Distinguish a deliberate stop-sharing from a one-person disconnect, so the
                // far side can pop up "X stopped sharing" rather than a bare "disconnected".
                if (session.EndReason is { } why) context.Response.Headers["X-FastShare-End"] = why;
                context.Response.StatusCode = 410;
            }
            else
            {
                context.Response.StatusCode = 205;
            }
        });

        // A peer saying goodbye on its way out. Best effort on their side, so the sweeper
        // still has to exist - but when it arrives, everyone sees them go immediately.
        // The far side announcing a transfer it is about to run, so we can show a percentage
        // against a real total and keep it as one batch. Needs a session (it goes through the
        // auth middleware), and reveals nothing - it only reports sizes about their own work.
        app.MapPost("/api/activity", (HttpContext context) =>
        {
            if (context.Items["session"] is not SessionStore.Session session) return Results.Unauthorized();

            var action = context.Request.Query["action"].ToString();
            var phase = context.Request.Query["phase"].ToString();

            // Their download is us sending; their upload is us receiving.
            var direction = action == "download" ? TransferDirection.Upload : TransferDirection.Download;
            var label = session.DisplayName;

            if (phase == "queued")
            {
                // Waiting in their queue behind something else; nothing moves yet.
                int.TryParse(context.Request.Query["position"], out var position);
                TrafficMonitor.Queue(session.PeerId, label, direction, Math.Max(1, position));
            }
            else if (phase == "unqueued")
            {
                TrafficMonitor.Unqueue(session.PeerId, direction);
            }
            else if (phase == "prepare")
            {
                // What they are doing before it starts (2.4.40); an empty stage means "nothing any more".
                long.TryParse(context.Request.Query["need"], out var need);
                long.TryParse(context.Request.Query["free"], out var free);
                TrafficMonitor.Prepare(session.PeerId, label, direction,
                    context.Request.Query["stage"].ToString(), need, free);
            }
            else if (phase == "end")
            {
                // "done", or how it ended early (2.4.25+) so this side does not call it received;
                // "partial-N" (2.4.27+): it finished, but N files did not make it.
                var result = context.Request.Query["result"].ToString();
                if (result.StartsWith("partial-", StringComparison.Ordinal) && int.TryParse(result["partial-".Length..], out var failedCount))
                    TrafficMonitor.Complete(session.PeerId, direction, problem: $"{failedCount:N0} failed - {label} has the list (Activity Log, Failed)");
                else
                    TrafficMonitor.Complete(session.PeerId, direction, result switch
                    {
                        "cancelled" => $"Cancelled by {label}",
                        "failed" => $"Stopped early on {label}'s side",
                        _ => null,
                    });
            }
            else
            {
                long.TryParse(context.Request.Query["bytes"], out var bytes);
                int.TryParse(context.Request.Query["files"], out var files);

                // The peer's own drive for this transfer, so a peer-driven row can show their
                // end too. Absent from older builds, in which case their end stays unknown.
                var drive = context.Request.Query["drive"].ToString();
                TrafficMonitor.Announce(session.PeerId, label, direction, bytes, files,
                    string.IsNullOrWhiteSpace(drive) ? null : drive);
            }

            return Results.Ok();
        });

        // "May we resume the unfinished transfer?" - asked on our screen, answered by polling.
        app.MapPost("/api/resume/ask", (HttpContext context) =>
        {
            if (context.Items["session"] is not SessionStore.Session session) return Results.Unauthorized();
            var what = context.Request.Query["what"].ToString();
            if (what.Length > 400) what = what[..400];
            var kind = context.Request.Query["kind"].ToString() == "space" ? "space" : "resume";
            return Results.Json(new { id = sessions.AskResume(session.PeerId, session.DisplayName, what, kind) });
        });

        app.MapPost("/api/resume/withdraw", (HttpContext context, string? id) =>
        {
            if (context.Items["session"] is null) return Results.Unauthorized();
            sessions.WithdrawResume(id ?? "");
            return Results.Ok();
        });

        app.MapGet("/api/resume/answer", (HttpContext context, string? id) =>
            context.Items["session"] is null ? Results.Unauthorized()
                : Results.Json(new { state = sessions.ResumeState(id ?? "") }));

        app.MapPost("/api/session/leave", (HttpContext context) =>
        {
            // Why they are leaving (2.4.25+): closing FastShare, or just disconnecting from us.
            var why = context.Request.Query["why"].ToString() == "left" ? DenyReasons.PeerLeft : DenyReasons.PeerClosed;
            var session = sessions.EndByToken(BearerOf(context), why);
            return Results.Json(new { ok = session is not null });
        });

        app.MapPost("/api/session/request", (HttpContext context, SessionRequest request) =>
        {
            var remote = context.Connection.RemoteIpAddress?.ToString() ?? "";

            // Names are shown on a prompt; nothing legitimate is anywhere near this long.
            if (request.PeerId is not { Length: > 0 and <= 64 } ||
                request.Username?.Length > 200 || request.MachineName?.Length > 200)
            {
                return Results.BadRequest();
            }

            var status = sessions.Request(request, remote, out var requestId,
                sessions.VerifiedKeyOf(request, hostFingerprint));
            return Results.Json(new SessionResponse(status.State, status.Grant, requestId, status.Reason));
        });

        app.MapGet("/api/session/status", (string id) =>
        {
            var status = sessions.Poll(id);
            return Results.Json(new SessionResponse(status.State, status.Grant, id, status.Reason));
        });
    }

    private static string BearerOf(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.Ordinal) ? header[prefix.Length..] : "";
    }

    /// <summary>
    /// The shares this particular caller may see. Narrowing here means a peer granted only
    /// some of your folders cannot reach the others through any endpoint - listings, manifests
    /// and path checks all run against the restricted set, so there is no per-handler filter
    /// to forget.
    /// </summary>
    /// <summary>Who is on the other end of this request, for the transfer records.</summary>
    private static string? PeerNameOf(HttpContext context) =>
        context.Items["session"] is SessionStore.Session session ? session.DisplayName : null;

    /// <summary>
    /// Stable identity for grouping traffic, so everything one person is pulling right now
    /// becomes a single line however many requests it takes. Falls back to the address for
    /// the loopback case, which has no session.
    /// </summary>
    private static string PeerKeyOf(HttpContext context) =>
        context.Items["session"] is SessionStore.Session session
            ? session.PeerId
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string PeerLabelOf(HttpContext context) =>
        PeerNameOf(context)
        ?? (IsSelf(context) ? "You" : context.Connection.RemoteIpAddress?.ToString() ?? "Someone");

    /// <summary>
    /// Traffic to our own machine is not counted.
    ///
    /// On loopback both ends are us: the downloader already reports every byte as received,
    /// so counting the same bytes again as sent would double every figure and put two rows
    /// on the Session tab for one download.
    /// </summary>
    private static bool IsSelf(HttpContext context) =>
        IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None) &&
        // The tunnel host is reached over loopback too, but on behalf of the far side: its
        // requests carry their reverse session and are real traffic from them.
        context.Items["session"] is not SessionStore.Session { IsReverse: true };

    private ShareSet CurrentShares() => SharesSource?.Invoke() ?? _shares;

    /// <summary>
    /// A private server on 127.0.0.1 for writes that arrive over our tunnel to a host.
    ///
    /// The tunnel only ever carried reads. Rather than re-implement uploads, deletes, renames
    /// and folder creation for it - with their own copy of every permission and folder-rule
    /// check - the tunnel hands each such request to this, an ordinary FastShare server with
    /// the same endpoints and the same checks, authenticated as the host's reverse session.
    /// So what the host may do is exactly what our User Permissions and Folder Permissions say.
    /// </summary>
    public static ShareServer ForTunnel(SessionStore sessions, Func<ShareSet> shares) =>
        new(sessions, owns: false) { SharesSource = shares };

    /// <summary>
    /// A 500 that says what went wrong in words, without the stack or any path of ours -
    /// the far side only ever needs the gist ("The process cannot access the file...").
    /// </summary>
    private static IResult Problem(Exception ex) =>
        Results.Problem(ex is IOException or UnauthorizedAccessException ? ex.Message : "Something went wrong on this end.");

    private static ShareSet Visible(HttpContext context, Func<ShareSet> all) =>
        context.Items["session"] is SessionStore.Session session
            ? all().Restrict(session.Permissions.Shares)
            : all();

    private static void MapEndpoints(WebApplication app, Func<ShareSet> shares)
    {
        // Immediate children of one folder. Called per folder as the far side browses, so
        // opening a share with a million files costs one small request, not a tree walk.
        app.MapGet("/api/list", (HttpContext context, string? path) =>
            Listing.Children(Visible(context, shares), path) is { } entries
                ? Results.Json(entries)
                : Results.NotFound());

        // Full recursive listing of a subtree. Requested once when a transfer starts so the
        // far side can plan all its parallel work from a single round trip.
        app.MapGet("/api/manifest", (HttpContext context, string? path) =>
            Listing.Tree(Visible(context, shares), path, FolderOp.Download) is { } manifest
                ? Results.Json(manifest)
                : Results.NotFound());

        // The kind of disk a shared path lives on ("HDD"/"SSD"/"NVMe"), so the far side can
        // show "SSD → HDD" for an upload it is about to push here - the one drive end it has no
        // other way to learn. Gated like every other endpoint, and it resolves through the
        // per-peer view, so it reveals nothing a listing would not.
        app.MapGet("/api/drive", (HttpContext context, string? path) =>
        {
            if (!Visible(context, shares).TryResolve(path, out var full))
                return Results.NotFound();

            var drive = DriveDetect.Label(DriveDetect.ForPath(full));

            // Free space on that disk (2.4.28), so someone sending here can say "not enough
            // room" before a byte moves instead of failing 80 GB in.
            long? free = null;
            try { free = DiskSpace.Free(full); } catch { }

            return Results.Json(new { drive = drive == "?" ? null : drive, free });
        });

        // One file with Range support, so a large file can be pulled as parallel chunks.
        // PhysicalFile uses TransmitFile on Windows: the bytes go from the filesystem cache
        // to the socket without passing through user space.
        // SHA-256 of files the caller may download, for the optional check after a transfer.
        // How many at once is decided per drive by Verify's gate (8 on NVMe, fewer on slower
        // disks), shared by every request - so a client keeping two requests in flight keeps
        // the disk busy without ever reading more at once than it likes.
        app.MapPost("/api/hash", async (HttpContext context, HashRequest request, CancellationToken ct) =>
        {
            if (request.Paths is null || request.Paths.Count > 1000) return Results.BadRequest();
            var view = Visible(context, shares);

            // Their check is part of the transfer they announced: it stays open meanwhile.
            using var checking = TrafficMonitor.BeginCheck(PeerKeyOf(context));
            var hashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            await Parallel.ForEachAsync(request.Paths.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = Verify.MaxReaders, CancellationToken = ct },
                async (remote, token) =>
                {
                    hashes[remote] = view.TryResolve(remote, out var full) && File.Exists(full) && view.Allows(FolderOp.Download, full)
                        ? await Verify.HashGatedAsync(full, token, checking.Read)
                        : "";
                });
            return Results.Json(new { hashes });
        });

        app.MapGet("/api/file", (HttpContext context, string? path) =>
        {
            var view = Visible(context, shares);
            if (!view.TryResolve(path, out var full) || !File.Exists(full))
                return Results.NotFound();
            if (!view.Allows(FolderOp.Download, full)) return Results.StatusCode(403);

            // Open it once first: a file another program holds (Blender saving a .blend) used
            // to surface on their side as a bare "500". 423 Locked says what it is.
            try { using (new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { } }
            catch (IOException) { return Results.StatusCode(423); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }

            // Someone downloading from us is us sending. Counted here because the Uploader
            // never runs in this direction - without it the "sent" figures stayed at zero
            // however much anyone pulled off this machine.
            //
            // Measured on completion from the response's own Content-Length, which is the
            // range length on a 206 and the whole file on a 200. Counting requests instead
            // was wrong both ways round: the eight-stream engine asks for a large file as
            // eight ranges, so a file was either reported eight times or - as it ended up -
            // skipped entirely whenever a Range header was present.
            long length = 0;
            try { length = new FileInfo(full).Length; } catch { }

            var peerKey = PeerKeyOf(context);
            var peerLabel = PeerLabelOf(context);
            var counted = !IsSelf(context);
            var remote = path ?? full;

            context.Response.OnCompleted(() =>
            {
                var served = context.Response.ContentLength ?? 0;
                if (counted && served > 0)
                {
                    TrafficMonitor.Add(peerKey, peerLabel, TransferDirection.Upload,
                        remote, length, served);
                }
                return Task.CompletedTask;
            });

            return Results.File(full, "application/octet-stream",
                lastModified: File.GetLastWriteTimeUtc(full), enableRangeProcessing: true);
        });

        // Many small files down one connection. Without this, each file costs a round trip,
        // and at ~20 ms that caps you near 50 files/sec no matter how much bandwidth exists.
        app.MapPost("/api/bundle", async (HttpContext context, BundleRequest request, CancellationToken ct) =>
        {
            if (request.Paths is null || request.Paths.Count == 0) return Results.BadRequest();

            // Validate everything before a single byte goes out; once the stream starts there
            // is no way to send an error status. A path outside the share is refused outright;
            // a file that has merely gone since the manifest is left out, and the far side
            // reports it missing - one deleted file used to fail the whole bundle of 256.
            var visible = Visible(context, shares);
            var resolved = new List<(string Full, string Remote)>(request.Paths.Count);
            foreach (var remote in request.Paths)
            {
                if (!visible.TryResolve(remote, out var full)) return Results.BadRequest();
                if (Encoding.UTF8.GetByteCount(remote) > BundleFormat.MaxPathBytes)
                    return Results.BadRequest();
                if (!visible.Allows(FolderOp.Download, full)) continue;   // left out, like a vanished file
                resolved.Add((full, remote));
            }

            context.Response.ContentType = "application/octet-stream";
            var body = context.Response.Body;

            var peerKey = PeerKeyOf(context);
            var peerLabel = PeerLabelOf(context);
            var counted = !IsSelf(context);

            var header = ArrayPool<byte>.Shared.Rent(BundleFormat.MaxPathBytes + 16);
            var buffer = ArrayPool<byte>.Shared.Rent(BundleIo.BufferSize);
            try
            {
                foreach (var (full, remote) in resolved)
                {
                    // Opened before its header is written: a file that is locked or gone is
                    // skipped cleanly instead of failing the response halfway through a frame.
                    Microsoft.Win32.SafeHandles.SafeFileHandle handle;
                    try { handle = BundleIo.OpenForRead(full); }
                    catch { continue; }

                    long length;
                    using (handle)
                    {
                        length = RandomAccess.GetLength(handle);
                        await BundleIo.WriteHeaderAsync(body, remote, length, header, ct);
                        await BundleIo.CopyToAsync(handle, body, length, buffer, null, ct);
                    }

                    if (counted)
                    {
                        TrafficMonitor.Add(peerKey, peerLabel, TransferDirection.Upload,
                            remote, length, length);
                    }
                }
                await BundleIo.WriteEndAsync(body, header, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return Results.Empty;
        });

        // ---- uploads -------------------------------------------------------

        // Creates and preallocates the file so parallel chunk writes cannot race on creation
        // and the filesystem can lay it out in one extent.
        //
        // With "part" (every current client) the bytes go to a temp file beside the target and
        // only /api/upload/commit replaces the real one. Without it - an older client - they go
        // straight into the destination as they always did.
        app.MapPost("/api/upload/begin", (HttpContext context, BeginUploadRequest request) =>
        {
            var view = Visible(context, shares);
            if (!view.TryResolve(request.Path, out var full) || request.Size < 0)
                return Results.BadRequest();
            if (!view.Allows(FolderOp.Send, full)) return Results.StatusCode(403);
            if (Directory.Exists(full)) return Results.Conflict();

            try
            {
                var directory = Path.GetDirectoryName(full);
                if (directory is not null) Directory.CreateDirectory(directory);

                var target = request.Part ? PartFiles.PathFor(full) : full;
                using var handle = File.OpenHandle(target, FileMode.Create, FileAccess.Write,
                    FileShare.ReadWrite, FileOptions.None, preallocationSize: request.Size);
                RandomAccess.SetLength(handle, request.Size);
                return Results.Ok();
            }
            catch (Exception ex) { return Problem(ex); }
        });

        app.MapPost("/api/upload/commit", (HttpContext context, CommitUploadRequest request) =>
        {
            var view = Visible(context, shares);
            if (!view.TryResolve(request.Path, out var full)) return Results.BadRequest();
            if (!view.Allows(FolderOp.Send, full)) return Results.StatusCode(403);

            var part = PartFiles.PathFor(full);
            try
            {
                if (!File.Exists(part)) return Results.NotFound();
                if (new FileInfo(part).Length != request.Size)
                {
                    File.Delete(part);
                    return Results.Problem("The upload arrived incomplete.");
                }

                File.Move(part, full, overwrite: true);
                PartFiles.StampModified(full, request.ModifiedUnixMs);
                return Results.Ok();
            }
            catch (Exception ex) { return Problem(ex); }
        });

        app.MapPost("/api/upload/abort", (HttpContext context, AbortUploadRequest request) =>
        {
            if (!Visible(context, shares).TryResolve(request.Path, out var full)) return Results.BadRequest();
            BundleIo.TryDelete(PartFiles.PathFor(full));
            return Results.Ok();
        });

        app.MapPut("/api/upload/chunk", async (HttpContext context, string path, long offset,
            CancellationToken ct) =>
        {
            var view = Visible(context, shares);
            if (!view.TryResolve(path, out var full) || offset < 0) return Results.BadRequest();
            if (!view.Allows(FolderOp.Send, full)) return Results.StatusCode(403);
            if (context.Request.Query["part"] == "1") full = PartFiles.PathFor(full);

            var peerKey = PeerKeyOf(context);
            var peerLabel = PeerLabelOf(context);
            var counted = !IsSelf(context);

            // /api/upload/begin preallocated the file at its final size, so this is what the
            // whole thing will weigh - which is how the counter knows when the last chunk of
            // it has landed and the file can be counted once.
            long expected = 0;
            try { expected = new FileInfo(full).Length; } catch { }

            var buffer = ArrayPool<byte>.Shared.Rent(BundleIo.BufferSize);
            try
            {
                using var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite, FileOptions.Asynchronous);

                var position = offset;

                // Report progress every so often rather than on every buffer: TrafficMonitor
                // takes a lock, and taking it on each small read across eight parallel upload
                // streams is needless contention. Roughly twice a megabyte keeps the status
                // bar smooth without hammering the lock.
                long sinceReport = 0;
                const long reportEvery = 512 * 1024;

                while (true)
                {
                    var read = await context.Request.Body.ReadAsync(buffer, ct);
                    if (read <= 0) break;
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), position, ct);
                    position += read;
                    sinceReport += read;

                    if (counted && sinceReport >= reportEvery)
                    {
                        TrafficMonitor.Add(peerKey, peerLabel, TransferDirection.Download,
                            path, expected, sinceReport);
                        sinceReport = 0;
                    }
                }

                // The tail below the reporting threshold, so the totals still add up exactly.
                if (counted && sinceReport > 0)
                {
                    TrafficMonitor.Add(peerKey, peerLabel, TransferDirection.Download,
                        path, expected, sinceReport);
                }

                return Results.Ok();
            }
            catch (FileNotFoundException) { return Results.BadRequest(); }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        });

        // Each file lands on a temp name and is moved into place once complete, so a bundle
        // that dies halfway never leaves a blank or half-written file where a good one was.
        // A file this end cannot keep (locked, read-only, refused path) is skipped and listed
        // in a 207, so the sender can report it by name instead of the bundle "succeeding".
        // "v=2" frames also carry each file's date and can create empty folders.
        app.MapPost("/api/upload/bundle", async (HttpContext context, string dir, CancellationToken ct) =>
        {
            var visible = Visible(context, shares);
            if (!visible.TryResolve(dir, out _)) return Results.BadRequest();

            var v2 = context.Request.Query["v"] == "2";
            var peerKey = PeerKeyOf(context);
            var peerLabel = PeerLabelOf(context);
            var counted = !IsSelf(context);
            var failures = new List<string>();
            string? lastDirectory = null;

            var scratch = ArrayPool<byte>.Shared.Rent(BundleFormat.MaxPathBytes + 16);
            var buffer = ArrayPool<byte>.Shared.Rent(BundleIo.BufferSize);
            try
            {
                var body = context.Request.Body;
                while (true)
                {
                    var frame = await BundleIo.ReadHeaderAsync(body, scratch, ct, withTime: v2);
                    if (frame is null) break;
                    var (relative, length, modified) = frame.Value;
                    var remote = $"{dir.TrimEnd('/')}/{relative}";

                    // Every incoming path is re-validated against the share set. This is the
                    // only thing preventing an upload from writing outside the share.
                    if (!visible.TryResolve(remote, out var full))
                    {
                        if (length > 0) await BundleIo.SkipAsync(body, length, buffer, ct);
                        failures.Add($"{relative}: not allowed there");
                        continue;
                    }

                    if (!visible.Allows(FolderOp.Send, full))
                    {
                        if (length > 0) await BundleIo.SkipAsync(body, length, buffer, ct);
                        failures.Add($"{relative}: uploading is turned off for that folder");
                        continue;
                    }

                    if (length == BundleFormat.Directory)
                    {
                        try { Directory.CreateDirectory(full); }
                        catch (Exception ex) { failures.Add($"{relative}: {ex.Message}"); }
                        continue;
                    }

                    // Consecutive files usually share a folder; creating it once per folder
                    // rather than once per file saves a filesystem call on every small file.
                    var directory = Path.GetDirectoryName(full);
                    if (directory is not null && !string.Equals(directory, lastDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(directory);
                        lastDirectory = directory;
                    }

                    var problem = await BundleIo.ReceiveFileAsync(full, body, length, modified, buffer, null, ct);
                    if (problem is not null)
                    {
                        failures.Add($"{relative}: {problem}");
                        continue;
                    }

                    if (counted)
                    {
                        TrafficMonitor.Add(peerKey, peerLabel, TransferDirection.Download, remote, length, length);
                    }
                }

                return failures.Count == 0
                    ? Results.Ok()
                    : Results.Json(new { errors = failures }, statusCode: 207);
            }
            catch (Exception ex) { return Problem(ex); }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        });

        // ---- delete --------------------------------------------------------

        app.MapPost("/api/delete", (HttpContext context, DeleteRequest request) =>
        {
            if (request.Paths is null || request.Paths.Count == 0) return Results.BadRequest();

            var view = Visible(context, shares);
            var targets = new List<string>(request.Paths.Count);
            foreach (var remote in request.Paths)
            {
                if (!view.TryResolve(remote, out var full)) return Results.BadRequest();

                // Removing a whole share would silently un-share it rather than delete
                // content the far side actually selected. Refuse it.
                if (view.IsShareRoot(full)) return Results.BadRequest();
                targets.Add(full);
            }

            var failures = new List<string>();
            foreach (var target in targets)
            {
                if (!view.AllowsTree(FolderOp.Delete, target))
                {
                    failures.Add($"{Path.GetFileName(target)}: deleting is turned off for that folder");
                    continue;
                }

                LocalFs.DeleteTree(target, failures);
            }

            return failures.Count == 0
                ? Results.Ok()
                : Results.Json(new { errors = failures }, statusCode: 207);
        });

        // ---- create, rename, copy, move --------------------------------------

        app.MapPost("/api/create", (HttpContext context, CreateRequest request) =>
        {
            var view = Visible(context, shares);
            if (!view.TryResolve(request.Path, out var full)) return Results.BadRequest();
            if (!view.Allows(FolderOp.Modify, full)) return Results.StatusCode(403);
            if (Directory.Exists(full) || File.Exists(full)) return Results.Conflict();

            try
            {
                if (request.IsDirectory)
                {
                    Directory.CreateDirectory(full);
                }
                else
                {
                    var parent = Path.GetDirectoryName(full);
                    if (parent is not null) Directory.CreateDirectory(parent);
                    using (File.Create(full)) { }
                }
                return Results.Ok();
            }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        app.MapPost("/api/rename", (HttpContext context, RenameRequest request) =>
        {
            // A bare name only. Accepting a path here would let a rename walk out of the share
            // through the back door, sidestepping every check on the way in.
            var name = request.Name?.Trim() ?? "";
            if (name.Length == 0 || name is "." or ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.Contains(Path.DirectorySeparatorChar) ||
                name.Contains(Path.AltDirectorySeparatorChar))
            {
                return Results.BadRequest();
            }

            var view = Visible(context, shares);
            if (!view.TryResolve(request.Path, out var full)) return Results.BadRequest();
            if (view.IsShareRoot(full)) return Results.BadRequest();
            if (!view.AllowsTree(FolderOp.Modify, full)) return Results.StatusCode(403);

            var parent = Path.GetDirectoryName(full);
            if (parent is null) return Results.BadRequest();

            var target = Path.Combine(parent, name);
            if (File.Exists(target) || Directory.Exists(target)) return Results.Conflict();

            try
            {
                if (Directory.Exists(full)) Directory.Move(full, target);
                else if (File.Exists(full)) File.Move(full, target);
                else return Results.NotFound();
                return Results.Ok();
            }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        app.MapPost("/api/transplant", (HttpContext context, TransplantRequest request) =>
        {
            if (request.Paths is null || request.Paths.Count == 0) return Results.BadRequest();

            // A move deletes from the source as well as writing, so it needs both rights.
            if (request.Move && context.Items["session"] is SessionStore.Session session
                             && !session.Permissions.Delete)
            {
                return Results.StatusCode(403);
            }

            var view = Visible(context, shares);
            if (!view.TryResolve(request.Destination, out var destination) ||
                !Directory.Exists(destination))
            {
                return Results.BadRequest();
            }
            if (!view.Allows(FolderOp.Modify, destination)) return Results.StatusCode(403);

            var sources = new List<string>(request.Paths.Count);
            foreach (var path in request.Paths)
            {
                if (!view.TryResolve(path, out var full)) return Results.BadRequest();
                if (request.Move && view.IsShareRoot(full)) return Results.BadRequest();

                // Moving takes it out of where it was; copying reads it.
                var allowed = request.Move
                    ? view.AllowsTree(FolderOp.Modify, full) && view.AllowsTree(FolderOp.Delete, full)
                    : view.AllowsTree(FolderOp.Download, full);
                if (!allowed) return Results.StatusCode(403);
                sources.Add(full);
            }

            var failures = new List<string>();
            foreach (var source in sources)
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
                var target = Path.Combine(destination, name);

                try
                {
                    if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"{name}: already there");
                        continue;
                    }

                    if (Directory.Exists(source))
                    {
                        // Copying a folder into itself would recurse forever.
                        if (target.StartsWith(source + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            failures.Add($"{name}: cannot go inside itself");
                            continue;
                        }

                        if (request.Move) Directory.Move(source, target);
                        else CopyTree(source, target);
                    }
                    else if (File.Exists(source))
                    {
                        if (request.Move) File.Move(source, target, overwrite: true);
                        else File.Copy(source, target, overwrite: true);
                    }
                }
                catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
            }

            return failures.Count == 0
                ? Results.Ok()
                : Results.Json(new { errors = failures }, statusCode: 207);
        });

    }

    /// <summary>Recursive folder copy. A move needs none of this - Directory.Move is atomic.</summary>
    private static void CopyTree(string source, string destination) => LocalFs.CopyTree(source, destination);

    private static async Task<string?> DetectPublicHostAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var ip = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            return IPAddress.TryParse(ip, out _) ? ip : null;
        }
        catch
        {
            return null;   // PublicHost can be set by hand in config.json
        }
    }
}
