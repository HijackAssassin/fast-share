using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace FastShare;

public partial class MainWindow
{
    // =======================================================================
    // Shut down this PC when all transfers finish
    // =======================================================================
    //
    // For leaving a big transfer running overnight. Only for the transfers running now: it is
    // not saved, it can only be switched on while something is moving, and it switches itself
    // off before shutting down - so the next launch never shuts the PC down on its own.

    /// <summary>Armed: shut down once nothing is left to move.</summary>
    public bool ShutdownWhenDone { get; private set; }

    /// <summary>Automatic retries waiting to start (the 15-second pause before one).</summary>
    private int _pendingRetries;

    /// <summary>Since when nothing has been moving, while armed.</summary>
    private DateTime? _idleSinceUtc;

    /// <summary>
    /// Anything still to do: our transfers and their queue, other people's transfers against
    /// us, and automatic retries about to start. A retry pauses 15 seconds first, and in that
    /// pause nothing is "running" - without counting it the PC would shut down just before the
    /// transfer resumed.
    /// </summary>
    public bool HasWorkPending => TransferInProgress() || _pendingRetries > 0 || TrafficMonitor.Queued().Count > 0;

    public void SetShutdownWhenDone(bool on)
    {
        // Only for transfers happening now; switching it on with nothing moving would shut the
        // PC down fifteen seconds later.
        if (on && !HasWorkPending) on = false;
        if (on == ShutdownWhenDone) return;

        ShutdownWhenDone = on;
        _idleSinceUtc = null;
        ActivityLog.Note(TransferDirection.Download,
            on ? "This PC will shut down when all transfers finish" : "Shut down when transfers finish: off",
            LogKind.Network);
        Diagnostics.Log("shutdown", on ? "armed" : "disarmed");
    }

    /// <summary>
    /// Called every UI tick. Waits for 15 seconds with nothing moving - long enough that the
    /// gap between two queued jobs, or between someone else's bundles, does not count as the
    /// end - then switches the option off and starts the countdown.
    /// </summary>
    private void CheckShutdownWhenDone()
    {
        if (!ShutdownWhenDone) return;

        if (HasWorkPending)
        {
            _idleSinceUtc = null;
            if (!TransferText.Text.EndsWith(ShutdownSuffix, StringComparison.Ordinal))
                TransferText.Text += ShutdownSuffix;
            return;
        }

        _idleSinceUtc ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _idleSinceUtc < TimeSpan.FromSeconds(15)) return;

        ShutdownWhenDone = false;   // unticked first, whatever happens next
        _idleSinceUtc = null;
        Diagnostics.Log("shutdown", "all transfers finished - shutting down");
        ActivityLog.Note(TransferDirection.Download, "All transfers finished - shutting down this PC", LogKind.Network);
        ShutDownWindows();
    }

    private const string ShutdownSuffix = "  · PC shuts down when done";

    /// <summary>
    /// A normal Windows shutdown - not forced, so a program with unsaved work gets to object.
    /// FastShare closes like at any logoff (it says goodbye to connected PCs on the way out).
    /// FASTSHARE_NO_SHUTDOWN=1 only logs it, for testing.
    /// </summary>
    private static void ShutDownWindows()
    {
        if (Environment.GetEnvironmentVariable("FASTSHARE_NO_SHUTDOWN") == "1")
        {
            Diagnostics.Log("shutdown", "would shut down now (FASTSHARE_NO_SHUTDOWN is set)");
            return;
        }

        Diagnostics.Log("shutdown", "shutting down Windows");
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Diagnostics.Log("shutdown", "could not start the shutdown", ex);
            ActivityLog.Error("Could not shut down the PC: " + ex.Message);
        }
    }
}
