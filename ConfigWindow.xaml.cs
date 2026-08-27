using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace LaptopQaUsbBuilder;

public partial class ConfigWindow : Window, INotifyPropertyChanged
{
    private readonly ObservableCollection<PartitionConfig> _items;
    private readonly string _originalTheme;
    private bool _defaultsLocked = true;
    private PartitionConfig? _draggedDefaultPartition;
    private Point _defaultPartitionDragStart;
    private DropIndicatorAdorner? _defaultDropIndicator;
    private int _defaultDropDestinationIndex = -1;
    private const string DefaultPartitionDragFormat = "LaptopQaUsbBuilder.DefaultPartition";
    private const double PartitionDragStartDistance = 12;
    public List<PartitionConfig> Result { get; private set; } = [];
    public string SelectedLanguage { get; private set; }
    public string SelectedTheme { get; private set; }
    public bool ForceUnsignedDrivers { get; private set; }
    public WindowsSetupConfig WindowsSetup { get; private set; }
    public bool CanAddDefaultPartitions => !_defaultsLocked && _items.Count < 4;
    public bool CanRemoveDefaultPartitions => !_defaultsLocked && _items.Count > 1;
    public bool DefaultsEditable => !_defaultsLocked;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ConfigWindow(IEnumerable<PartitionConfig> current, string language, string theme, bool forceUnsignedDrivers, WindowsSetupConfig? windowsSetup = null)
    {
        InitializeComponent();
        SelectedLanguage = Localization.Resolve(language).Code;
        SelectedTheme = ThemeService.Normalize(theme);
        ForceUnsignedDrivers = forceUnsignedDrivers;
        WindowsSetup = windowsSetup?.Clone() ?? new WindowsSetupConfig();
        _originalTheme = SelectedTheme;
        _items = new ObservableCollection<PartitionConfig>(current.Select(p => p.Clone()));
        PartitionGrid.ItemsSource = _items;
        RemoveButtonsList.ItemsSource = _items;
        ReorderHandlesList.ItemsSource = _items;
        FormatColumn.ItemsSource = PartitionConfig.AllowedFormats;
        LanguagePicker.ItemsSource = Localization.Languages;
        LanguagePicker.SelectedItem = Localization.Resolve(SelectedLanguage);
        RebuildThemeChoices();
        ForceUnsignedDriversCheckBox.IsChecked = ForceUnsignedDrivers;
        TargetDiskTextBox.Text = WindowsSetup.TargetDisk.ToString(); InstallPartitionTextBox.Text = WindowsSetup.InstallPartition.ToString();
        EfiSizeTextBox.Text = WindowsSetup.EfiSizeMb.ToString(); MsrSizeTextBox.Text = WindowsSetup.MsrSizeMb.ToString(); WindowsShrinkTextBox.Text = WindowsSetup.WindowsShrinkMb.ToString();
        EfiLabelTextBox.Text = WindowsSetup.EfiLabel; WindowsLabelTextBox.Text = WindowsSetup.WindowsLabel; RecoveryLabelTextBox.Text = WindowsSetup.RecoveryLabel;
        EfiLetterTextBox.Text = WindowsSetup.EfiLetter; WindowsLetterTextBox.Text = WindowsSetup.WindowsLetter; RecoveryLetterTextBox.Text = WindowsSetup.RecoveryLetter;
        EditionTextBox.Text = WindowsSetup.Edition; ProductKeyTextBox.Text = WindowsSetup.ProductKey; PromptBeforeInstallCheckBox.IsChecked = WindowsSetup.PromptBeforeInstall;
        ApplyLanguage();
        ThemeService.Apply(this, SelectedTheme);
        Loaded += (_, _) => ThemeService.Apply(this, SelectedTheme);
        SetDefaultsLocked();
    }

