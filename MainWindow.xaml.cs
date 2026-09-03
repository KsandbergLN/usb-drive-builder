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
    private readonly List<QueueDriveProgressState> _queueDriveProgress = [];
    private readonly List<double> _queueDriveCompletion = [];
    private readonly List<UsbDisk> _queueProgressDisks = [];
    private int _activeQueueDriveIndex = -1;
    private double _activeQueueProcessStart;
    private double _activeQueueProcessEnd;
    private bool _trackingSharedPreparation;
    private double _sharedPreparationPercent;
    private double _sharedPreparationStageStart;
    private double _sharedPreparationStageEnd;
    private double _queuePreparationShare;
    private static readonly string VersionLabel = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.100"}";
    private const string MainPartitionDragFormat = "LaptopQaUsbBuilder.MainPartition";
    private const string ScriptRunnerName = "LaptopQA-RunScripts.cmd";
    private const string ScriptCleanupName = "LaptopQA-Cleanup.ps1";
    internal const string CacheCleanupGuardName = "LaptopQAUsbBuilder.CacheCleanupGuard";
    private Semaphore? _cacheCleanupGuard;
    private bool _preservePreparedMediaForRetry;

    public MainWindow()
    {
        InitializeComponent();
        BorderlessWindowResizer.Attach(this);
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1180, Math.Max(MinWidth, workArea.Width - 32));
        Height = Math.Min(700, Math.Max(MinHeight, workArea.Height - 32));
        _preferences = LoadPreferences();
        Localization.ApplyCulture(_preferences.Language);
        _defaultPartitions = LoadPartitionConfig();
        _partitions = _defaultPartitions.Select(p => p.Clone()).ToList();
        MainPartitionList.ItemsSource = _partitions;
        ApplyPartitionConfig();
        ApplyLanguage();
        ThemeService.Apply(this, _preferences.Theme);
        StateChanged += (_, _) => UpdateWindowStateVisuals();
        Loaded += (_, _) =>
        {
            ThemeService.Apply(this, _preferences.Theme);
        };
        Loaded += async (_, _) =>
        {
            AddActivity("USB Drive Builder started in administrator mode.");
            await RefreshDisksAsync();
        };
        Closing += (_, e) =>
        {
            if (!_isBuilding && !_isPreflighting)
            {
                CleanupTemporaryCaches(_preservePreparedMediaForRetry);
                if (BuildCacheCleanup.IsCleanupRequested)
                    BuildCacheCleanup.StartCleanupAfterExit(Environment.ProcessId);
                return;
            }
            e.Cancel = true;
            MessageBox.Show(_isPreflighting ? "Wait for the build safety checks to finish before closing." : "Wait for the active USB build to finish before closing.",
                _isPreflighting ? "Preparing build" : "Build in progress", MessageBoxButton.OK, MessageBoxImage.Information);
        };
    }

    private static void CleanupTemporaryCaches(bool preservePreparedMediaForRetry)
    {
        Semaphore? guard = null;
        var ownsGuard = false;
        try
        {
            guard = Semaphore.OpenExisting(CacheCleanupGuardName);
            ownsGuard = guard.WaitOne(0);
            if (!ownsGuard)
            {
                guard.Dispose();
                return;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // No other USB Drive Builder instance is preparing media.
        }
        catch (UnauthorizedAccessException)
        {
            // If Windows will not expose another user's guard, do not risk deleting shared cache data.
            return;
        }

        try
        {
            BuildCacheCleanup.ClearStagingBestEffort();
            if (BuildCacheCleanup.IsCleanupRequested || !preservePreparedMediaForRetry)
                BuildCacheCleanup.ClearBestEffort();
            BuildCacheCleanup.CompleteRequestIfEmpty();
        }
        finally
        {
            if (ownsGuard) guard?.Release();
            guard?.Dispose();
        }
    }

    private bool TryAcquireCacheCleanupGuard()
    {
        _cacheCleanupGuard = new Semaphore(1, 1, CacheCleanupGuardName);
        if (_cacheCleanupGuard.WaitOne(0)) return true;
        _cacheCleanupGuard.Dispose();
        _cacheCleanupGuard = null;
        return false;
    }

    private void ReleaseCacheCleanupGuard()
    {
        if (_cacheCleanupGuard is null) return;
        try { _cacheCleanupGuard.Release(); }
        catch (SemaphoreFullException) { }
        finally { _cacheCleanupGuard.Dispose(); _cacheCleanupGuard = null; }
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
        if (!TryAcquireCacheCleanupGuard())
        {
            MessageBox.Show("Another USB Drive Builder instance is preparing or building Windows media. Wait for it to finish before starting another build.",
                "Build already active", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaptopQAUsbBuilder", "Logs");
        Directory.CreateDirectory(logFolder);
        _logPath = Path.Combine(logFolder, $"Build-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        ActivityList.Items.Clear();
        _buildCancellation = new CancellationTokenSource();
        SetPreflightState(true);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        var cancelled = false;
        var cancellationCleanupComplete = true;
        try { await BuildCoreAsync(); }
        catch (OperationCanceledException)
        {
            cancelled = true;
            AddActivity("Build cancelled by user.");
            cancellationCleanupComplete = await Task.Run(BuildCacheCleanup.ClearStagingBestEffort);
        }
        catch (Exception ex)
        {
            Log($"Unexpected build error: {LogSanitizer.SanitizeException(ex)}");
            if (_isBuilding) SetBuildingState(false);
            MessageBox.Show($"The build could not continue.\n\n{ex.Message}\n\nLog: {_logPath}",
                "Build failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _trackingSharedPreparation = false;
            _buildCancellation?.Dispose();
            _buildCancellation = null;
            SetPreflightState(false);
            SetBuildingState(false);
            ReleaseCacheCleanupGuard();
        }
        if (cancelled)
        {
            SetNonTransferActivity(cancellationCleanupComplete
                ? "Cancelled — temporary staging cleaned up"
                : "Cancelled — locked staging will be retried on close");
            SetStatus("✕ Cancelled", "#AE3338");
        }
    }

    private async Task BuildCoreAsync()
    {
        var cancellationToken = _buildCancellation?.Token ?? CancellationToken.None;
        cancellationToken.ThrowIfCancellationRequested();
        var queuedDisks = SelectedDisks();
        if (queuedDisks.Count == 0) return;
        InitializeQueueDriveProgress(queuedDisks);
        BeginSharedPreparation(_partitions.Any(partition => partition.HasIso) ? 55 : 5);
        SetSharedPreparationStage(0, 2);
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
        SetSharedPreparationProgress(4);

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
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(source))
            {
                MessageBox.Show($"Source folder not found:\n{source}", "Missing source", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            foreach (var disk in queuedDisks)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(source))
            {
                MessageBox.Show($"Source file not found:\n{source}", "Missing source", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            foreach (var disk in queuedDisks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await SourceIsOnDiskAsync(source, disk.Number))
                {
                    MessageBox.Show($"A copy source is stored on queued Disk {disk.Number} and would be erased before it could be copied:\n{source}",
                        "Source is on target disk", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }
        SetSharedPreparationProgress(7);

        foreach (var partition in _partitions.Where(item => item.HasScripts || item.GenerateAutounattend))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var answerFileSource = partition.GenerateAutounattend ? null : partition.AutounattendSource ?? partition.FolderXmlSource;
                partition.PreparedAutounattendXml = partition.HasScripts
                    ? BuildScriptAutounattend(answerFileSource, answerFileSource is null ? _preferences.WindowsSetup : null)
                    : BuildGeneratedAutounattend(_preferences.WindowsSetup);
                AddActivity(partition.HasScripts
                    ? (string.IsNullOrWhiteSpace(answerFileSource)
                        ? $"Prepared a generated Autounattend.xml to run scripts for {partition.Name}."
                        : $"Prepared the selected Autounattend.xml with the script runner for {partition.Name}.")
                    : $"Prepared a generated Autounattend.xml for {partition.Name}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"The Windows Setup script command could not be added to Autounattend.xml.\n\n{ex.Message}",
                    "Autounattend preparation failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        SetSharedPreparationProgress(9);

        await RefreshAllPartitionContentSizesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        SetSharedPreparationProgress(11);

        foreach (var partition in _partitions.Where(p => p.HasIso))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (partition.FileSystem != "NTFS" || partition.IsRemaining || !PartitionConfig.TryParseSize(partition.SizeText, out var partitionBytes))
            {
                MessageBox.Show($"{partition.Name} must be a fixed-size NTFS partition to create bootable Windows media.",
                    "Invalid boot partition", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                SetStatus("Preparing Windows media", "#B36A13");
                SetSharedPreparationStage(11, 14);
                BuildProgress.Value = 0;
                var isoInfo = await InspectBootableIsoAsync(partition.IsoSource!);
                SetSharedPreparationProgress(14);
                BuildProgress.Value = 25;
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
                var selection = new WindowsIsoSelection(edition.Index, edition.Name, edition.EditionId,
                    partition.DriverFolders.ToArray(), partition.DriverFiles.ToArray(), partition.DriverArchives.ToArray(),
                    partition.ForceUnsignedDrivers);
                var preparer = new WindowsMediaPreparer(
                    SetMediaPreparationActivity,
                    UpdateDismActivity,
                    warning =>
                    {
                        SetNonTransferActivity(warning);
                        AddActivity($"WARNING: {warning}");
                        Log($"DISM activity warning: {warning}");
                    },
                    Log, MountIsoAsync, DismountIsoAsync,
                    _buildCancellation?.Token ?? CancellationToken.None);
                var prepared = await preparer.PrepareAsync(partition.IsoSource!, isoInfo, selection);
                partition.PreparedMediaPath = prepared.MediaPath;
                partition.ExtractedIsoBytes = prepared.TotalBytes;
                BuildProgress.Value = 100;
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
            catch (OperationCanceledException) { throw; }
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
        BuildProgress.Value = 100;
        CompleteSharedPreparation();
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
        BuildProgress.IsIndeterminate = false;
        BuildProgress.Value = 0;

        var succeeded = 0;
        var failures = new List<string>();
        for (var queueIndex = 0; queueIndex < queuedDisks.Count; queueIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var disk = queuedDisks[queueIndex];
            StartQueueDiskEstimate();
            SetActiveQueueDrive(queueIndex);
            var writeStart = _queuePreparationShare;
            var writeSpan = 100 - writeStart;
            SetQueueDriveProcessRange(queueIndex, writeStart, writeStart + writeSpan * 0.15);
            SetStatus($"Building {queueIndex + 1} of {queuedDisks.Count}", "#B36A13");
            BuildProgress.IsIndeterminate = false;
            BuildProgress.Value = 0;
            SetNonTransferActivity("Preparing drive...");
            try
            {
                AddActivity($"QUEUE {queueIndex + 1}/{queuedDisks.Count}: Locked target to Disk {disk.Number}: {disk.FriendlyName}.");
                SetNonTransferActivity("Clearing the USB partition layout...");
                AddActivity("Clearing all existing USB partition and volume metadata before creating the requested layout.");
                var result = await CreatePartitionsAsync(disk);
                BuildProgress.IsIndeterminate = false;
                BuildProgress.Value = 100;
                SetQueueDriveCompletion(queueIndex, writeStart + writeSpan * 0.15);
                foreach (var partition in _partitions) AddActivity($"Created {partition.Name} ({partition.SizeText}, {partition.FileSystem}).");
                var copyPartitions = _partitions.Select((partition, index) => (partition, index))
                    .Where(item => item.partition.HasAnyContent).ToList();
                for (var copyIndex = 0; copyIndex < copyPartitions.Count; copyIndex++)
                {
                    var (partition, partitionIndex) = copyPartitions[copyIndex];
                    if (partitionIndex >= result.Letters.Count || string.IsNullOrWhiteSpace(result.Letters[partitionIndex]))
                        throw new InvalidOperationException($"Windows did not assign a drive letter to {partition.Name}.");
                    SetQueueDriveProcessRange(queueIndex,
                        writeStart + writeSpan * (0.15 + 0.75 * copyIndex / Math.Max(1, copyPartitions.Count)),
                        writeStart + writeSpan * (0.15 + 0.75 * (copyIndex + 1) / Math.Max(1, copyPartitions.Count)));
                    BuildProgress.Value = 0;
                    await CopyPartitionSourcesAsync(partition, $"{result.Letters[partitionIndex]}:\\", 0, 100);
                }
                if (copyPartitions.Count == 0) SetQueueDriveCompletion(queueIndex, writeStart + writeSpan * 0.90);
                AddActivity("Verifying partition labels and file systems.");
                SetQueueDriveProcessRange(queueIndex, writeStart + writeSpan * 0.90, 100);
                SetNonTransferActivity("Verifying partitions...");
                BuildProgress.Value = 0;
                await VerifyPartitionsAsync(result.DiskNumber, disk.UniqueId);
                BuildProgress.Value = 100;
                CompleteQueueDrive(queueIndex);
                succeeded++;
                CompleteQueueDiskEstimate();
                AddActivity($"Disk {disk.Number} completed and verified.");
                Log($"Disk {disk.Number} completed and verified.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                FailQueueDrive(queueIndex);
                CompleteQueueDiskEstimate();
                failures.Add($"Disk {disk.Number}: {ex.Message}");
                AddActivity($"Disk {disk.Number} FAILED: {ex.Message}. Continuing queue.");
                Log($"Disk {disk.Number} ERROR: {LogSanitizer.SanitizeException(ex)}");
            }
        }
        BuildProgress.IsIndeterminate = false;
        _activeQueueDriveIndex = -1;
        RenderQueueDriveProgress();
        CompleteEtaTracking();
        SetBuildingState(false);
        ConfirmText.Clear();
        _preservePreparedMediaForRetry = failures.Count > 0 && _partitions.Any(partition =>
            partition.HasIso && !string.IsNullOrWhiteSpace(partition.PreparedMediaPath) &&
            Directory.Exists(partition.PreparedMediaPath));
        if (_preservePreparedMediaForRetry)
            AddActivity("Prepared Windows media and its supporting driver caches will be retained after close for the next retry.");
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
        script.AppendLine($"if($d.IsReadOnly){{Set-Disk -Number {disk.Number} -IsReadOnly $false -ErrorAction Stop}}");
        script.AppendLine($"if($d.IsOffline){{Set-Disk -Number {disk.Number} -IsOffline $false -ErrorAction Stop}}");
        script.AppendLine("$diskpartScript=Join-Path ([IO.Path]::GetTempPath()) ('USBDriveBuilder-'+[guid]::NewGuid().ToString('N')+'.txt')");
        script.AppendLine("try{");
        script.AppendLine($"  [IO.File]::WriteAllLines($diskpartScript,[string[]]@('select disk {disk.Number}','attributes disk clear readonly noerr','clean','exit'),[Text.Encoding]::ASCII)");
        script.AppendLine("  $diskpartOutput=@(& (Join-Path $env:SystemRoot 'System32\\diskpart.exe') /s $diskpartScript 2>&1|ForEach-Object{[string]$_})");
        script.AppendLine("  $diskpartExit=$LASTEXITCODE");
        script.AppendLine("}finally{Remove-Item -LiteralPath $diskpartScript -Force -ErrorAction SilentlyContinue}");
        script.AppendLine("if($diskpartExit -ne 0){$summary=($diskpartOutput|Where-Object{$_}|Select-Object -Last 8)-join ' | ';if($diskpartExit -eq -2147024463){throw 'The USB device disconnected while Windows was writing its partition table (Windows error 433). Reconnect it directly to another USB port. If this repeats, the USB drive cannot reliably accept partition-table writes and should be replaced.'};throw \"DiskPart could not clear the USB partition layout (exit code $diskpartExit). $summary\"}");
        script.AppendLine("$d=$null;for($i=0;$i -lt 120;$i++){Update-HostStorageCache -ErrorAction SilentlyContinue;$d=@(Get-Disk|Where-Object{[string]$_.UniqueId -eq '" + expectedId + "'}|Select-Object -First 1);if($d){$partitionCount=@(Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue).Count;if($partitionCount -eq 0){break}};Start-Sleep -Milliseconds 500}");
        script.AppendLine("if(-not $d){throw 'Windows did not rediscover the wiped USB drive. Disconnect and reconnect it, then refresh and retry.'}");
        script.AppendLine("$diskNumber=$d.Number;$partitionCount=@(Get-Partition -DiskNumber $diskNumber -ErrorAction SilentlyContinue).Count");
        script.AppendLine("if($d.BusType -ne 'USB' -or $d.IsBoot -or $d.IsSystem){throw 'The target identity or safety state changed after wiping the USB disk.'}");
        script.AppendLine("if($partitionCount -ne 0){$remainingDetail=(@(Get-Partition -DiskNumber $diskNumber -ErrorAction SilentlyContinue|ForEach-Object{\"Partition $($_.PartitionNumber), offset $($_.Offset), size $($_.Size)\"}) -join ' | ');$summary=($diskpartOutput|Where-Object{$_}|Select-Object -Last 8)-join ' | ';throw \"The full-disk wipe completed but Windows still reports $partitionCount partition(s). $remainingDetail $summary\"}");
        script.AppendLine("if($d.PartitionStyle -ne 'MBR'){try{Set-Disk -Number $diskNumber -PartitionStyle MBR -ErrorAction Stop}catch{throw 'Windows cleared the USB disk but could not set its empty partition table to MBR: '+$_.Exception.Message}}");
        script.AppendLine("for($i=0;$i -lt 40;$i++){Update-HostStorageCache -ErrorAction SilentlyContinue;Start-Sleep -Milliseconds 250;$d=Get-Disk -Number $diskNumber;if($d.PartitionStyle -eq 'MBR'){break}}");
        script.AppendLine("$d=Get-Disk -Number $diskNumber;$partitionCount=@(Get-Partition -DiskNumber $diskNumber -ErrorAction SilentlyContinue).Count");
        script.AppendLine("if($d.PartitionStyle -ne 'MBR' -or $partitionCount -ne 0){throw \"Windows did not leave the USB disk as an empty MBR disk. It reports $($d.PartitionStyle) with $partitionCount partition(s).\"}");

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
                script.AppendLine($"$remainingSize=[math]::Floor(((Get-Disk -Number $diskNumber).LargestFreeExtent-{reservedAfter})/1MB)*1MB");
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
            script.AppendLine($"${variable}=New-Partition -DiskNumber $diskNumber {sizeArgument} -AssignDriveLetter{mbrType} -ErrorAction Stop");
            var allocation = item.FileSystem == "NTFS" ? " -AllocationUnitSize 4096" : "";
            script.AppendLine($"try{{Format-Volume -Partition ${variable} -FileSystem {item.FileSystem} -NewFileSystemLabel '{PsQuote(item.Name)}'{allocation} -Confirm:$false -Force -ErrorAction Stop|Out-Null}}catch{{throw 'Failed to format partition {index + 1} ({PsQuote(item.Name)}) as {item.FileSystem}: '+$_.Exception.Message}}");
        }

        script.AppendLine("Get-Partition -DiskNumber $diskNumber|Where-Object IsActive|Set-Partition -IsActive $false");

        var letterExpressions = string.Join(",", Enumerable.Range(1, _partitions.Count).Select(number => $"[string](($p{number}|Get-Volume).DriveLetter)"));
        script.AppendLine($"[pscustomobject]@{{DiskNumber=$diskNumber;Letters=@({letterExpressions})}} | ConvertTo-Json -Compress");
        var wipeStarted = DateTime.UtcNow;
        var wipeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        wipeTimer.Tick += (_, _) =>
        {
            var elapsed = DateTime.UtcNow - wipeStarted;
            CurrentEtaText.Text = $"Current activity: Clearing and preparing USB drive — {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00} elapsed";
        };
        wipeTimer.Start();
        string json;
        try { json = await RunPowerShellAsync(script.ToString()); }
        finally { wipeTimer.Stop(); }
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
            new XElement(unattend + "Path", $"cmd.exe /d /c \"if exist C:\\Windows\\Setup\\Scripts\\{ScriptRunnerName} (call C:\\Windows\\Setup\\Scripts\\{ScriptRunnerName}) else (echo {ScriptRunnerName} missing>C:\\Windows\\Temp\\LaptopQA-Runner-Missing.log) & exit /b 0\"")));

        document.Declaration = null;
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + document;
    }

    internal static string BuildGeneratedAutounattend(WindowsSetupConfig setup)
    {
        XNamespace unattend = "urn:schemas-microsoft-com:unattend";
        XNamespace wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var document = new XDocument(new XElement(unattend + "unattend",
            new XAttribute(XNamespace.Xmlns + "wcm", wcm.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName)));
        AddGeneratedWindowsSetup(document, setup, unattend, wcm);
        document.Declaration = null;
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + document;
    }

    private static void AddGeneratedWindowsSetup(XDocument document, WindowsSetupConfig setup, XNamespace unattend, XNamespace wcm)
    {
        ValidateGeneratedWindowsSetup(setup);
        var root = document.Root!;
        var settings = new XElement(unattend + "settings", new XAttribute("pass", "windowsPE"));
        settings.Add(new XElement(unattend + "component",
            new XAttribute("name", "Microsoft-Windows-International-Core-WinPE"),
            new XAttribute("processorArchitecture", "amd64"),
            new XAttribute("publicKeyToken", "31bf3856ad364e35"),
            new XAttribute("language", "neutral"),
            new XAttribute("versionScope", "nonSxS"),
            new XElement(unattend + "SetupUILanguage", new XElement(unattend + "UILanguage", setup.OobeLanguage)),
            new XElement(unattend + "InputLocale", setup.OobeKeyboard),
            new XElement(unattend + "SystemLocale", setup.OobeLanguage),
            new XElement(unattend + "UILanguage", setup.OobeLanguage),
            new XElement(unattend + "UserLocale", setup.OobeLanguage)));
        var component = new XElement(unattend + "component", new XAttribute("name", "Microsoft-Windows-Setup"), new XAttribute("processorArchitecture", "amd64"), new XAttribute("publicKeyToken", "31bf3856ad364e35"), new XAttribute("language", "neutral"), new XAttribute("versionScope", "nonSxS"));
        var imageInstall = new XElement(unattend + "ImageInstall",
            new XElement(unattend + "OSImage",
                new XElement(unattend + "InstallFrom",
                    new XElement(unattend + "MetaData", new XAttribute(wcm + "action", "add"), new XElement(unattend + "Key", "/IMAGE/NAME"), new XElement(unattend + "Value", setup.Edition))),
                new XElement(unattend + "InstallTo", new XElement(unattend + "DiskID", setup.TargetDisk), new XElement(unattend + "PartitionID", setup.InstallPartition))));
        component.Add(imageInstall);
        component.Add(new XElement(unattend + "UserData", new XElement(unattend + "AcceptEula", "true")));
        component.Add(new XElement(unattend + "UseConfigurationSet", "true"));

        // Windows PE can reliably execute a short command at a time.  Keep the partition
        // script writes separate: shell grouping and nested quotes can silently join two
        // DiskPart commands together (for example, FORMAT and ASSIGN) on some WinPE builds.
        var commands = new XElement(unattend + "RunSynchronous");
        var order = 1;
        if (setup.PromptBeforeInstall)
        {
            var confirmationLines = new[]
            {
                $"answer = MsgBox(\"Windows Setup is ready to reimage this computer.\" & vbCrLf & vbCrLf & \"This will erase all data on Disk {setup.TargetDisk} and install a fresh copy of Windows.\" & vbCrLf & vbCrLf & \"Select Yes to begin, or No to return without making changes.\", vbYesNo + vbQuestion + vbDefaultButton2, \"Ready to Reimage\")",
                "If answer <> vbYes Then",
                "WScript.Echo \"Reimage cancelled by user.\"",
                "WScript.Quit 1",
                "End If"
            };
            for (var index = 0; index < confirmationLines.Length; index++)
            {
                var redirect = index == 0 ? ">" : ">>";
                AddRunSynchronousCommand(commands, unattend, wcm, ref order, "Create reimage confirmation", $"cmd.exe /c echo {EscapeCommandEcho(confirmationLines[index])} {redirect} X:\\confirm-reimage.vbs");
            }
            AddRunSynchronousCommand(commands, unattend, wcm, ref order, "Confirm Windows installation", "cscript.exe //nologo //E:vbscript \"X:\\confirm-reimage.vbs\"");
        }

        var diskpartLines = new[]
        {
            $"SELECT DISK={setup.TargetDisk}",
            "CLEAN",
            "CONVERT GPT",
            $"CREATE PARTITION EFI SIZE={setup.EfiSizeMb}",
            $"FORMAT QUICK FS=FAT32 LABEL={setup.EfiLabel}",
            $"ASSIGN LETTER={setup.EfiLetter}",
            $"CREATE PARTITION MSR SIZE={setup.MsrSizeMb}",
            "CREATE PARTITION PRIMARY",
            $"SHRINK MINIMUM={setup.WindowsShrinkMb}",
            $"FORMAT QUICK FS=NTFS LABEL={setup.WindowsLabel}",
            $"ASSIGN LETTER={setup.WindowsLetter}",
            "CREATE PARTITION PRIMARY",
            $"FORMAT QUICK FS=NTFS LABEL={setup.RecoveryLabel}",
            $"ASSIGN LETTER={setup.RecoveryLetter}",
            "SET ID=de94bba4-06d1-4d40-a16a-bfd50179d6ac",
            "GPT ATTRIBUTES=0x8000000000000001"
        };
        for (var index = 0; index < diskpartLines.Length; index++)
        {
            var redirect = index == 0 ? ">" : ">>";
            AddRunSynchronousCommand(commands, unattend, wcm, ref order, "Write GPT partition script", $"cmd.exe /c echo {diskpartLines[index]} {redirect} X:\\diskpart.txt");
        }
        AddRunSynchronousCommand(commands, unattend, wcm, ref order, "Run DiskPart", "cmd.exe /c diskpart.exe /s X:\\diskpart.txt > X:\\diskpart.log 2>&1");
        component.Add(commands);
        settings.Add(component);
        root.AddFirst(settings);

        var oobeSettings = new XElement(unattend + "settings", new XAttribute("pass", "oobeSystem"));
        oobeSettings.Add(new XElement(unattend + "component",
            new XAttribute("name", "Microsoft-Windows-International-Core"),
            new XAttribute("processorArchitecture", "amd64"),
            new XAttribute("publicKeyToken", "31bf3856ad364e35"),
            new XAttribute("language", "neutral"),
            new XAttribute("versionScope", "nonSxS"),
            new XElement(unattend + "InputLocale", setup.OobeKeyboard),
            new XElement(unattend + "SystemLocale", setup.OobeLanguage),
            new XElement(unattend + "UILanguage", setup.OobeLanguage),
            new XElement(unattend + "UserLocale", setup.OobeLanguage)));
        root.Add(oobeSettings);
    }

    private static void AddRunSynchronousCommand(XElement commands, XNamespace unattend, XNamespace wcm, ref int order, string description, string path)
    {
        commands.Add(new XElement(unattend + "RunSynchronousCommand",
            new XAttribute(wcm + "action", "add"),
            new XElement(unattend + "Order", order++),
            new XElement(unattend + "Description", description),
            new XElement(unattend + "Path", path)));
    }

    private static string EscapeCommandEcho(string value) => value
        .Replace("^", "^^")
        .Replace("&", "^&")
        .Replace("|", "^|")
        .Replace("<", "^<")
        .Replace(">", "^>")
        .Replace("(", "^(")
        .Replace(")", "^)");

    private static void ValidateGeneratedWindowsSetup(WindowsSetupConfig setup)
    {
        ValidateDiskpartLabel(setup.EfiLabel, "EFI label");
        ValidateDiskpartLabel(setup.WindowsLabel, "Windows label");
        ValidateDiskpartLabel(setup.RecoveryLabel, "Recovery label");
        ValidateDriveLetter(setup.EfiLetter, "EFI letter");
        ValidateDriveLetter(setup.WindowsLetter, "Windows letter");
        ValidateDriveLetter(setup.RecoveryLetter, "Recovery letter");
        if (!System.Text.RegularExpressions.Regex.IsMatch(setup.OobeLanguage, "^[A-Za-z]{2,3}-[A-Za-z]{2,4}$"))
            throw new InvalidOperationException("OOBE language must use a Windows language tag such as en-US.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(setup.OobeKeyboard, "^[0-9A-Fa-f]{4}:[0-9A-Fa-f]{8}$"))
            throw new InvalidOperationException("OOBE keyboard must use a locale and keyboard layout such as 0409:00000409.");
    }

    private static void ValidateDiskpartLabel(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !char.IsLetterOrDigit(character) && character is not ' ' and not '-' and not '_'))
            throw new InvalidOperationException($"{fieldName} may contain only letters, numbers, spaces, hyphens, and underscores.");
    }

    private static void ValidateDriveLetter(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 1 || !char.IsLetter(value[0]))
            throw new InvalidOperationException($"{fieldName} must be a single letter.");
    }

    private static string BuildScriptRunner(IEnumerable<string> scriptPaths)
    {
        var paths = scriptPaths.ToArray();
        var lines = new List<string>
        {
            "@echo off",
            "setlocal EnableExtensions DisableDelayedExpansion",
            "for %%I in (\"%~dp0.\") do set \"LaptopQaOriginal=%%~fI\"",
            "set \"LaptopQaWork=%ProgramData%\\USBDriveBuilder\\ScriptRun\"",
            "if exist \"%LaptopQaWork%\" rmdir /s /q \"%LaptopQaWork%\"",
            "md \"%LaptopQaWork%\" 2>nul"
        };
        foreach (var path in paths)
        {
            var name = EscapeBatchLiteral(Path.GetFileName(path));
            lines.Add($"copy /y \"%~dp0{name}\" \"%LaptopQaWork%\\{name}\" >nul");
        }
        lines.Add($"copy /y \"%~dp0{ScriptCleanupName}\" \"%LaptopQaWork%\\{ScriptCleanupName}\" >nul");
        lines.Add("pushd \"%LaptopQaWork%\"");
        foreach (var path in paths)
        {
            var name = EscapeBatchLiteral(Path.GetFileName(path));
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".cmd":
                case ".bat":
                    lines.Add($"call \"%LaptopQaWork%\\{name}\"");
                    break;
                case ".ps1":
                    lines.Add($"\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%LaptopQaWork%\\{name}\"");
                    break;
                case ".vbs":
                case ".js":
                case ".wsf":
                    lines.Add($"\"%SystemRoot%\\System32\\cscript.exe\" //B //NoLogo \"%LaptopQaWork%\\{name}\"");
                    break;
            }
        }
        lines.Add("popd");
        lines.Add($"start \"\" /b \"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%LaptopQaWork%\\{ScriptCleanupName}\" -OriginalRoot \"%LaptopQaOriginal%\" -WorkingRoot \"%LaptopQaWork%\"");
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
            "param([string]$OriginalRoot, [string]$WorkingRoot)",
            "$ErrorActionPreference = 'SilentlyContinue'",
            "Start-Sleep -Seconds 2",
            $"$removeNames = @({string.Join(", ", names)})",
            "foreach ($name in $removeNames) { Remove-Item -LiteralPath (Join-Path $OriginalRoot $name) -Force }",
            "Remove-Item -LiteralPath (Join-Path $OriginalRoot 'LaptopQA-Cleanup.ps1') -Force",
            "if (-not (Get-ChildItem -LiteralPath $OriginalRoot -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $OriginalRoot -Force }",
            "Remove-Item -LiteralPath $WorkingRoot -Recurse -Force") + Environment.NewLine;
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
        var script = $"@(Get-WindowsImage -ImagePath '{PsQuote(imagePath)}' -LogPath '{PsQuote(dismLog)}' -ErrorAction Stop|ForEach-Object{{[pscustomobject]@{{Index=$_.ImageIndex;Name=$_.ImageName;Description=$_.ImageDescription;Size=[long]$_.ImageSize;EditionId=$_.EditionId}}}})|ConvertTo-Json -Compress";
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
        string driveLetter;
        try { driveLetter = (await RunPowerShellAsync(mountScript)).Trim().TrimEnd(':'); }
        catch
        {
            try { await DismountIsoAsync(isoPath); } catch { }
            throw;
        }
        if (driveLetter.Length != 1 || !char.IsLetter(driveLetter[0]))
        {
            await DismountIsoAsync(isoPath);
            throw new InvalidOperationException("Windows mounted the ISO but returned an invalid drive letter.");
        }
        return driveLetter;
    }

    private Task DismountIsoAsync(string isoPath) =>
        RunPowerShellAsync($"Dismount-DiskImage -ImagePath '{PsQuote(isoPath)}' -ErrorAction Stop", CancellationToken.None);

    private async Task CopySourceAsync(string source, string destination, string name, int startProgress, int endProgress, string? excludedFile = null)
    {
        BuildProgress.Value = startProgress;
        AddActivity($"Copying {name} content from {source}.");
        Log($"Copying {source} to {destination}");
        CurrentEtaText.Text = $"Current activity: Scanning {name}...";
        var cancellationToken = _buildCancellation?.Token ?? CancellationToken.None;
        var totalBytes = await Task.Run(() => CalculateDirectoryBytes(source, excludedFile, cancellationToken), cancellationToken);
        BeginTransferActivity(name, totalBytes, startProgress, endProgress);
        try
        {
            await Task.Run(() =>
            {
                var directories = new Stack<(string Source, string Target)>();
                directories.Push((source, destination));
                while (directories.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
                        cancellationToken.ThrowIfCancellationRequested();
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
            }, cancellationToken);
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
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                int bytesRead;
                while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    _buildCancellation?.Token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, bytesRead);
                    ReportTransferBytes(bytesRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(destination); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        try { File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static long CalculateDirectoryBytes(string source, string? excludedFile = null,
        CancellationToken cancellationToken = default)
        => CalculateDirectoryStats(source, excludedFile, cancellationToken).TotalBytes;

    private static (long TotalBytes, long LargestFileBytes) CalculateDirectoryStats(string source, string? excludedFile = null,
        CancellationToken cancellationToken = default)
    {
        long total = 0;
        long largest = 0;
        var directories = new Stack<string>();
        directories.Push(source);
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = directories.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
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

    private static (long TotalBytes, long LargestFileBytes) CalculateSelectedContentStats(PartitionConfig partition,
        CancellationToken cancellationToken = default)
    {
        long total = 0;
        long largest = 0;
        foreach (var folder in partition.SourceFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stats = CalculateDirectoryStats(folder, cancellationToken: cancellationToken);
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
            cancellationToken.ThrowIfCancellationRequested();
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

    private async Task RefreshAllPartitionContentSizesAsync(CancellationToken cancellationToken = default)
    {
        var stats = await Task.WhenAll(_partitions.Select(partition =>
            Task.Run(() => CalculateSelectedContentStats(partition, cancellationToken), cancellationToken)));
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

    private void InitializeQueueDriveProgress(IReadOnlyList<UsbDisk> disks)
    {
        _queueDriveProgress.Clear();
        _queueDriveProgress.AddRange(Enumerable.Repeat(QueueDriveProgressState.Pending, disks.Count));
        _queueDriveCompletion.Clear();
        _queueDriveCompletion.AddRange(Enumerable.Repeat(0d, disks.Count));
        _queueProgressDisks.Clear();
        _queueProgressDisks.AddRange(disks);
        foreach (var disk in _queueProgressDisks) disk.QueueState = QueueDriveProgressState.Pending;
        _activeQueueDriveIndex = -1;
        _activeQueueProcessStart = 0;
        _activeQueueProcessEnd = 0;
        RefreshQueueDriveCards();
        RenderQueueDriveProgress();
    }

    private void BeginSharedPreparation(double queueShare)
    {
        _queuePreparationShare = Math.Clamp(queueShare, 0, 90);
        _sharedPreparationPercent = 0;
        _sharedPreparationStageStart = 0;
        _sharedPreparationStageEnd = 0;
        _trackingSharedPreparation = true;
        SetSharedPreparationProgress(0);
    }

    private void CompleteSharedPreparation()
    {
        SetSharedPreparationProgress(100);
        _trackingSharedPreparation = false;
    }

    private void SetSharedPreparationStage(double start, double end)
    {
        _sharedPreparationStageStart = Math.Clamp(start, 0, 100);
        _sharedPreparationStageEnd = Math.Clamp(end, _sharedPreparationStageStart, 100);
        SetSharedPreparationProgress(_sharedPreparationStageStart);
    }

    private void SetSharedPreparationProgress(double percent)
    {
        if (!_trackingSharedPreparation || _queueDriveCompletion.Count == 0) return;
        _sharedPreparationPercent = Math.Max(_sharedPreparationPercent, Math.Clamp(percent, 0, 100));
        var queueCompletion = _queuePreparationShare * _sharedPreparationPercent / 100d;
        for (var index = 0; index < _queueDriveCompletion.Count; index++)
            _queueDriveCompletion[index] = Math.Max(_queueDriveCompletion[index], queueCompletion);
        RenderQueueDriveProgress();
    }

    private void SetMediaPreparationActivity(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.StartsWith("using cached ")) SetSharedPreparationStage(94, 100);
        else if (lower.StartsWith("checking and extracting ")) SetSharedPreparationStage(15, 18);
        else if (lower.StartsWith("checking compressed driver pack ")) SetSharedPreparationStage(16, 19);
        else if (lower.StartsWith("extracting compressed driver pack ")) SetSharedPreparationStage(18, 22);
        else if (lower.StartsWith("expanding ")) SetSharedPreparationStage(20, 25);
        else if (lower.StartsWith("checking driver packages ")) SetSharedPreparationStage(25, 29);
        else if (lower.StartsWith("hashing the iso ")) SetSharedPreparationStage(29, 34);
        else if (lower.StartsWith("preparing windows media ")) SetSharedPreparationStage(34, 37);
        else if (lower.StartsWith("copying windows iso ")) SetSharedPreparationStage(37, 43);
        else if (lower.StartsWith("staging selected drivers ")) SetSharedPreparationStage(37, 43);
        else if (lower.StartsWith("checking ") && lower.Contains("staging duplicates")) SetSharedPreparationStage(40, 44);
        else if (lower.StartsWith("hashing ") && lower.Contains("driver candidates")) SetSharedPreparationStage(42, 47);
        else if (lower.StartsWith("creating the deduplicated ")) SetSharedPreparationStage(47, 51);
        else if (lower.StartsWith("exporting ")) SetSharedPreparationStage(51, 66);
        else if (lower.StartsWith("mounting ")) SetSharedPreparationStage(66, 70);
        else if (lower.StartsWith("injecting drivers ")) SetSharedPreparationStage(70, 73);
        else if (lower.StartsWith("injecting driver folder ") || lower.StartsWith("injecting individual driver ")) SetSharedPreparationStage(73, 84);
        else if (lower.StartsWith("retrying ")) SetSharedPreparationStage(76, 84);
        else if (lower.StartsWith("committing ")) SetSharedPreparationStage(84, 98);
        else if (lower.StartsWith("prepared ")) SetSharedPreparationStage(98, 100);
        SetNonTransferActivity(message);
        BuildProgress.IsIndeterminate = false;
        BuildProgress.Value = 0;
        AddActivity(message);
    }

    private void ClearQueueDriveProgress()
    {
        _queueDriveProgress.Clear();
        _queueDriveCompletion.Clear();
        foreach (var disk in _queueProgressDisks) disk.QueueState = QueueDriveProgressState.Pending;
        _queueProgressDisks.Clear();
        _activeQueueDriveIndex = -1;
        _activeQueueProcessStart = 0;
        _activeQueueProcessEnd = 0;
        RefreshQueueDriveCards();
        RenderQueueDriveProgress();
    }

    private void SetActiveQueueDrive(int index)
    {
        if (index < 0 || index >= _queueDriveProgress.Count) return;
        if (_activeQueueDriveIndex >= 0 && _activeQueueDriveIndex < _queueDriveProgress.Count &&
            _queueDriveProgress[_activeQueueDriveIndex] == QueueDriveProgressState.Active)
        {
            _queueDriveProgress[_activeQueueDriveIndex] = QueueDriveProgressState.Pending;
            _queueProgressDisks[_activeQueueDriveIndex].QueueState = QueueDriveProgressState.Pending;
        }
        _activeQueueDriveIndex = index;
        _activeQueueProcessStart = 0;
        _activeQueueProcessEnd = 0;
        _queueDriveProgress[index] = QueueDriveProgressState.Active;
        _queueProgressDisks[index].QueueState = QueueDriveProgressState.Active;
        RefreshQueueDriveCards();
        RenderQueueDriveProgress();
    }

    private void CompleteQueueDrive(int index)
    {
        if (index < 0 || index >= _queueDriveProgress.Count) return;
        SetQueueDriveCompletion(index, 100);
        _queueDriveProgress[index] = QueueDriveProgressState.Completed;
        _queueProgressDisks[index].QueueState = QueueDriveProgressState.Completed;
        if (_activeQueueDriveIndex == index) _activeQueueDriveIndex = -1;
        RefreshQueueDriveCards();
        RenderQueueDriveProgress();
    }

    private void FailQueueDrive(int index)
    {
        if (index < 0 || index >= _queueDriveProgress.Count) return;
        _queueDriveProgress[index] = QueueDriveProgressState.Failed;
        _queueProgressDisks[index].QueueState = QueueDriveProgressState.Failed;
        if (_activeQueueDriveIndex == index) _activeQueueDriveIndex = -1;
        RefreshQueueDriveCards();
        RenderQueueDriveProgress();
    }

    private void SetQueueDriveProcessRange(int index, double start, double end)
    {
        if (index < 0 || index >= _queueDriveCompletion.Count) return;
        _activeQueueDriveIndex = index;
        _activeQueueProcessStart = Math.Clamp(start, 0, 100);
        _activeQueueProcessEnd = Math.Clamp(end, _activeQueueProcessStart, 100);
        SetQueueDriveCompletion(index, _activeQueueProcessStart);
    }

    private void SetQueueDriveCompletion(int index, double value)
    {
        if (index < 0 || index >= _queueDriveCompletion.Count) return;
        var clamped = Math.Clamp(value, 0, 100);
        if (Math.Abs(_queueDriveCompletion[index] - clamped) < 0.25) return;
        _queueDriveCompletion[index] = clamped;
        RenderQueueDriveProgress();
    }

    private void BuildProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_trackingSharedPreparation)
        {
            var preparationFraction = Math.Clamp(e.NewValue / 100d, 0, 1);
            SetSharedPreparationProgress(_sharedPreparationStageStart +
                (_sharedPreparationStageEnd - _sharedPreparationStageStart) * preparationFraction);
            return;
        }
        if (_activeQueueDriveIndex < 0 || _activeQueueDriveIndex >= _queueDriveCompletion.Count ||
            _activeQueueProcessEnd <= _activeQueueProcessStart) return;
        var fraction = Math.Clamp(e.NewValue / 100d, 0, 1);
        SetQueueDriveCompletion(_activeQueueDriveIndex,
            _activeQueueProcessStart + (_activeQueueProcessEnd - _activeQueueProcessStart) * fraction);
    }

    private void RenderQueueDriveProgress()
    {
        if (QueueProgressSegments is null) return;
        QueueProgressSegments.Children.Clear();
        QueueProgressSegments.ColumnDefinitions.Clear();
        var selected = GetThemeBrush("DriveSelectedBorder", Color.FromRgb(32, 184, 106));
        var pending = GetThemeBrush("ThemeShellStroke", Color.FromRgb(217, 226, 223));
        var failed = GetThemeBrush("DriveFailedBackground", Color.FromRgb(199, 94, 99));
        for (var index = 0; index < _queueDriveProgress.Count; index++)
        {
            QueueProgressSegments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var state = _queueDriveProgress[index];
            Brush background = state switch
            {
                QueueDriveProgressState.Active => CreateStripedDriveBrush(selected.Color),
                QueueDriveProgressState.Completed => new SolidColorBrush(Darken(selected.Color, 0.58)),
                QueueDriveProgressState.Failed => failed,
                _ => pending
            };
            var status = state switch
            {
                QueueDriveProgressState.Active => "Writing",
                QueueDriveProgressState.Completed => "Completed",
                QueueDriveProgressState.Failed => "Failed",
                _ => "Waiting"
            };
            var completion = index < _queueDriveCompletion.Count ? _queueDriveCompletion[index] : 0;
            if (state is QueueDriveProgressState.Completed or QueueDriveProgressState.Failed) completion = 100;
            var track = new Border
            {
                Background = pending,
                CornerRadius = new CornerRadius(4),
                Margin = index == 0 ? new Thickness(0) : new Thickness(2, 0, 0, 0),
                ToolTip = $"Drive {index + 1}: {status}"
            };
            var fill = new Border { Background = background, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
            track.Child = fill;
            track.SizeChanged += (_, _) => fill.Width = Math.Max(0, track.ActualWidth * completion / 100d);
            track.Loaded += (_, _) => fill.Width = Math.Max(0, track.ActualWidth * completion / 100d);
            Grid.SetColumn(track, index);
            QueueProgressSegments.Children.Add(track);
        }
    }

    private void RefreshQueueDriveCards() => DiskPicker?.Items.Refresh();

    private SolidColorBrush GetThemeBrush(string resourceKey, Color fallback) =>
        TryFindResource(resourceKey) as SolidColorBrush ?? new SolidColorBrush(fallback);

    private static Color Darken(Color color, double factor) => Color.FromRgb(
        (byte)Math.Clamp((int)Math.Round(color.R * factor), 0, 255),
        (byte)Math.Clamp((int)Math.Round(color.G * factor), 0, 255),
        (byte)Math.Clamp((int)Math.Round(color.B * factor), 0, 255));

    private static Brush CreateStripedDriveBrush(Color green)
    {
        var dark = Darken(green, 0.58);
        var brush = new LinearGradientBrush(
            [new GradientStop(green, 0), new GradientStop(green, 0.48), new GradientStop(dark, 0.49),
             new GradientStop(dark, 0.72), new GradientStop(green, 0.73), new GradientStop(green, 1)],
            new Point(0, 0), new Point(0.12, 0.12)) { SpreadMethod = GradientSpreadMethod.Repeat };
        var movement = new TranslateTransform();
        brush.RelativeTransform = movement;
        movement.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 0.12,
            Duration = TimeSpan.FromMilliseconds(850),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        });
        return brush;
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

    private void UpdateDismActivity(string text, double? percent)
    {
        SetNonTransferActivity(text);
        BuildProgress.IsIndeterminate = false;
        if (percent.HasValue)
            BuildProgress.Value = Math.Clamp(percent.Value, 0, 100);
        else
            BuildProgress.Value = 0;
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

    private async Task<string> RunPowerShellAsync(string script, CancellationToken? cancellationToken = null)
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
        var effectiveCancellation = cancellationToken ?? _buildCancellation?.Token ?? CancellationToken.None;
        try { await process.WaitForExitAsync(effectiveCancellation); }
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
        var canCancel = building || _isPreflighting;
        CancelBuildButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        CancelBuildButton.IsEnabled = canCancel && !(_buildCancellation?.IsCancellationRequested ?? false);
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
        var canCancel = preflighting || _isBuilding;
        CancelBuildButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        CancelBuildButton.IsEnabled = canCancel && !(_buildCancellation?.IsCancellationRequested ?? false);
        if (preflighting)
        {
            SetStatus("Preparing build", "#B36A13");
            BuildProgress.IsIndeterminate = false;
            BuildProgress.Value = 0;
            ClearQueueDriveProgress();
            CurrentEtaText.Text = "Current activity: Checking targets, sources, and ISO media...";
            QueueEtaText.Visibility = Visibility.Collapsed;
            AddActivity("Preparing build: checking targets, sources, and ISO media before erasure.");
        }
        else if (!_isBuilding)
        {
            BuildProgress.IsIndeterminate = false;
            BuildProgress.Value = 0;
            if (_queueDriveProgress.Count == 0) ClearQueueDriveProgress();
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
    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateWindowStateVisuals()
    {
        if (MaximizeRestoreButton is null || MainShell is null) return;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeRestoreButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeRestoreButton.ToolTip = maximized ? "Restore" : "Maximize";
        MainShell.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(28);
    }
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
        if (!partition.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            await AddPartitionContentSourcesAsync(partition, this);
            return;
        }
        var dialog = new PartitionContentDialog(partition, _preferences.Theme) { Owner = this };
        dialog.ActionHandler = async (action, owner) => await HandlePartitionContentActionAsync(partition, action, owner);
        dialog.ShowDialog();
        await RefreshPartitionContentSizeAsync(partition, true);
        PartitionConfigurationChanged();
    }

    private async Task HandlePartitionContentActionAsync(PartitionConfig partition, PartitionContentAction action, Window owner)
    {
        switch (action)
        {
            case PartitionContentAction.Files:
            case PartitionContentAction.Folder:
                await AddPartitionContentSourcesAsync(partition, owner);
                break;
            case PartitionContentAction.Autounattend: await AddPartitionAutounattendAsync(partition, owner); break;
            case PartitionContentAction.Iso: await AddPartitionIsoAsync(partition, owner); break;
            case PartitionContentAction.ScriptFiles: await AddPartitionScriptFilesAsync(partition, owner); break;
            case PartitionContentAction.Drivers: await AddPartitionDriversAsync(partition, owner); break;
        }
    }

    private async Task AddPartitionContentSourcesAsync(PartitionConfig partition, Window owner)
    {
        var dialog = new ContentSourcesDialog(partition.Name, partition.SourceFiles, partition.SourceFolders, _preferences.Theme) { Owner = owner };
        if (dialog.ShowDialog() != true) return;
        partition.SourceFiles.Clear();
        foreach (var path in dialog.SourceFiles) partition.SourceFiles.Add(path);
        partition.SourceFolders.Clear();
        foreach (var path in dialog.SourceFolders) partition.SourceFolders.Add(path);
        await RefreshPartitionContentSizeAsync(partition, true);
        UpdateBuildButton();
    }

    private async Task AddPartitionAutounattendAsync(PartitionConfig partition, Window owner)
    {
        if (partition.FileSystem != "NTFS" && !partition.HasIso) return;
        var dialog = new OpenFileDialog { Title = $"Select Autounattend.xml for {partition.Name}", Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*", CheckFileExists = true, Multiselect = false, InitialDirectory = PickerLocationStore.Get("XML") };
        if (dialog.ShowDialog(owner) != true) return;
        PickerLocationStore.Set("XML", Path.GetDirectoryName(dialog.FileName));
        partition.AutounattendSource = dialog.FileName;
        partition.GenerateAutounattend = false;
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
        partition.SourceFiles.Clear(); partition.SourceFolders.Clear(); partition.AutounattendSource = null; partition.GenerateAutounattend = false; partition.ClearIsoSelection(); partition.SelectedContentBytes = 0; partition.LargestSelectedFileBytes = 0; MainPartitionList.Items.Refresh(); UpdatePartitionPreview(SelectedDisks()); UpdateBuildButton();
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
    public QueueDriveProgressState QueueState { get; set; }
    public string DiskTitle => string.IsNullOrWhiteSpace(DriveLetters) ? $"Disk {Number}" : $"Disk {Number}  |  {DriveLetters}";
    public string SizeDisplay => $"{Size / (1024d * 1024 * 1024):N2} GB";
    public string Display => $"Disk {Number}  |  {FriendlyName}  |  {Size / (1024d * 1024 * 1024):N2} GB";
}

public sealed class PartitionResult
{
    public int DiskNumber { get; set; }
    public List<string> Letters { get; set; } = [];
}

public enum QueueDriveProgressState
{
    Pending,
    Active,
    Completed,
    Failed
}
