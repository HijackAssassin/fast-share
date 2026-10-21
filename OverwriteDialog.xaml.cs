using System.Windows;

namespace FastShare;

/// <summary>
/// "This will overwrite N files", with the names in a box beneath: three at first, "Show more"
/// opens it to ten lines, and anything past ten scrolls. Answers the same three ways the old
/// message box did, so callers read it as Yes (overwrite) / No (skip those) / Cancel.
/// </summary>
public partial class OverwriteDialog : Window
{
    private const int Collapsed = 3;
    private const int Expanded = 10;
    private const double LineHeight = 20;

    private readonly IReadOnlyList<string> _names;

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

    public OverwriteDialog(Window owner, IReadOnlyList<string> names, string where, string verb)
    {
        InitializeComponent();
        Owner = owner;
        _names = names;

        HeadlineText.Text = $"This will overwrite {names.Count:N0} file(s) {where}";
        SkipButton.Content = $"Skip these, {verb} the rest";

        NamesList.ItemsSource = names.Take(Collapsed).ToList();
        MoreButton.Content = $"Show more ({names.Count - Collapsed:N0} more)";
        MoreButton.Visibility = names.Count > Collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        NamesList.ItemsSource = _names;
        NamesScroller.MaxHeight = Expanded * LineHeight;
        NamesScroller.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        MoreButton.Visibility = Visibility.Collapsed;
    }

    private void OnOverwriteClick(object sender, RoutedEventArgs e) { Result = MessageBoxResult.Yes; DialogResult = true; }
    private void OnSkipClick(object sender, RoutedEventArgs e) { Result = MessageBoxResult.No; DialogResult = true; }
    private void OnCancelClick(object sender, RoutedEventArgs e) { Result = MessageBoxResult.Cancel; DialogResult = false; }
}
