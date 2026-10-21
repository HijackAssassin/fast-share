namespace FastShare;

/// <summary>
/// Getting the share port open, by whatever means works.
///
/// The order matters for how long it takes. A port that is already forwarded answers in well
/// under a second, and UPnP discovery costs a few seconds per network interface - so the two
/// are started together and the probe is allowed to win. If the port is already open, nothing
/// waits for the router; only when the probe comes back closed does the UPnP attempt matter.
/// </summary>
public static class Reachability
{
    /// <summary>
    /// What happened. <paramref name="Reachable"/> is deliberately a plain yes/no: from the
    /// user's point of view a port they cannot be reached on is shut, whether that is because
    /// it was never forwarded or because the probe could not tell. "Unknown" explained nothing
    /// and left the status sitting there looking broken. The nuance lives in Detail.
    /// </summary>
    public sealed record Result(bool Reachable, bool ViaUpnp, string Explanation, string Detail)
    {
        public string Headline => Reachable ? (ViaUpnp ? "Open (UPnP)" : "Open") : "Closed";
        public string Brush => Reachable ? "Good" : "Bad";

        /// <summary>
        /// What the router said to the UPnP request, or null while it has not answered yet. When
        /// the port is already forwarded that request finishes in the background after the result
        /// is out, and <see cref="Updated"/> is raised a second time with this filled in.
        /// </summary>
        public Upnp.MapResult? UpnpResult { get; init; }
    }

    /// <summary>
    /// The last answer, so the Session tab and Settings show the same thing without either of
    /// them re-running a multi-second check every time they are looked at.
    /// </summary>
    public static Result? Last { get; private set; }

    /// <summary>Raised when <see cref="Last"/> changes, so anything showing it can update.</summary>
    public static event Action<Result>? Updated;

    /// <summary>
    /// The startup check. Runs whether or not anything is sharing.
    ///
    /// A TCP probe can only tell a forwarded port from a closed one if something answers, so
    /// with the app idle the honest answer would always be "Unknown" - which is why the status
    /// only ever turned green after pressing Start Sharing. Here a throwaway listener is bound
    /// for the length of the probe purely so there is something on the other end. It accepts
    /// nothing, serves nothing, and is gone a second later.
    /// </summary>
    public static async Task<Result> CheckAtStartupAsync(string? publicHost, int port,
        bool alreadyListening, CancellationToken ct = default)
    {
        if (alreadyListening) return await RecordAsync(publicHost, port, ct);

        System.Net.Sockets.TcpListener? stand = null;
        try
        {
            stand = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
            stand.Start();

            // Answer whatever the probe opens, then forget about it. Nothing is read or
            // written - completing the handshake is the entire point.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true) (await stand.AcceptTcpClientAsync(ct)).Dispose();
                }
                catch { /* stopped, which is the normal way out */ }
            }, ct);
        }
        catch (Exception ex)
        {
            // Something else already holds the port. Worth saying plainly, because it is also
            // the reason Start Sharing would fail.
            Diagnostics.Log("port", $"could not bind {port} for the startup check", ex);
            stand = null;
        }

        try { return await RecordAsync(publicHost, port, ct); }
        finally { try { stand?.Stop(); } catch { } }
    }

    private static async Task<Result> RecordAsync(string? publicHost, int port, CancellationToken ct)
    {
        var (result, mapping) = await CheckAsync(publicHost, port, ct);

        Last = result;
        Updated?.Invoke(result);
        Diagnostics.Log("port", result.Explanation);

        // Port already open: the UPnP answer comes later. Shown when it does, so the Share tab
        // can say whether UPnP works here too.
        if (result.UpnpResult is null)
            _ = mapping.ContinueWith(t =>
            {
                if (!ReferenceEquals(Last, result)) return;   // a newer check has replaced it
                Last = result with { UpnpResult = t.IsCompletedSuccessfully ? t.Result : Upnp.MapResult.NoGateway };
                Updated?.Invoke(Last);
            }, TaskScheduler.Default);

        return result;
    }

    /// <summary>
    /// Checks the port and, if it is shut, asks the router to forward it and checks again.
    /// </summary>
    public static async Task<Result> EnsureOpenAsync(string? publicHost, int port,
        CancellationToken ct = default) => (await CheckAsync(publicHost, port, ct)).Result;

    private static async Task<(Result Result, Task<Upnp.MapResult> Mapping)> CheckAsync(string? publicHost,
        int port, CancellationToken ct)
    {
        // Both at once. The probe usually finishes first; the mapping attempt is already
        // under way by the time we know whether it was needed.
        var probe = Diagnostics.CheckPortAsync(publicHost, port, ct);
        var mapping = Task.Run(() => Upnp.MapPortAsync(port, ct), ct);

        var first = await probe;
        if (first == Diagnostics.PortState.Open)
        {
            // Already reachable. Let the mapping attempt finish in the background rather than
            // waiting on it - a duplicate mapping is harmless, and cancelling mid-SOAP is not
            // obviously kinder to the router than letting it complete.
            Forget(mapping);
            return (new Result(true, false,
                $"Port {port} is reachable from the internet.",
                "Forwarded on your router."), mapping);
        }

        var mapped = await mapping;

        if (mapped != Upnp.MapResult.Mapped)
        {
            var why = mapped == Upnp.MapResult.NoGateway
                ? "UPnP failed — no router answered"
                : "UPnP failed — the router refused";

            return (new Result(false, false,
                $"Port {port} cannot be reached from the internet.",
                $"Port not forwarded, and {why}. Forward it on your router to host.") { UpnpResult = mapped }, mapping);
        }

        // The router says it added the mapping. That is a claim, not proof, so it gets tested.
        var second = await Diagnostics.CheckPortAsync(publicHost, port, ct);

        return (second == Diagnostics.PortState.Open
            ? new Result(true, true, $"Port {port} was forwarded automatically.",
                "UPnP opened it for you.") { UpnpResult = mapped }
            : new Result(false, false,
                $"Port {port} still cannot be reached from the internet.",
                "The router accepted a UPnP mapping but it does not answer from outside. " +
                "Some routers report success and do nothing.") { UpnpResult = mapped }, mapping);
    }

    /// <summary>
    /// Lets a task finish unobserved without its failure taking the process down. UPnP throwing
    /// in the background is expected often enough that it must never become fatal.
    /// </summary>
    private static void Forget(Task task) =>
        _ = task.ContinueWith(t => Diagnostics.Log("upnp", "background mapping failed", t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
}