    private void AddDefaultPartition_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAddDefaultPartitions) return;
        ResizeTo(_items.Count + 1);
    }

    private void RemoveDefaultPartitionRow_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRemoveDefaultPartitions || (sender as FrameworkElement)?.DataContext is not PartitionConfig partition) return;
        _items.Remove(partition);
        NormalizePartitionList();
    }

    private void ResizeTo(int count)
    {
        while (_items.Count > count) _items.RemoveAt(_items.Count - 1);
        while (_items.Count < count)
            _items.Add(new PartitionConfig { Number = _items.Count + 1, Name = $"PARTITION {_items.Count + 1}", SizeText = _items.Any(p => p.IsRemaining) ? "10 GB" : "*", FileSystem = "exFAT" });
        NormalizePartitionList();
    }

    private void NormalizePartitionList()
    {
        for (var i = 0; i < _items.Count; i++) _items[i].Number = i + 1;
        if (_items.Count > 0 && !_items.Any(p => p.IsRemaining)) _items[^1].SizeText = "*";
        PartitionGrid.Items.Refresh();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAddDefaultPartitions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRemoveDefaultPartitions)));
    }

    private void DefaultsLock_Click(object sender, RoutedEventArgs e)
    {
        _defaultsLocked = !_defaultsLocked;
        SetDefaultsLocked();
    }

    private void DefaultPartitionDragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!DefaultsEditable || (sender as FrameworkElement)?.DataContext is not PartitionConfig partition) return;
        _draggedDefaultPartition = partition;
        _defaultDropDestinationIndex = _items.IndexOf(partition);
        _defaultPartitionDragStart = e.GetPosition(this);
        e.Handled = true;
    }

    private void DefaultPartitionArea_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggedDefaultPartition = null;
        _defaultDropDestinationIndex = -1;
        ClearDefaultDropIndicator();
    }

    private void DefaultPartitionArea_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedDefaultPartition is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggedDefaultPartition = null;
            return;
        }
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _defaultPartitionDragStart.X) < PartitionDragStartDistance &&
            Math.Abs(position.Y - _defaultPartitionDragStart.Y) < PartitionDragStartDistance) return;
        var data = new DataObject(DefaultPartitionDragFormat, _draggedDefaultPartition);
        DragDrop.DoDragDrop(DefaultPartitionArea, data, DragDropEffects.Move);
        ClearDefaultDropIndicator();
        _draggedDefaultPartition = null;
        _defaultDropDestinationIndex = -1;
        e.Handled = true;
    }

    private void PartitionGrid_DragOver(object sender, DragEventArgs e)
    {
        DataGridRow? row = null;
        var showAfter = false;
        var dragged = e.Data.GetData(DefaultPartitionDragFormat) as PartitionConfig;
        var valid = DefaultsEditable && dragged is not null;
        if (valid) valid = TryGetDefaultDropTarget(e, dragged!, out row, out showAfter, out _);
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (valid) ShowDefaultDropIndicator(row!, showAfter); else ClearDefaultDropIndicator();
        e.Handled = true;
    }

    private void PartitionGrid_DragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(DefaultPartitionArea);
        if (point.X < 0 || point.Y < 0 || point.X > DefaultPartitionArea.ActualWidth || point.Y > DefaultPartitionArea.ActualHeight)
            ClearDefaultDropIndicator();
    }

    private void PartitionGrid_Drop(object sender, DragEventArgs e)
    {
        ClearDefaultDropIndicator();
        if (!DefaultsEditable || e.Data.GetData(DefaultPartitionDragFormat) is not PartitionConfig dragged) return;
        var oldIndex = _items.IndexOf(dragged);
        if (oldIndex < 0 || !TryGetDefaultDropTarget(e, dragged, out _, out _, out var destinationIndex)) return;
        if (destinationIndex == oldIndex)
        {
            PartitionGrid.SelectedItem = dragged;
            _draggedDefaultPartition = null;
            e.Handled = true;
            return;
        }
        _items.RemoveAt(oldIndex);
        _items.Insert(Math.Clamp(destinationIndex, 0, _items.Count), dragged);
        NormalizePartitionList();
        PartitionGrid.SelectedItem = dragged;
        _draggedDefaultPartition = null;
        e.Handled = true;
    }

    private bool TryGetDefaultDropTarget(DragEventArgs e, PartitionConfig dragged, out DataGridRow? row, out bool showAfter, out int destinationIndex)
    {
        destinationIndex = GetDefaultDestinationIndex(e.GetPosition(DefaultPartitionArea).Y);
        showAfter = false;
        row = PartitionGrid.ItemContainerGenerator.ContainerFromIndex(destinationIndex) as DataGridRow;
        return row is not null;
    }

    private int GetDefaultDestinationIndex(double pointerY)
    {
        var destination = Math.Max(0, PartitionGrid.Items.Count - 1);
        DataGridRow? destinationRow = null;
        for (var index = 0; index < PartitionGrid.Items.Count; index++)
        {
            if (PartitionGrid.ItemContainerGenerator.ContainerFromIndex(index) is not DataGridRow row) continue;
            var top = row.TranslatePoint(new Point(0, 0), DefaultPartitionArea).Y;
            if (pointerY >= top + row.ActualHeight) continue;
            destination = index;
            destinationRow = row;
            break;
        }
        if (_defaultDropDestinationIndex >= 0 && destination != _defaultDropDestinationIndex && destinationRow is not null)
        {
            var top = destinationRow.TranslatePoint(new Point(0, 0), DefaultPartitionArea).Y;
            var depth = pointerY - top;
            if (depth < destinationRow.ActualHeight * 0.25 || depth > destinationRow.ActualHeight * 0.75)
                return _defaultDropDestinationIndex;
        }
        _defaultDropDestinationIndex = destination;
        return destination;
    }

    private void ShowDefaultDropIndicator(DataGridRow row, bool showAfter)
    {
        if (_defaultDropIndicator?.AdornedElement == row && _defaultDropIndicator.IsAfter == showAfter) return;
        _defaultDropIndicator?.Detach();
        _defaultDropIndicator = DropIndicatorAdorner.Attach(row, showAfter);
    }

    private void ClearDefaultDropIndicator()
    {
        _defaultDropIndicator?.Detach();
        _defaultDropIndicator = null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void SetDefaultsLocked()
    {
        PartitionGrid.IsReadOnly = _defaultsLocked;
        DefaultsLockButton.Content = _defaultsLocked ? "\uE72E" : "\uE785";
        DefaultsLockButton.ToolTip = _defaultsLocked ? "Unlock default partition editing" : "Lock default partition editing";
        DefaultsLockButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,
            _defaultsLocked ? "AddButtonBackground" : "ClearButtonBackground");
        DefaultsLockButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty,
            _defaultsLocked ? "AddButtonForeground" : "ClearButtonForeground");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAddDefaultPartitions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRemoveDefaultPartitions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DefaultsEditable)));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        PartitionGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PartitionGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!Validate(out var message))
        {
            MessageBox.Show(message, "Invalid partition settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = _items.Select(p => p.Clone()).ToList();
        SelectedLanguage = (LanguagePicker.SelectedItem as LanguageOption)?.Code ?? "en-US";
        SelectedTheme = (ThemePicker.SelectedItem as ThemeOption)?.Key ?? "Light";
        ForceUnsignedDrivers = ForceUnsignedDriversCheckBox.IsChecked == true;
        WindowsSetup = new WindowsSetupConfig
        {
            TargetDisk = ParseInt(TargetDiskTextBox.Text, WindowsSetup.TargetDisk),
            InstallPartition = ParseInt(InstallPartitionTextBox.Text, WindowsSetup.InstallPartition),
            EfiSizeMb = ParseInt(EfiSizeTextBox.Text, WindowsSetup.EfiSizeMb),
            MsrSizeMb = ParseInt(MsrSizeTextBox.Text, WindowsSetup.MsrSizeMb),
            WindowsShrinkMb = ParseInt(WindowsShrinkTextBox.Text, WindowsSetup.WindowsShrinkMb),
            EfiLabel = EfiLabelTextBox.Text.Trim(), WindowsLabel = WindowsLabelTextBox.Text.Trim(), RecoveryLabel = RecoveryLabelTextBox.Text.Trim(),
            EfiLetter = EfiLetterTextBox.Text.Trim(), WindowsLetter = WindowsLetterTextBox.Text.Trim(), RecoveryLetter = RecoveryLetterTextBox.Text.Trim(),
            ProductKey = ProductKeyTextBox.Text.Trim(), Edition = EditionTextBox.Text.Trim(), PromptBeforeInstall = PromptBeforeInstallCheckBox.IsChecked == true
        };
        DialogResult = true;
    }

    private static int ParseInt(string text, int fallback) => int.TryParse(text, out var value) && value > 0 ? value : fallback;

    private bool Validate(out string message)
    {
        message = "";
        if (_items.Count is < 1 or > 4) { message = "Choose between 1 and 4 default partitions for an MBR USB."; return false; }
        if (_items.Count(p => p.IsRemaining) != 1)
        { message = "Exactly one partition must use * for remaining space."; return false; }
        if (_items.Select(p => p.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _items.Count)
        { message = "Every volume label must be unique."; return false; }

        foreach (var item in _items)
        {
            item.Name = item.Name.Trim();
            if (string.IsNullOrWhiteSpace(item.Name)) { message = $"Partition {item.Number} needs a volume label."; return false; }
            if (item.Name.IndexOfAny(['\\', '/', '?', '*', ':', '|', '"', '<', '>']) >= 0 || item.Name.Any(char.IsControl))
            { message = $"Partition {item.Number} contains a character that is not valid in a volume label."; return false; }
            if (!PartitionConfig.AllowedFormats.Contains(item.FileSystem)) { message = $"Partition {item.Number} has an unsupported format."; return false; }
            var maxLength = item.FileSystem == "FAT32" ? 11 : item.FileSystem == "exFAT" ? 15 : 32;
            if (item.Name.Length > maxLength) { message = $"{item.FileSystem} label '{item.Name}' exceeds {maxLength} characters."; return false; }
            if (!item.IsRemaining)
            {
                if (!PartitionConfig.TryParseSize(item.SizeText, out var bytes))
                { message = $"Partition {item.Number} needs a size such as 50 MB or 20 GB."; return false; }
                if (bytes < 32L * 1024 * 1024)
                { message = $"Partition {item.Number} must be at least 32 MB."; return false; }
                if (item.FileSystem == "FAT32" && bytes > 32L * 1024 * 1024 * 1024)
                { message = $"Partition {item.Number} exceeds Windows' 32 GB FAT32 formatting limit."; return false; }
            }
        }
        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DialogResult != true && Owner is not null) ThemeService.Apply(Owner, _originalTheme);
        base.OnClosing(e);
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguagePicker.SelectedItem is not LanguageOption language) return;
        SelectedLanguage = language.Code;
        Localization.ApplyCulture(SelectedLanguage);
        RebuildThemeChoices();
        ApplyLanguage();
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemePicker.SelectedItem is not ThemeOption theme) return;
        SelectedTheme = theme.Key;
        ThemeService.Apply(this, SelectedTheme);
        if (Owner is not null) ThemeService.Apply(Owner, SelectedTheme);
    }

    private void RebuildThemeChoices()
    {
        var current = SelectedTheme;
        ThemePicker.ItemsSource = new[]
        {
            new ThemeOption("Light", Localization.Text(SelectedLanguage, "Light")),
            new ThemeOption("Dark", Localization.Text(SelectedLanguage, "Dark")),
            new ThemeOption("AMOLED", "AMOLED")
        };
        ThemePicker.SelectedItem = ((IEnumerable<ThemeOption>)ThemePicker.ItemsSource).First(t => t.Key == current);
    }

    private void ApplyLanguage()
    {
        string T(string key) => Localization.Text(SelectedLanguage, key);
        DialogTitleText.Text = "Configuration"; DialogSubtitleText.Text = "Configure default partitions and Windows image servicing.";
        LanguageLabel.Text = T("Language"); ThemeLabel.Text = T("Theme"); DefaultPartitionsLabel.Text = "Default partitions"; RemainingHint.Text = T("Remaining Hint");
        VolumeLabelColumn.Header = T("Volume label"); SizeColumn.Header = T("Size Header"); FormatColumn.Header = T("Format");
        SizeHelpText.Text = T("Size Help") + "  FAT32: 11, exFAT: 15, NTFS: 32.";
        CancelButton.Content = T("Cancel"); SaveButton.Content = T("Save");
    }
}

