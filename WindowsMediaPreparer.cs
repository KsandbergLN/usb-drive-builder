using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LaptopQaUsbBuilder;

public sealed class WindowsMediaPreparer
{
    private readonly Action<string> _activity;
    private readonly Action<string> _log;
    private readonly Func<string, Task<string>> _mountIso;
    private readonly Func<string, Task> _dismountIso;
    private readonly string _cacheRoot;

    public WindowsMediaPreparer(Action<string> activity, Action<string> log,
        Func<string, Task<string>> mountIso, Func<string, Task> dismountIso, string cacheRoot)
    {
        _activity = activity;
        _log = log;
        _mountIso = mountIso;
        _dismountIso = dismountIso;
        _cacheRoot = cacheRoot;
    }

    public async Task<PreparedWindowsMedia> PrepareAsync(string isoPath, BootableIsoInfo info, WindowsIsoSelection selection)
    {
        if (selection.AddDrivers)
        {
            foreach (var folder in selection.DriverFolders)
                if (!Directory.Exists(folder)) throw new InvalidOperationException($"A selected drivers folder is no longer available: {folder}");
            foreach (var file in selection.DriverFiles)
                if (!File.Exists(file)) throw new InvalidOperationException($"A selected INF driver is no longer available: {file}");
            foreach (var archive in selection.DriverArchives)
                if (!File.Exists(archive)) throw new InvalidOperationException($"A selected compressed driver pack is no longer available: {archive}");
            selection = await ResolveDriverPacksAsync(selection);
            selection = await ResolveCompressedDriverPayloadsAsync(selection);
            ValidateDriverPackages(selection);
        }
        var cacheKey = await CreateCacheKeyAsync(isoPath, selection);
        var mediaCacheRoot = Path.Combine(_cacheRoot, "MediaCache", cacheKey);
        var cachedMedia = Path.Combine(mediaCacheRoot, "media");
        var completeMarker = Path.Combine(mediaCacheRoot, ".complete");
        var rejectionReport = Path.Combine(mediaCacheRoot, "driver-rejections.json");
        if (File.Exists(completeMarker) && IsCompleteWindowsMedia(cachedMedia))
        {
            _activity($"Using cached {selection.EditionName} Windows media.");
            _log($"Windows media cache hit: {cacheKey}");
            return new PreparedWindowsMedia(cachedMedia, CalculateDirectoryBytes(cachedMedia), true, cacheKey,
                LoadDriverRejections(rejectionReport));
        }

        var stagingParent = Path.Combine(_cacheRoot, "Staging");
        Directory.CreateDirectory(stagingParent);
        EnsureStagingSpace(stagingParent, info, selection);
        var stagingRoot = Path.Combine(stagingParent, Guid.NewGuid().ToString("N"));
        var mediaRoot = Path.Combine(stagingRoot, "media");
        Directory.CreateDirectory(mediaRoot);

        try
        {
            _activity("Preparing Windows media on local storage...");
            _log($"Windows media cache miss: {cacheKey}. Staging {Path.GetFileName(isoPath)} locally.");
            var driveLetter = await _mountIso(isoPath);
            try
            {
                await CopyDirectoryAsync($"{driveLetter}:\\", mediaRoot);
            }
            finally
            {
                await _dismountIso(isoPath);
            }

            var sources = Path.Combine(mediaRoot, "sources");
            var sourceInstallImage = Path.Combine(sources, info.InstallImageName);
            var selectedInstallImage = Path.Combine(sources, "install.selected.wim");
            MakeWritable(sourceInstallImage);
            _activity($"Exporting {selection.EditionName} with maximum WIM compression...");
            await RunDismAsync($"/Export-Image /SourceImageFile:\"{sourceInstallImage}\" /SourceIndex:{selection.EditionIndex} /DestinationImageFile:\"{selectedInstallImage}\" /Compress:max");
            File.Delete(sourceInstallImage);
            var finalInstallImage = Path.Combine(sources, "install.wim");
            if (!sourceInstallImage.Equals(finalInstallImage, StringComparison.OrdinalIgnoreCase) && File.Exists(finalInstallImage)) File.Delete(finalInstallImage);
            File.Move(selectedInstallImage, finalInstallImage);

            var driverRejections = new List<DriverInjectionRejection>();
            if (selection.AddDrivers)
            {
                var rejected = await ServiceImageAsync(finalInstallImage, 1, selection, stagingRoot, selection.EditionName);
                driverRejections.AddRange(rejected.Select(path => new DriverInjectionRejection($"Installed Windows ({selection.EditionName})", path)));
            }

            if (!IsCompleteWindowsMedia(mediaRoot))
                throw new InvalidOperationException("Prepared Windows media verification failed before caching.");

            DeleteDirectoryTree(mediaCacheRoot);
            Directory.CreateDirectory(mediaCacheRoot);
            if (!await TryMoveCompletedCacheAsync(mediaRoot, cachedMedia, "Windows media"))
                throw new IOException("Windows media preparation completed, but its cache directory remained locked after several retries.");
            File.WriteAllText(completeMarker,
                $"Edition={selection.EditionName}{Environment.NewLine}Created={DateTimeOffset.Now:O}{Environment.NewLine}DriverFolders={selection.DriverFolders.Count}{Environment.NewLine}DriverFiles={selection.DriverFiles.Count}{Environment.NewLine}DriverPacks={selection.DriverArchives.Count}{Environment.NewLine}ForceUnsigned={selection.ForceUnsigned}");
            File.WriteAllText(rejectionReport, System.Text.Json.JsonSerializer.Serialize(driverRejections));
            var totalBytes = CalculateDirectoryBytes(cachedMedia);
            _activity($"Prepared {selection.EditionName} media cached for reuse.");
            _log($"Windows media cache created: {cacheKey}, {totalBytes} bytes.");
            return new PreparedWindowsMedia(cachedMedia, totalBytes, false, cacheKey, driverRejections);
        }
        catch
        {
            await DiscardOwnMountsAsync(stagingRoot);
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private async Task<List<string>> ServiceImageAsync(string imagePath, int index, WindowsIsoSelection selection,
        string stagingRoot, string displayName)
    {
        var safeName = displayName.Replace(' ', '-');
        var mountPath = Path.Combine(stagingRoot, $"mount-{safeName}-{index}");
        var scratchPath = Path.Combine(stagingRoot, $"scratch-{safeName}-{index}");
        Directory.CreateDirectory(mountPath);
        Directory.CreateDirectory(scratchPath);
        var mounted = false;
        var rejectedDrivers = new List<string>();
        try
        {
            _activity($"Mounting {displayName} for driver injection...");
            await RunDismAsync($"/Mount-Image /ImageFile:\"{imagePath}\" /Index:{index} /MountDir:\"{mountPath}\" /Optimize /ScratchDir:\"{scratchPath}\"");
            mounted = true;
            _activity($"Injecting drivers into {displayName}...");
            _log($"Injecting drivers into {Path.GetFileName(imagePath)} index {index}; ForceUnsigned={selection.ForceUnsigned}.");
            var force = selection.ForceUnsigned ? " /ForceUnsigned" : "";
            foreach (var folder in selection.DriverFolders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var driverFiles = Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).ToArray();
                var driverPackageCount = driverFiles.Length;
                _activity($"Injecting driver folder {Path.GetFileName(folder)} ({driverPackageCount} package(s))...");
                var outcome = await RunDismAsync($"/Image:\"{mountPath}\" /Add-Driver /Driver:\"{folder}\" /Recurse{force} /ScratchDir:\"{scratchPath}\"",
                    driverPackageCount);
                rejectedDrivers.AddRange(outcome.RejectedDrivers);
                if (outcome.RequiresIndividualRetry)
                {
                    _activity($"Retrying {driverPackageCount} package(s) from {Path.GetFileName(folder)} individually...");
                    _log($"The recursive DISM batch for '{folder}' stopped before confirming a valid package. Retrying each INF so invalid packages cannot stop the remaining drivers.");
                    foreach (var driverFile in driverFiles)
                    {
                        var retryOutcome = await RunDismAsync($"/Image:\"{mountPath}\" /Add-Driver /Driver:\"{driverFile}\"{force} /ScratchDir:\"{scratchPath}\"",
                            1, allowAllRejectedDrivers: true);
                        rejectedDrivers.AddRange(retryOutcome.RejectedDrivers);
                    }
                }
            }
            foreach (var file in selection.DriverFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _activity($"Injecting individual driver {Path.GetFileName(file)}...");
                var outcome = await RunDismAsync($"/Image:\"{mountPath}\" /Add-Driver /Driver:\"{file}\"{force} /ScratchDir:\"{scratchPath}\"",
                    1, allowAllRejectedDrivers: true);
                rejectedDrivers.AddRange(outcome.RejectedDrivers);
            }
            _activity($"Committing {displayName} driver changes...");
            await RunDismAsync($"/Unmount-Image /MountDir:\"{mountPath}\" /Commit /ScratchDir:\"{scratchPath}\"");
            mounted = false;
        }
        finally
        {
            if (mounted)
            {
                try { await RunDismAsync($"/Unmount-Image /MountDir:\"{mountPath}\" /Discard"); }
                catch (Exception cleanupError) { _log($"DISM cleanup warning: {LogSanitizer.SanitizeException(cleanupError)}"); }
            }
            TryDeleteDirectory(mountPath);
            TryDeleteDirectory(scratchPath);
        }
        return rejectedDrivers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<DismRunOutcome> RunDismAsync(string arguments, int? driverPackageCount = null, bool allowAllRejectedDrivers = false)
    {
        _log($"DISM {arguments}");
        var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaptopQAUsbBuilder", "Logs");
        Directory.CreateDirectory(logFolder);
        var dismLog = Path.Combine(logFolder, "DISM-servicing.log");
        var logStart = File.Exists(dismLog) ? new FileInfo(dismLog).Length : 0;
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "dism.exe"),
            Arguments = $"/English /LogPath:\"{dismLog}\" /LogLevel:2 {arguments}", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start DISM.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode == 0) return DismRunOutcome.Success;
        var logDelta = ReadLogFromOffset(dismLog, logStart);
        var rejectedDriverDetails = ExtractRejectedDrivers(logDelta);
        var rejectedDrivers = rejectedDriverDetails.Select(detail => detail.DriverPath).ToList();
        foreach (var detail in rejectedDriverDetails)
        {
            var codes = detail.ErrorCodes.Count > 0 ? string.Join(", ", detail.ErrorCodes) : $"DISM exit code {process.ExitCode}";
            _log($"Driver package failed to install [{codes}]: {detail.DriverPath}");
        }
        var installedAny = output.Contains("successfully installed", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("already installed", StringComparison.OrdinalIgnoreCase);
        var skippableDriverPackageError = process.ExitCode == 13;
        if (driverPackageCount is not null && skippableDriverPackageError && rejectedDrivers.Count > 0)
        {
            const string reason = "invalid package data (0x8007000D)";
            _activity($"Skipped {rejectedDrivers.Count} driver package(s) with {reason} and continued servicing.");
            _log($"DISM driver injection returned exit code {process.ExitCode} for package-scoped {reason}. Continuing without a blocking prompt. AcceptedAnyReported={installedAny}; AllowAllRejected={allowAllRejectedDrivers}. Complete rejected package count: {rejectedDrivers.Count}.");
            return new DismRunOutcome(rejectedDrivers);
        }
        var lines = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Concat(output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        var completeListNote = rejectedDrivers.Count > 1
            ? $" {rejectedDrivers.Count} driver packages failed; the complete list was written to the build log."
            : "";
        var reportedCodes = rejectedDriverDetails.SelectMany(detail => detail.ErrorCodes)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var diskFull = process.ExitCode == 112 || reportedCodes.Contains("0x80070070", StringComparer.OrdinalIgnoreCase) ||
                       output.Contains("not enough space", StringComparison.OrdinalIgnoreCase) ||
                       error.Contains("not enough space", StringComparison.OrdinalIgnoreCase);
        var useful = diskFull
            ? $"DISM ran out of local system-drive space while servicing Windows (0x80070070). The {rejectedDrivers.Count} package(s) listed in the log were active when space ran out and are not necessarily invalid. Free space in %LOCALAPPDATA%\\LaptopQAUsbBuilder or elsewhere on the system drive, then try again."
            : rejectedDrivers.Count > 0 && process.ExitCode is 2 or 3
            ? $"DISM could not import driver package '{rejectedDrivers[0]}' because a required package file or path is missing (0x8007000{process.ExitCode}).{completeListNote} Re-extract the complete driver package and try again."
            : rejectedDrivers.Count > 0
            ? $"DISM rejected driver package '{rejectedDrivers[0]}'. The package contains invalid or incompatible driver data.{completeListNote}"
            : lines.LastOrDefault(line => line.Contains("Error:", StringComparison.OrdinalIgnoreCase))
                     ?? lines.LastOrDefault(line => !line.Contains("DISM log file", StringComparison.OrdinalIgnoreCase))
                     ?? $"DISM failed with exit code {process.ExitCode}.";
        throw new InvalidOperationException(useful);
    }

    private static IReadOnlyList<DriverInjectionRejection> LoadDriverRejections(string path)
    {
        try
        {
            return File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize<List<DriverInjectionRejection>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private static string ReadLogFromOffset(string path, long offset)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    private static List<RejectedDriverDetail> ExtractRejectedDrivers(string logText)
    {
        var details = new List<RejectedDriverDetail>();
        foreach (var line in logText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("Failed to import driver package", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("Failed to install the driver package", StringComparison.OrdinalIgnoreCase)) continue;
            var firstQuote = line.IndexOf('\'');
            var secondQuote = firstQuote < 0 ? -1 : line.IndexOf('\'', firstQuote + 1);
            if (firstQuote < 0 || secondQuote <= firstQuote + 1) continue;
            var path = line[(firstQuote + 1)..secondQuote];
            var errorCode = ExtractDismErrorCode(line);
            var existing = details.FirstOrDefault(detail => detail.DriverPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                details.Add(new RejectedDriverDetail(path, errorCode is null ? [] : [errorCode]));
            }
            else if (errorCode is not null && !existing.ErrorCodes.Contains(errorCode, StringComparer.OrdinalIgnoreCase))
            {
                var index = details.IndexOf(existing);
                details[index] = existing with { ErrorCodes = existing.ErrorCodes.Concat([errorCode]).ToArray() };
            }
        }
        return details;
    }

    private static string? ExtractDismErrorCode(string line)
    {
        var marker = line.LastIndexOf("hr:0x", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var start = marker + 3;
        var end = start;
        while (end < line.Length && (char.IsAsciiHexDigit(line[end]) || line[end] is 'x' or 'X')) end++;
        return end > start ? line[start..end] : null;
    }

    private async Task<WindowsIsoSelection> ResolveDriverPacksAsync(WindowsIsoSelection selection)
    {
        if (selection.DriverArchives.Count == 0) return selection;

        var extractedFolders = new List<string>();
        foreach (var archive in selection.DriverArchives.Distinct(StringComparer.OrdinalIgnoreCase))
            extractedFolders.Add(await ExtractDriverPackAsync(archive));

        return selection with
        {
            DriverFolders = selection.DriverFolders.Concat(extractedFolders)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private async Task<string> ExtractDriverPackAsync(string archivePath)
    {
        var extension = Path.GetExtension(archivePath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".cab", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Compressed driver packs must be ZIP or CAB files: {archivePath}");

        _activity($"Checking compressed driver pack {Path.GetFileName(archivePath)}...");
        var archiveHash = (await HashFileAsync(archivePath)).ToLowerInvariant();
        var cacheParent = Path.Combine(_cacheRoot, "DriverPackCache");
        Directory.CreateDirectory(cacheParent);
        var cacheRoot = Path.Combine(cacheParent, archiveHash);
        var completeMarker = Path.Combine(cacheRoot, ".complete");
        if (File.Exists(completeMarker) && Directory.Exists(cacheRoot) &&
            Directory.EnumerateFiles(cacheRoot, "*.inf", SearchOption.AllDirectories).Any())
        {
            _log($"Compressed driver pack cache hit: {Path.GetFileName(archivePath)} ({archiveHash}).");
            return cacheRoot;
        }

        var temporaryRoot = Path.Combine(cacheParent, $"extract-{archiveHash}-{Guid.NewGuid():N}");
        var retainTemporaryRoot = false;
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            _activity($"Extracting compressed driver pack {Path.GetFileName(archivePath)}...");
            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => ExtractZipSafely(archivePath, temporaryRoot));
            else
                await ExtractCabAsync(archivePath, temporaryRoot);

            var infCount = Directory.EnumerateFiles(temporaryRoot, "*.inf", SearchOption.AllDirectories).Count();
            if (infCount == 0)
                throw new InvalidOperationException($"Compressed driver pack '{Path.GetFileName(archivePath)}' does not contain any INF driver packages.");

            File.WriteAllText(Path.Combine(temporaryRoot, ".complete"),
                $"Source={Path.GetFileName(archivePath)}{Environment.NewLine}SHA256={archiveHash}{Environment.NewLine}INF={infCount}{Environment.NewLine}Extracted={DateTimeOffset.Now:O}");
            if (!await TryMoveCompletedCacheAsync(temporaryRoot, cacheRoot, "compressed driver pack"))
            {
                retainTemporaryRoot = true;
                _log($"The completed driver-pack cache directory remained temporarily locked. Continuing from its completed working directory: {temporaryRoot}");
                return temporaryRoot;
            }
            _log($"Extracted compressed driver pack {Path.GetFileName(archivePath)} to cache; {infCount} INF package(s), SHA256 {archiveHash}.");
            return cacheRoot;
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"Compressed driver pack '{Path.GetFileName(archivePath)}' is damaged, unsupported, or password protected. {ex.Message}", ex);
        }
        finally
        {
            if (!retainTemporaryRoot) TryDeleteDirectory(temporaryRoot);
        }
    }

    private static void ExtractZipSafely(string archivePath, string destinationRoot)
    {
        var safeRoot = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(destinationRoot,
                entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The archive contains an unsafe path: {entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
    }

    private static async Task ExtractCabAsync(string archivePath, string destinationRoot)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "expand.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(archivePath);
        start.ArgumentList.Add("-F:*");
        start.ArgumentList.Add(destinationRoot);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Windows CAB extraction tool.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidDataException($"Windows CAB extraction returned exit code {process.ExitCode}. {detail.Trim()}");
        }
    }

    private async Task<WindowsIsoSelection> ResolveCompressedDriverPayloadsAsync(WindowsIsoSelection selection)
    {
        var resolvedRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        async Task<string> ResolveRootAsync(string root)
        {
            if (resolvedRoots.TryGetValue(root, out var existing)) return existing;
            var resolved = await ExpandCompressedDriverRootAsync(root);
            resolvedRoots[root] = resolved;
            return resolved;
        }

        var folders = new List<string>();
        foreach (var folder in selection.DriverFolders.Distinct(StringComparer.OrdinalIgnoreCase))
            folders.Add(await ResolveRootAsync(folder));

        var files = new List<string>();
        foreach (var file in selection.DriverFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(file) ?? throw new InvalidOperationException($"The selected INF path is invalid: {file}");
            var resolvedParent = await ResolveRootAsync(parent);
            files.Add(Path.Combine(resolvedParent, Path.GetRelativePath(parent, file)));
        }

        return selection with { DriverFolders = folders, DriverFiles = files };
    }

    private async Task<string> ExpandCompressedDriverRootAsync(string sourceRoot)
    {
        var safeRoot = Path.GetFullPath(sourceRoot) + Path.DirectorySeparatorChar;
        var expansions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var infPath in Directory.EnumerateFiles(sourceRoot, "*.inf", SearchOption.AllDirectories))
        {
            var packageRoot = Path.GetDirectoryName(infPath) ?? sourceRoot;
            foreach (var missingRelativePath in FindMissingDriverFiles(infPath).MissingFiles)
            {
                var expectedPath = Path.GetFullPath(Path.Combine(packageRoot, missingRelativePath));
                if (!expectedPath.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) continue;
                var compressedPath = GetLegacyCompressedPath(expectedPath);
                if (compressedPath is not null && File.Exists(compressedPath))
                    expansions.TryAdd(expectedPath, compressedPath);
            }
        }

        if (expansions.Count == 0) return sourceRoot;

        _activity($"Expanding {expansions.Count} compressed driver payload file(s) from {Path.GetFileName(sourceRoot)}...");
        var sourceManifest = CreateDriverManifest(sourceRoot).ToLowerInvariant();
        var cacheParent = Path.Combine(_cacheRoot, "DriverPayloadCache");
        Directory.CreateDirectory(cacheParent);
        var cacheRoot = Path.Combine(cacheParent, sourceManifest);
        var completeMarker = Path.Combine(cacheRoot, ".complete");
        if (File.Exists(completeMarker) && expansions.Keys.All(expected =>
                File.Exists(Path.Combine(cacheRoot, Path.GetRelativePath(sourceRoot, expected)))))
        {
            _log($"Expanded driver payload cache hit for {sourceRoot} ({sourceManifest}).");
            return cacheRoot;
        }

        var temporaryRoot = Path.Combine(cacheParent, $"expand-{sourceManifest}-{Guid.NewGuid():N}");
        var retainTemporaryRoot = false;
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await CopyDirectoryAsync(sourceRoot, temporaryRoot);
            foreach (var pair in expansions)
            {
                var stagedExpected = Path.Combine(temporaryRoot, Path.GetRelativePath(sourceRoot, pair.Key));
                var stagedCompressed = Path.Combine(temporaryRoot, Path.GetRelativePath(sourceRoot, pair.Value));
                Directory.CreateDirectory(Path.GetDirectoryName(stagedExpected)!);
                // Some vendor packages ship both the normal payload and a legacy
                // compressed companion. Prefer the complete normal file; only
                // expand when the expected payload is genuinely absent.
                if (File.Exists(stagedExpected))
                {
                    _log($"Using existing driver payload {Path.GetFileName(stagedExpected)}; compressed companion {Path.GetFileName(pair.Value)} was not expanded.");
                    continue;
                }
                await ExpandCompressedFileAsync(stagedCompressed, stagedExpected);
                if (!File.Exists(stagedExpected))
                    throw new InvalidDataException($"Windows did not produce the expected driver payload '{Path.GetFileName(stagedExpected)}'.");
                _log($"Expanded compressed driver payload {Path.GetFileName(pair.Value)} as {Path.GetFileName(pair.Key)}.");
            }

            File.WriteAllText(Path.Combine(temporaryRoot, ".complete"),
                $"Source={sourceRoot}{Environment.NewLine}Manifest={sourceManifest}{Environment.NewLine}ExpandedFiles={expansions.Count}{Environment.NewLine}Created={DateTimeOffset.Now:O}");
            if (!await TryMoveCompletedCacheAsync(temporaryRoot, cacheRoot, "expanded driver payload"))
            {
                retainTemporaryRoot = true;
                _log($"The completed expanded-driver cache directory remained temporarily locked. Continuing from its completed working directory: {temporaryRoot}");
                return temporaryRoot;
            }
            return cacheRoot;
        }
        finally
        {
            if (!retainTemporaryRoot) TryDeleteDirectory(temporaryRoot);
        }
    }

    private async Task<bool> TryMoveCompletedCacheAsync(string source, string destination, string description)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                DeleteDirectoryTree(destination);
                Directory.Move(source, destination);
                if (attempt > 1) _log($"Published {description} cache after {attempt} attempts.");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                _log($"{description} cache publish attempt {attempt}/6 was blocked: {LogSanitizer.SanitizeException(ex)}");
                if (attempt < 6) await Task.Delay(attempt * 350);
            }
        }
        _log($"Could not publish {description} cache after retries: {LogSanitizer.SanitizeException(lastError!)}");
        return false;
    }

