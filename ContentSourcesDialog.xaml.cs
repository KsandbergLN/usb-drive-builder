using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace LaptopQaUsbBuilder;

public partial class ContentSourcesDialog : Window
{
    private readonly ObservableCollection<ContentSourceItem> _sources = [];
    public IReadOnlyList<string> SourceFiles { get; private set; } = [];
    public IReadOnlyList<string> SourceFolders { get; private set; } = [];

    public ContentSourcesDialog(string partitionName, IEnumerable<string> files, IEnumerable<string> folders, string theme)
    {
        InitializeComponent();
        TitleText.Text = $"Content for {partitionName}";
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase)) _sources.Add(new ContentSourceItem("File", path, ContentSourceKind.File));
        foreach (var path in folders.Distinct(StringComparer.OrdinalIgnoreCase)) _sources.Add(new ContentSourceItem("Folder", path, ContentSourceKind.Folder));
        SourcesList.ItemsSource = _sources;
        ThemeService.Apply(this, theme);
        Loaded += (_, _) => ThemeService.Apply(this, theme);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select files to copy to the partition root",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
            InitialDirectory = PickerLocationStore.Get("Files")
        };
        if (dialog.ShowDialog(this) != true) return;
        PickerLocationStore.Set("Files", Path.GetDirectoryName(dialog.FileNames[0]));
        foreach (var path in dialog.FileNames)
            AddSource("File", path, ContentSourceKind.File);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder to merge into the partition root", Multiselect = false, InitialDirectory = PickerLocationStore.Get("Folder") };
        if (dialog.ShowDialog(this) != true) return;
        PickerLocationStore.Set("Folder", dialog.FolderName);
        AddSource("Folder", dialog.FolderName, ContentSourceKind.Folder);
    }

    private void AddSource(string kind, string path, ContentSourceKind kindValue)
    {
        if (!_sources.Any(item => item.KindValue == kindValue && item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            _sources.Add(new ContentSourceItem(kind, path, kindValue));
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is ContentSourceItem selected) _sources.Remove(selected);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _sources.Clear();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SourceFiles = _sources.Where(item => item.KindValue == ContentSourceKind.File).Select(item => item.Path).ToArray();
        SourceFolders = _sources.Where(item => item.KindValue == ContentSourceKind.Folder).Select(item => item.Path).ToArray();
        DialogResult = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}

public enum ContentSourceKind { File, Folder }
public sealed record ContentSourceItem(string Kind, string Path, ContentSourceKind KindValue);
