using Microsoft.Win32;
using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LaptopQaUsbBuilder;

public partial class MainWindow : Window
{
    public string CurrentTheme => _preferences.Theme;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private bool _isBuilding;
    private bool _isPreflighting;
    private CancellationTokenSource? _buildCancellation;
    private bool _updatingPartitionGrid;
    private PartitionConfig? _draggedPartition;
    private Point _partitionDragStart;
    private double _partitionDragStartDistance = 12;
    private DropIndicatorAdorner? _mainDropIndicator;
    private int _mainDropDestinationIndex = -1;
    private string? _logPath;
    private List<PartitionConfig> _partitions = [];
    private List<PartitionConfig> _defaultPartitions = [];
    private AppPreferences _preferences = new();
    private readonly object _etaSync = new();
    private long _activityBytesTotal;
    private long _activityBytesCopied;
    private long _activitySampleBytes;
    private long _queueBytesTotal;
    private long _queueBytesCopied;
    private long _queueSampleBytes;
    private long _queueBytesPerDrive;
    private long _queueDiskStartBytes;
    private DateTime _activityStartedUtc;
    private DateTime _activitySampleUtc;
    private DateTime _queueStartedUtc;
    private DateTime _queueSampleUtc;
    private DateTime _lastEtaUiUpdateUtc;
    private double _activityBytesPerSecond;
    private double _queueBytesPerSecond;
    private double _activityProgressStart;
    private double _activityProgressEnd;
    private string _activityName = "Transfer";
    private static readonly string VersionLabel = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.50"}";
    private const string MainPartitionDragFormat = "LaptopQaUsbBuilder.MainPartition";
    private const string ScriptRunnerName = "LaptopQA-RunScripts.cmd";
    private const string ScriptCleanupName = "LaptopQA-Cleanup.ps1";

    public MainWindow()
    {
        InitializeComponent();
        _preferences = LoadPreferences();
        Localization.ApplyCulture(_preferences.Language);
        _defaultPartitions = LoadPartitionConfig();
        _partitions = _defaultPartitions.Select(p => p.Clone()).ToList();
        MainPartitionList.ItemsSource = _partitions;
        ApplyPartitionConfig();
        ApplyLanguage();
        ThemeService.Apply(this, _preferences.Theme);
        Loaded += (_, _) => ThemeService.Apply(this, _preferences.Theme);
        Loaded += async (_, _) =>
        {
            AddActivity("USB Drive Builder started in administrator mode.");
            await RefreshDisksAsync();
        };
        Closing += (_, e) =>
        {
            if (!_isBuilding && !_isPreflighting)
            {
                CleanupTemporaryCaches();
                return;
            }
            e.Cancel = true;
            MessageBox.Show(_isPreflighting ? "Wait for the build safety checks to finish before closing." : "Wait for the active USB build to finish before closing.",
                _isPreflighting ? "Preparing build" : "Build in progress", MessageBoxButton.OK, MessageBoxImage.Information);
        };
    }

    private static void CleanupTemporaryCaches()
    {
        var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaptopQAUsbBuilder");
        foreach (var name in new[] { "MediaCache", "DriverPackCache", "DriverPayloadCache" })
        {
            var path = Path.Combine(cacheRoot, name);
            for (var attempt = 0; attempt < 3 && Directory.Exists(path); attempt++)
            {
                try { Directory.Delete(path, true); }
                catch (IOException) { Thread.Sleep(150); }
                catch (UnauthorizedAccessException) { Thread.Sleep(150); }
            }
        }
    }

