using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace FastShare;

/// <summary>
/// The full contents behind a batched transfer line.
///
/// A transfer of ten thousand files gets one row on the Session tab; this is where the ten
/// thousand names live, on demand. The list virtualizes, so however long it is only the rows
/// on screen are ever built - opening the details of a huge transfer is instant either way.
/// When files failed, a switch shows just those, each with why.
/// </summary>
public partial class DetailsDialog : Window
{
    private readonly IReadOnlyList<string> _all;
    private readonly IReadOnlyList<string> _failed;
    private IReadOnlyList<string> _shown;

    public DetailsDialog(Window owner, string heading, IReadOnlyList<string> lines,
        IReadOnlyList<string>? failed = null, bool showFailed = false)
    {
        InitializeComponent();
        Owner = owner;
        Heading.Text = heading;
        _all = lines;
        _shown = lines;

        // "folder/file.png: reason" becomes the name, then its reason indented beneath it.
        _failed = (failed ?? []).SelectMany(f =>
        {
            var split = f.LastIndexOf(": ", System.StringComparison.Ordinal);
            return split > 0
                ? new[] { f[..split], "    " + f[(split + 2)..] }
                : new[] { f };
        }).ToList();

        if (failed is { Count: > 0 })
        {
            Switch.Visibility = Visibility.Visible;
            AllButton.Content = "All files";
            FailedButton.Content = $"Failed ({failed.Count:N0})";
            if (showFailed) FailedButton.IsChecked = true;
        }

        Lines.ItemsSource = _shown;
    }

    private void OnViewChanged(object sender, RoutedEventArgs e)
    {
        if (Lines is null) return;
        _shown = FailedButton.IsChecked == true ? _failed : _all;
        Lines.ItemsSource = _shown;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(string.Join(System.Environment.NewLine, _shown)); }
        catch { /* another app can hold the clipboard open; nothing useful to do */ }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
