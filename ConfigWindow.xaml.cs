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
using Microsoft.Win32;
using System.Text;

namespace LaptopQaUsbBuilder;

public partial class ConfigWindow : Window, INotifyPropertyChanged
{
    private static readonly string[] CacheDirectoryNames = ["MediaCache", "DriverPackCache", "DriverPayloadCache"];
    private static readonly IReadOnlyList<OobeLanguageOption> OobeLanguages =
    [
        new("en-US", "English (United States)"), new("es-ES", "Spanish (Spain)"), new("fr-FR", "French (France)"), new("de-DE", "German (Germany)"),
        new("pt-BR", "Portuguese (Brazil)"), new("zh-CN", "Chinese, Simplified (China)"), new("ja-JP", "Japanese (Japan)"), new("hi-IN", "Hindi (India)"),
        new("bn-IN", "Bengali (India)"), new("ta-IN", "Tamil (India)"), new("te-IN", "Telugu (India)"), new("mr-IN", "Marathi (India)")
    ];
    private static readonly IReadOnlyList<OobeKeyboardOption> OobeKeyboards =
    [
        new("0409:00000409", "US"), new("0C0A:0000040A", "Spanish"), new("040C:0000040C", "French"), new("0407:00000407", "German"),
        new("0416:00000416", "Portuguese (Brazil ABNT2)"), new("0804:00000804", "Chinese Simplified (Microsoft Pinyin)"), new("0411:00000411", "Japanese"), new("0439:00000439", "Hindi Traditional"),
        new("0445:00000445", "Bengali"), new("0449:00000449", "Tamil"), new("044A:0000044A", "Telugu"), new("044E:0000044E", "Marathi")
    ];
    private static readonly IReadOnlyList<WindowsEditionOption> WindowsEditions =
    [
        new("Windows 11 Pro"), new("Windows 11 Home"), new("Windows 11 Pro N"), new("Windows 11 Education"), new("Windows 11 Enterprise"),
        new("Windows 10 Pro"), new("Windows 10 Home"), new("Windows 10 Pro N"), new("Windows 10 Education"), new("Windows 10 Enterprise")
    ];
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
        EditionPicker.ItemsSource = WindowsEditions;
        EditionPicker.SelectedItem = WindowsEditions.FirstOrDefault(option => option.Name.Equals(WindowsSetup.Edition, StringComparison.OrdinalIgnoreCase)) ?? WindowsEditions[0];
        PromptBeforeInstallCheckBox.IsChecked = WindowsSetup.PromptBeforeInstall;
        OobeLanguagePicker.ItemsSource = OobeLanguages; OobeKeyboardPicker.ItemsSource = OobeKeyboards;
        OobeLanguagePicker.SelectedItem = OobeLanguages.FirstOrDefault(option => option.Code.Equals(WindowsSetup.OobeLanguage, StringComparison.OrdinalIgnoreCase)) ?? OobeLanguages[0];
        OobeKeyboardPicker.SelectedItem = OobeKeyboards.FirstOrDefault(option => option.Code.Equals(WindowsSetup.OobeKeyboard, StringComparison.OrdinalIgnoreCase)) ?? OobeKeyboards[0];
        ApplyLanguage();
        ThemeService.Apply(this, SelectedTheme);
        Loaded += async (_, _) =>
        {
            ThemeService.Apply(this, SelectedTheme);
            await RefreshCacheSizeAsync();
        };
        SetDefaultsLocked();
    }

    private async Task RefreshCacheSizeAsync()
    {
        CacheSizeText.Text = "Calculating cache size...";
        var size = await Task.Run(GetCacheSize);
        CacheSizeText.Text = size == 0
            ? "No cached Windows media or temporary driver data."
            : $"{FormatBytes(size)} cached locally. Logs and saved settings are not included.";
        ClearCacheButton.IsEnabled = size > 0;
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var size = await Task.Run(GetCacheSize);
        if (size == 0)
        {
            await RefreshCacheSizeAsync();
            return;
        }
        if (ThemedMessageDialog.Show(this,
                $"Delete {FormatBytes(size)} of cached Windows media and temporary driver data?\n\nBuild logs and saved settings will be kept.",
                "Clear build cache", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        ClearCacheButton.IsEnabled = false;
        CacheSizeText.Text = "Clearing cache...";
        var result = await Task.Run(ClearCache);
        await RefreshCacheSizeAsync();
        if (!result.Acquired)
        {
            ThemedMessageDialog.Show(this,
                "The cache is currently being used by another USB Drive Builder window. Close that build or wait for media preparation to finish, then try again.",
                "Cache in use", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (result.RemainingBytes > 0)
        {
            ThemedMessageDialog.Show(this,
                $"Most cached data was removed, but {FormatBytes(result.RemainingBytes)} is still locked or in use. Close other programs that may be scanning those files and try again.",
                "Cache partially cleared", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ThemedMessageDialog.Show(this, "The build cache was cleared. Logs and saved settings were kept.",
            "Cache cleared", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static CacheClearResult ClearCache()
    {
        Semaphore? guard = null;
        var acquired = false;
        try
        {
            guard = new Semaphore(1, 1, MainWindow.CacheCleanupGuardName);
            acquired = guard.WaitOne(0);
            if (!acquired) return new CacheClearResult(false, GetCacheSize());
            foreach (var path in GetCachePaths())
            {
                for (var attempt = 0; attempt < 3 && Directory.Exists(path); attempt++)
                {
                    try { Directory.Delete(path, true); }
                    catch (IOException) { Thread.Sleep(150); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(150); }
                }
            }
            return new CacheClearResult(true, GetCacheSize());
        }
        catch (UnauthorizedAccessException)
        {
            return new CacheClearResult(false, GetCacheSize());
        }
        finally
        {
            if (acquired)
            {
                try { guard?.Release(); }
                catch (SemaphoreFullException) { }
            }
            guard?.Dispose();
        }
    }

    private static long GetCacheSize()
    {
        long total = 0;
        foreach (var path in GetCachePaths())
        {
            if (!Directory.Exists(path)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    private static IEnumerable<string> GetCachePaths()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaptopQAUsbBuilder");
        return CacheDirectoryNames.Select(name => Path.Combine(root, name));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private readonly record struct CacheClearResult(bool Acquired, long RemainingBytes);

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
        GeneratedSetupPanel.IsEnabled = !_defaultsLocked;
        GeneratedSetupPanel.Opacity = _defaultsLocked ? 0.52 : 1;
        GeneratedSetupOptionsPanel.IsEnabled = !_defaultsLocked;
        GeneratedSetupOptionsPanel.Opacity = _defaultsLocked ? 0.52 : 1;
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
        WindowsSetup = ReadWindowsSetup();
        DialogResult = true;
    }

    private WindowsSetupConfig ReadWindowsSetup() => new()
    {
        TargetDisk = ParseInt(TargetDiskTextBox.Text, WindowsSetup.TargetDisk),
        InstallPartition = ParseInt(InstallPartitionTextBox.Text, WindowsSetup.InstallPartition),
        EfiSizeMb = ParseInt(EfiSizeTextBox.Text, WindowsSetup.EfiSizeMb),
        MsrSizeMb = ParseInt(MsrSizeTextBox.Text, WindowsSetup.MsrSizeMb),
        WindowsShrinkMb = ParseInt(WindowsShrinkTextBox.Text, WindowsSetup.WindowsShrinkMb),
        EfiLabel = EfiLabelTextBox.Text.Trim(), WindowsLabel = WindowsLabelTextBox.Text.Trim(), RecoveryLabel = RecoveryLabelTextBox.Text.Trim(),
        EfiLetter = EfiLetterTextBox.Text.Trim(), WindowsLetter = WindowsLetterTextBox.Text.Trim(), RecoveryLetter = RecoveryLetterTextBox.Text.Trim(),
        Edition = (EditionPicker.SelectedItem as WindowsEditionOption)?.Name ?? WindowsEditions[0].Name,
        PromptBeforeInstall = PromptBeforeInstallCheckBox.IsChecked == true,
        OobeLanguage = (OobeLanguagePicker.SelectedItem as OobeLanguageOption)?.Code ?? OobeLanguages[0].Code,
        OobeKeyboard = (OobeKeyboardPicker.SelectedItem as OobeKeyboardOption)?.Code ?? OobeKeyboards[0].Code
    };

    private void GenerateAutounattend_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save generated Autounattend.xml",
            Filter = "Autounattend.xml|Autounattend.xml|XML files (*.xml)|*.xml|All files (*.*)|*.*",
            FileName = "Autounattend.xml",
            DefaultExt = ".xml",
            AddExtension = true,
            InitialDirectory = PickerLocationStore.Get("XML")
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, MainWindow.BuildGeneratedAutounattend(ReadWindowsSetup()), new UTF8Encoding(false));
            PickerLocationStore.Set("XML", Path.GetDirectoryName(dialog.FileName));
            ThemedMessageDialog.Show(this, $"Generated Autounattend.xml was saved to:\n{dialog.FileName}", "Autounattend.xml generated", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ThemedMessageDialog.Show(this, $"Autounattend.xml could not be generated.\n\n{ex.Message}", "Autounattend generation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        if (OobeLanguagePicker.SelectedItem is not OobeLanguageOption)
        { message = "Choose an OOBE language."; return false; }
        if (OobeKeyboardPicker.SelectedItem is not OobeKeyboardOption)
        { message = "Choose an OOBE keyboard."; return false; }
        if (EditionPicker.SelectedItem is not WindowsEditionOption)
        { message = "Choose a Windows edition."; return false; }

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
        SizeHelpText.Text = T("Size Help") + "  Volume-label limits: FAT32 up to 11 characters; exFAT up to 15; NTFS up to 32.";
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
    public bool PromptBeforeInstall { get; set; } = true;
    public string OobeLanguage { get; set; } = "en-US";
    public string OobeKeyboard { get; set; } = "0409:00000409";
    public WindowsSetupConfig Clone() => (WindowsSetupConfig)MemberwiseClone();
}

public sealed record OobeLanguageOption(string Code, string Name)
{
    public override string ToString() => $"{Name} ({Code})";
}

public sealed record OobeKeyboardOption(string Code, string Name)
{
    public override string ToString() => $"{Name} ({Code})";
}

public sealed record WindowsEditionOption(string Name)
{
    public override string ToString() => Name;
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
    public bool GenerateAutounattend { get; set; }
    [JsonIgnore]
    public long SelectedContentBytes { get; set; }
    [JsonIgnore]
    public long LargestSelectedFileBytes { get; set; }
    [JsonIgnore]
    public long? ExtractedIsoBytes { get; set; }
    public bool IsRemaining => SizeText.Trim() == "*";
    public string PreviewText => $"{CalculatedSizeText ?? SizeText}  |  {FileSystem}";
    public string? FolderXmlSource => SourceFolders.Select(FindRootXmlFile).FirstOrDefault(path => path is not null);
    public bool HasAutounattend => GenerateAutounattend || !string.IsNullOrWhiteSpace(AutounattendSource) || FolderXmlSource is not null;
    public bool HasIso => !string.IsNullOrWhiteSpace(IsoSource);
    public bool HasScripts => ScriptFiles.Count > 0;
    public bool HasDrivers => DriverFolders.Count + DriverFiles.Count + DriverArchives.Count > 0;
    public bool HasAnyContent => SourceFiles.Count + SourceFolders.Count + ScriptFiles.Count > 0 ||
                                 HasAutounattend || HasIso || HasDrivers;
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
    public string AutounattendToolTip => GenerateAutounattend
        ? "A new Autounattend.xml will be generated from the Windows Setup defaults in Config."
        : !string.IsNullOrWhiteSpace(AutounattendSource)
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
                .Concat(GenerateAutounattend ? ["Autounattend.xml: generated from Config defaults"] : [])
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
        clone.GenerateAutounattend = GenerateAutounattend;
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
        GenerateAutounattend = false;
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