public sealed class WindowsSetupConfig
{
    public int TargetDisk { get; set; } = 0;
    public int InstallPartition { get; set; } = 3;
    public int EfiSizeMb { get; set; } = 1500;
    public int MsrSizeMb { get; set; } = 16;
    public int WindowsShrinkMb { get; set; } = 1000;
    public string EfiLabel { get; set; } = "System";
    public string WindowsLabel { get; set; } = "Windows";
    public string RecoveryLabel { get; set; } = "Recovery";
    public string EfiLetter { get; set; } = "S";
    public string WindowsLetter { get; set; } = "W";
    public string RecoveryLetter { get; set; } = "R";
    public string Edition { get; set; } = "Windows 11 Pro";
    public string ProductKey { get; set; } = "VK7JG-NPHTM-C97JM-9MPGT-3V66T";
    public bool PromptBeforeInstall { get; set; } = true;
    public WindowsSetupConfig Clone() => (WindowsSetupConfig)MemberwiseClone();
}

public sealed class PartitionConfig
{
    public static readonly string[] AllowedFormats = ["FAT32", "NTFS", "exFAT"];
    public int Number { get; set; }
    public string Name { get; set; } = "PARTITION";
    public string SizeText { get; set; } = "*";
    public string FileSystem { get; set; } = "exFAT";
    [JsonIgnore]
    public string? CalculatedSizeText { get; set; }
    [JsonIgnore]
    public ObservableCollection<string> SourceFiles { get; set; } = [];
    [JsonIgnore]
    public ObservableCollection<string> SourceFolders { get; set; } = [];
    [JsonIgnore]
    public ObservableCollection<string> ScriptFiles { get; set; } = [];
    [JsonIgnore]
    public string? AutounattendSource { get; set; }
    [JsonIgnore]
    public string? IsoSource { get; set; }
    [JsonIgnore]
    public int? IsoEditionIndex { get; set; }
    [JsonIgnore]
    public string? IsoEditionName { get; set; }
    [JsonIgnore]
    public ObservableCollection<string> DriverFolders { get; set; } = [];
    [JsonIgnore]
    public ObservableCollection<string> DriverFiles { get; set; } = [];
    [JsonIgnore]
    public ObservableCollection<string> DriverArchives { get; set; } = [];
    [JsonIgnore]
    public bool ForceUnsignedDrivers { get; set; }
    [JsonIgnore]
    public string? PreparedMediaPath { get; set; }
    [JsonIgnore]
    public string? PreparedAutounattendXml { get; set; }
    [JsonIgnore]
    public long SelectedContentBytes { get; set; }
    [JsonIgnore]
    public long LargestSelectedFileBytes { get; set; }
    [JsonIgnore]
    public long? ExtractedIsoBytes { get; set; }
    public bool IsRemaining => SizeText.Trim() == "*";
    public string PreviewText => $"{CalculatedSizeText ?? SizeText}  |  {FileSystem}";
    public string? FolderXmlSource => SourceFolders.Select(FindRootXmlFile).FirstOrDefault(path => path is not null);
    public bool HasAutounattend => !string.IsNullOrWhiteSpace(AutounattendSource) || FolderXmlSource is not null;
    public bool HasIso => !string.IsNullOrWhiteSpace(IsoSource);
    public bool HasScripts => ScriptFiles.Count > 0;
    public bool HasDrivers => DriverFolders.Count + DriverFiles.Count + DriverArchives.Count > 0;
    public bool HasAnyContent => SourceFiles.Count + SourceFolders.Count + ScriptFiles.Count > 0 ||
                                 !string.IsNullOrWhiteSpace(AutounattendSource) || HasIso || HasDrivers;
    public string AddedContentSummary
    {
        get
        {
            var labels = new List<string>();
            if (HasAutounattend) labels.Add("AUXML");
            if (HasIso) labels.Add("ISO");
            if (SourceFolders.Count > 0) labels.Add("Folder");
            if (SourceFiles.Count > 0) labels.Add("Files");
            if (HasDrivers) labels.Add("Drivers");
            if (HasScripts) labels.Add("Scripts");
            return string.Join("  •  ", labels);
        }
    }
    public string AutounattendToolTip => !string.IsNullOrWhiteSpace(AutounattendSource)
        ? $"Autounattend.xml selected:\n{AutounattendSource}"
        : FolderXmlSource is not null
            ? $"XML detected in a selected folder:\n{FolderXmlSource}"
            : "Select Autounattend.xml for this NTFS partition, or add a folder containing an XML file.";
    public string IsoToolTip => string.IsNullOrWhiteSpace(IsoSource)
        ? "Select a Windows ISO to create bootable NTFS UEFI media."
        : string.Join(Environment.NewLine,
            $"Bootable Windows ISO selected:\n{IsoSource}",
            string.IsNullOrWhiteSpace(IsoEditionName) ? "" : $"Edition: {IsoEditionName}",
            HasDrivers ? $"Drivers: {DriverFolders.Count} folder(s), {DriverFiles.Count} INF file(s), {DriverArchives.Count} compressed pack(s)" : "Drivers: skipped").Trim();
    public string SourcesToolTip => !HasAnyContent
        ? "No content selected."
        : string.Join(Environment.NewLine,
            SourceFiles.Select(path => $"File: {path}")
                .Concat(SourceFolders.Select(path => $"Folder: {path}"))
                .Concat(ScriptFiles.Select(path => $"Setup script/support file: {path}"))
                .Concat(string.IsNullOrWhiteSpace(AutounattendSource) ? [] : [$"Autounattend.xml: {AutounattendSource}"])
                .Concat(string.IsNullOrWhiteSpace(IsoSource) ? [] : [$"ISO: {IsoSource}"])
                .Concat(string.IsNullOrWhiteSpace(IsoEditionName) ? [] : [$"Windows edition: {IsoEditionName}"])
                .Concat(DriverFolders.Select(path => $"Driver folder: {path}"))
                .Concat(DriverFiles.Select(path => $"Driver INF: {path}"))
                .Concat(DriverArchives.Select(path => $"Compressed driver pack: {path}")));
    public PartitionConfig Clone()
    {
        var clone = new PartitionConfig { Number = Number, Name = Name, SizeText = SizeText, FileSystem = FileSystem };
        foreach (var path in SourceFiles) clone.SourceFiles.Add(path);
        foreach (var path in SourceFolders) clone.SourceFolders.Add(path);
        foreach (var path in ScriptFiles) clone.ScriptFiles.Add(path);
        clone.AutounattendSource = AutounattendSource;
        clone.IsoSource = IsoSource;
        clone.IsoEditionIndex = IsoEditionIndex;
        clone.IsoEditionName = IsoEditionName;
        foreach (var path in DriverFolders) clone.DriverFolders.Add(path);
        foreach (var path in DriverFiles) clone.DriverFiles.Add(path);
        foreach (var path in DriverArchives) clone.DriverArchives.Add(path);
        clone.ForceUnsignedDrivers = ForceUnsignedDrivers;
        return clone;
    }