    private static string? GetLegacyCompressedPath(string expectedPath)
    {
        var fileName = Path.GetFileName(expectedPath);
        if (fileName.Length < 2 || fileName.EndsWith('_')) return null;
        return Path.Combine(Path.GetDirectoryName(expectedPath) ?? "", fileName[..^1] + "_");
    }

    private static async Task ExpandCompressedFileAsync(string compressedPath, string destinationPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "expand.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(compressedPath);
        start.ArgumentList.Add(destinationPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Windows compressed-file expansion tool.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidDataException($"Windows could not expand '{Path.GetFileName(compressedPath)}' (exit code {process.ExitCode}). {detail.Trim()}");
        }
    }

    private void ValidateDriverPackages(WindowsIsoSelection selection)
    {
        _activity("Checking driver packages for missing required files...");
        var infFiles = selection.DriverFolders
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories))
            .Concat(selection.DriverFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var incomplete = infFiles.Select(FindMissingDriverFiles)
            .Where(result => result.MissingFiles.Count > 0)
            .ToArray();
        if (incomplete.Length == 0)
        {
            _log($"Driver package preflight passed for {infFiles.Length} INF package(s).");
            return;
        }

        foreach (var package in incomplete)
            _log($"Incomplete driver package: {package.InfPath}. Missing required file(s): {string.Join("; ", package.MissingFiles)}");
        var first = incomplete[0];
        var displayedFiles = first.MissingFiles.Take(12).Select(path => $"• {path}").ToList();
        if (first.MissingFiles.Count > displayedFiles.Count)
            displayedFiles.Add($"• …and {first.MissingFiles.Count - displayedFiles.Count} more file(s) from this package");
        var additional = incomplete.Length > 1
            ? $"\n\n{incomplete.Length - 1} additional incomplete driver package(s) are listed in the build log."
            : "";
        throw new IncompleteDriverPackageException(
            $"Driver package preflight found missing required files. Windows cannot import this package.\n\nINF:\n{first.InfPath}\n\nMissing:\n{string.Join("\n", displayedFiles)}{additional}\n\nRe-extract the complete driver package and try again.");
    }

    private static IncompleteDriverPackage FindMissingDriverFiles(string infPath)
    {
        var packageRoot = Path.GetDirectoryName(infPath) ?? "";
        var diskPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var requiredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(infPath);
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var section = "";

        foreach (var rawLine in lines)
        {
            var line = StripInfComment(rawLine).Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (!sections.ContainsKey(section)) sections[section] = [];
                continue;
            }
            if (section.Length > 0 && line.Length > 0) sections[section].Add(line);
        }

        foreach (var sourceNamesSection in sections.Where(item => IsApplicableInfSection(item.Key, "SourceDisksNames")))
        {
            foreach (var line in sourceNamesSection.Value)
            {
                if (!TrySplitInfEntry(line, out var diskId, out var value)) continue;
                var fields = value.Split(',');
                if (fields.Length > 3)
                {
                    var diskPath = CleanInfValue(fields[3]).TrimStart('\\');
                    if (!diskPath.Contains('%')) diskPaths[diskId] = diskPath;
                }
            }
        }

        var sourceFiles = new Dictionary<string, (string Path, int Score)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceFilesSection in sections.Where(item => IsApplicableInfSection(item.Key, "SourceDisksFiles")))
        {
            var score = InfDecorationScore(sourceFilesSection.Key);
            foreach (var line in sourceFilesSection.Value)
            {
                if (!TrySplitInfEntry(line, out var fileName, out var value)) continue;
                fileName = CleanInfValue(fileName);
                if (fileName.Length == 0 || fileName.Contains('%')) continue;
                var fields = value.Split(',');
                var diskId = fields.Length > 0 ? CleanInfValue(fields[0]) : "";
                var relativeParts = new List<string>();
                if (diskPaths.TryGetValue(diskId, out var diskPath) && diskPath.Length > 0) relativeParts.Add(diskPath);
                if (fields.Length > 1)
                {
                    var subdirectory = CleanInfValue(fields[1]).TrimStart('\\');
                    if (subdirectory.Length > 0 && !subdirectory.Contains('%')) relativeParts.Add(subdirectory);
                }
                relativeParts.Add(fileName);
                var path = relativeParts.Aggregate(packageRoot, Path.Combine);
                if (!sourceFiles.TryGetValue(fileName, out var existing) || score >= existing.Score)
                    sourceFiles[fileName] = (path, score);
            }
        }

        var catalogEntries = sections.TryGetValue("Version", out var versionLines)
            ? versionLines.Select(line => TrySplitInfEntry(line, out var name, out var value) ? (Name: name, Value: value) : default)
                .Where(item => item.Name is not null && item.Name.StartsWith("CatalogFile", StringComparison.OrdinalIgnoreCase) && IsApplicableCatalogEntry(item.Name))
                .ToArray()
            : [];
        var bestCatalogScore = catalogEntries.Length == 0 ? -1 : catalogEntries.Max(item => CatalogDecorationScore(item.Name));
        foreach (var catalogEntry in catalogEntries.Where(item => CatalogDecorationScore(item.Name) == bestCatalogScore))
        {
            var catalog = CleanInfValue(catalogEntry.Value.Split(',')[0]);
            if (!catalog.Contains('%')) requiredPaths.Add(Path.Combine(packageRoot, catalog));
        }

        var referencedCopySections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directlyReferencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidateSection in sections.Where(item => IsApplicableX64Section(item.Key, sections)))
        {
            foreach (var line in candidateSection.Value)
            {
                if (!TrySplitInfEntry(line, out var name, out var value)) continue;
                if (name.Equals("ServiceBinary", StringComparison.OrdinalIgnoreCase))
                {
                    var binary = CleanInfValue(value.Split(',')[0]).Replace("%12%", "", StringComparison.OrdinalIgnoreCase).TrimStart('\\');
                    if (!binary.Contains('%') && binary.Length > 0) directlyReferencedFiles.Add(binary);
                }
                else if (name.Equals("CopyFiles", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var reference in value.Split(',').Select(CleanInfValue).Where(item => item.Length > 0))
                    {
                        if (reference.StartsWith('@')) directlyReferencedFiles.Add(reference[1..]);
                        else referencedCopySections.Add(reference);
                    }
                }
            }
        }

        foreach (var copySectionName in referencedCopySections)
        {
            if (!sections.TryGetValue(copySectionName, out var copyLines)) continue;
            foreach (var line in copyLines)
            {
                var fields = line.Split(',');
                var destinationName = CleanInfValue(fields[0]);
                var sourceName = fields.Length > 1 && CleanInfValue(fields[1]).Length > 0
                    ? CleanInfValue(fields[1]) : destinationName;
                if (sourceName.Length > 0 && !sourceName.Contains('%')) directlyReferencedFiles.Add(sourceName);
            }
        }

        foreach (var reference in directlyReferencedFiles)
        {
            var lookupName = Path.GetFileName(reference);
            if (sourceFiles.TryGetValue(lookupName, out var source)) requiredPaths.Add(source.Path);
        }

        var missing = requiredPaths.Where(path => !File.Exists(path))
            .Select(path => Path.GetRelativePath(packageRoot, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new IncompleteDriverPackage(infPath, missing);
    }

    private static bool IsApplicableSectionName(string section) =>
        !section.Contains("x86", StringComparison.OrdinalIgnoreCase) &&
        !section.Contains("arm", StringComparison.OrdinalIgnoreCase) &&
        !section.Contains("ia64", StringComparison.OrdinalIgnoreCase);

    private static bool IsApplicableX64Section(string section,
        IReadOnlyDictionary<string, List<string>> sections)
    {
        if (!IsApplicableSectionName(section)) return false;
        if (section.Contains("amd64", StringComparison.OrdinalIgnoreCase)) return true;
        return !sections.ContainsKey(section + ".NTamd64");
    }

    private static int InfDecorationScore(string section) =>
        section.Contains("amd64", StringComparison.OrdinalIgnoreCase) ? 3 :
        section.Contains(".nt", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

    private static int CatalogDecorationScore(string name) =>
        name.Contains("amd64", StringComparison.OrdinalIgnoreCase) ? 3 :
        name.Contains(".nt", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

    private static bool IsApplicableInfSection(string section, string baseName)
    {
        if (section.Equals(baseName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!section.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)) return false;
        return !section.Contains("x86", StringComparison.OrdinalIgnoreCase) &&
               !section.Contains("arm", StringComparison.OrdinalIgnoreCase) &&
               !section.Contains("ia64", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApplicableCatalogEntry(string name) =>
        !name.Contains("x86", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("arm", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("ia64", StringComparison.OrdinalIgnoreCase);

    private static bool TrySplitInfEntry(string line, out string name, out string value)
    {
        var equals = line.IndexOf('=');
        if (equals <= 0)
        {
            name = value = "";
            return false;
        }
        name = line[..equals].Trim();
        value = line[(equals + 1)..].Trim();
        return name.Length > 0;
    }

    private static string StripInfComment(string line)
    {
        var quote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quote = !quote;
            else if (line[i] == ';' && !quote) return line[..i];
        }
        return line;
    }

    private static string CleanInfValue(string value) => value.Trim().Trim('"');

    private async Task<string> CreateCacheKeyAsync(string isoPath, WindowsIsoSelection selection)
    {
        _activity("Hashing the ISO for media cache reuse...");
        var isoHash = await HashFileAsync(isoPath);
        var driverManifest = selection.AddDrivers
            ? CreateDriverManifest(selection.DriverFolders, selection.DriverFiles)
            : "NO_DRIVERS";
        var descriptor = string.Join("|", "CACHE_V9_DRIVER_PACKAGE_PREFLIGHT", isoHash, selection.EditionIndex, selection.EditionName,
            selection.AddDrivers, selection.ForceUnsigned, driverManifest);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor))).ToLowerInvariant();
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = SHA256.Create();
        return Convert.ToHexString(await hash.ComputeHashAsync(stream));
    }

    private static string CreateDriverManifest(string root)
    {
        var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return $"{Path.GetRelativePath(root, path).ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries))));
    }

    private static string CreateDriverManifest(IEnumerable<string> folders, IEnumerable<string> files)
    {
        var entries = folders.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(folder => $"FOLDER|{CreateDriverManifest(folder)}")
            .Concat(files.Distinct(StringComparer.OrdinalIgnoreCase).Select(file =>
            {
                var parent = Path.GetDirectoryName(file) ?? throw new InvalidOperationException($"The selected INF path is invalid: {file}");
                var siblingEntries = Directory.EnumerateFiles(parent, "*", SearchOption.TopDirectoryOnly)
                    .Select(path =>
                    {
                        var info = new FileInfo(path);
                        return $"{Path.GetFileName(path).ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                    })
                    .OrderBy(value => value, StringComparer.Ordinal);
                var siblingManifest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", siblingEntries))));
                return $"INF|{Path.GetFileName(file).ToUpperInvariant()}|{siblingManifest}";
            }))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries))));
    }

    private static async Task CopyDirectoryAsync(string source, string destination)
    {
        await Task.Run(() =>
        {
            var pending = new Stack<(string Source, string Destination)>();
            pending.Push((source, destination));
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                Directory.CreateDirectory(current.Destination);
                foreach (var file in Directory.EnumerateFiles(current.Source))
                    File.Copy(file, Path.Combine(current.Destination, Path.GetFileName(file)), true);
                foreach (var folder in Directory.EnumerateDirectories(current.Source))
                    pending.Push((folder, Path.Combine(current.Destination, Path.GetFileName(folder))));
            }
        });
    }

    private async Task DiscardOwnMountsAsync(string stagingRoot)
    {
        if (!Directory.Exists(stagingRoot)) return;
        foreach (var mount in Directory.EnumerateDirectories(stagingRoot, "mount-*", SearchOption.TopDirectoryOnly))
        {
            try { await RunDismAsync($"/Unmount-Image /MountDir:\"{mount}\" /Discard"); }
            catch { }
        }
    }

    private static bool IsCompleteWindowsMedia(string root) =>
        Directory.Exists(root) && File.Exists(Path.Combine(root, "efi", "boot", "bootx64.efi")) &&
        File.Exists(Path.Combine(root, "sources", "boot.wim")) && File.Exists(Path.Combine(root, "sources", "install.wim")) &&
        File.Exists(Path.Combine(root, "boot", "bcd")) && File.Exists(Path.Combine(root, "efi", "microsoft", "boot", "bcd"));

    private static void EnsureStagingSpace(string stagingRoot, BootableIsoInfo info, WindowsIsoSelection selection)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(stagingRoot))!;
        var available = new DriveInfo(root).AvailableFreeSpace;
        var driverRoots = selection.DriverFolders
            .Concat(selection.DriverFiles.Select(path => Path.GetDirectoryName(path) ?? ""))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var driverBytes = driverRoots.Aggregate(0L, (total, path) =>
        {
            var bytes = CalculateDirectoryBytes(path);
            return total > long.MaxValue - bytes ? long.MaxValue : total + bytes;
        });
        var mediaBytes = info.TotalBytes > (long.MaxValue - 12L * 1024 * 1024 * 1024) / 2
            ? long.MaxValue
            : info.TotalBytes * 2 + 12L * 1024 * 1024 * 1024;
        var estimated = mediaBytes > long.MaxValue - driverBytes ? long.MaxValue : mediaBytes + driverBytes;
        if (available < estimated)
            throw new InvalidOperationException($"Local Windows staging needs approximately {FormatBytes(estimated)}, including {FormatBytes(driverBytes)} of selected driver content, but only {FormatBytes(available)} is available on {root}. Free system-drive space or remove old caches under %LOCALAPPDATA%\\LaptopQAUsbBuilder before trying again.");
    }

    private static void MakeWritable(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Required Windows image was not found.", path);
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
    }

    private static long CalculateDirectoryBytes(string root)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var length = new FileInfo(file).Length;
            total = total > long.MaxValue - length ? long.MaxValue : total + length;
        }
        return total;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { DeleteDirectoryTree(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteDirectoryTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(directory, FileAttributes.Directory);
        Directory.Delete(path, true);
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):N2} GB"
        : $"{bytes / (1024d * 1024):N0} MB";
}

public sealed record PreparedWindowsMedia(string MediaPath, long TotalBytes, bool CacheHit, string CacheKey,
    IReadOnlyList<DriverInjectionRejection> DriverRejections);
public sealed record DriverInjectionRejection(string Image, string DriverPath);
public sealed record IncompleteDriverPackage(string InfPath, IReadOnlyList<string> MissingFiles);
public sealed record RejectedDriverDetail(string DriverPath, IReadOnlyList<string> ErrorCodes);
public sealed class IncompleteDriverPackageException(string message) : InvalidOperationException(message);
public sealed record DismRunOutcome(IReadOnlyList<string> RejectedDrivers, bool RequiresIndividualRetry = false)
{
    public static DismRunOutcome Success { get; } = new([]);
}
