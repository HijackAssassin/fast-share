using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FastShare;

/// <summary>One line in Folder Permissions: a shared folder, or a folder inside one with its own rule.</summary>
public sealed class FolderRuleRow
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsShare { get; init; }
    public FolderRule? Rule { get; init; }
    public string Summary => Rule?.Summary ?? "no limits";
}

public partial class MainWindow
{
    // =======================================================================
    // Folder Permissions (Share tab, third column)
    // =======================================================================
    //
    // Limits on particular folders that beat whatever a user may do. Stored in
    // config.FolderRules by real path and enforced by the server through ShareSet.Allows, so
    // they apply to everyone connected - including someone browsing us back over a tunnel -
    // and never to this PC browsing its own shares.

    private bool _suppressRuleWrite;

    private FolderRule? RuleFor(string path)
    {
        var key = FolderRule.Normalise(path);
        return _config.FolderRules.FirstOrDefault(r =>
            string.Equals(FolderRule.Normalise(r.Path), key, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshFolderRules(string? select = null)
    {
        var rows = new List<FolderRuleRow>();
        foreach (var share in _config.Folders)
        {
            var root = FolderRule.Normalise(share);
            rows.Add(new FolderRuleRow
            {
                Name = Path.GetFileName(root) is { Length: > 0 } n ? n : root,
                Path = root,
                IsShare = true,
                Rule = RuleFor(root),
            });

            // Rules on folders inside this share, listed under it.
            foreach (var rule in _config.FolderRules
                         .Where(r => FolderRule.Normalise(r.Path) is var p && !p.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                                     p.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
            {
                var path = FolderRule.Normalise(rule.Path);
                rows.Add(new FolderRuleRow
                {
                    Name = "    " + (Path.GetFileName(root) is { Length: > 0 } n2 ? n2 : root) + "\\" + Path.GetRelativePath(root, path),
                    Path = path,
                    Rule = rule,
                });
            }
        }

        var keep = select ?? (FolderRuleList.SelectedItem as FolderRuleRow)?.Path;
        FolderRuleList.ItemsSource = rows;
        FolderRuleList.SelectedItem = rows.FirstOrDefault(r => string.Equals(r.Path, keep, StringComparison.OrdinalIgnoreCase));
        if (FolderRuleList.SelectedItem is null) FolderRuleEditor.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Clicking the folder that is already selected closes its panel again, like the user list.
    /// </summary>
    private void OnFolderRuleListClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FolderRuleList.SelectedItem is null) return;

        var clicked = (e.OriginalSource as DependencyObject).FindListBoxItem();
        if (clicked?.DataContext != FolderRuleList.SelectedItem) return;

        e.Handled = true;
        FolderRuleList.SelectedItem = null;
    }

    /// <summary>Right-clicking a folder does nothing: its panel opens on a left-click only.</summary>
    private void OnFolderRuleRightDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;

    private void OnFolderRuleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FolderRuleList.SelectedItem is not FolderRuleRow row)
        {
            FolderRuleEditor.Visibility = Visibility.Collapsed;
            RemoveFolderRuleButton.IsEnabled = false;
            return;
        }

        RemoveFolderRuleButton.IsEnabled = !row.IsShare;
        FolderRuleEditor.Visibility = Visibility.Visible;
        FolderRuleName.Text = row.Name.Trim();

        var rule = row.Rule ?? new FolderRule();
        _suppressRuleWrite = true;
        try
        {
            RuleDownload.IsChecked = rule.Download;
            RuleSend.IsChecked = rule.Send;
            RuleModify.IsChecked = rule.Modify;
            RuleDelete.IsChecked = rule.Delete;
        }
        finally { _suppressRuleWrite = false; }
    }

    private void OnFolderRuleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressRuleWrite || FolderRuleList.SelectedItem is not FolderRuleRow row) return;

        var rule = RuleFor(row.Path);
        if (rule is null)
        {
            rule = new FolderRule { Path = row.Path };
            _config.FolderRules.Add(rule);
        }

        rule.Download = RuleDownload.IsChecked == true;
        rule.Send = RuleSend.IsChecked == true;
        rule.Modify = RuleModify.IsChecked == true;
        rule.Delete = RuleDelete.IsChecked == true;

        // A shared folder with everything on is the same as no rule; keep the list tidy.
        // A rule on an inner folder stays listed until it is removed.
        if (row.IsShare && rule.IsOpen) _config.FolderRules.Remove(rule);

        ApplyFolderRules();
        FolderRuleStatus.Text = rule.IsOpen
            ? "No limits here: each person's own permissions apply."
            : $"Now {rule.Summary} in {row.Name.Trim()} and everything inside it, for everyone.";
        RefreshFolderRules(row.Path);
    }

