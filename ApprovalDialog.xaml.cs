using System.Windows;

namespace FastShare;

public enum ApprovalDecision { Reject, Accept, Block }

/// <summary>One shared folder, offered to this peer or withheld from them.</summary>
public sealed class ShareChoice
{
    public required string Name { get; init; }
    public bool IsSelected { get; set; } = true;
}

/// <summary>
/// The prompt that decides whether a peer gets in, and with what.
///
/// Because codes are short and guessable, this dialog - not the code - is what actually keeps
/// strangers out. It therefore defaults to the least dangerous answer: download only, and
/// closing the window counts as a rejection rather than an approval.
/// </summary>
public partial class ApprovalDialog : Window
{
    private readonly List<ShareChoice> _choices;

    public ApprovalDecision Decision { get; private set; } = ApprovalDecision.Reject;
    public Permissions Granted { get; private set; } = Permissions.ReadOnly;

    /// <summary>True if the prompt closed itself because nobody is waiting for the answer any more.</summary>
    public bool Withdrawn { get; private set; }

    /// <summary>"Allow this user to connect without requesting", as ticked when they answered.</summary>
    public bool AutoApprove => AutoApproveBox.IsChecked == true;

    /// <summary>Pre-ticks the box for someone already allowed in without asking.</summary>
    public bool AutoApproveSuggested { set => AutoApproveBox.IsChecked = value; }

    private readonly System.Windows.Threading.DispatcherTimer _stillWanted;

    public ApprovalDialog(SessionStore.PendingApproval pending, string? nickname = null,
        Func<bool>? stillWaiting = null)
    {
        InitializeComponent();

        // The request can time out, or the person asking can give up, while this sits here.
        // Answering then would do nothing, so the prompt closes itself instead.
        _stillWanted = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _stillWanted.Tick += (_, _) =>
        {
            if (stillWaiting is null || stillWaiting()) return;
            _stillWanted.Stop();
            Withdrawn = true;
            DialogResult = false;
        };
        _stillWanted.Start();
        Closed += (_, _) => _stillWanted.Stop();

        // Shown under whatever name this machine knows them by. Seeing a request from
        // "DESKTOP-4KQ1P8" when you have called them Johannes everywhere else is a puzzle
        // nobody should have to solve while deciding whether to let someone in.
        var shown = string.IsNullOrWhiteSpace(nickname) ? pending.Username : nickname!;

        HeadlineText.Text = $"{shown} wants to connect";
        FromText.Text = string.IsNullOrWhiteSpace(nickname)
            ? pending.Username
            : $"{nickname} ({pending.Username})";

        switch (pending.Identity)
        {
            case IdentityCheck.Mismatch:
                // Never offered to someone who could not prove who they are.
                AutoApproveBox.IsEnabled = false;
                AutoApproveNote.Text = "Not available: this PC could not prove who it is.";

                // Their own name, not ours for the peer they claim to be - the nickname is
                // exactly what an impostor would be borrowing.
                HeadlineText.Text = $"{pending.Username} wants to connect";
                FromText.Text = $"{pending.Username} — claims to be {shown}";
                IdentityText.Text =
                    $"⚠  This can't prove it's the same PC as the {shown} you let in before. " +
                    $"It may be someone else using their ID - or {shown} on an old version or after a reinstall. " +
                    "The switches have been reset to download-only. If you weren't expecting this, Kick.";
                IdentityBox.Visibility = Visibility.Visible;
                break;

            case IdentityCheck.Verified:
                IdentityText.Text = "✓  Verified — the same PC you let in before.";
                IdentityText.Foreground = (System.Windows.Media.Brush)FindResource("Good");
                IdentityBox.Visibility = Visibility.Visible;
                IdentityBox.Background = System.Windows.Media.Brushes.Transparent;
                IdentityBox.BorderThickness = new Thickness(0);
                IdentityBox.Padding = new Thickness(0);
                break;

            case IdentityCheck.Unverified when !string.IsNullOrWhiteSpace(nickname):
                IdentityText.Text = "Their FastShare is an older version, so it can't prove it's the same PC as before.";
                IdentityText.Foreground = (System.Windows.Media.Brush)FindResource("Subtle");
                IdentityBox.Visibility = Visibility.Visible;
                IdentityBox.Background = System.Windows.Media.Brushes.Transparent;
                IdentityBox.BorderThickness = new Thickness(0);
                IdentityBox.Padding = new Thickness(0);
                break;
        }
        AddressText.Text = pending.RemoteAddress;
        IdText.Text = pending.PeerId;

        // Pre-select whatever you granted this peer last time. The prompt still appears every
        // time - a remembered answer is a convenience, not a reason to skip asking.
        DownloadSwitch.IsChecked = pending.Suggested.Download;
        SendSwitch.IsChecked = pending.Suggested.Send;
        ModifySwitch.IsChecked = pending.Suggested.Modify;
        DeleteSwitch.IsChecked = pending.Suggested.Delete;

        // A null or empty remembered list means every folder, which is also the default for
        // someone we have never seen.
        var previously = pending.Suggested.Shares;
        var everything = previously is null || previously.Count == 0;

        _choices = pending.AvailableShares
            .Select(name => new ShareChoice
            {
                Name = name,
                IsSelected = everything || previously!.Contains(name, StringComparer.OrdinalIgnoreCase),
            })
            .ToList();

        ShareChoices.ItemsSource = _choices;

        // Nothing to choose between if only one folder is shared.
        if (_choices.Count < 2)
        {
            FoldersHeading.Visibility = Visibility.Collapsed;
            FoldersBox.Visibility = Visibility.Collapsed;
        }
    }

    private Permissions Current()
    {
        var chosen = _choices.Where(c => c.IsSelected).Select(c => c.Name).ToList();

        // Storing null rather than "all of them" keeps the grant correct when folders are
        // added later: an explicit list would silently withhold anything shared afterwards.
        var shares = chosen.Count == _choices.Count ? null : chosen;

        return new Permissions(
            DownloadSwitch.IsChecked == true,
            SendSwitch.IsChecked == true,
            DeleteSwitch.IsChecked == true,
            shares,
            ModifySwitch.IsChecked == true);
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        Decision = ApprovalDecision.Accept;
        Granted = Current();
        DialogResult = true;
    }

    private void OnRejectClick(object sender, RoutedEventArgs e)
    {
        Decision = ApprovalDecision.Reject;
        DialogResult = false;
    }

    private void OnBlockClick(object sender, RoutedEventArgs e)
    {
        Decision = ApprovalDecision.Block;
        DialogResult = false;
    }
}
