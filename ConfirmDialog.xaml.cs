using System.Windows;

namespace FastShare;

/// <summary>
/// A yes/no question shaped like the connection request prompt.
///
/// MessageBox would do the job, but it looks nothing like the rest of the app - and this one
/// appears immediately after the approval dialog, where a sudden change of style reads as a
/// different kind of question than it is.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(Window owner, string title, string headline, string body,
        string yes, string no)
    {
        InitializeComponent();
        Owner = owner;

        Title = title;
        HeadlineText.Text = headline;
        SubText.Text = title;
        BodyText.Text = body;
        YesButton.Content = yes;
        NoButton.Content = no;
    }

    /// <summary>A button was pressed (rather than the window closed with X).</summary>
    public bool Answered { get; private set; }

    private void OnYesClick(object sender, RoutedEventArgs e) { Answered = true; DialogResult = true; }
    private void OnNoClick(object sender, RoutedEventArgs e) { Answered = true; DialogResult = false; }
}
