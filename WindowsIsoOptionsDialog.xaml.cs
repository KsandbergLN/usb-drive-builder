using System.IO;
using System.Windows;
using System.Windows.Input;

namespace LaptopQaUsbBuilder;

public partial class WindowsIsoOptionsDialog : Window
{
    public WindowsIsoEditionSelection? Selection { get; private set; }

    public WindowsIsoOptionsDialog(string isoPath, IReadOnlyList<WindowsImageEdition> editions, string theme)
    {
        InitializeComponent();
        IsoNameText.Text = Path.GetFileName(isoPath);
        IsoNameText.ToolTip = isoPath;
        EditionPicker.ItemsSource = editions;
        EditionPicker.SelectedItem = FindPreferredEdition(editions) ?? editions.FirstOrDefault();
        ThemeService.Apply(this, theme);
        Loaded += (_, _) => ThemeService.Apply(this, theme);
    }

    private static WindowsImageEdition? FindPreferredEdition(IEnumerable<WindowsImageEdition> editions) =>
        editions.OrderBy(edition => EditionPreference(edition.Name)).FirstOrDefault();

    private static int EditionPreference(string name)
    {
        if (name.Equals("Windows 11 Pro", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.EndsWith(" Pro", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains(" Pro", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains(" Education", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains(" Workstations", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(" N", StringComparison.OrdinalIgnoreCase)) return 2;
        return 10;
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (EditionPicker.SelectedItem is not WindowsImageEdition edition)
        {
            MessageBox.Show("Select a Windows edition.", "Edition required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Selection = new WindowsIsoEditionSelection(edition.Index, edition.Name);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}

public sealed record WindowsImageEdition(int Index, string Name, string Description, long Size, string EditionId = "")
{
    public string DisplayName => $"{Name}  (index {Index})";
}

public sealed record WindowsIsoEditionSelection(int EditionIndex, string EditionName);

public sealed record WindowsIsoSelection(
    int EditionIndex,
    string EditionName,
    string EditionId,
    IReadOnlyList<string> DriverFolders,
    IReadOnlyList<string> DriverFiles,
    IReadOnlyList<string> DriverArchives,
    bool ForceUnsigned,
    string CompressionMode)
{
    public bool AddDrivers => DriverFolders.Count + DriverFiles.Count + DriverArchives.Count > 0;
}
