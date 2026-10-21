using System.Windows;
using System.Windows.Threading;

namespace FastShare;

public partial class MainWindow
{
    // =======================================================================
    // Quiet update check
    // =======================================================================
    //
    // Check for updates used to happen only when someone opened Settings and pressed the
    // button, so friends ran old builds for weeks. Now FastShare asks by itself - as soon as it
    // starts and every hour after - in the same order as the button (the maintainer's update
    // server, then the website), and if there is something newer shows "Update available" next
    // to the settings gear. Nothing is downloaded or installed without a click.

    private DispatcherTimer? _updateTimer;
    private string? _updateOffered;

    /// <summary>
    /// First thing at launch, then hourly. It used to wait 20 seconds and then 12 hours, so a
    /// build published just after you started FastShare went unnoticed until the next day.
    /// Each check is one small request (two when the update server is closed).
    /// </summary>
    private void StartUpdateChecks()
    {
        _ = CheckForUpdateQuietlyAsync();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _updateTimer.Tick += async (_, _) => await CheckForUpdateQuietlyAsync();
        _updateTimer.Start();
    }

    private async Task CheckForUpdateQuietlyAsync()
    {
        try
        {
            string? offered = null;
            var peer = await Updater.CheckAsync(_config.UpdateHost, _config.UpdatePort, AppConfig.MaintainerFingerprint);
            if (peer is not null) offered = peer.Version;
            else if (await WebUpdate.CheckAsync() is { } web) offered = web.Version;

            if (offered is null || !Updater.IsNewer(offered)) return;
            if (offered == _updateOffered) return;

            _updateOffered = offered;
            UpdateAvailableText.Text = $"Update available (v{offered})";
            UpdateAvailableButton.Visibility = Visibility.Visible;
            Diagnostics.Log("update", $"v{offered} is available (quiet check)");
            NotifyIfAway(("FastShare update available", $"Version {offered} is ready. Click \"Update available\" to install it."));
        }
        catch (Exception ex)
        {
            Diagnostics.Log("update", "quiet check failed", ex);
        }
    }

    /// <summary>Opens Settings and starts the update, exactly as pressing Check for updates there.</summary>
    private void OnUpdateAvailableClick(object sender, RoutedEventArgs e) =>
        OpenSettings(startUpdate: true);
}
