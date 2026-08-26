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
    public IReadOnlyList<string> DriverArchives { get; private set; } = [];

    public DriverSourcesDialog(IEnumerable<string> folders, IEnumerable<string> files, IEnumerable<string> archives,
        bool forceUnsigned, string theme)
    {
        InitializeComponent();
        foreach (var path in folders.Distinct(StringComparer.OrdinalIgnoreCase)) _sources.Add(new DriverSourceItem("Folder", path, DriverSourceKind.Folder));
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase)) _sources.Add(new DriverSourceItem("INF file", path, DriverSourceKind.Inf));
        foreach (var path in archives.Distinct(StringComparer.OrdinalIgnoreCase)) _sources.Add(new DriverSourceItem("Driver pack", path, DriverSourceKind.Archive));
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
        if (!_sources.Any(item => item.KindValue == DriverSourceKind.Folder && item.Path.Equals(dialog.FolderName, StringComparison.OrdinalIgnoreCase)))
            _sources.Add(new DriverSourceItem("Folder", dialog.FolderName, DriverSourceKind.Folder));
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select INF files or compressed driver packs",
            Filter = "Driver files and packs (*.inf;*.zip;*.cab)|*.inf;*.zip;*.cab|INF packages (*.inf)|*.inf|Compressed driver packs (*.zip;*.cab)|*.zip;*.cab",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;
        foreach (var path in dialog.FileNames)
        {
            var isInf = Path.GetExtension(path).Equals(".inf", StringComparison.OrdinalIgnoreCase);
            var sourceKind = isInf ? DriverSourceKind.Inf : DriverSourceKind.Archive;
            if (!_sources.Any(item => item.KindValue == sourceKind && item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                _sources.Add(new DriverSourceItem(isInf ? "INF file" : "Driver pack", path, sourceKind));
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is DriverSourceItem selected) _sources.Remove(selected);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _sources.Clear();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DriverFolders = _sources.Where(item => item.KindValue == DriverSourceKind.Folder).Select(item => item.Path).ToArray();
        DriverFiles = _sources.Where(item => item.KindValue == DriverSourceKind.Inf).Select(item => item.Path).ToArray();
        DriverArchives = _sources.Where(item => item.KindValue == DriverSourceKind.Archive).Select(item => item.Path).ToArray();
        DialogResult = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
}

public enum DriverSourceKind { Folder, Inf, Archive }
public sealed record DriverSourceItem(string Kind, string Path, DriverSourceKind KindValue);
