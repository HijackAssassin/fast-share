using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;

namespace FastShare;

public partial class MainWindow
{
    // =======================================================================
    // Dev mode: diagnostics shared between the two PCs
    // =======================================================================
    //
    // With dev mode on, every few seconds this PC writes a stats line to its own log and sends
    // its new log lines plus that snapshot to each host it is connected to. A host that also
    // has dev mode on saves what arrives to diag\<peer>.log, so one PC holds both sides' view
    // of the same transfer. It travels over the connection that already exists - the side with
    // the shut port pushes, since nothing can reach in to it. Off by default on both ends.

    private DispatcherTimer? _devTimer;

    /// <summary>Log lines written since the last push. Bounded, so a host that never answers costs nothing.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _devLines = new();

    private TimeSpan _devLastCpu;
    private DateTime _devLastSample = DateTime.UtcNow;

    private void StartDevMode()
    {
        Diagnostics.Written -= OnDiagnosticsWritten;
        _devTimer?.Stop();
        _devTimer = null;
        while (_devLines.TryDequeue(out _)) { }

        _server.DiagnosticsEnabled = _config.DevMode;
        if (!_config.DevMode) return;

        Diagnostics.Written += OnDiagnosticsWritten;
        _devTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _devTimer.Tick += async (_, _) => await DevTickAsync();
        _devTimer.Start();
        Diagnostics.Log("dev", $"dev mode on — FastShare {AppVersion.Text}, elevated={DefenderTune.IsElevated()}");
        Diagnostics.Log("sys", SystemDescription());
        SampleSystem(out _, out _);   // prime the deltas so the first stats line is not "since boot"
    }

