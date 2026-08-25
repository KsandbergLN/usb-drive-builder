using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace LaptopQaUsbBuilder;

public partial class DriverSourcesDialog : Window
{
    private readonly ObservableCollection<DriverSourceItem> _sources = [];
    public IReadOnlyList<string> DriverFolders { get; private set; } = [];
    public IReadOnlyList<string> DriverFiles { get; private set; } = [];

    public DriverSourcesDialog(IEnumerable<string> folders, IEnumerable<string> files, bool forceUnsigned, string theme)
    {
        InitializeComponent();
        foreach (var path in folders.Distinct(StringComparer.OrdinalIgnoreCase)) _sources.Add(new DriverSourceItem("Folder", path, true));
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase)) _sources.Add(new DriverSourceItem("INF file", path, false));
        SourcesList.ItemsSource = _sources;
        UnsignedWarning.Visibility = forceUnsigned ? Visibility.Visible : Visibility.Collapsed;
        ThemeService.Apply(this, theme);
        Loaded += (_, _) => ThemeService.Apply(this, theme);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder containing INF driver packages", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (!Directory.EnumerateFiles(dialog.FolderName, "*.inf", SearchOption.AllDirectories).Any())
            {
                MessageBox.Show("The selected folder does not contain any INF driver packages.", "No drivers found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"The selected folder could not be read.\n\n{ex.Message}", "Drivers folder unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!_sources.Any(item => item.IsFolder && item.Path.Equals(dialog.FolderName, StringComparison.OrdinalIgnoreCase)))
            _sources.Add(new DriverSourceItem("Folder", dialog.FolderName, true));
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select individual INF driver packages", Filter = "Driver packages (*.inf)|*.inf", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        foreach (var path in dialog.FileNames)
            if (!_sources.Any(item => !item.IsFolder && item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                _sources.Add(new DriverSourceItem("INF file", path, false));
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is DriverSourceItem selected) _sources.Remove(selected);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _sources.Clear();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DriverFolders = _sources.Where(item => item.IsFolder).Select(item => item.Path).ToArray();
        DriverFiles = _sources.Where(item => !item.IsFolder).Select(item => item.Path).ToArray();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
}

public sealed record DriverSourceItem(string Kind, string Path, bool IsFolder);
