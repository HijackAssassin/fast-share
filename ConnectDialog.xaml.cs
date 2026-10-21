using System.Windows;

namespace FastShare;

/// <summary>
/// Modal "Connecting…" popup. Runs the connect attempt itself so the caller does not have
/// to juggle UI state, and on failure offers Retry without making the user re-enter
/// anything. Returns true only if an attempt succeeded.
/// </summary>
public partial class ConnectDialog : Window
{
    private readonly Func<CancellationToken, Task> _attempt;
    private CancellationTokenSource? _cts;
    private bool _closing;
    private readonly bool _local;

    /// <param name="local">The address is on this network (192.168.x.x and the like), so a
    /// failure is never about port forwarding and the advice should say what it is about.</param>
    public ConnectDialog(Window owner, string target, Func<CancellationToken, Task> attempt, bool local = false)
    {
        InitializeComponent();
        Owner = owner;
        _attempt = attempt;
        _local = local;
        BusyText.Text = $"Connecting to {target}…";
        if (local)
            FailHint.Text = "On the same network this usually means the other PC is not sharing right now, " +
                            "the address has changed (check Share → Same network on that PC), or Windows Firewall " +
                            "on that PC is blocking FastShare on this network.";
        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        BusyPanel.Visibility = Visibility.Visible;
        FailPanel.Visibility = Visibility.Collapsed;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            await _attempt(_cts.Token);
            if (_closing) return;

            _closing = true;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_closing)
        {
            // Only a user-initiated close lands here. A connect timeout also arrives as an
            // OperationCanceledException, and without the _closing guard it was swallowed as
            // if the user had cancelled - leaving the dialog spinning forever.
        }
        catch (Exception ex)
        {
            if (_closing) return;
            ShowFailure(ex);
        }
    }

    private void ShowFailure(Exception ex)
    {
        BusyPanel.Visibility = Visibility.Collapsed;
        FailPanel.Visibility = Visibility.Visible;
        FailText.Text = Describe(ex);
        Title = "Connection failed";
        RetryButton.Focus();
    }

    /// <summary>Turns socket-level exception text into something worth reading.</summary>
    private string Describe(Exception ex)
    {
        var message = ex is AggregateException aggregate
            ? aggregate.GetBaseException().Message
            : ex.Message;

        if (message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            return "The other PC refused the connection — nothing is listening on that port.";

        if (message.Contains("did not properly respond", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            ex is TaskCanceledException or TimeoutException)
        {
            // Worth spelling out, because the obvious thing to try when someone is connected
            // to you is to connect back to them - and that cannot work if their port is shut.
            // Their files are already reachable; just not this way.
            if (_local)
                return "No answer within a few seconds from that local address.";

            return "No answer within a few seconds. The address may be wrong, or the port is not forwarded.\n\n" +
                   "If this person is already connected to you, you do not need to connect back — " +
                   "they appear under \"Sharing with you\" on the Connections tab. Double-click them there.";
        }

        if (message.Contains("RemoteCertificateValidationCallback", StringComparison.OrdinalIgnoreCase))
            return "The certificate did not match this code. The host likely regenerated it — ask for a fresh code.";

        if (message.Contains("401"))
            return "The host rejected the access token. That code is no longer valid — ask for a fresh one.";

        return message;
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await RunAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _closing = true;
        DialogResult = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
