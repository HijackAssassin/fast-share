using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace FastShare;

/// <summary>Inverts a bool, for XAML that needs the opposite of a property it already has.</summary>
public sealed class NotBoolConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>
/// The handful of visual-tree lookups the code-behind needs.
///
/// Kept apart from ViewModels.cs deliberately: that file is compiled by the test harnesses,
/// which have no WPF, and pulling System.Windows into it would break them.
/// </summary>
public static class WpfHelpers
{
    /// <summary>The ListBoxItem an event came from, or null if it came from the empty area.</summary>
    public static ListBoxItem? FindListBoxItem(this DependencyObject? from)
    {
        while (from is not null)
        {
            if (from is ListBoxItem item) return item;
            from = VisualTreeHelper.GetParent(from);
        }
        return null;
    }
}
