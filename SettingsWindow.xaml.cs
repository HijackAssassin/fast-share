using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace FastShare;

/// <summary>
/// Identity and block list. Edits are applied to the config only when Save is pressed, so
/// backing out of the window changes nothing.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(Window owner, AppConfig config)
    {
        InitializeComponent();
        Owner = owner;
        _config = config;

        // Tall enough for everything, but never taller than the screen.
        MaxHeight = SystemParameters.WorkArea.Height;

        UsernameBox.Text = config.Username ?? Environment.MachineName;


        VersionText.Text = $"Version: {AppVersion.Text}";
        PortBox.Text = config.Port.ToString();
        AutoExcludeBox.IsChecked = config.KeepDefenderExcluded;
        TrayBox.IsChecked = config.CloseToTray;
        DevBox.IsChecked = config.DevMode;
        SpeedUnitBox.SelectedIndex = config.SpeedUnit == "MB" ? 1 : 0;
        VerifyBox.IsChecked = config.CheckFiles;

        // Only while something is moving (or it is already on, so it can be switched off).
        if (owner is MainWindow main)
        {
            ShutdownBox.IsChecked = main.ShutdownWhenDone;
            ShutdownBox.IsEnabled = main.HasWorkPending || main.ShutdownWhenDone;
            if (!ShutdownBox.IsEnabled)
                ShutdownNote.Text = "Available while a transfer is running. " + ShutdownNote.Text;
        }

        ShowPortState();
    }

    /// <summary>
    /// Shows the answer from the startup check rather than running one.
    ///
    /// This used to probe every time the window opened, which meant several seconds of
    /// "Checking…" on each visit and an answer that disagreed with the Session tab depending
    /// on what was running at the time. One check, one answer, shown in both places.
    /// </summary>
    private void ShowPortState()
    {
        // Show every answer the moment it lands while this window is open - the first one if
        // the startup check is still running, and the one after Save port.
        Reachability.Updated += OnPortChecked;
        Closed += (_, _) => Reachability.Updated -= OnPortChecked;

        // Still running: say so rather than "not checked yet", which reads like it never will
        // be. The check starts on launch, so this is only ever a second or two.
        if (Reachability.Last is not { } state)
        {
            ShowChecking();
            return;
        }

        PortStatus.Text = state.Headline;
        PortStatus.SetResourceReference(ForegroundProperty, state.Brush);
        PortStatus.ToolTip = state.Explanation;
        PortNote.Text = state.Detail;
    }

    private void OnPortChecked(Reachability.Result result) =>
        Dispatcher.Invoke(() =>
        {
            PortStatus.Text = result.Headline;
            PortStatus.SetResourceReference(ForegroundProperty, result.Brush);
            PortStatus.ToolTip = result.Explanation;
            PortNote.Text = result.Detail;
        });


    private void ShowChecking()
    {
        PortStatus.Text = "Checking…";
        PortStatus.SetResourceReference(ForegroundProperty, "Subtle");
        PortStatus.ToolTip = null;
        PortNote.Text = "Testing the port and trying UPnP…";
    }

    /// <summary>The port box as a number, or null (having said so) when it is not one.</summary>
    private int? ReadPort()
    {
        if (int.TryParse(PortBox.Text.Trim(), out var port) && port is > 0 and <= 65535) return port;

        MessageBox.Show(this, "Port must be a number between 1 and 65535.", "Invalid port",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        PortBox.Focus();
        return null;
    }

    /// <summary>
    /// The code packs the public address and the port, so a new port is a new code: everyone
    /// holding the old one - approved PCs included - is locked out until they get it.
    /// </summary>
    private bool ConfirmPortChange(int port)
    {
        var body =
            $"Your code includes your port, so changing it from {_config.Port} to {port} gives you a new code.\n\n" +
            "• Anyone with your old code - including PCs you already approved - will need the new one.\n" +
            $"• Port {port} must be forwarded on your router, or people outside your network cannot reach you.\n" +
            "• Anyone connected to you now will be disconnected while sharing restarts.";

        return new ConfirmDialog(this, "Change sharing port", "Changing the port changes your code",
            body, "Change port", "Cancel").ShowDialog() == true;
    }

    /// <summary>
    /// Applies the port now (restarting sharing on it) and re-checks it. The same number just
    /// checks again, so this doubles as "is my port open yet?" after forwarding it.
    /// </summary>
    private async void OnSavePortClick(object sender, RoutedEventArgs e)
    {
        if (ReadPort() is not { } port || Owner is not MainWindow main) return;

        SavePortButton.IsEnabled = false;
        try
        {
            if (port == _config.Port)
            {
                ShowChecking();
                await main.RecheckPortAsync();
                return;
            }

            if (!ConfirmPortChange(port)) return;

            ShowChecking();
            if (!await main.ChangePortAsync(port, this))
            {
                PortBox.Text = _config.Port.ToString();
                if (Reachability.Last is { } last) OnPortChecked(last);
            }
        }
        finally
        {
            SavePortButton.IsEnabled = true;
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ReadPort() is not { } port) return;

        // A port typed but not applied with Save port gets the same warning here.
        if (port != _config.Port)
        {
            if (!ConfirmPortChange(port)) return;

            if (Owner is MainWindow owner)
            {
                IsEnabled = false;
                var changed = await owner.ChangePortAsync(port, this);
                IsEnabled = true;
                if (!changed) return;
            }
            else _config.Port = port;
        }

        var username = UsernameBox.Text.Trim();

        // Blank falls back to the machine name rather than storing an empty label.
        _config.Username = string.IsNullOrWhiteSpace(username) ? null : username;

        _config.KeepDefenderExcluded = AutoExcludeBox.IsChecked == true;
        _config.CloseToTray = TrayBox.IsChecked == true;
        _config.DevMode = DevBox.IsChecked == true;
        _config.SpeedUnit = (SpeedUnitBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "Mbit";
        Format.Megabytes = _config.SpeedUnit == "MB";
        _config.CheckFiles = VerifyBox.IsChecked == true;

        // Not saved in config: it belongs to the transfers running now.
        if (Owner is MainWindow main && ShutdownBox.IsEnabled) main.SetShutdownWhenDone(ShutdownBox.IsChecked == true);

        _config.Save();

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    // =======================================================================
    // Updates
    // =======================================================================

    /// <summary>
    /// Pulls the latest build from the maintainer's PC and swaps it in. There is no GitHub any
    /// more; the build lives on their machine, served over the same forwarded port sharing
    /// uses, so this only works while they are online and sharing.
    /// </summary>
    private void OnSpeedTestClick(object sender, RoutedEventArgs e)
    {
        if (Owner is not MainWindow main) return;
        SpeedTestStatus.Text = main.StartSpeedTests();
    }

    /// <summary>Runs Check for updates as if pressed (the main window's "Update available" button).</summary>
    public void StartUpdate() => OnUpdateClick(this, new RoutedEventArgs());

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateStatus.Visibility = Visibility.Visible;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "Checking for a newer build…";

        // Swapping the exe cancels whatever is running, so it waits until nothing is.
        if (Owner is MainWindow { IsTransferring: true })
        {
            UpdateStatus.Text = "A transfer is in progress. Check for updates again once it has finished.";
            UpdateButton.IsEnabled = true;
            return;
        }

        // Always pinned to the maintainer's own certificate, so a build can only ever come from
        // their PC - not from whoever answers on the address, and not on trust-on-first-use.
        var fingerprint = AppConfig.MaintainerFingerprint;

        // First the maintainer's update server (it can hand out a build before it is public);
        // only if nobody answers there, the download on the website.
        var build = await Updater.CheckAsync(_config.UpdateHost, _config.UpdatePort, fingerprint);
        WebUpdate.Build? web = null;

        if (build is null)
        {
            UpdateStatus.Text = "Checking the website for a newer build…";
            web = await WebUpdate.CheckAsync();
            if (web is null)
            {
                UpdateStatus.Text =
                    "Couldn't check for updates: the maintainer's update server isn't open and the " +
                    "website couldn't be reached. Try again later.";
                UpdateButton.IsEnabled = true;
                return;
            }
        }

        var offered = build?.Version ?? web!.Version;
        var size = build?.Bytes ?? web!.Bytes;
        var from = build is not null ? "the maintainer's PC" : new Uri(WebUpdate.Used).Authority;

        if (!Updater.IsNewer(offered))
        {
            UpdateStatus.Text = $"You're up to date (v{AppVersion.Text}).";
            UpdateButton.IsEnabled = true;
            return;
        }

        var ask = new ConfirmDialog(this, "Update available",
            $"FastShare {offered} is available",
            $"You have {AppVersion.Text}. The download is {(size > 0 ? Format.Bytes(size) : "about 70 MB")}, from {from}.\n\n" +
            "FastShare will close, install the update in the same folder and open again by itself. " +
            "Anyone connected to you will be told you are restarting.",
            "Download", "No thanks");

        if (ask.ShowDialog() != true)
        {
            UpdateStatus.Text = $"v{offered} is available whenever you want it.";
            UpdateButton.IsEnabled = true;
            return;
        }

        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;

        try
        {
            void Progress(long received, long total) => Dispatcher.BeginInvoke(() =>
            {
                UpdateProgress.Value = total > 0 ? received * 100.0 / total : 0;
                UpdateStatus.Text = total > 0
                    ? $"Downloading v{offered}… {Format.Bytes(received)} of {Format.Bytes(total)}"
                    : $"Downloading v{offered}… {Format.Bytes(received)}";
            });

            var zip = build is not null
                ? await Updater.DownloadAsync(_config.UpdateHost, _config.UpdatePort, fingerprint, Progress, CancellationToken.None)
                : await WebUpdate.DownloadAsync(web!, Progress, CancellationToken.None);

            UpdateStatus.Text = "Installing and restarting…";

            // Swaps the files, launches the new copy, and hands it our id so it waits for us
            // to exit before binding the share port. Then we go.
            Updater.Apply(zip);

            // Out through the normal close, so everyone connected is told we are going.
            // (Application.Shutdown skipped that, leaving them to notice the silence.)
            Close();
            _ = Application.Current.Dispatcher.BeginInvoke(() => (Application.Current.MainWindow as MainWindow)?.QuitApp());
        }
        catch (Exception ex)
        {
            Diagnostics.Log("update", "failed to apply", ex);
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateStatus.Text = $"Update failed: {ex.Message}";
            UpdateButton.IsEnabled = true;
        }
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.IO.File.Exists(Diagnostics.FilePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                    $"/select,\"{Diagnostics.FilePath}\"") { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show(this, "Nothing has been logged yet.", "No log",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open the log",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
