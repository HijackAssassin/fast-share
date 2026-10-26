namespace FastShare;

public partial class MainWindow
{
    // =======================================================================
    // Discord status ("Playing Fast Share")
    // =======================================================================
    //
    // Worked out on every UI tick from what the status bar already knows; DiscordPresence sends
    // it only when it changes, and slowly enough for Discord's rate limit. Says what is happening
    // (sending, receiving, percent, speed) but never which files or with whom.

    /// <summary>The idle status's clock: how long Fast Share has been open.</summary>
    private readonly DateTime _openedUtc = DateTime.UtcNow;

    private void UpdateDiscordPresence()
    {
        if (_config.DiscordPresence) DiscordPresence.Show(DiscordActivity());
    }

    private PresenceActivity DiscordActivity()
    {
        // Our own transfers first: they have exact totals and a start time.
        if (_downStats is not null || _upStats is not null)
        {
            var details = (_downStats, _upStats) switch
            {
                ({ }, { }) => "Sending and receiving files",
                ({ }, null) => "Receiving files",
                _ => "Sending files",
            };

            var stats = (_downStats ?? _upStats)!;
            var row = (_downStats is not null ? _downLane : _upLane).ActiveRow;
            var rate = (_downStats is null ? 0 : _downMeter.BytesPerSecond) + (_upStats is null ? 0 : _upMeter.BytesPerSecond);

            var state = stats.Check is { } check
                ? $"Checking files {check.Percent:0}%"
                : Progress(stats.BytesDone, stats.TotalBytes, rate);
            return new PresenceActivity(details, state, row?.Entry.WhenUtc);
        }

        // Someone else's transfer against us: what they are moving, from our side.
        var flows = TrafficMonitor.Active();
        if (flows.Count > 0)
        {
            var receiving = flows.Exists(f => f.Direction == TransferDirection.Download);
            var sending = flows.Exists(f => f.Direction == TransferDirection.Upload);
            var details = receiving && sending ? "Sending and receiving files" : receiving ? "Receiving files" : "Sending files";

            var state = Progress(flows.Sum(f => f.Bytes), flows.All(f => f.Total > 0) ? flows.Sum(f => f.Total) : 0,
                _servedInRate + _servedOutRate);
            return new PresenceActivity(details, state, flows.Min(f => f.StartedUtc));
        }

        return new PresenceActivity(_server.IsRunning ? "Sharing folders" : "Idle", null, _openedUtc);
    }

    /// <summary>"45% · 812 Mbit/s", or just the speed when the total is not known.</summary>
    private static string Progress(long done, long total, double bytesPerSecond)
    {
        var speed = Format.Rate(bytesPerSecond);
        return total > 0 ? $"{Math.Clamp(100.0 * done / total, 0, 100):0}% · {speed}" : speed;
    }
}
