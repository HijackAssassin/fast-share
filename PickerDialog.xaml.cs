using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FastShare;

/// <summary>
/// Picks files and folders together, in any mix.
///
/// Windows has no dialog that does this. OpenFileDialog does files, OpenFolderDialog does
/// folders, and neither will let you take two files and a folder in one go - so choosing
/// which kind you wanted had to be asked up front, before the picker even opened. This is a
/// small browser instead: highlight anything, add it, browse somewhere else, add more.
/// </summary>
public partial class PickerDialog : Window
{
    /// <summary>Everything chosen, once the dialog closes with true.</summary>
    public List<string> Chosen { get; } = [];

    private string _current = "";

    public PickerDialog(Window owner, string? startIn)
    {
        InitializeComponent();
        Owner = owner;

        Places.ItemsSource = BuildPlaces();

        var start = Directory.Exists(startIn)
            ? startIn!
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Navigate(start);
    }

    // =======================================================================
    // Places and navigation
    // =======================================================================

    private sealed record Place(string Name, string Path);

    private static List<Place> BuildPlaces()
    {
        var places = new List<Place>();

        void Add(string name, Environment.SpecialFolder folder)
        {
            var path = Environment.GetFolderPath(folder);
            if (Directory.Exists(path)) places.Add(new Place(name, path));
        }

        Add("Desktop", Environment.SpecialFolder.DesktopDirectory);
        Add("Documents", Environment.SpecialFolder.MyDocuments);
        Add("Pictures", Environment.SpecialFolder.MyPictures);
        Add("Videos", Environment.SpecialFolder.MyVideos);

        // Downloads has no SpecialFolder of its own, so it is built from the profile path.
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) places.Add(new Place("Downloads", downloads));

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            places.Add(new Place(drive.Name.TrimEnd(Path.DirectorySeparatorChar), drive.RootDirectory.FullName));
        }

        return places;
    }

    private void Navigate(string path)
    {
        try
        {
            var entries = new List<Row>();

            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                var info = new DirectoryInfo(directory);
                if (Hidden(info)) continue;
                entries.Add(new Row(info.Name, info.FullName, true, 0, info.LastWriteTime));
            }

            foreach (var file in Directory.EnumerateFiles(path))
            {
                var info = new FileInfo(file);
                if (Hidden(info)) continue;
                entries.Add(new Row(info.Name, info.FullName, false, info.Length, info.LastWriteTime));
            }

            Items.ItemsSource = entries
                .OrderByDescending(r => r.IsFolder)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _current = path;
            PathBox.Text = path;
            UpButton.IsEnabled = Directory.GetParent(path) is not null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot open that folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Hidden and system entries stay out of the way, as they do in Explorer.</summary>
    private static bool Hidden(FileSystemInfo info) =>
        (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    private void OnUpClick(object sender, RoutedEventArgs e)
    {
        if (Directory.GetParent(_current) is { } parent) Navigate(parent.FullName);
    }

    private void OnGoClick(object sender, RoutedEventArgs e)
    {
        var typed = PathBox.Text.Trim().Trim('"');
        if (Directory.Exists(typed)) Navigate(typed);
        else if (File.Exists(typed)) { AddPaths([typed]); PathBox.Text = _current; }
        else MessageBox.Show(this, "That path does not exist.", "Not found",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnGoClick(sender, e);
    }

    private void OnPlaceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string path) Navigate(path);
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Items.SelectedItem is not Row row) return;

        // A folder opens; a file is chosen. Double-clicking a file to "open" it has no
        // meaning here, so the useful reading is "I want that one".
        if (row.IsFolder) Navigate(row.FullPath);
        else AddPaths([row.FullPath]);
    }

    // =======================================================================
    // Choosing
    // =======================================================================

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AddButton.IsEnabled = Items.SelectedItems.Count > 0;

    private void OnAddClick(object sender, RoutedEventArgs e) =>
        AddPaths(Items.SelectedItems.Cast<Row>().Select(r => r.FullPath));

    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!Chosen.Contains(path, StringComparer.OrdinalIgnoreCase)) Chosen.Add(path);
        }

        RefreshBasket();
    }

    private void OnClearBasketClick(object sender, RoutedEventArgs e)
    {
        Chosen.Clear();
        RefreshBasket();
    }

    private void RefreshBasket()
    {
        UploadButton.IsEnabled = Chosen.Count > 0;

        if (Chosen.Count == 0)
        {
            BasketText.Text = "Nothing chosen yet.";
            return;
        }

        var folders = Chosen.Count(Directory.Exists);
        var files = Chosen.Count - folders;

        var parts = new List<string>();
        if (files > 0) parts.Add($"{files:N0} file{(files == 1 ? "" : "s")}");
        if (folders > 0) parts.Add($"{folders:N0} folder{(folders == 1 ? "" : "s")}");

        BasketText.Text = string.Join(", ", parts) + " — " +
                          string.Join(", ", Chosen.Select(Path.GetFileName).Take(6)) +
                          (Chosen.Count > 6 ? $", and {Chosen.Count - 6:N0} more" : "");
    }

    private void OnUploadClick(object sender, RoutedEventArgs e)
    {
        // Nothing added but something highlighted is obviously meant to be included: pressing
        // Upload straight after selecting should not silently send nothing.
        if (Chosen.Count == 0 && Items.SelectedItems.Count > 0) OnAddClick(sender, e);
        if (Chosen.Count == 0) return;

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>One row in the browser.</summary>
    private sealed record Row(string Name, string FullPath, bool IsFolder, long Size, DateTime Modified)
    {
        public string Glyph => IsFolder ? "" : "";
        public string SizeText => IsFolder ? "" : Format.Bytes(Size);
        public string ModifiedText => Modified.ToString("g");
    }
}
