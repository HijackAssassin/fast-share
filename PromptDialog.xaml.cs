using System.IO;
using System.Windows;
using System.Windows.Input;

namespace FastShare;

/// <summary>
/// Asks for a single name — a new folder, a new file, a rename.
///
/// It validates here rather than letting the far side reject it, because a name with a slash
/// or a colon in it is a mistake worth catching before it becomes an error message.
/// </summary>
public partial class PromptDialog : Window
{
    public string Value => ValueBox.Text.Trim();

    public PromptDialog(Window owner, string title, string label, string initial = "",
        string? hint = null, string? cancelText = null)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        if (cancelText is not null) CancelButton.Content = cancelText;
        LabelText.Text = label;
        ValueBox.Text = initial;

        if (hint is not null)
        {
            HintText.Text = hint;
            HintText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            ValueBox.Focus();

            // Select just the stem, so renaming "holiday.jpg" does not make you retype ".jpg".
            var stem = Path.GetFileNameWithoutExtension(initial);
            if (stem.Length > 0 && stem.Length < initial.Length) ValueBox.Select(0, stem.Length);
            else ValueBox.SelectAll();
        };
    }

    /// <summary>Shows the prompt and returns the name, or null if cancelled or unusable.</summary>
    public static string? Ask(Window owner, string title, string label, string initial = "",
        string? hint = null, string? cancelText = null)
    {
        var dialog = new PromptDialog(owner, title, label, initial, hint, cancelText);
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private bool IsUsable()
    {
        var name = Value;
        return name.Length > 0
               && name is not ("." or "..")
               && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
               && !name.Contains(Path.DirectorySeparatorChar)
               && !name.Contains(Path.AltDirectorySeparatorChar);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!IsUsable())
        {
            HintText.Text = "That name cannot be used — avoid \\ / : * ? \" < > |";
            HintText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOkClick(sender, e);
        else if (e.Key == Key.Escape) DialogResult = false;
    }
}