    public void ClearIsoSelection()
    {
        IsoSource = null;
        IsoEditionIndex = null;
        IsoEditionName = null;
        DriverFolders.Clear();
        DriverFiles.Clear();
        DriverArchives.Clear();
        ForceUnsignedDrivers = false;
        PreparedMediaPath = null;
        PreparedAutounattendXml = null;
        ExtractedIsoBytes = null;
        ScriptFiles.Clear();
    }

    private static string? FindRootXmlFile(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.xml", SearchOption.TopDirectoryOnly).FirstOrDefault()
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static List<PartitionConfig> CreateDefaults() =>
    [
        new() { Number = 1, Name = "DELL DIAG", SizeText = "50 MB", FileSystem = "FAT32" },
        new() { Number = 2, Name = "Win11 Boot", SizeText = "20 GB", FileSystem = "NTFS" },
        new() { Number = 3, Name = "IT SUPP", SizeText = "*", FileSystem = "exFAT" }
    ];

    public static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        var match = Regex.Match(text.Trim(), @"^(\d+(?:\.\d+)?)\s*(MB|GB)$", RegexOptions.IgnoreCase);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, out var value) || value <= 0) return false;
        var multiplier = match.Groups[2].Value.Equals("GB", StringComparison.OrdinalIgnoreCase) ? 1024m * 1024 * 1024 : 1024m * 1024;
        if (value * multiplier > long.MaxValue) return false;
        bytes = (long)(value * multiplier);
        return true;
    }

    public static bool IsSizeSyntaxValid(string? text) =>
        !string.IsNullOrWhiteSpace(text) && (text.Trim() == "*" || TryParseSize(text, out _));
}

public sealed class SizeEntryValidConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        PartitionConfig.IsSizeSyntaxValid(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
