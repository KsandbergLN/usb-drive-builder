using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LaptopQaUsbBuilder;

public enum PartitionContentAction
{
    None,
    Files,
    Folder,
    Autounattend,
    Iso,
    ScriptFiles,
    Drivers
}

public partial class PartitionContentDialog : Window
{
    private readonly string _theme;
    private readonly PartitionConfig _partition;
    private bool _busy;
    public Func<PartitionContentAction, Window, Task>? ActionHandler { get; set; }

    public PartitionContentDialog(PartitionConfig partition, string theme)
    {
        InitializeComponent();
        _partition = partition;
        _theme = ThemeService.Normalize(theme);
        TitleText.Text = $"Add content to {partition.Name}";
        SubtitleText.Text = "Add as many content types as needed, then select Close.";
        ThemeService.Apply(this, _theme);
        RefreshState();
        Loaded += (_, _) => { ThemeService.Apply(this, _theme); RefreshState(); };
    }

    private async Task ExecuteAsync(PartitionContentAction action)
    {
        if (_busy || ActionHandler is null) return;
        _busy = true;
        ContentPanel.IsEnabled = false;
        try
        {
            await ActionHandler(action, this);
            RefreshState();
        }
        finally
        {
            ContentPanel.IsEnabled = true;
            _busy = false;
        }
    }

    private void RefreshState()
    {
        var isNtfs = _partition.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        XmlButton.Visibility = isNtfs ? Visibility.Visible : Visibility.Collapsed;
        IsoButton.Visibility = isNtfs ? Visibility.Visible : Visibility.Collapsed;
        IsoExtrasSection.Visibility = isNtfs ? Visibility.Visible : Visibility.Collapsed;
        StandardButtons.Columns = isNtfs ? 3 : 1;
        Width = isNtfs ? 620 : 390;
        SetSelected(FilesButton, _partition.SourceFiles.Count + _partition.SourceFolders.Count > 0);
        SetSelected(XmlButton, _partition.HasAutounattend);
        SetSelected(IsoButton, _partition.HasIso);
        SetSelected(DriversButton, _partition.HasDrivers);
        SetSelected(ScriptsButton, _partition.HasScripts);
        InvalidateMeasure();
    }

    private static void SetSelected(System.Windows.Controls.Button button, bool selected)
    {
        if (selected)
        {
            button.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AddButtonBackground");
            button.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AddButtonForeground");
        }
        else
        {
            button.Background = new SolidColorBrush(Color.FromRgb(104, 127, 135));
            button.Foreground = Brushes.White;
        }
    }

    private async void Files_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(PartitionContentAction.Files);
    private async void Folder_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(PartitionContentAction.Folder);
    private async void Xml_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(PartitionContentAction.Autounattend);
    private async void Iso_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(PartitionContentAction.Iso);
    private async void ScriptFiles_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(PartitionContentAction.ScriptFiles);
    private async void Drivers_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(PartitionContentAction.Drivers);
    private void Close_Click(object sender, RoutedEventArgs e) { if (!_busy) Close(); }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
