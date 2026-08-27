using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace LaptopQaUsbBuilder;

public partial class ScriptSourcesDialog : Window
{
    private static readonly string[] ReservedNames = ["LaptopQA-RunScripts.cmd", "LaptopQA-Cleanup.ps1"];
    private readonly ObservableCollection<string> _sources = [];
    public IReadOnlyList<string> ScriptFiles { get; private set; } = [];

    public ScriptSourcesDialog(IEnumerable<string> files, string theme)
    {
        InitializeComponent();
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase)) _sources.Add(path);
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

        var problems = new List<string>();
        foreach (var path in dialog.FileNames)
        {
            if (_sources.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(path);
            if (ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"'{name}' is reserved for the app-generated script runner.");
                continue;
            }
            if (_sources.Any(existing => Path.GetFileName(existing).Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"'{name}' is already selected from another location.");
                continue;
            }
            _sources.Add(path);
        }

        if (problems.Count > 0)
            MessageBox.Show(string.Join("\n", problems.Distinct(StringComparer.OrdinalIgnoreCase)),
                "Some files were not added", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is string selected) _sources.Remove(selected);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _sources.Clear();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        ScriptFiles = _sources.ToArray();
        DialogResult = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