    private void OnAddFolderRuleClick(object sender, RoutedEventArgs e)
    {
        if (_config.Folders.Count == 0)
        {
            MessageBox.Show(this, "Add a shared folder first.", "Folder Permissions",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var start = (FolderRuleList.SelectedItem as FolderRuleRow)?.Path ?? _config.Folders[0];
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder inside one of your shared folders",
            InitialDirectory = Directory.Exists(start) ? start : null,
        };
        if (dialog.ShowDialog(this) != true) return;

        var chosen = FolderRule.Normalise(dialog.FolderName);
        var inside = _config.Folders.Select(FolderRule.Normalise).Any(root =>
            chosen.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            chosen.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase));

        if (!inside)
        {
            MessageBox.Show(this, "That folder is not inside any of the folders you share, so nobody can reach it anyway.",
                "Folder Permissions", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (RuleFor(chosen) is null)
        {
            // Starts with nothing turned off; the switches below it set the limits.
            _config.FolderRules.Add(new FolderRule { Path = chosen });
            _config.Save();
        }
        RefreshFolderRules(chosen);
    }

    private void OnRemoveFolderRuleClick(object sender, RoutedEventArgs e)
    {
        if (FolderRuleList.SelectedItem is not FolderRuleRow { IsShare: false } row) return;
        if (RuleFor(row.Path) is { } rule) _config.FolderRules.Remove(rule);
        ApplyFolderRules();
        RefreshFolderRules();
    }

    /// <summary>Saves the rules and hands them to the running server, which applies them at once.</summary>
    private void ApplyFolderRules()
    {
        _config.Save();
        if (_server.IsRunning) _server.UpdateShares(_config.Folders, _config.FolderRules);
        ActivityLog.Note(TransferDirection.Upload, "Folder permissions updated", LogKind.Network);
    }

    /// <summary>A share that is no longer shared takes its folder rules with it.</summary>
    private void DropRulesUnder(string share)
    {
        var root = FolderRule.Normalise(share);
        _config.FolderRules.RemoveAll(r =>
        {
            var p = FolderRule.Normalise(r.Path);
            return p.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase);
        });
    }

    // =======================================================================
    // Blocked (Connections tab, under Favorites)
    // =======================================================================

    private void RefreshBlocked()
    {
        var list = _config.Blocked.OrderBy(b => b.Username, StringComparer.OrdinalIgnoreCase).ToList();
        BlockedPeersList.ItemsSource = list;
        var any = list.Count > 0;
        BlockedPeersList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        UnblockPeerButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        BlockedEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        UnblockPeerButton.IsEnabled = false;

        foreach (var row in _recent) row.IsBlocked = IsBlockedPeer(row.Model.PeerId);
        foreach (var row in _favorites) row.IsBlocked = IsBlockedPeer(row.PeerId);
    }

    private bool IsBlockedPeer(string? peerId) =>
        !string.IsNullOrEmpty(peerId) &&
        _config.Blocked.Any(b => string.Equals(b.PeerId, peerId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Blocks someone from a Recent or Favorites row: asks, adds them to the block list, and
    /// ends anything open with them. They stay in Recent/Favorites (marked Blocked), so the same
    /// right-click can unblock them.
    /// </summary>
    private void BlockFromList(string peerId, string name)
    {
        var confirm = MessageBox.Show(this,
            $"""
             Block {name}?

             They lose access now and cannot connect to you until you unblock them. They stay in your lists - right-click them again to unblock.
             """,
            "Block", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        if (!IsBlockedPeer(peerId))
        {
            _config.Blocked.Add(new BlockedPeer { PeerId = peerId, Username = name, BlockedUtc = DateTime.UtcNow });
            _config.Save();
        }

        _server.Sessions.Revoke(peerId, DenyReasons.Blocked);
        _server.Tunnels.Drop(peerId);
        foreach (var open in _connections.Where(c => string.Equals(PeerIdOf(c), peerId, StringComparison.OrdinalIgnoreCase)).ToList())
            CloseConnection(open, announce: false);

        ActivityLog.Note(TransferDirection.Upload, $"Blocked {name}", LogKind.Network);
        UpdateConnectionSummary();
        RefreshBlocked();
    }

    private void UnblockPeer(string peerId, string name)
    {
        _config.Blocked = _config.Blocked
            .Where(b => !string.Equals(b.PeerId, peerId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _config.Save();
        ActivityLog.Note(TransferDirection.Download,
            $"Unblocked {name} — they can ask to connect again", LogKind.Network);
        RefreshBlocked();
    }

    /// <summary>
    /// Before dialling someone we blocked: are you sure? Blocking stops them reaching us, so
    /// connecting to them is allowed - just never by accident. True = go ahead.
    /// </summary>
    private bool ConfirmConnectBlocked(string? peerId, string name)
    {
        if (!IsBlockedPeer(peerId)) return true;
        return MessageBox.Show(this,
            $"You blocked {name}. Are you sure you want to connect to them?\n\n" +
            "They stay blocked: they still cannot connect to you or share back.",
            "Blocked user", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void OnBlockedSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UnblockPeerButton.IsEnabled = BlockedPeersList.SelectedItem is BlockedPeer;

    private void OnUnblockPeerClick(object sender, RoutedEventArgs e)
    {
        if (BlockedPeersList.SelectedItem is not BlockedPeer peer) return;
        UnblockPeer(peer.PeerId, peer.Username);
    }
}
