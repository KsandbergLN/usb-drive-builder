using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace LaptopQaUsbBuilder;

public partial class ScriptSourcesDialog : Window
{
    private static readonly string[] ReservedNames = ["LaptopQA-RunScripts.cmd", "LaptopQA-Cleanup.ps1"];
    private readonly ObservableCollection<ScriptSourceItem> _sources = [];
    public IReadOnlyList<string> ScriptFiles { get; private set; } = [];

    public ScriptSourcesDialog(IEnumerable<string> files, string theme)
    {
        InitializeComponent();
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase)) _sources.Add(new ScriptSourceItem(path));
        SourcesList.ItemsSource = _sources;
        ThemeService.Apply(this, theme);
        Loaded += (_, _) => ThemeService.Apply(this, theme);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Windows Setup scripts and supporting files",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
            InitialDirectory = PickerLocationStore.Get("Scripts")
        };
        if (dialog.ShowDialog(this) != true) return;
        PickerLocationStore.Set("Scripts", Path.GetDirectoryName(dialog.FileNames[0]));

        AddPaths(dialog.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder of Windows Setup scripts and supporting files", Multiselect = false, InitialDirectory = PickerLocationStore.Get("ScriptsFolder") };
        if (dialog.ShowDialog(this) != true) return;
        PickerLocationStore.Set("ScriptsFolder", dialog.FolderName);
        try
        {
            AddPaths(Directory.EnumerateFiles(dialog.FolderName, "*", SearchOption.AllDirectories));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"The selected folder could not be read.\n\n{ex.Message}", "Scripts folder unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var problems = new List<string>();
        foreach (var path in paths)
        {
            if (_sources.Any(source => source.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            var name = Path.GetFileName(path);
            if (ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"'{name}' is reserved for the app-generated script runner.");
                continue;
            }
            if (_sources.Any(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"'{name}' is already selected from another location.");
                continue;
            }
            _sources.Add(new ScriptSourceItem(path));
        }

        if (problems.Count > 0)
            MessageBox.Show(string.Join("\n", problems.Distinct(StringComparer.OrdinalIgnoreCase)),
                "Some files were not added", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is ScriptSourceItem selected) _sources.Remove(selected);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _sources.Clear();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        ScriptFiles = _sources.Select(source => source.Path).ToArray();
        DialogResult = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

}

public sealed record ScriptSourceItem(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}