    /// <summary>The hardware a speed figure has to be read against: CPU, cores, memory, network links.</summary>
    private static string SystemDescription()
    {
        string cpu = "?";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "?";
        }
        catch { }

        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024 * 1024.0);

        var links = new List<string>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                if (nic.GetIPProperties().GatewayAddresses.Count == 0) continue;
                links.Add($"{nic.Name} ({nic.NetworkInterfaceType}, {nic.Speed / 1_000_000} Mbit/s)");
            }
        }
        catch { }

        return $"{cpu} · {Environment.ProcessorCount} logical cores · {memory:0.#} GB RAM · " +
               $"{Environment.OSVersion.VersionString} · links: {(links.Count == 0 ? "none" : string.Join(", ", links))}";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private long _sysIdle, _sysBusyTotal, _tcpSent, _tcpResent;

    /// <summary>
    /// Whole-PC CPU (all cores, 0-100) and the share of TCP segments this PC re-sent since the
    /// last call. Re-sends are packet loss seen from the sending side: a line that drops packets
    /// halves each stream's speed every time, which is what caps a transfer below the link.
    /// </summary>
    private void SampleSystem(out double cpuPct, out double retransPct)
    {
        cpuPct = 0;
        retransPct = 0;
        try
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var total = kernel + user;   // kernel time includes idle
                var dIdle = idle - _sysIdle;
                var dTotal = total - _sysBusyTotal;
                if (_sysBusyTotal > 0 && dTotal > 0) cpuPct = Math.Round((1 - dIdle / (double)dTotal) * 100);
                _sysIdle = idle;
                _sysBusyTotal = total;
            }

            var tcp = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetTcpIPv4Statistics();
            var dSent = tcp.SegmentsSent - _tcpSent;
            var dResent = tcp.SegmentsResent - _tcpResent;
            if (_tcpSent > 0 && dSent > 0) retransPct = Math.Round(dResent * 100.0 / dSent, 2);
            _tcpSent = tcp.SegmentsSent;
            _tcpResent = tcp.SegmentsResent;
        }
        catch { }
    }

    private void OnDiagnosticsWritten(string line)
    {
        if (_devLines.Count < 5000) _devLines.Enqueue(line);
    }

    private bool _devPushing;
    private readonly HashSet<string> _devSysSent = [];

    private async Task DevTickAsync()
    {
        Diagnostics.Log("stats", JsonSerializer.Serialize(DevSnapshot()));

        if (_devPushing) return;
        _devPushing = true;
        try
        {
            var lines = new List<string>();
            while (lines.Count < 2000 && _devLines.TryDequeue(out var line)) lines.Add(line.TrimEnd());
            if (lines.Count == 0) return;

            var hosts = _connections.Where(c => c.Api is { } api && api.HostCaps?.Contains(Caps.Diagnostics) == true
                                                && !c.DiagnosticsRefused).ToList();
            foreach (var connection in hosts)
            {
                // Lines logged before this host was connected (dev mode switched on at startup,
                // say) never reach it, so each host is sent the hardware line once on its own.
                var batch = lines;
                if (_devSysSent.Add(connection.Id))
                    batch = [$"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [sys]  {SystemDescription()}", .. lines];

                var accepted = await connection.Api!.SendDiagnosticsAsync(batch);
                if (accepted == false)
                {
                    connection.DiagnosticsRefused = true;   // their dev mode is off; stop trying
                    Diagnostics.Log("dev", $"{connection.Name} is not collecting diagnostics (dev mode off there)");
                }
            }
        }
        finally { _devPushing = false; }
    }

    /// <summary>What this PC is doing right now, in one line.</summary>
    private object DevSnapshot()
    {
        var process = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var cpu = process.TotalProcessorTime;
        var cpuPercent = (cpu - _devLastCpu).TotalSeconds / Math.Max((now - _devLastSample).TotalSeconds, 0.001) * 100;
        var seconds = Math.Max((now - _devLastSample).TotalSeconds, 0.001);
        _devLastCpu = cpu;
        _devLastSample = now;

        SampleSystem(out var systemCpu, out var retrans);

        // Stream-seconds spent waiting, as a share of the wall time: 8 streams all waiting on
        // the network for the whole 3 s reads as recvNet 800%. The split is what matters.
        var waits = WaitTimer.Take();
        object Pct(double s) => Math.Round(s / seconds * 100);

        object? Stats(TransferStats? s, RateMeter meter) => s is null ? null : new
        {
            files = $"{s.FilesDone}/{s.TotalFiles}",
            mb = $"{s.BytesDone / 1e6:0}/{s.TotalBytes / 1e6:0}",
            rateMBs = Math.Round(meter.BytesPerSecond / 1e6, 1),
            errors = s.Errors.Count,
        };

        return new
        {
            v = AppVersion.Text,
            sharing = _server.IsSharing,
            download = Stats(_downStats, _downMeter),
            upload = Stats(_upStats, _upMeter),
            peerFlows = TrafficMonitor.Active().Select(f => new
            {
                who = f.PeerName,
                dir = f.Direction == TransferDirection.Upload ? "they-pull" : "they-push",
                files = f.Files,
                mb = Math.Round(f.Bytes / 1e6),
                totalMb = Math.Round(f.Total / 1e6),
            }),
            servedMBs = new { outbound = Math.Round(_servedOutRate / 1e6, 1), inbound = Math.Round(_servedInRate / 1e6, 1) },
            connections = _connections.Where(c => c.Api is not null)
                .Select(c => new { c.Name, pingMs = c.LastPingMs is { } p ? Math.Round(p) : (double?)null, missed = c.MissedPings }),
            sessions = _server.Sessions.Active.Where(s => !s.IsLoopback)
                .Select(s => new { s.DisplayName, rttMs = s.ReportedRttMs is { } r ? Math.Round(r) : (double?)null, s.IsReverse }),
            queue = QueuedCount,
            cpuPct = Math.Round(cpuPercent),
            sysCpuPct = systemCpu,
            tcpRetransPct = retrans,
            waitPct = new
            {
                recvNet = Pct(waits.RecvNet), recvDisk = Pct(waits.RecvDisk),
                sendDisk = Pct(waits.SendDisk), sendNet = Pct(waits.SendNet),
            },
            memMB = process.WorkingSet64 / (1024 * 1024),
            threads = process.Threads.Count,
            defender = new { keep = _config.KeepDefenderExcluded, excluded = _config.DefenderExcluded },
        };
    }
}
