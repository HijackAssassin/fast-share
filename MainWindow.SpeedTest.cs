namespace FastShare;

public partial class MainWindow
{
    // =======================================================================
    // Speed test (SpeedTest.cs does the measuring)
    // =======================================================================
    //
    // The side that connected out runs it, because only it can open connections to the other.
    // So from Settings: every host we are connected to is measured directly, and everyone
    // connected to us is asked - on their next ping - to measure the line to us and send the
    // figures back.

    private bool _speedTesting;

    /// <summary>Starts every test it can; returns a line for the Settings window.</summary>
    public string StartSpeedTests()
    {
        if (TransferInProgress())
            return "A transfer is running, so the test would measure what is left over. Try again once it has finished.";

        var hosts = _connections
            .Where(c => c.Kind == ConnectionKind.Outbound && c.Api is { } api && api.HostCaps?.Contains(Caps.SpeedTest) == true)
            .ToList();
        foreach (var host in hosts) _ = RunSpeedTestAsync(host, reportBack: false);

        var asked = _server.IsSharing ? _server.Sessions.AskForSpeedTest() : 0;

        if (hosts.Count == 0 && asked == 0)
            return "Nobody to test against. Connect to someone (or have them connect to you) first; both need FastShare 2.4.7 or newer.";

        var parts = new List<string>();
        if (hosts.Count > 0) parts.Add($"testing the line to {string.Join(", ", hosts.Select(h => h.Name))}");
        if (asked > 0) parts.Add($"asked {asked} connected PC(s) to test the line to you");
        return char.ToUpper(parts[0][0]) + string.Join("; ", parts)[1..] +
               ". About 40 seconds; results appear in the status bar and the Activity Log.";
    }

    private async Task RunSpeedTestAsync(Connection connection, bool reportBack)
    {
        if (_speedTesting || connection.Api is not { } api) return;

        // A running transfer would share the line with the test and both figures would be wrong.
        if (TransferInProgress())
        {
            if (reportBack) await api.SendSpeedResultAsync("skipped - a transfer is running on this PC");
            return;
        }

        _speedTesting = true;
        try
        {
            // Off the UI thread: every read resuming on the dispatcher would throttle the very
            // thing being measured.
            var result = await Task.Run(() => SpeedTest.RunAsync(api,
                text => Dispatcher.BeginInvoke(() => FlashStatus(text)), CancellationToken.None));

            var line = $"Speed test to {connection.Name}: {result.Summary}";
            ActivityLog.Note(TransferDirection.Download, line, LogKind.Network);
            FlashStatus(line);
            if (reportBack) await api.SendSpeedResultAsync(result.Summary);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("speedtest", $"to {connection.Name} failed", ex);
            FlashStatus($"Speed test to {connection.Name} failed: {ex.Message}");
            if (reportBack) await api.SendSpeedResultAsync($"failed: {ex.Message}");
        }
        finally
        {
            _speedTesting = false;
        }
    }

    /// <summary>Someone we asked sent their figures back (arrives off the UI thread).</summary>
    private void OnSpeedTestReported(string text) => Dispatcher.BeginInvoke(() =>
    {
        ActivityLog.Note(TransferDirection.Upload, "Speed test: " + text, LogKind.Network);
        FlashStatus("Speed test: " + text);
        NotifyIfAway(("Speed test finished", text));
    });
}