    private async Task RefreshDisksAsync()
    {
        try
        {
            RefreshButton.IsEnabled = false;
            FooterText.Text = $"Administrator mode  |  {VersionLabel}  |  Scanning for USB disks";
            var script = "$ProgressPreference='SilentlyContinue';$items=@(Get-Disk|Where-Object BusType -eq 'USB'|Where-Object OperationalStatus -ne 'Offline'|Sort-Object Number|ForEach-Object{$d=$_;$letters=@(Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue|Get-Volume -ErrorAction SilentlyContinue|Where-Object DriveLetter|Sort-Object DriveLetter|ForEach-Object{[string]$_.DriveLetter+':'});[pscustomobject]@{Number=$d.Number;FriendlyName=$d.FriendlyName;SerialNumber=$d.SerialNumber;UniqueId=$d.UniqueId;Size=$d.Size;IsBoot=$d.IsBoot;IsSystem=$d.IsSystem;DriveLetters=($letters -join ', ')}});ConvertTo-Json -InputObject $items -Compress";
            var json = await RunPowerShellAsync(script);
            var disks = DeserializeUsbDisks(json);
            DiskPicker.ItemsSource = disks;
            DiskPicker.UnselectAll();
            FooterText.Text = disks.Count == 0
                ? $"Administrator mode  |  {VersionLabel}  |  No USB disks detected"
                : $"Administrator mode  |  {VersionLabel}  |  {disks.Count} USB disk(s) detected";
            if (disks.Count == 0)
            {
                UpdatePartitionPreview([]);
                AddActivity("No USB disks detected. Insert a drive and select Refresh.");
            }
        }
        catch (Exception ex)
        {
            AddActivity($"Disk scan failed: {ex.Message}");
            MessageBox.Show($"Unable to scan USB disks.\n\n{ex.Message}", "USB scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            UpdateBuildButton();
        }
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_isBuilding || _isPreflighting) return;
        var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaptopQAUsbBuilder", "Logs");
        Directory.CreateDirectory(logFolder);
        _logPath = Path.Combine(logFolder, $"Build-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        ActivityList.Items.Clear();
        SetPreflightState(true);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        _buildCancellation = new CancellationTokenSource();
        try { await BuildCoreAsync(); }
        catch (OperationCanceledException)
        {
            AddActivity("Build cancelled by user.");
            SetStatus("✕ Cancelled", "#AE3338");
        }
        catch (Exception ex)
        {
            Log($"Unexpected build error: {LogSanitizer.SanitizeException(ex)}");
            if (_isBuilding) SetBuildingState(false);
            MessageBox.Show($"The build could not continue.\n\n{ex.Message}\n\nLog: {_logPath}",
                "Build failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _buildCancellation?.Dispose(); _buildCancellation = null; SetPreflightState(false); SetBuildingState(false); }
    }

    private async Task BuildCoreAsync()
    {
        var queuedDisks = SelectedDisks();
        if (queuedDisks.Count == 0) return;
        if (!ValidatePartitionLayout(out var layoutError))
        {
            MessageBox.Show(layoutError, "Invalid partition settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var requiredSize = _partitions.Where(p => !p.IsRemaining).Sum(p => PartitionConfig.TryParseSize(p.SizeText, out var bytes) ? bytes : 0) + 64L * 1024 * 1024;
        var tooSmall = queuedDisks.Where(d => d.Size < requiredSize).ToList();
        if (tooSmall.Count > 0)
        {
            MessageBox.Show($"These drives are too small for the configured layout (minimum {FormatBytes(requiredSize)}):\n\n{string.Join("\n", tooSmall.Select(d => $"Disk {d.Number} - {d.FriendlyName} ({FormatBytes(d.Size)})"))}",
                "Drive too small", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var tooLargeForMbr = queuedDisks.Where(d => d.Size > 2L * 1024 * 1024 * 1024 * 1024).ToList();
        if (tooLargeForMbr.Count > 0)
        {
            MessageBox.Show($"MBR cannot address all space on these drives:\n\n{string.Join("\n", tooLargeForMbr.Select(d => $"Disk {d.Number} - {d.FriendlyName} ({FormatBytes(d.Size)})"))}",
                "Drive exceeds MBR limit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var fixedSize = _partitions.Where(p => !p.IsRemaining).Sum(p => PartitionConfig.TryParseSize(p.SizeText, out var bytes) ? bytes : 0);
        var remainingPartition = _partitions.Single(p => p.IsRemaining);
        var fat32TooLarge = queuedDisks.Where(d => remainingPartition.FileSystem == "FAT32" && d.Size - fixedSize > 32L * 1024 * 1024 * 1024).ToList();
        if (fat32TooLarge.Count > 0)
        {
            MessageBox.Show("The remaining-space partition would exceed Windows' 32 GB FAT32 formatting limit. Choose NTFS or exFAT for that partition, or increase the fixed partitions.",
                "FAT32 partition too large", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folderSources = _partitions.SelectMany(p => p.SourceFolders.Concat(p.DriverFolders))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fileSources = _partitions.SelectMany(p => p.SourceFiles
                .Concat(p.ScriptFiles)
                .Concat(p.DriverFiles)
                .Concat(p.DriverArchives)
                .Concat(string.IsNullOrWhiteSpace(p.AutounattendSource) ? [] : [p.AutounattendSource])
                .Concat(string.IsNullOrWhiteSpace(p.IsoSource) ? [] : [p.IsoSource]))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var source in folderSources)
        {
            if (!Directory.Exists(source))
            {
                MessageBox.Show($"Source folder not found:\n{source}", "Missing source", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            foreach (var disk in queuedDisks)
            {
                if (await SourceIsOnDiskAsync(source, disk.Number))
                {
                    MessageBox.Show($"A copy source is stored on queued Disk {disk.Number} and would be erased before it could be copied:\n{source}",
                        "Source is on target disk", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }
        foreach (var source in fileSources)
        {
            if (!File.Exists(source))
            {
                MessageBox.Show($"Source file not found:\n{source}", "Missing source", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            foreach (var disk in queuedDisks)
                if (await SourceIsOnDiskAsync(source, disk.Number))
                {
                    MessageBox.Show($"A copy source is stored on queued Disk {disk.Number} and would be erased before it could be copied:\n{source}",
                        "Source is on target disk", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
        }

        foreach (var partition in _partitions.Where(item => item.HasScripts))
        {
            try
            {
                var answerFileSource = partition.AutounattendSource ?? partition.FolderXmlSource;
                partition.PreparedAutounattendXml = BuildScriptAutounattend(answerFileSource, answerFileSource is null ? _preferences.WindowsSetup : null);
                AddActivity(string.IsNullOrWhiteSpace(answerFileSource)
                    ? $"Prepared a generated Autounattend.xml to run scripts for {partition.Name}."
                    : $"Prepared the selected Autounattend.xml with the script runner for {partition.Name}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"The Windows Setup script command could not be added to Autounattend.xml.\n\n{ex.Message}",
                    "Autounattend preparation failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        await RefreshAllPartitionContentSizesAsync();

        foreach (var partition in _partitions.Where(p => p.HasIso))
        {
            if (partition.FileSystem != "NTFS" || partition.IsRemaining || !PartitionConfig.TryParseSize(partition.SizeText, out var partitionBytes))
            {
                MessageBox.Show($"{partition.Name} must be a fixed-size NTFS partition to create bootable Windows media.",
                    "Invalid boot partition", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                SetStatus("Preparing Windows media", "#B36A13");
                var isoInfo = await InspectBootableIsoAsync(partition.IsoSource!);
                if (partition.IsoEditionIndex is not { } editionIndex ||
                    isoInfo.Editions.FirstOrDefault(item => item.Index == editionIndex) is not { } edition)
                    throw new InvalidOperationException("The selected Windows edition is no longer present in this ISO. Select the ISO again and choose an edition.");
                if (partition.HasDrivers)
                {
                    foreach (var folder in partition.DriverFolders)
                        if (!Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Any())
                            throw new InvalidOperationException($"The selected drivers folder no longer contains INF packages: {folder}");
                    foreach (var file in partition.DriverFiles)
                        if (!Path.GetExtension(file).Equals(".inf", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"An individual driver selection is not an INF file: {file}");
                    foreach (var archive in partition.DriverArchives)
                        if (!Path.GetExtension(archive).Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetExtension(archive).Equals(".cab", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"A compressed driver pack is not a ZIP or CAB file: {archive}");
                }

                partition.IsoEditionName = edition.Name;
                partition.ForceUnsignedDrivers = partition.HasDrivers && _preferences.ForceUnsignedDrivers;
                var selection = new WindowsIsoSelection(edition.Index, edition.Name,
                    partition.DriverFolders.ToArray(), partition.DriverFiles.ToArray(), partition.DriverArchives.ToArray(),
                    partition.ForceUnsignedDrivers);
                var preparer = new WindowsMediaPreparer(
                    message => { SetNonTransferActivity(message); AddActivity(message); },
                    Log, MountIsoAsync, DismountIsoAsync);
                var prepared = await preparer.PrepareAsync(partition.IsoSource!, isoInfo, selection);
                partition.PreparedMediaPath = prepared.MediaPath;
                partition.ExtractedIsoBytes = prepared.TotalBytes;
                if (prepared.DriverRejections.Count > 0)
                {
                    AddActivity($"Driver servicing continued with {prepared.DriverRejections.Count} skipped package(s). See the build log for details.");
                    foreach (var rejection in prepared.DriverRejections)
                        Log($"Driver skipped from {rejection.Image} (DISM 0x8007000D invalid package data): {rejection.DriverPath}");
                }
                var requiredCapacity = EstimateRequiredPartitionCapacity(partition);
                if (requiredCapacity > partitionBytes)
                    throw new InvalidOperationException($"The prepared Windows media and other selected content need approximately {FormatBytes(requiredCapacity)}, but {partition.Name} is only {FormatBytes(partitionBytes)}.");
            }
            catch (IncompleteDriverPackageException ex)
            {
                SetStatus(Localization.Text(_preferences.Language, "Ready"), "#147A4B");
                Log($"Driver package preflight failed: {LogSanitizer.SanitizeException(ex)}");
                MessageBox.Show(ex.Message, "Incomplete driver package", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            catch (Exception ex)
            {
                SetStatus(Localization.Text(_preferences.Language, "Ready"), "#147A4B");
                Log($"Windows media preparation failed: {LogSanitizer.SanitizeException(ex)}");
                MessageBox.Show($"The selected ISO cannot be prepared as bootable Windows media.\n\n{ex.Message}",
                    "Windows media preparation failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        UpdatePartitionPreview(queuedDisks);
        var capacityWarnings = GetContentCapacityWarnings(queuedDisks);
        SetStatus(Localization.Text(_preferences.Language, "Ready"), "#147A4B");
        if (capacityWarnings.Count > 0)
        {
            MessageBox.Show($"Selected files and folders will not fit in the configured partition space:\n\n{string.Join("\n", capacityWarnings)}\n\nIncrease the affected partition size or remove content before building.",
                "Partition content will not fit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SetPreflightState(false);
        SetBuildingState(true);
        SetStatus("Building", "#B36A13");
        CurrentEtaText.Text = "Current activity: Preparing drive...";
        QueueEtaText.Visibility = Visibility.Collapsed;
        InitializeEtaTracking(0, queuedDisks.Count);
        BuildProgress.IsIndeterminate = true;

        var succeeded = 0;
        var failures = new List<string>();
        for (var queueIndex = 0; queueIndex < queuedDisks.Count; queueIndex++)
        {
            var disk = queuedDisks[queueIndex];
            StartQueueDiskEstimate();
            SetStatus($"Building {queueIndex + 1} of {queuedDisks.Count}", "#B36A13");
            BuildProgress.IsIndeterminate = true;
            SetNonTransferActivity("Preparing drive...");
            try
            {
                AddActivity($"QUEUE {queueIndex + 1}/{queuedDisks.Count}: Locked target to Disk {disk.Number}: {disk.FriendlyName}.");
                AddActivity("Clearing existing partitions and creating the requested layout.");
                var result = await CreatePartitionsAsync(disk);
                BuildProgress.IsIndeterminate = false;
                BuildProgress.Value = 35;
                foreach (var partition in _partitions) AddActivity($"Created {partition.Name} ({partition.SizeText}, {partition.FileSystem}).");
                var copyPartitions = _partitions.Select((partition, index) => (partition, index))
                    .Where(item => item.partition.HasAnyContent).ToList();
                for (var copyIndex = 0; copyIndex < copyPartitions.Count; copyIndex++)
                {
                    var (partition, partitionIndex) = copyPartitions[copyIndex];
                    if (partitionIndex >= result.Letters.Count || string.IsNullOrWhiteSpace(result.Letters[partitionIndex]))
                        throw new InvalidOperationException($"Windows did not assign a drive letter to {partition.Name}.");
                    var start = 35 + 60 * copyIndex / Math.Max(1, copyPartitions.Count);
                    var end = 35 + 60 * (copyIndex + 1) / Math.Max(1, copyPartitions.Count);
                    await CopyPartitionSourcesAsync(partition, $"{result.Letters[partitionIndex]}:\\", start, end);
                }
                if (copyPartitions.Count == 0) BuildProgress.Value = 95;
                AddActivity("Verifying partition labels and file systems.");
                SetNonTransferActivity("Verifying partitions...");
                await VerifyPartitionsAsync(disk.Number, disk.UniqueId);
                BuildProgress.Value = 100;
                succeeded++;
                CompleteQueueDiskEstimate();
                AddActivity($"Disk {disk.Number} completed and verified.");
                Log($"Disk {disk.Number} completed and verified.");
            }
            catch (Exception ex)
            {
                CompleteQueueDiskEstimate();
                failures.Add($"Disk {disk.Number}: {ex.Message}");
                AddActivity($"Disk {disk.Number} FAILED: {ex.Message}. Continuing queue.");
                Log($"Disk {disk.Number} ERROR: {LogSanitizer.SanitizeException(ex)}");
            }
        }
        BuildProgress.IsIndeterminate = false;
        CompleteEtaTracking();
        SetBuildingState(false);
        ConfirmText.Clear();
        SetStatus(failures.Count == 0 ? "✓ Complete" : "✕ Queue finished with errors", failures.Count == 0 ? "#147A4B" : "#AE3338");
        var failureText = failures.Count == 0 ? "" : $"\n\nFailures:\n{string.Join("\n", failures)}";
        MessageBox.Show($"Queue finished.\n\nSucceeded: {succeeded}\nFailed: {failures.Count}{failureText}\n\nLog: {_logPath}",
            "USB queue complete", MessageBoxButton.OK, failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async Task<PartitionResult> CreatePartitionsAsync(UsbDisk disk)
    {
        var expectedId = PsQuote(disk.UniqueId ?? "");
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference='Stop'");
        script.AppendLine("$ProgressPreference='SilentlyContinue'");
        script.AppendLine($"$d=Get-Disk -Number {disk.Number}");
        script.AppendLine("if($d.BusType -ne 'USB'){throw 'Selected disk is no longer a USB disk.'}");
        script.AppendLine("if($d.IsBoot -or $d.IsSystem){throw 'Refusing to modify a boot or system disk.'}");
        script.AppendLine($"if('{expectedId}' -and [string]$d.UniqueId -ne '{expectedId}'){{throw 'The USB device changed after selection. Refresh and select it again.'}}");
        script.AppendLine($"if($d.IsReadOnly){{Set-Disk -Number {disk.Number} -IsReadOnly $false}}");
        script.AppendLine($"if($d.IsOffline){{Set-Disk -Number {disk.Number} -IsOffline $false}}");
        script.AppendLine($"if($d.PartitionStyle -ne 'RAW'){{Clear-Disk -Number {disk.Number} -RemoveData -RemoveOEM -Confirm:$false}}");
        script.AppendLine("for($i=0;$i -lt 20;$i++){Update-HostStorageCache -ErrorAction SilentlyContinue;$d=Get-Disk -Number " + disk.Number + ";$partitionCount=@(Get-Partition -DiskNumber " + disk.Number + " -ErrorAction SilentlyContinue).Count;if($d.PartitionStyle -eq 'RAW' -or ($d.PartitionStyle -eq 'MBR' -and $partitionCount -eq 0)){break};Start-Sleep -Milliseconds 250}");
        script.AppendLine($"$d=Get-Disk -Number {disk.Number};$partitionCount=@(Get-Partition -DiskNumber {disk.Number} -ErrorAction SilentlyContinue).Count");
        script.AppendLine($"if($d.PartitionStyle -eq 'RAW'){{Initialize-Disk -Number {disk.Number} -PartitionStyle MBR | Out-Null}}elseif($d.PartitionStyle -eq 'MBR' -and $partitionCount -eq 0){{}}elseif($d.PartitionStyle -eq 'GPT'){{throw 'Windows did not clear the GPT partition table. Disconnect and reconnect the USB stick, then retry.'}}else{{throw \"The cleared USB disk is in an unexpected state: $($d.PartitionStyle) with $partitionCount partition(s).\"}}");
        script.AppendLine($"Update-HostStorageCache -ErrorAction SilentlyContinue;$d=Get-Disk -Number {disk.Number};if($d.PartitionStyle -ne 'MBR'){{throw \"The USB disk could not be prepared as MBR. Windows reports $($d.PartitionStyle).\"}}");

        for (var index = 0; index < _partitions.Count; index++)
        {
            var item = _partitions[index];
            var variable = $"p{index + 1}";
            string sizeArgument;
            if (item.IsRemaining && index == _partitions.Count - 1)
            {
                sizeArgument = "-UseMaximumSize";
            }
            else if (item.IsRemaining)
            {
                var reservedAfter = _partitions.Skip(index + 1).Sum(p => PartitionConfig.TryParseSize(p.SizeText, out var bytes) ? bytes : 0);
                script.AppendLine($"$remainingSize=[math]::Floor(((Get-Disk -Number {disk.Number}).LargestFreeExtent-{reservedAfter})/1MB)*1MB");
                script.AppendLine("if($remainingSize -lt 32MB){throw 'The remaining-space partition would be smaller than 32 MB.'}");
                sizeArgument = "-Size $remainingSize";
            }
            else
            {
                sizeArgument = PartitionConfig.TryParseSize(item.SizeText, out var sizeBytes)
                    ? $"-Size {sizeBytes}"
                    : throw new InvalidOperationException($"Invalid size for partition {index + 1}.");
            }
            var mbrType = item.FileSystem == "FAT32" ? " -MbrType FAT32" : " -MbrType IFS";
            script.AppendLine($"${variable}=New-Partition -DiskNumber {disk.Number} {sizeArgument} -AssignDriveLetter{mbrType}");
            var allocation = item.FileSystem == "NTFS" ? " -AllocationUnitSize 4096" : "";
            script.AppendLine($"${variable} | Format-Volume -FileSystem {item.FileSystem} -NewFileSystemLabel '{PsQuote(item.Name)}'{allocation} -Confirm:$false -Force | Out-Null");
        }

        script.AppendLine($"Get-Partition -DiskNumber {disk.Number}|Where-Object IsActive|Set-Partition -IsActive $false");

        var letterExpressions = string.Join(",", Enumerable.Range(1, _partitions.Count).Select(number => $"[string](($p{number}|Get-Volume).DriveLetter)"));
        script.AppendLine($"[pscustomobject]@{{Letters=@({letterExpressions})}} | ConvertTo-Json -Compress");
        var json = await RunPowerShellAsync(script.ToString());
        return JsonSerializer.Deserialize<PartitionResult>(json, _jsonOptions)
               ?? throw new InvalidOperationException("Windows did not return the new partition drive letters.");
    }

    private async Task VerifyPartitionsAsync(int diskNumber, string? uniqueId)
    {
        var id = PsQuote(uniqueId ?? "");
        var expected = string.Join(",", _partitions.Select(p => $"[pscustomobject]@{{Label='{PsQuote(p.Name)}';Fs='{p.FileSystem}'}}"));
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference='Stop'");
        script.AppendLine($"$d=Get-Disk -Number {diskNumber}");
        script.AppendLine($"if($d.BusType -ne 'USB' -or ('{id}' -and [string]$d.UniqueId -ne '{id}')){{throw 'Target USB identity changed during verification.'}}");
        script.AppendLine("if($d.PartitionStyle -ne 'MBR'){throw 'Verification found that the USB disk is not MBR.'}");
        script.AppendLine($"if(Get-Partition -DiskNumber {diskNumber}|Where-Object IsActive){{throw 'A legacy-active partition was found; the installer would not be UEFI-only.'}}");
        script.AppendLine($"$v=@(Get-Partition -DiskNumber {diskNumber} | Get-Volume | Where-Object FileSystemLabel)");
        script.AppendLine($"$expected=@({expected})");
        script.AppendLine($"if($v.Count -ne {_partitions.Count}){{throw 'Verification found an unexpected number of formatted partitions.'}}");
        script.AppendLine("foreach($e in $expected){$item=$v|Where-Object FileSystemLabel -eq $e.Label|Select-Object -First 1;if(-not $item -or $item.FileSystem -ine $e.Fs){throw \"Verification failed for $($e.Label).\"}}");
        script.AppendLine("'OK'");
        await RunPowerShellAsync(script.ToString());
    }

    private async Task CopyFoldersAsync(IReadOnlyList<string> folders, string destination, string name, int startProgress, int endProgress)
    {
        if (folders.Count == 0)
        {
            AddActivity($"No folders selected for {name}; leaving it empty.");
            BuildProgress.Value = endProgress;
            return;
        }

        var progressRange = endProgress - startProgress;
        for (var index = 0; index < folders.Count; index++)
        {
            var folderStart = startProgress + progressRange * index / folders.Count;
            var folderEnd = startProgress + progressRange * (index + 1) / folders.Count;
            await CopySourceAsync(folders[index], destination, $"{name} folder {index + 1} of {folders.Count}", folderStart, folderEnd);
        }
    }

    private async Task CopyPartitionSourcesAsync(PartitionConfig partition, string destination, int startProgress, int endProgress)
    {
        var sources = partition.SourceFolders.Select(path => (Path: path, IsFolder: true, TargetName: (string?)null))
            .Concat(partition.SourceFiles.Select(path => (Path: path, IsFolder: false, TargetName: (string?)Path.GetFileName(path))))
            .ToList();
        var hasIso = partition.FileSystem == "NTFS" && !string.IsNullOrWhiteSpace(partition.IsoSource);
        var hasAutounattend = (partition.FileSystem == "NTFS" || hasIso) &&
                              (!string.IsNullOrWhiteSpace(partition.AutounattendSource) ||
                               !string.IsNullOrWhiteSpace(partition.PreparedAutounattendXml));
        var hasScripts = hasIso && partition.HasScripts;
        var operationCount = sources.Count + (hasIso ? 1 : 0) + (hasScripts ? 1 : 0) + (hasAutounattend ? 1 : 0);
        if (operationCount == 0) { BuildProgress.Value = endProgress; return; }

        for (var index = 0; index < sources.Count; index++)
        {
            var sourceStart = startProgress + (endProgress - startProgress) * index / operationCount;
            var sourceEnd = startProgress + (endProgress - startProgress) * (index + 1) / operationCount;
            var source = sources[index];
            if (source.IsFolder)
            {
                await CopySourceAsync(source.Path, destination, $"{partition.Name} folder {index + 1} of {sources.Count}", sourceStart, sourceEnd);
            }
            else
            {
                var target = Path.Combine(destination, source.TargetName ?? Path.GetFileName(source.Path));
                AddActivity($"Copying file {source.TargetName ?? Path.GetFileName(source.Path)} to {partition.Name}.");
                Log($"Copying {source.Path} to {target}");
                await CopyFileWithProgressAsync(source.Path, target, Path.GetFileName(source.Path), sourceStart, sourceEnd);
            }
        }

        var operationIndex = sources.Count;
        if (hasIso)
        {
            var isoStart = startProgress + (endProgress - startProgress) * operationIndex / operationCount;
            var isoEnd = startProgress + (endProgress - startProgress) * ++operationIndex / operationCount;
            await CopyPreparedWindowsMediaAsync(partition, destination, isoStart, isoEnd);
        }

        if (hasScripts)
        {
            var scriptsStart = startProgress + (endProgress - startProgress) * operationIndex / operationCount;
            var scriptsEnd = startProgress + (endProgress - startProgress) * ++operationIndex / operationCount;
            await CopyWindowsSetupScriptsAsync(partition, destination, scriptsStart, scriptsEnd);
        }

        if (hasAutounattend)
        {
            var xmlStart = startProgress + (endProgress - startProgress) * operationIndex / operationCount;
            var target = Path.Combine(destination, "Autounattend.xml");
            if (!string.IsNullOrWhiteSpace(partition.PreparedAutounattendXml))
            {
                BuildProgress.Value = xmlStart;
                AddActivity($"Writing script-enabled Autounattend.xml to {partition.Name}.");
                Log($"Writing generated script-enabled Autounattend.xml to {target}");
                await File.WriteAllTextAsync(target, partition.PreparedAutounattendXml, new UTF8Encoding(false));
                BuildProgress.Value = endProgress;
            }
            else
            {
                AddActivity($"Copying Autounattend.xml to {partition.Name}.");
                Log($"Copying {partition.AutounattendSource} to {target}");
                await CopyFileWithProgressAsync(partition.AutounattendSource!, target, "Autounattend.xml", xmlStart, endProgress);
            }
        }
        AddActivity($"Selected content copied to {partition.Name}.");
    }

    private async Task CopyWindowsSetupScriptsAsync(PartitionConfig partition, string destination, int startProgress, int endProgress)
    {
        var scriptsDestination = Path.Combine(destination, "sources", "$OEM$", "$$", "Setup", "Scripts");
        Directory.CreateDirectory(scriptsDestination);
        var sources = partition.ScriptFiles.ToList();
        AddActivity($"Adding {sources.Count} Windows Setup script/support source(s) to {partition.Name}.");

        for (var index = 0; index < sources.Count; index++)
        {
            var sourceStart = startProgress + (endProgress - startProgress) * index / sources.Count;
            var sourceEnd = startProgress + (endProgress - startProgress) * (index + 1) / sources.Count;
            var source = sources[index];
            var target = Path.Combine(scriptsDestination, Path.GetFileName(source));
            AddActivity($"Copying Setup script/support file {Path.GetFileName(source)} to {partition.Name}.");
            Log($"Copying {source} to {target}");
            await CopyFileWithProgressAsync(source, target, Path.GetFileName(source), sourceStart, sourceEnd);
        }

        var runnerPath = Path.Combine(scriptsDestination, ScriptRunnerName);
        var cleanupPath = Path.Combine(scriptsDestination, ScriptCleanupName);
        await File.WriteAllTextAsync(runnerPath, BuildScriptRunner(partition.ScriptFiles), Encoding.ASCII);
        await File.WriteAllTextAsync(cleanupPath, BuildScriptCleanup(partition.ScriptFiles), new UTF8Encoding(false));
        BuildProgress.Value = endProgress;
        AddActivity($"Windows Setup scripts and automatic cleanup added to sources\\$OEM$\\$$\\Setup\\Scripts on {partition.Name}.");
    }

    private static string BuildScriptAutounattend(string? sourcePath, WindowsSetupConfig? generatedSetup = null)
    {
        XNamespace unattend = "urn:schemas-microsoft-com:unattend";
        XNamespace wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        XDocument document;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            document = new XDocument(new XElement(unattend + "unattend",
                new XAttribute(XNamespace.Xmlns + "wcm", wcm.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName)));
            if (generatedSetup is not null) AddGeneratedWindowsSetup(document, generatedSetup, unattend, wcm);
        }
        else
        {
            document = XDocument.Load(sourcePath, LoadOptions.PreserveWhitespace);
        }

        var root = document.Root;
        if (root is null || root.Name != unattend + "unattend")
            throw new InvalidOperationException("The selected XML is not a standard Windows unattend document (urn:schemas-microsoft-com:unattend). The original file was not changed.");
        if (root.GetNamespaceOfPrefix("wcm") is null)
            root.Add(new XAttribute(XNamespace.Xmlns + "wcm", wcm.NamespaceName));
        if (root.GetNamespaceOfPrefix("xsi") is null)
            root.Add(new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName));

        foreach (var oldCommand in root.Descendants(unattend + "RunSynchronousCommand")
                     .Where(command => command.Element(unattend + "Path")?.Value.Contains(ScriptRunnerName, StringComparison.OrdinalIgnoreCase) == true)
                     .ToList())
            oldCommand.Remove();

        var settings = root.Elements(unattend + "settings")
            .FirstOrDefault(element => element.Attribute("pass")?.Value.Equals("specialize", StringComparison.OrdinalIgnoreCase) == true);
        if (settings is null)
        {
            settings = new XElement(unattend + "settings", new XAttribute("pass", "specialize"));
            root.Add(settings);
        }

        var component = settings.Elements(unattend + "component").FirstOrDefault(element =>
            element.Attribute("name")?.Value.Equals("Microsoft-Windows-Deployment", StringComparison.OrdinalIgnoreCase) == true &&
            element.Attribute("processorArchitecture")?.Value.Equals("amd64", StringComparison.OrdinalIgnoreCase) == true);
        if (component is null)
        {
            component = new XElement(unattend + "component",
                new XAttribute("name", "Microsoft-Windows-Deployment"),
                new XAttribute("processorArchitecture", "amd64"),
                new XAttribute("publicKeyToken", "31bf3856ad364e35"),
                new XAttribute("language", "neutral"),
                new XAttribute("versionScope", "nonSxS"));
            settings.Add(component);
        }

        var runSynchronous = component.Element(unattend + "RunSynchronous");
        if (runSynchronous is null)
        {
            runSynchronous = new XElement(unattend + "RunSynchronous");
            component.Add(runSynchronous);
        }
        var nextOrder = runSynchronous.Elements(unattend + "RunSynchronousCommand")
            .Select(command => int.TryParse(command.Element(unattend + "Order")?.Value, out var order) ? order : 0)
            .DefaultIfEmpty(0).Max() + 1;
        if (nextOrder > 500)
            throw new InvalidOperationException("The selected Autounattend.xml already uses the maximum RunSynchronous order. Remove or reorder an existing specialize command and try again.");
        runSynchronous.Add(new XElement(unattend + "RunSynchronousCommand",
            new XAttribute(wcm + "action", "add"),
            new XElement(unattend + "Order", nextOrder),
            new XElement(unattend + "Description", "Run USB Drive Builder Windows Setup scripts"),
            new XElement(unattend + "Path", $"cmd.exe /d /c \"%WINDIR%\\Setup\\Scripts\\{ScriptRunnerName}\"")));

        document.Declaration = null;
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + document;
    }

    private static void AddGeneratedWindowsSetup(XDocument document, WindowsSetupConfig setup, XNamespace unattend, XNamespace wcm)
    {
        var root = document.Root!;
        var settings = new XElement(unattend + "settings", new XAttribute("pass", "windowsPE"));
        var component = new XElement(unattend + "component", new XAttribute("name", "Microsoft-Windows-Setup"), new XAttribute("processorArchitecture", "amd64"), new XAttribute("publicKeyToken", "31bf3856ad364e35"), new XAttribute("language", "neutral"), new XAttribute("versionScope", "nonSxS"));
        var firstDiskpart = $"SELECT DISK={setup.TargetDisk}&echo:CLEAN&echo:CONVERT GPT&echo:CREATE PARTITION EFI SIZE={setup.EfiSizeMb}&echo:FORMAT QUICK FS=FAT32 LABEL=^\"{setup.EfiLabel}^\"&echo:ASSIGN LETTER={setup.EfiLetter}&echo:CREATE PARTITION MSR SIZE={setup.MsrSizeMb}&echo:CREATE PARTITION PRIMARY";
        var secondDiskpart = $"SHRINK MINIMUM={setup.WindowsShrinkMb}&echo:FORMAT QUICK FS=NTFS LABEL=^\"{setup.WindowsLabel}^\"&echo:ASSIGN LETTER={setup.WindowsLetter}&echo:CREATE PARTITION PRIMARY&echo:FORMAT QUICK FS=NTFS LABEL=^\"{setup.RecoveryLabel}^\"&echo:ASSIGN LETTER={setup.RecoveryLetter}";
        var thirdDiskpart = "SET ID=^\"de94bba4-06d1-4d40-a16a-bfd50179d6ac^\"&echo:GPT ATTRIBUTES=0x8000000000000001";
        var commands = new XElement(unattend + "RunSynchronous");
        var order = 1;
        if (setup.PromptBeforeInstall)
            commands.Add(new XElement(unattend + "RunSynchronousCommand", new XAttribute(wcm + "action", "add"), new XElement(unattend + "Order", order++), new XElement(unattend + "Description", "Confirm Windows installation"), new XElement(unattend + "Path", $"cmd.exe /c choice /C YN /N /D N /T 30 /M \\\"Windows Setup is ready to reimage this computer. This will erase all data on Disk {setup.TargetDisk} and install a fresh copy of Windows. Select Y to begin, or N to return without making changes.\\\" & if errorlevel 2 exit /b 1")));
        commands.Add(new XElement(unattend + "RunSynchronousCommand", new XAttribute(wcm + "action", "add"), new XElement(unattend + "Order", order++), new XElement(unattend + "Description", "Write GPT partition script"), new XElement(unattend + "Path", $"cmd.exe /c \">\"X:\\diskpart.txt\" (echo:{firstDiskpart})")));
        commands.Add(new XElement(unattend + "RunSynchronousCommand", new XAttribute(wcm + "action", "add"), new XElement(unattend + "Order", order++), new XElement(unattend + "Description", "Add Windows and Recovery partitions"), new XElement(unattend + "Path", $"cmd.exe /c \">>\"X:\\diskpart.txt\" (echo:{secondDiskpart})")));
        commands.Add(new XElement(unattend + "RunSynchronousCommand", new XAttribute(wcm + "action", "add"), new XElement(unattend + "Order", order++), new XElement(unattend + "Description", "Mark the Recovery partition"), new XElement(unattend + "Path", $"cmd.exe /c \">>\"X:\\diskpart.txt\" (echo:{thirdDiskpart})")));
        commands.Add(new XElement(unattend + "RunSynchronousCommand", new XAttribute(wcm + "action", "add"), new XElement(unattend + "Order", order), new XElement(unattend + "Description", "Run DiskPart"), new XElement(unattend + "Path", "cmd.exe /c \"diskpart.exe /s \"X:\\diskpart.txt\" >>\"X:\\diskpart.log\" || ( type \"X:\\diskpart.log\" & echo diskpart encountered an error. & pause & exit /b 1 )\"")));
        component.Add(commands);
        var imageInstall = new XElement(unattend + "ImageInstall",
            new XElement(unattend + "OSImage",
                new XElement(unattend + "InstallFrom",
                    new XElement(unattend + "MetaData", new XAttribute(wcm + "action", "add"), new XElement(unattend + "Key", "/IMAGE/NAME"), new XElement(unattend + "Value", setup.Edition))),
                new XElement(unattend + "InstallTo", new XElement(unattend + "DiskID", setup.TargetDisk), new XElement(unattend + "PartitionID", setup.InstallPartition))));
        component.Add(imageInstall);
        component.Add(new XElement(unattend + "UserData", new XElement(unattend + "AcceptEula", "true")));
        component.Add(new XElement(unattend + "UseConfigurationSet", "true"));
        settings.Add(component); root.AddFirst(settings);
    }

    private static string BuildScriptRunner(IEnumerable<string> scriptPaths)
    {
        var lines = new List<string> { "@echo off", "setlocal EnableExtensions DisableDelayedExpansion" };
        foreach (var path in scriptPaths)
        {
            var name = EscapeBatchLiteral(Path.GetFileName(path));
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".cmd":
                case ".bat":
                    lines.Add($"\"%ComSpec%\" /d /s /c \"\"%~dp0{name}\"\"");
                    break;
                case ".ps1":
                    lines.Add($"\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0{name}\"");
                    break;
                case ".vbs":
                case ".js":
                case ".wsf":
                    lines.Add($"\"%SystemRoot%\\System32\\cscript.exe\" //B //NoLogo \"%~dp0{name}\"");
                    break;
            }
        }
        lines.Add($"start \"\" /b \"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0{ScriptCleanupName}\"");
        lines.Add("endlocal");
        lines.Add("exit /b 0");
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static string BuildScriptCleanup(IEnumerable<string> scriptPaths)
    {
        var names = scriptPaths.Select(Path.GetFileName)
            .Append(ScriptRunnerName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => $"'{name!.Replace("'", "''")}'");
        return string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'SilentlyContinue'",
            "Start-Sleep -Seconds 2",
            "$scriptRoot = Split-Path -LiteralPath $PSCommandPath -Parent",
            $"$removeNames = @({string.Join(", ", names)})",
            "foreach ($name in $removeNames) { Remove-Item -LiteralPath (Join-Path $scriptRoot $name) -Force }",
            "$cleanupPath = $PSCommandPath",
            "Remove-Item -LiteralPath $cleanupPath -Force",
            "if (-not (Get-ChildItem -LiteralPath $scriptRoot -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $scriptRoot -Force }") + Environment.NewLine;
    }

    private static string EscapeBatchLiteral(string value) => value.Replace("%", "%%");

    private async Task CopyPreparedWindowsMediaAsync(PartitionConfig partition, string destination, int startProgress, int endProgress)
    {
        BuildProgress.Value = startProgress;
        if (string.IsNullOrWhiteSpace(partition.PreparedMediaPath) || !Directory.Exists(partition.PreparedMediaPath))
            throw new InvalidOperationException("Prepared Windows media is unavailable. Start the build again to recreate it.");
        SetNonTransferActivity($"Copying {partition.IsoEditionName ?? "Windows"} media...");
        AddActivity($"Copying prepared {partition.IsoEditionName ?? "Windows"} media to {partition.Name}.");
        await CopySourceAsync(partition.PreparedMediaPath, destination, $"{partition.Name} Windows media", startProgress, endProgress);
        if (!File.Exists(Path.Combine(destination, "efi", "boot", "bootx64.efi")) ||
            !File.Exists(Path.Combine(destination, "sources", "boot.wim")) ||
            !File.Exists(Path.Combine(destination, "sources", "install.wim")) ||
            !File.Exists(Path.Combine(destination, "bootmgr")) ||
            !File.Exists(Path.Combine(destination, "boot", "bcd")) ||
            !File.Exists(Path.Combine(destination, "efi", "microsoft", "boot", "bcd")))
            throw new InvalidOperationException("Boot-file verification failed after copying the prepared Windows media.");
        AddActivity($"Complete {partition.IsoEditionName ?? "Windows"} boot set verified on {partition.Name}.");
    }

    private async Task<BootableIsoInfo> InspectBootableIsoAsync(string isoPath)
    {
        var driveLetter = await MountIsoAsync(isoPath);
        try { return await InspectMountedWindowsIsoAsync($"{driveLetter}:\\"); }
        finally { await DismountIsoAsync(isoPath); }
    }

    private async Task<BootableIsoInfo> InspectMountedWindowsIsoAsync(string root)
    {
        var bootFile = Path.Combine(root, "efi", "boot", "bootx64.efi");
        var bootWim = Path.Combine(root, "sources", "boot.wim");
        var bootManager = Path.Combine(root, "bootmgr");
        var biosBcd = Path.Combine(root, "boot", "bcd");
        var uefiBcd = Path.Combine(root, "efi", "microsoft", "boot", "bcd");
        var installWim = Path.Combine(root, "sources", "install.wim");
        var installEsd = Path.Combine(root, "sources", "install.esd");
        if (!File.Exists(bootFile) || !File.Exists(bootWim) || !File.Exists(bootManager) || !File.Exists(biosBcd) || !File.Exists(uefiBcd))
            throw new InvalidOperationException("This is not a complete supported 64-bit Windows installer ISO. One or more required Windows Setup boot files are missing.");
        if (!File.Exists(installWim) && !File.Exists(installEsd))
            throw new InvalidOperationException("Windows Setup image sources\\install.wim or sources\\install.esd was not found.");

        var installImage = File.Exists(installWim) ? installWim : installEsd;
        var editions = await GetWindowsImageEditionsAsync(installImage);
        if (editions.Count == 0) throw new InvalidOperationException("Windows Setup did not report any installable editions in the selected ISO.");
        return new BootableIsoInfo(CalculateDirectoryBytes(root), Path.GetFileName(installImage), editions);
    }

    private async Task<List<WindowsImageEdition>> GetWindowsImageEditionsAsync(string imagePath)
    {
        var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaptopQAUsbBuilder", "Logs");
        Directory.CreateDirectory(logFolder);
        var dismLog = Path.Combine(logFolder, "DISM-inspect.log");
        var script = $"@(Get-WindowsImage -ImagePath '{PsQuote(imagePath)}' -LogPath '{PsQuote(dismLog)}' -ErrorAction Stop|ForEach-Object{{[pscustomobject]@{{Index=$_.ImageIndex;Name=$_.ImageName;Description=$_.ImageDescription;Size=[long]$_.ImageSize}}}})|ConvertTo-Json -Compress";
        var json = await RunPowerShellAsync(script);
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => JsonSerializer.Deserialize<List<WindowsImageEdition>>(json, _jsonOptions) ?? [],
            JsonValueKind.Object => JsonSerializer.Deserialize<WindowsImageEdition>(json, _jsonOptions) is { } edition ? [edition] : [],
            _ => []
        };
    }

    private async Task<string> MountIsoAsync(string isoPath)
    {
        var escapedPath = PsQuote(isoPath);
        var mountScript = $"$path='{escapedPath}';$existing=Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue;if($existing -and $existing.Attached){{throw 'The selected ISO is already mounted. Dismount it in Windows before building.'}};try{{Mount-DiskImage -ImagePath $path -PassThru -ErrorAction Stop|Out-Null;$v=$null;for($i=0;$i -lt 20 -and -not $v;$i++){{Start-Sleep -Milliseconds 250;$v=Get-DiskImage -ImagePath $path|Get-Volume -ErrorAction SilentlyContinue|Where-Object DriveLetter|Select-Object -First 1}};if(-not $v){{throw 'Windows mounted the ISO but did not assign it a drive letter.'}};$v.DriveLetter}}catch{{Dismount-DiskImage -ImagePath $path -ErrorAction SilentlyContinue;throw}}";
        var driveLetter = (await RunPowerShellAsync(mountScript)).Trim().TrimEnd(':');
        if (driveLetter.Length != 1 || !char.IsLetter(driveLetter[0]))
        {
            await DismountIsoAsync(isoPath);
            throw new InvalidOperationException("Windows mounted the ISO but returned an invalid drive letter.");
        }
        return driveLetter;
    }

    private Task DismountIsoAsync(string isoPath) =>
        RunPowerShellAsync($"Dismount-DiskImage -ImagePath '{PsQuote(isoPath)}' -ErrorAction Stop");

    private async Task CopySourceAsync(string source, string destination, string name, int startProgress, int endProgress, string? excludedFile = null)
    {
        BuildProgress.Value = startProgress;
        AddActivity($"Copying {name} content from {source}.");
        Log($"Copying {source} to {destination}");
        CurrentEtaText.Text = $"Current activity: Scanning {name}...";
        var totalBytes = await Task.Run(() => CalculateDirectoryBytes(source, excludedFile));
        BeginTransferActivity(name, totalBytes, startProgress, endProgress);
        try
        {
            await Task.Run(() =>
            {
                var directories = new Stack<(string Source, string Target)>();
                directories.Push((source, destination));
                while (directories.Count > 0)
                {
                    var current = directories.Pop();
                    Directory.CreateDirectory(current.Target);
                    string[] files;
                    string[] childDirectories;
                    try
                    {
                        files = Directory.GetFiles(current.Source);
                        childDirectories = Directory.GetDirectories(current.Source);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Dispatcher.Invoke(() => AddActivity($"Skipped protected folder: {current.Source}"));
                        continue;
                    }
                    catch (IOException ex)
                    {
                        Dispatcher.Invoke(() => AddActivity($"Skipped unreadable folder: {current.Source} ({ex.Message})"));
                        continue;
                    }

                    foreach (var file in files)
                    {
                        if (excludedFile is not null && file.Equals(excludedFile, StringComparison.OrdinalIgnoreCase)) continue;
                        CopyFileWithProgress(file, Path.Combine(current.Target, Path.GetFileName(file)));
                    }
                    foreach (var folder in childDirectories)
                    {
                        var folderName = Path.GetFileName(folder);
                        if (folderName.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                            folderName.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase))
                        {
                            Dispatcher.Invoke(() => AddActivity($"Skipped Windows metadata folder: {folderName}"));
                            continue;
                        }

                        try
                        {
                            var attributes = File.GetAttributes(folder);
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                Dispatcher.Invoke(() => AddActivity($"Skipped linked folder: {folder}"));
                                continue;
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            Dispatcher.Invoke(() => AddActivity($"Skipped protected folder: {folder}"));
                            continue;
                        }
                        directories.Push((folder, Path.Combine(current.Target, Path.GetFileName(folder))));
                    }
                }
            });
            BuildProgress.Value = endProgress;
        }
        finally
        {
            EndTransferActivity();
        }
        AddActivity($"{name} content copied successfully.");
    }

    private async Task CopyFileWithProgressAsync(string source, string destination, string name, double startProgress, double endProgress)
    {
        var totalBytes = GetFileLength(source);
        BeginTransferActivity(name, totalBytes, startProgress, endProgress);
        try
        {
            await Task.Run(() => CopyFileWithProgress(source, destination));
            BuildProgress.Value = endProgress;
        }
        finally
        {
            EndTransferActivity();
        }
    }

    private void CopyFileWithProgress(string source, string destination)
    {
        const int bufferSize = 256 * 1024;
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                int bytesRead;
                while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, bytesRead);
                    ReportTransferBytes(bytesRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        try { File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static long CalculateDirectoryBytes(string source, string? excludedFile = null)
        => CalculateDirectoryStats(source, excludedFile).TotalBytes;

    private static (long TotalBytes, long LargestFileBytes) CalculateDirectoryStats(string source, string? excludedFile = null)
    {
        long total = 0;
        long largest = 0;
        var directories = new Stack<string>();
        directories.Push(source);
        while (directories.Count > 0)
        {
            var current = directories.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    if (excludedFile is not null && file.Equals(excludedFile, StringComparison.OrdinalIgnoreCase)) continue;
                    var length = GetFileLength(file);
                    total = SaturatingAdd(total, length);
                    largest = Math.Max(largest, length);
                }
                foreach (var folder in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(folder);
                    if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) directories.Push(folder);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (total, largest);
    }

    private static (long TotalBytes, long LargestFileBytes) CalculateSelectedContentStats(PartitionConfig partition)
    {
        long total = 0;
        long largest = 0;
        foreach (var folder in partition.SourceFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var stats = CalculateDirectoryStats(folder);
            total = SaturatingAdd(total, stats.TotalBytes);
            largest = Math.Max(largest, stats.LargestFileBytes);
        }

        var files = partition.SourceFiles
            .Concat(partition.ScriptFiles)
            .Concat(string.IsNullOrWhiteSpace(partition.AutounattendSource) ? [] : [partition.AutounattendSource])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var length = GetFileLength(file);
            total = SaturatingAdd(total, length);
            largest = Math.Max(largest, length);
        }
        return (total, largest);
    }

    private async Task RefreshPartitionContentSizeAsync(PartitionConfig partition, bool showWarning)
    {
        SetStatus("Checking content", "#B36A13");
        var stats = await Task.Run(() => CalculateSelectedContentStats(partition));
        partition.SelectedContentBytes = stats.TotalBytes;
        partition.LargestSelectedFileBytes = stats.LargestFileBytes;
        UpdatePartitionPreview(SelectedDisks());
        MainPartitionList.Items.Refresh();
        SetStatus(Localization.Text(_preferences.Language, "Ready"), "#147A4B");

        if (!showWarning) return;
        var disks = SelectedDisks();
        var warnings = disks.Count > 0
            ? GetContentCapacityWarnings(disks, partition)
            : GetFixedPartitionCapacityWarnings(partition);
        if (warnings.Count > 0)
            MessageBox.Show($"The selected files and folders will not fit:\n\n{string.Join("\n", warnings)}\n\nIncrease the partition size or remove content.",
                "Partition content will not fit", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task RefreshAllPartitionContentSizesAsync()
    {
        var stats = await Task.WhenAll(_partitions.Select(partition =>
            Task.Run(() => CalculateSelectedContentStats(partition))));
        for (var index = 0; index < _partitions.Count; index++)
        {
            _partitions[index].SelectedContentBytes = stats[index].TotalBytes;
            _partitions[index].LargestSelectedFileBytes = stats[index].LargestFileBytes;
        }
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long EstimateRequiredPartitionCapacity(PartitionConfig partition)
    {
        var isoBytes = partition.ExtractedIsoBytes ??
            (string.IsNullOrWhiteSpace(partition.IsoSource) ? 0 : GetFileLength(partition.IsoSource));
        var contentBytes = SaturatingAdd(partition.SelectedContentBytes, isoBytes);
        if (contentBytes == 0) return 0;
        var reserve = partition.HasIso
            ? 256L * 1024 * 1024
            : Math.Clamp(contentBytes / 100, 1L * 1024 * 1024, 256L * 1024 * 1024);
        return SaturatingAdd(contentBytes, reserve);
    }

    private long GetPartitionCapacity(PartitionConfig partition, UsbDisk disk)
    {
        if (!partition.IsRemaining)
            return PartitionConfig.TryParseSize(partition.SizeText, out var size) ? size : 0;
        var fixedSize = _partitions.Where(item => !item.IsRemaining)
            .Sum(item => PartitionConfig.TryParseSize(item.SizeText, out var bytes) ? bytes : 0);
        return Math.Max(0, disk.Size - fixedSize);
    }

    private List<string> GetContentCapacityWarnings(IReadOnlyList<UsbDisk> disks, PartitionConfig? onlyPartition = null)
    {
        var warnings = new List<string>();
        foreach (var partition in _partitions.Where(item => onlyPartition is null || ReferenceEquals(item, onlyPartition)))
            if (HasFat32FileSizeViolation(partition))
                warnings.Add($"{partition.Name}: contains a file larger than FAT32's 4 GB single-file limit");
        foreach (var disk in disks)
        foreach (var partition in _partitions.Where(item => onlyPartition is null || ReferenceEquals(item, onlyPartition)))
        {
            var required = EstimateRequiredPartitionCapacity(partition);
            if (required == 0) continue;
            var capacity = GetPartitionCapacity(partition, disk);
            if (required > capacity)
                warnings.Add($"Disk {disk.Number} · {partition.Name}: approximately {FormatBytes(required)} required, {FormatBytes(capacity)} available");
        }
        return warnings;
    }

    private static List<string> GetFixedPartitionCapacityWarnings(PartitionConfig partition)
    {
        if (HasFat32FileSizeViolation(partition))
            return [$"{partition.Name}: contains a file larger than FAT32's 4 GB single-file limit"];
        if (partition.IsRemaining || !PartitionConfig.TryParseSize(partition.SizeText, out var capacity)) return [];
        var required = EstimateRequiredPartitionCapacity(partition);
        return required > capacity
            ? [$"{partition.Name}: approximately {FormatBytes(required)} required, {FormatBytes(capacity)} available"]
            : [];
    }

    private static bool HasFat32FileSizeViolation(PartitionConfig partition) =>
        partition.FileSystem == "FAT32" && partition.LargestSelectedFileBytes > uint.MaxValue;

    private static long GetFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private void InitializeEtaTracking(long bytesPerDrive, int driveCount)
    {
        var now = DateTime.UtcNow;
        lock (_etaSync)
        {
            _queueBytesPerDrive = Math.Max(0, bytesPerDrive);
            _queueBytesTotal = _queueBytesPerDrive * driveCount;
            _queueBytesCopied = 0;
            _queueSampleBytes = 0;
            _queueStartedUtc = now;
            _queueSampleUtc = now;
            _queueBytesPerSecond = 0;
            _lastEtaUiUpdateUtc = DateTime.MinValue;
        }
        CurrentEtaText.Text = "Current activity: Preparing drive...";
    }

    private void StartQueueDiskEstimate()
    {
        lock (_etaSync) _queueDiskStartBytes = _queueBytesCopied;
    }

    private void CompleteQueueDiskEstimate()
    {
        lock (_etaSync)
        {
            var expectedEnd = Math.Min(_queueBytesTotal, _queueDiskStartBytes + _queueBytesPerDrive);
            if (_queueBytesCopied < expectedEnd) _queueBytesCopied = expectedEnd;
            _queueSampleBytes = _queueBytesCopied;
            _queueSampleUtc = DateTime.UtcNow;
        }
        UpdateEtaDisplay();
    }

    private void BeginTransferActivity(string name, long totalBytes, double startProgress, double endProgress)
    {
        var now = DateTime.UtcNow;
        lock (_etaSync)
        {
            _activityBytesTotal = Math.Max(0, totalBytes);
            _activityBytesCopied = 0;
            _activitySampleBytes = 0;
            _activityStartedUtc = now;
            _activitySampleUtc = now;
            _activityBytesPerSecond = 0;
            _activityProgressStart = startProgress;
            _activityProgressEnd = endProgress;
            _activityName = name;
        }
        CurrentEtaText.Text = totalBytes > 0 ? $"Current activity: {name} (0%)" : $"Current activity: {name}";
    }

    private void EndTransferActivity()
    {
        lock (_etaSync)
        {
            _activityBytesTotal = 0;
            _activityBytesCopied = 0;
            _activityBytesPerSecond = 0;
        }
    }

    private void SetNonTransferActivity(string text)
    {
        EndTransferActivity();
        CurrentEtaText.Text = $"Current activity: {text}";
    }

    private void ReportTransferBytes(long bytes)
    {
        if (bytes <= 0) return;
        var now = DateTime.UtcNow;
        var refreshUi = false;
        lock (_etaSync)
        {
            _activityBytesCopied += bytes;
            _queueBytesCopied += bytes;

            var activitySeconds = (now - _activitySampleUtc).TotalSeconds;
            if (activitySeconds >= 0.5)
            {
                var rate = (_activityBytesCopied - _activitySampleBytes) / activitySeconds;
                _activityBytesPerSecond = SmoothRate(_activityBytesPerSecond, rate);
                _activitySampleBytes = _activityBytesCopied;
                _activitySampleUtc = now;
            }

            var queueSeconds = (now - _queueSampleUtc).TotalSeconds;
            if (queueSeconds >= 0.5)
            {
                var rate = (_queueBytesCopied - _queueSampleBytes) / queueSeconds;
                _queueBytesPerSecond = SmoothRate(_queueBytesPerSecond, rate);
                _queueSampleBytes = _queueBytesCopied;
                _queueSampleUtc = now;
            }

            if ((now - _lastEtaUiUpdateUtc).TotalSeconds >= 0.5)
            {
                _lastEtaUiUpdateUtc = now;
                refreshUi = true;
            }
        }
        if (refreshUi) Dispatcher.BeginInvoke(new Action(UpdateEtaDisplay));
    }

    private void UpdateEtaDisplay()
    {
        long activityTotal;
        long activityCopied;
        long queueTotal;
        long queueCopied;
        double activityRate;
        double queueRate;
        double progressStart;
        double progressEnd;
        DateTime activityStarted;
        DateTime queueStarted;
        string activityName;
        lock (_etaSync)
        {
            activityTotal = _activityBytesTotal;
            activityCopied = _activityBytesCopied;
            queueTotal = _queueBytesTotal;
            queueCopied = _queueBytesCopied;
            activityRate = _activityBytesPerSecond;
            queueRate = _queueBytesPerSecond;
            progressStart = _activityProgressStart;
            progressEnd = _activityProgressEnd;
            activityStarted = _activityStartedUtc;
            queueStarted = _queueStartedUtc;
            activityName = _activityName;
        }

        if (activityTotal > 0)
        {
            var fraction = Math.Clamp((double)activityCopied / activityTotal, 0, 1);
            BuildProgress.Value = progressStart + (progressEnd - progressStart) * fraction;
            CurrentEtaText.Text = $"Current activity: {activityName} ({fraction:P0})";
        }
    }

    private void CompleteEtaTracking()
    {
        EndTransferActivity();
        CurrentEtaText.Text = "Current activity: Finished";
    }

    private static double SmoothRate(double current, double sample) => current <= 0 ? sample : current * 0.75 + sample * 0.25;

    private async Task CopyUnattendAsync(string source, string destination)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            AddActivity("No Autounattend.xml selected.");
            return;
        }

        AddActivity("Copying Autounattend.xml to the Win11 Boot partition root.");
        Log($"Copying {source} to {Path.Combine(destination, "Autounattend.xml")}");
        await Task.Run(() => File.Copy(source, Path.Combine(destination, "Autounattend.xml"), true));
        AddActivity("Autounattend.xml copied successfully.");
    }

    private async Task<bool> SourceIsOnDiskAsync(string source, int diskNumber)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(source));
        if (string.IsNullOrWhiteSpace(root) || root.StartsWith("\\\\")) return false;
        var letter = root[0];
        var result = await RunPowerShellAsync($"$p=Get-Partition -DriveLetter '{letter}' -ErrorAction SilentlyContinue;if($p){{$p.DiskNumber}}else{{-1}}");
        return int.TryParse(result.Trim(), out var sourceDisk) && sourceDisk == diskNumber;
    }

    private async Task<string> RunPowerShellAsync(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Windows storage service.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(_buildCancellation?.Token ?? CancellationToken.None); }
        catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Windows storage operation failed." : CleanPowerShellError(error));
        return output;
    }

    private static string CleanPowerShellError(string error)
    {
        var trimmed = error.Trim();
        if (trimmed.StartsWith("#< CLIXML", StringComparison.OrdinalIgnoreCase))
        {
            var xmlStart = trimmed.IndexOf('<', "#< CLIXML".Length);
            if (xmlStart >= 0)
            {
                try
                {
                    var document = System.Xml.Linq.XDocument.Parse(trimmed[xmlStart..]);
                    var serializedErrors = document.Descendants()
                        .Where(element => element.Name.LocalName == "S" &&
                                          string.Equals(element.Attribute("S")?.Value, "Error", StringComparison.OrdinalIgnoreCase))
                        .Select(element => DecodeCliXmlText(element.Value));
                    foreach (var serializedError in serializedErrors)
                    {
                        var firstLine = FirstMeaningfulErrorLine(serializedError);
                        if (firstLine is not null) return firstLine;
                    }
                }
                catch (System.Xml.XmlException) { }
            }
        }

        var withoutMarkup = Regex.Replace(trimmed.Replace("#< CLIXML", "", StringComparison.OrdinalIgnoreCase), "<[^>]+>", " ");
        return FirstMeaningfulErrorLine(DecodeCliXmlText(withoutMarkup)) ?? "Windows storage operation failed.";
    }

    private static string DecodeCliXmlText(string value) =>
        WebUtility.HtmlDecode(Regex.Replace(value, @"_x([0-9A-Fa-f]{4})_",
            match => char.ConvertFromUtf32(Convert.ToInt32(match.Groups[1].Value, 16))));

    private static string? FirstMeaningfulErrorLine(string value) =>
        value.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

    private void SetBuildingState(bool building)
    {
        _isBuilding = building;
        ConfigButton.IsEnabled = !building;
        DiskPicker.IsEnabled = !building; RefreshButton.IsEnabled = !building;
        MainPartitionList.IsEnabled = !building; AddPartitionButton.IsEnabled = !building; MainDefaultsButton.IsEnabled = !building;
        ConfirmText.IsEnabled = !building;
        CancelBuildButton.Visibility = building ? Visibility.Visible : Visibility.Collapsed;
        CancelBuildButton.IsEnabled = building;
        UpdateBuildButton();
    }

    private void CancelBuild_Click(object sender, RoutedEventArgs e)
    {
        if (_buildCancellation is null) return;
        CancelBuildButton.IsEnabled = false;
        SetNonTransferActivity("Cancelling build...");
        AddActivity("Build cancellation requested.");
        _buildCancellation.Cancel();
    }

    private void SetPreflightState(bool preflighting)
    {
        if (_isPreflighting == preflighting) return;
        _isPreflighting = preflighting;
        var interactive = !preflighting && !_isBuilding;
        ConfigButton.IsEnabled = interactive;
        DiskPicker.IsEnabled = interactive;
        RefreshButton.IsEnabled = interactive;
        MainPartitionList.IsEnabled = interactive;
        AddPartitionButton.IsEnabled = interactive;
        MainDefaultsButton.IsEnabled = interactive;
        ConfirmText.IsEnabled = interactive;
        Cursor = preflighting ? Cursors.Wait : null;
        if (preflighting)
        {
            SetStatus("Preparing build", "#B36A13");
            BuildProgress.IsIndeterminate = true;
            CurrentEtaText.Text = "Current activity: Checking targets, sources, and ISO media...";
            QueueEtaText.Visibility = Visibility.Collapsed;
            AddActivity("Preparing build: checking targets, sources, and ISO media before erasure.");
        }
        else if (!_isBuilding)
        {
            BuildProgress.IsIndeterminate = false;
            BuildProgress.Value = 0;
            CurrentEtaText.Text = "Current activity: Waiting";
            SetStatus(Localization.Text(_preferences.Language, "Ready"), "#147A4B");
        }
        UpdateBuildButton();
    }

    private void UpdateBuildButton()
    {
        if (!IsInitialized || BuildButton is null) return;
        BuildButton.IsEnabled = !_isBuilding && !_isPreflighting && DiskPicker.SelectedItems.Count > 0 && ConfirmText.Text == "ERASE";
    }

    private void AddActivity(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        ActivityList.Items.Add(line);
        ActivityList.ScrollIntoView(line);
        Log(message);
    }

    private void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(_logPath)) return;
        File.AppendAllText(_logPath, $"{DateTime.Now:s}  {message}{Environment.NewLine}");
    }

    private void SetStatus(string text, string color)
    {
        HeaderStatus.Text = text;
        HeaderStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void ApplyLanguage()
    {
        string T(string key) => Localization.Text(_preferences.Language, key);
        SubtitleText.Text = T("Subtitle"); SelectDriveTitle.Text = $"1. {T("Select USB Drive")}";
        RefreshButton.Content = T("Refresh");
        PartitionEditorTitle.Text = T("Partition Settings"); MainDefaultsButton.Content = "Defaults"; PartitionLayoutTitle.Text = T("Partition Layout"); PartitionLayoutNote.Text = T("GPT Note").Replace("GPT", "MBR");
        WarningText.Text = T("Warning"); ActivityTitle.Text = T("Activity"); ConfirmLabel.Text = T("Confirm ERASE"); BuildButton.Content = T("Build USB Queue");
        if (!_isBuilding) HeaderStatus.Text = T("Ready");
    }

    private static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LaptopQAUsbBuilder", "default-partition-settings.json");
    private static string PreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LaptopQAUsbBuilder", "preferences.json");

    private AppPreferences LoadPreferences()
    {
        try
        {
            if (!File.Exists(PreferencesPath)) return new AppPreferences();
            var result = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(PreferencesPath), _jsonOptions) ?? new AppPreferences();
            result.Language = Localization.Resolve(result.Language).Code; result.Theme = ThemeService.Normalize(result.Theme);
            return result;
        }
        catch { return new AppPreferences(); }
    }

    private void SavePreferences()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
        File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(_preferences, new JsonSerializerOptions { WriteIndented = true }));
    }

    private List<PartitionConfig> LoadPartitionConfig()
    {
        try
        {
            if (!File.Exists(DefaultSettingsPath)) return PartitionConfig.CreateDefaults();
            var loaded = JsonSerializer.Deserialize<List<PartitionConfig>>(File.ReadAllText(DefaultSettingsPath), _jsonOptions);
            if (loaded is not null)
                foreach (var partition in loaded)
                    if (partition.SizeText.Trim().Equals("Remaining", StringComparison.OrdinalIgnoreCase)) partition.SizeText = "*";
            if (loaded is null || loaded.Count is < 1 or > 4 || loaded.Count(p => p.IsRemaining) != 1) return PartitionConfig.CreateDefaults();
            if (loaded.Any(p => !PartitionConfig.AllowedFormats.Contains(p.FileSystem))) return PartitionConfig.CreateDefaults();
            for (var i = 0; i < loaded.Count; i++)
                if (!loaded[i].IsRemaining && (!PartitionConfig.TryParseSize(loaded[i].SizeText, out var bytes) || bytes < 32L * 1024 * 1024))
                    return PartitionConfig.CreateDefaults();
            for (var i = 0; i < loaded.Count; i++) loaded[i].Number = i + 1;
            return loaded;
        }
        catch
        {
            return PartitionConfig.CreateDefaults();
        }
    }

    private void SaveDefaultPartitionConfig()
    {
        var folder = Path.GetDirectoryName(DefaultSettingsPath)!;
        Directory.CreateDirectory(folder);
        File.WriteAllText(DefaultSettingsPath, JsonSerializer.Serialize(_defaultPartitions, new JsonSerializerOptions { WriteIndented = true }));
    }

    private bool ValidatePartitionLayout(out string message)
    {
        message = "";
        if (_partitions.Count is < 1 or > 4) { message = "Choose between 1 and 4 partitions for an MBR USB."; return false; }
        if (_partitions.Count(p => p.IsRemaining) != 1) { message = "Exactly one partition must use * for remaining space."; return false; }
        if (_partitions.Select(p => p.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _partitions.Count)
        { message = "Every volume label must be unique."; return false; }
        foreach (var item in _partitions)
        {
            item.Name = item.Name.Trim();
            if (string.IsNullOrWhiteSpace(item.Name)) { message = $"Partition {item.Number} needs a volume label."; return false; }
            if (item.Name.IndexOfAny(['\\', '/', '?', '*', ':', '|', '"', '<', '>']) >= 0 || item.Name.Any(char.IsControl))
            { message = $"Partition {item.Number} contains a character that is not valid in a volume label."; return false; }
            if (!PartitionConfig.AllowedFormats.Contains(item.FileSystem)) { message = $"Partition {item.Number} has an unsupported format."; return false; }
            var maxLength = item.FileSystem == "FAT32" ? 11 : item.FileSystem == "exFAT" ? 15 : 32;
            if (item.Name.Length > maxLength) { message = $"{item.FileSystem} label '{item.Name}' exceeds {maxLength} characters."; return false; }
            if (item.HasIso && (item.FileSystem != "NTFS" || item.IsRemaining)) { message = $"Partition {item.Number} must be a fixed-size NTFS partition for bootable Windows media."; return false; }
            if ((item.HasDrivers || item.HasScripts) && !item.HasIso) { message = $"Partition {item.Number} has Drivers or Scripts selected. Add a Windows ISO to that partition, or clear those selections."; return false; }
            if (item.IsRemaining) continue;
            if (!PartitionConfig.TryParseSize(item.SizeText, out var bytes)) { message = $"Partition {item.Number} needs a size such as 50 MB or 20 GB, or * for remaining space."; return false; }
            if (bytes < 32L * 1024 * 1024) { message = $"Partition {item.Number} must be at least 32 MB."; return false; }
            if (item.FileSystem == "FAT32" && bytes > 32L * 1024 * 1024 * 1024) { message = $"Partition {item.Number} exceeds Windows' 32 GB FAT32 formatting limit."; return false; }
            if (item.HasIso && bytes < 5L * 1024 * 1024 * 1024) { message = $"Bootable ISO partition {item.Number} must be at least 5 GB."; return false; }
        }
        return true;
    }

    private void PartitionConfigurationChanged(bool refreshList = true)
    {
        for (var index = 0; index < _partitions.Count; index++) _partitions[index].Number = index + 1;
        if (refreshList) MainPartitionList.Items.Refresh();
        UpdatePartitionPreview(SelectedDisks());
        UpdateBuildButton();
        ConfirmText.Clear();
    }

    private void QueuePartitionConfigurationChanged()
    {
        if (_updatingPartitionGrid || _isBuilding) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_updatingPartitionGrid) return;
            _updatingPartitionGrid = true;
            try { PartitionConfigurationChanged(false); }
            finally { _updatingPartitionGrid = false; }
        }));
    }

    private void PartitionField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) QueuePartitionConfigurationChanged();
    }

    private void PartitionFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PartitionConfig partition)
        {
            if (partition.FileSystem != "NTFS") partition.ClearIsoSelection();
            if (partition.FileSystem != "NTFS" && !partition.HasIso) partition.AutounattendSource = null;
        }
        if (IsLoaded) QueuePartitionConfigurationChanged();
    }

    private void PartitionDragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isBuilding || (sender as FrameworkElement)?.DataContext is not PartitionConfig partition) return;
        _draggedPartition = partition;
        _mainDropDestinationIndex = _partitions.IndexOf(partition);
        _partitionDragStart = e.GetPosition(this);
        var row = FindVisualAncestor<ListBoxItem>(sender as DependencyObject);
        _partitionDragStartDistance = Math.Max(12, row is null
            ? 12
            : ((FrameworkElement?)FindVisualDescendant<Border>(row, "PartitionRowCard") ?? row).ActualHeight);
        e.Handled = true;
    }

    private void MainPartitionList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggedPartition = null;
        _mainDropDestinationIndex = -1;
        ClearMainDropIndicator();
    }

    private void MainPartitionList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedPartition is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggedPartition = null;
            return;
        }
        var position = e.GetPosition(this);
        var deltaX = position.X - _partitionDragStart.X;
        var deltaY = position.Y - _partitionDragStart.Y;
        if (Math.Sqrt(deltaX * deltaX + deltaY * deltaY) < _partitionDragStartDistance) return;
        var data = new DataObject(MainPartitionDragFormat, _draggedPartition);
        DragDrop.DoDragDrop(MainPartitionList, data, DragDropEffects.Move);
        ClearMainDropIndicator();
        _draggedPartition = null;
        _mainDropDestinationIndex = -1;
        e.Handled = true;
    }

    private void MainPartitionList_DragOver(object sender, DragEventArgs e)
    {
        ListBoxItem? row = null;
        var showAfter = false;
        var dragged = e.Data.GetData(MainPartitionDragFormat) as PartitionConfig;
        var valid = !_isBuilding && dragged is not null;
        if (valid) valid = TryGetMainDropTarget(e, dragged!, out row, out showAfter, out _);
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (valid) ShowMainDropIndicator(row!, showAfter); else ClearMainDropIndicator();
        e.Handled = true;
    }

    private void MainPartitionList_DragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(MainPartitionList);
        if (point.X < 0 || point.Y < 0 || point.X > MainPartitionList.ActualWidth || point.Y > MainPartitionList.ActualHeight)
            ClearMainDropIndicator();
    }

    private void MainPartitionList_Drop(object sender, DragEventArgs e)
    {
        ClearMainDropIndicator();
        if (_isBuilding || e.Data.GetData(MainPartitionDragFormat) is not PartitionConfig dragged) return;
        var oldIndex = _partitions.IndexOf(dragged);
        if (oldIndex < 0 || !TryGetMainDropTarget(e, dragged, out _, out _, out var destinationIndex)) return;
        if (destinationIndex == oldIndex)
        {
            MainPartitionList.SelectedItem = dragged;
            _draggedPartition = null;
            e.Handled = true;
            return;
        }
        _partitions.RemoveAt(oldIndex);
        _partitions.Insert(Math.Clamp(destinationIndex, 0, _partitions.Count), dragged);
        PartitionConfigurationChanged();
        MainPartitionList.SelectedItem = dragged;
        _draggedPartition = null;
        e.Handled = true;
    }

    private bool TryGetMainDropTarget(DragEventArgs e, PartitionConfig dragged, out ListBoxItem? row, out bool showAfter, out int destinationIndex)
    {
        destinationIndex = GetMainDestinationIndex(e.GetPosition(MainPartitionList).Y);
        showAfter = false;
        row = MainPartitionList.ItemContainerGenerator.ContainerFromIndex(destinationIndex) as ListBoxItem;
        return row is not null;
    }

    private int GetMainDestinationIndex(double pointerY)
    {
        var destination = Math.Max(0, MainPartitionList.Items.Count - 1);
        ListBoxItem? destinationRow = null;
        for (var index = 0; index < MainPartitionList.Items.Count; index++)
        {
            if (MainPartitionList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row) continue;
            var top = row.TranslatePoint(new Point(0, 0), MainPartitionList).Y;
            if (pointerY >= top + row.ActualHeight) continue;
            destination = index;
            destinationRow = row;
            break;
        }
        if (_mainDropDestinationIndex >= 0 && destination != _mainDropDestinationIndex && destinationRow is not null)
        {
            var top = destinationRow.TranslatePoint(new Point(0, 0), MainPartitionList).Y;
            var depth = pointerY - top;
            if (depth < destinationRow.ActualHeight * 0.25 || depth > destinationRow.ActualHeight * 0.75)
                return _mainDropDestinationIndex;
        }
        _mainDropDestinationIndex = destination;
        return destination;
    }

    private void ShowMainDropIndicator(ListBoxItem row, bool showAfter)
    {
        UIElement targetBox = (UIElement?)FindVisualDescendant<Border>(row, "PartitionRowCard") ?? row;
        if (_mainDropIndicator?.AdornedElement == targetBox && _mainDropIndicator.IsAfter == showAfter) return;
        _mainDropIndicator?.Detach();
        _mainDropIndicator = DropIndicatorAdorner.Attach(targetBox, showAfter);
    }

    private void ClearMainDropIndicator()
    {
        _mainDropIndicator?.Detach();
        _mainDropIndicator = null;
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

    private static T? FindVisualDescendant<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && match.Name == name) return match;
            var nested = FindVisualDescendant<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void ApplyPartitionConfig()
    {
        var selected = SelectedDisks();
        MainPartitionList.ItemsSource = _partitions;
        MainPartitionList.Items.Refresh();
        UpdatePartitionPreview(selected);
        UpdateBuildButton();
    }

    private void UpdatePartitionPreview(IReadOnlyList<UsbDisk> disks)
    {
        PartitionPreview.Children.Clear();
        PartitionPreview.ColumnDefinitions.Clear();
        PartitionPreview.RowDefinitions.Clear();
        if (disks.Count == 0)
        {
            NoDriveSelectedText.Visibility = Visibility.Visible;
            foreach (var partition in _partitions) partition.CalculatedSizeText = null;
            PartitionLayoutNote.Text = "Each selected disk will use an MBR partition table and UEFI-only Windows boot media.";
            return;
        }
        NoDriveSelectedText.Visibility = Visibility.Collapsed;

        var fixedSize = _partitions.Where(p => !p.IsRemaining)
            .Sum(p => PartitionConfig.TryParseSize(p.SizeText, out var bytes) ? bytes : 0);
        var rowHeight = Math.Max(1, PartitionPreview.ActualHeight) / disks.Count;
        var showDiskLabel = disks.Count > 1;
        var compact = rowHeight < 48;

        PartitionPreview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(showDiskLabel ? 54 : 0) });
        PartitionPreview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var hasCapacityWarning = false;
        for (var diskIndex = 0; diskIndex < disks.Count; diskIndex++)
        {
            var disk = disks[diskIndex];
            PartitionPreview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            if (showDiskLabel)
            {
                var diskLabel = new TextBlock
                {
                    Text = $"Disk {disk.Number}", FontWeight = FontWeights.SemiBold,
                    FontSize = rowHeight < 18 ? 9 : 11, VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = $"Disk {disk.Number} - {disk.FriendlyName} - {FormatBytes(disk.Size)}"
                };
                Grid.SetRow(diskLabel, diskIndex);
                Grid.SetColumn(diskLabel, 0);
                PartitionPreview.Children.Add(diskLabel);
            }

            var partitionSizes = new List<long>(_partitions.Count);
            for (var i = 0; i < _partitions.Count; i++)
            {
                long size;
                if (_partitions[i].IsRemaining)
                {
                    size = Math.Max(1, disk.Size - fixedSize);
                    _partitions[i].CalculatedSizeText = FormatBytes(size);
                }
                else PartitionConfig.TryParseSize(_partitions[i].SizeText, out size);
                partitionSizes.Add(Math.Max(1, size));
            }
            var totalSize = Math.Max(1d, partitionSizes.Sum(size => (double)size));
            var strip = new Grid();
            Grid.SetRow(strip, diskIndex);
            Grid.SetColumn(strip, 1);
            PartitionPreview.Children.Add(strip);
            for (var i = 0; i < _partitions.Count; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(partitionSizes[i] / totalSize, GridUnitType.Star),
                    MinWidth = compact ? 34 : 90
                });
                var detailText = _partitions[i].IsRemaining ? $"{FormatBytes(partitionSizes[i])} | {_partitions[i].FileSystem}" : _partitions[i].PreviewText;
                var requiredCapacity = EstimateRequiredPartitionCapacity(_partitions[i]);
                var contentWillNotFit = requiredCapacity > partitionSizes[i] || HasFat32FileSizeViolation(_partitions[i]);
                hasCapacityWarning |= contentWillNotFit;
                var label = new TextBlock
                {
                    Text = compact ? $"{(contentWillNotFit ? "⚠ " : "")}{_partitions[i].Name}" : $"{(contentWillNotFit ? "⚠ " : "")}{_partitions[i].Name}\n{detailText}",
                    FontWeight = FontWeights.Bold, FontSize = compact ? (rowHeight < 18 ? 8 : 10) : 12,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "PartitionText");
                var tooltipContent = new StackPanel();
                tooltipContent.Children.Add(new TextBlock { Text = $"Disk {disk.Number} · {_partitions[i].Name}", FontWeight = FontWeights.Bold, FontSize = 13 });
                tooltipContent.Children.Add(new TextBlock { Text = $"Size: {FormatBytes(partitionSizes[i])}", Margin = new Thickness(0, 4, 0, 0) });
                tooltipContent.Children.Add(new TextBlock { Text = $"Format: {_partitions[i].FileSystem}", Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#526970")) });
                if (requiredCapacity > 0)
                    tooltipContent.Children.Add(new TextBlock { Text = $"Selected content: approximately {FormatBytes(requiredCapacity)} required", Margin = new Thickness(0, 3, 0, 0) });
                if (contentWillNotFit)
                    tooltipContent.Children.Add(new TextBlock { Text = "WARNING: Selected content will not fit.", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 0) });
                var segment = new Border
                {
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(compact ? 5 : 10),
                    Padding = compact ? new Thickness(5, 0, 5, 0) : new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(2, 1, 2, 1), Child = label,
                    ToolTip = new ToolTip { Content = tooltipContent }
                };
                segment.SetResourceReference(Border.BackgroundProperty, contentWillNotFit ? "WarningBackground" : $"PartitionBackground{i % 6}");
                segment.SetResourceReference(Border.BorderBrushProperty, contentWillNotFit ? "WarningBorder" : $"PartitionBorder{i % 6}");
                if (contentWillNotFit) label.SetResourceReference(TextBlock.ForegroundProperty, "WarningText");
                ToolTipService.SetInitialShowDelay(segment, 180);
                ToolTipService.SetShowDuration(segment, 12000);
                Grid.SetColumn(segment, i);
                strip.Children.Add(segment);
            }
        }
        PartitionLayoutNote.Text = hasCapacityWarning
            ? "Warning: selected content will not fit in one or more highlighted partitions."
            : "Each selected disk will use an MBR partition table and UEFI-only Windows boot media.";
    }

    private List<UsbDisk> DeserializeUsbDisks(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Deserialize<List<UsbDisk>>(json, _jsonOptions) ?? [];
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var disk = JsonSerializer.Deserialize<UsbDisk>(json, _jsonOptions);
            return disk is null ? [] : [disk];
        }
        return [];
    }

    private static string PsQuote(string value) => value.Replace("'", "''");
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):N2} GB"
        : $"{bytes / (1024d * 1024):N0} MB";

    private void DiskPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedDisks();
        if (selected.Count > 0)
        {
            UpdatePartitionPreview(selected);
            ConfirmText.Clear();
        }
        else
        {
            UpdatePartitionPreview([]);
        }
        UpdateBuildButton();
    }

    private List<UsbDisk> SelectedDisks() => DiskPicker.SelectedItems.Cast<UsbDisk>().OrderBy(d => d.Number).ToList();

    private void AddFolder(ObservableCollection<string> collection)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder whose contents will be copied", Multiselect = false, InitialDirectory = PickerLocationStore.Get("Folder") };
        if (dialog.ShowDialog() != true) return;
        PickerLocationStore.Set("Folder", dialog.FolderName);
        if (collection.Any(path => path.Equals(dialog.FolderName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("That folder is already in this list.", "Folder already added", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        collection.Add(dialog.FolderName);
        UpdateBuildButton();
    }

    private void RemoveFolder(ObservableCollection<string> collection, ListBox list)
    {
        if (list.SelectedItem is string selected) collection.Remove(selected);
        UpdateBuildButton();
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || e.ClickCount != 1) return;

        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current != this)
        {
            if (current is Button or TextBox or ComboBox or ListBox or ScrollBar)
                return;
            current = VisualTreeHelper.GetParent(current);
        }

        try { DragMove(); }
        catch (InvalidOperationException) { }
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) { if (!_isBuilding) Close(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDisksAsync();
    private void ConfirmText_TextChanged(object sender, TextChangedEventArgs e) => UpdateBuildButton();
    private void Source_TextChanged(object sender, TextChangedEventArgs e) => UpdateBuildButton();
    private void Config_Click(object sender, RoutedEventArgs e)
    {
        var originalLanguage = _preferences.Language;
        var dialog = new ConfigWindow(_defaultPartitions, _preferences.Language, _preferences.Theme, _preferences.ForceUnsignedDrivers, _preferences.WindowsSetup) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            Localization.ApplyCulture(originalLanguage);
            return;
        }
        _defaultPartitions = dialog.Result.Select(p => p.Clone()).ToList();
        _preferences.Language = dialog.SelectedLanguage;
        _preferences.Theme = dialog.SelectedTheme;
        _preferences.ForceUnsignedDrivers = dialog.ForceUnsignedDrivers;
        _preferences.WindowsSetup = dialog.WindowsSetup;
        SaveDefaultPartitionConfig();
        SavePreferences();
        Localization.ApplyCulture(_preferences.Language);
        ApplyLanguage();
        ThemeService.Apply(this, _preferences.Theme);
        AddActivity($"Default partition layout updated: {_defaultPartitions.Count} partition(s).");
    }
    private void AddPartition_Click(object sender, RoutedEventArgs e)
    {
        if (_partitions.Count >= 4) { MessageBox.Show("MBR supports a maximum of four partitions.", "Partition limit", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        _partitions.Add(new PartitionConfig
        {
            Number = _partitions.Count + 1,
            Name = $"PARTITION {_partitions.Count + 1}",
            SizeText = _partitions.Any(p => p.IsRemaining) ? "10 GB" : "*",
            FileSystem = "exFAT"
        });
        PartitionConfigurationChanged();
    }

    private void MainDefaults_Click(object sender, RoutedEventArgs e)
    {
        var hasSources = _partitions.Any(p => p.HasAnyContent);
        if (hasSources && MessageBox.Show("Restore the configured default partitions and clear the current content selections?", "Restore defaults", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _partitions = _defaultPartitions.Select(p => p.Clone()).ToList();
        MainPartitionList.ItemsSource = _partitions;
        PartitionConfigurationChanged();
        AddActivity("Default partition layout restored.");
    }

    private void RemovePartition_Click(object sender, RoutedEventArgs e)
    {
        if (_partitions.Count <= 1) { MessageBox.Show("At least one partition is required.", "Partition required", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var item = (sender as FrameworkElement)?.DataContext as PartitionConfig ?? MainPartitionList.SelectedItem as PartitionConfig ?? _partitions[^1];
        if (item.HasAnyContent && MessageBox.Show($"Remove {item.Name} and its selected content list?", "Remove partition", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _partitions.Remove(item);
        if (!_partitions.Any(p => p.IsRemaining)) _partitions[^1].SizeText = "*";
        PartitionConfigurationChanged();
    }

    private async void PartitionContentAdd_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PartitionConfig partition) return;
        var dialog = new PartitionContentDialog(partition, _preferences.Theme) { Owner = this };
        dialog.ActionHandler = async (action, owner) => await HandlePartitionContentActionAsync(partition, action, owner);
        dialog.ShowDialog();
    }

    private async Task HandlePartitionContentActionAsync(PartitionConfig partition, PartitionContentAction action, Window owner)
    {
        switch (action)
        {
            case PartitionContentAction.Files: await AddPartitionFilesAsync(partition, owner); break;
            case PartitionContentAction.Folder: await AddPartitionFolderAsync(partition, owner); break;
            case PartitionContentAction.Autounattend: await AddPartitionAutounattendAsync(partition, owner); break;
            case PartitionContentAction.Iso: await AddPartitionIsoAsync(partition, owner); break;
            case PartitionContentAction.ScriptFiles: await AddPartitionScriptFilesAsync(partition, owner); break;
            case PartitionContentAction.Drivers: await AddPartitionDriversAsync(partition, owner); break;
        }
    }

    private async Task AddPartitionFilesAsync(PartitionConfig partition, Window owner)
    {
        var dialog = new OpenFileDialog { Title = $"Select files for {partition.Name}", Filter = "All files (*.*)|*.*", CheckFileExists = true, Multiselect = true, InitialDirectory = PickerLocationStore.Get("Files") };
        if (dialog.ShowDialog(owner) != true) return;
        PickerLocationStore.Set("Files", Path.GetDirectoryName(dialog.FileNames[0]));
        foreach (var path in dialog.FileNames)
            if (!partition.SourceFiles.Any(existing => existing.Equals(path, StringComparison.OrdinalIgnoreCase))) partition.SourceFiles.Add(path);
        await RefreshPartitionContentSizeAsync(partition, true); UpdateBuildButton();
    }

    private async Task AddPartitionFolderAsync(PartitionConfig partition, Window owner)
    {
        var dialog = new OpenFolderDialog { Title = $"Select a folder for {partition.Name}", Multiselect = false, InitialDirectory = PickerLocationStore.Get("Folder") };
        if (dialog.ShowDialog(owner) != true) return;
        PickerLocationStore.Set("Folder", dialog.FolderName);
        if (!partition.SourceFolders.Any(existing => existing.Equals(dialog.FolderName, StringComparison.OrdinalIgnoreCase))) partition.SourceFolders.Add(dialog.FolderName);
        await RefreshPartitionContentSizeAsync(partition, true); UpdateBuildButton();
    }

    private async Task AddPartitionAutounattendAsync(PartitionConfig partition, Window owner)
    {
        if (partition.FileSystem != "NTFS" && !partition.HasIso) return;
        var dialog = new OpenFileDialog { Title = $"Select Autounattend.xml for {partition.Name}", Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*", CheckFileExists = true, Multiselect = false, InitialDirectory = PickerLocationStore.Get("XML") };
        if (dialog.ShowDialog(owner) != true) return;
        PickerLocationStore.Set("XML", Path.GetDirectoryName(dialog.FileName));
        partition.AutounattendSource = dialog.FileName;
        partition.PreparedAutounattendXml = null;
        await RefreshPartitionContentSizeAsync(partition, true); UpdateBuildButton();
    }

    private async Task AddPartitionIsoAsync(PartitionConfig partition, Window owner)
    {
        if (partition.FileSystem != "NTFS") return;
        if (_partitions.Any(item => !ReferenceEquals(item, partition) && item.HasIso))
        {
            MessageBox.Show("Only one bootable Windows ISO partition is supported on each USB drive. Clear the ISO from the other partition first.",
                "Bootable ISO already selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (partition.IsRemaining)
        {
            MessageBox.Show("A bootable Windows ISO partition must use a fixed NTFS size of at least 5 GB.",
                "Fixed size required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!PartitionConfig.TryParseSize(partition.SizeText, out var partitionBytes) || partitionBytes < 5L * 1024 * 1024 * 1024)
        {
            MessageBox.Show("Set this partition to a fixed NTFS size of at least 5 GB before selecting a Windows ISO.",
                "Boot partition size", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new OpenFileDialog { Title = $"Select an ISO for {partition.Name}", Filter = "ISO images (*.iso)|*.iso", CheckFileExists = true, Multiselect = false, InitialDirectory = PickerLocationStore.Get("ISO") };
        if (dialog.ShowDialog(owner) != true) return;
        PickerLocationStore.Set("ISO", Path.GetDirectoryName(dialog.FileName));
        if (new FileInfo(dialog.FileName).Length + 256L * 1024 * 1024 > partitionBytes)
        {
            MessageBox.Show($"The selected ISO needs more room than partition {partition.Name}. Increase the partition size and select it again.",
                "Boot partition too small", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            SetStatus("Inspecting Windows ISO", "#B36A13");
            Cursor = Cursors.Wait;
            var isoInfo = await InspectBootableIsoAsync(dialog.FileName);
            Cursor = null;
            var options = new WindowsIsoOptionsDialog(dialog.FileName, isoInfo.Editions, _preferences.Theme) { Owner = owner };
            if (options.ShowDialog() != true || options.Selection is not { } selection) return;
            partition.IsoSource = dialog.FileName;
            partition.IsoEditionIndex = selection.EditionIndex;
            partition.IsoEditionName = selection.EditionName;
            partition.PreparedMediaPath = null;
            partition.ExtractedIsoBytes = isoInfo.TotalBytes;
            PartitionConfigurationChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The selected ISO could not be inspected.\n\n{ex.Message}", "ISO inspection failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
            SetStatus(Localization.Text(_preferences.Language, "Ready"), "#147A4B");
        }
    }

    private async Task AddPartitionScriptFilesAsync(PartitionConfig partition, Window owner)
    {
        if (partition.FileSystem != "NTFS") return;
        var dialog = new ScriptSourcesDialog(partition.ScriptFiles, _preferences.Theme) { Owner = owner };
        if (dialog.ShowDialog() != true) return;
        partition.ScriptFiles.Clear();
        foreach (var path in dialog.ScriptFiles) partition.ScriptFiles.Add(path);
        partition.PreparedAutounattendXml = null;
        await RefreshPartitionContentSizeAsync(partition, true);
        UpdateBuildButton();
        AddActivity(partition.HasScripts
            ? $"Setup script content updated for {partition.Name}: {partition.ScriptFiles.Count} file(s)."
            : $"Setup script content removed from {partition.Name}.");
    }

    private Task AddPartitionDriversAsync(PartitionConfig partition, Window owner)
    {
        if (partition.FileSystem != "NTFS") return Task.CompletedTask;
        var dialog = new DriverSourcesDialog(partition.DriverFolders, partition.DriverFiles, partition.DriverArchives,
            _preferences.ForceUnsignedDrivers, _preferences.Theme) { Owner = owner };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        partition.DriverFolders.Clear();
        foreach (var path in dialog.DriverFolders) partition.DriverFolders.Add(path);
        partition.DriverFiles.Clear();
        foreach (var path in dialog.DriverFiles) partition.DriverFiles.Add(path);
        partition.DriverArchives.Clear();
        foreach (var path in dialog.DriverArchives) partition.DriverArchives.Add(path);
        partition.ForceUnsignedDrivers = partition.HasDrivers && _preferences.ForceUnsignedDrivers;
        partition.PreparedMediaPath = null;
        partition.ExtractedIsoBytes = null;
        PartitionConfigurationChanged();
        AddActivity(partition.HasDrivers
            ? $"Driver injection updated for {partition.Name}: {partition.DriverFolders.Count} folder(s), {partition.DriverFiles.Count} INF file(s), {partition.DriverArchives.Count} compressed pack(s)."
            : $"Driver injection removed from {partition.Name}.");
        return Task.CompletedTask;
    }

    private void PartitionSourcesClear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PartitionConfig partition || !partition.HasAnyContent) return;
        if (MessageBox.Show($"Clear all selected content for {partition.Name}?", "Clear partition content", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        partition.SourceFiles.Clear(); partition.SourceFolders.Clear(); partition.AutounattendSource = null; partition.ClearIsoSelection(); partition.SelectedContentBytes = 0; partition.LargestSelectedFileBytes = 0; MainPartitionList.Items.Refresh(); UpdatePartitionPreview(SelectedDisks()); UpdateBuildButton();
    }
}

public sealed record BootableIsoInfo(long TotalBytes, string InstallImageName, IReadOnlyList<WindowsImageEdition> Editions);

public sealed class UsbDisk
{
    public int Number { get; set; }
    public string FriendlyName { get; set; } = "USB Disk";
    public string? SerialNumber { get; set; }
    public string? UniqueId { get; set; }
    public long Size { get; set; }
    public bool IsBoot { get; set; }
    public bool IsSystem { get; set; }
    public string? DriveLetters { get; set; }
    public string DiskTitle => string.IsNullOrWhiteSpace(DriveLetters) ? $"Disk {Number}" : $"Disk {Number}  |  {DriveLetters}";
    public string SizeDisplay => $"{Size / (1024d * 1024 * 1024):N2} GB";
    public string Display => $"Disk {Number}  |  {FriendlyName}  |  {Size / (1024d * 1024 * 1024):N2} GB";
}

public sealed class PartitionResult
{
    public List<string> Letters { get; set; } = [];
}
