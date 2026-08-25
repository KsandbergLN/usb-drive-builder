using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LaptopQaUsbBuilder;

public sealed class WindowsMediaPreparer
{
    private readonly Action<string> _activity;
    private readonly Action<string> _log;
    private readonly Func<string, Task<string>> _mountIso;
    private readonly Func<string, Task> _dismountIso;

    public WindowsMediaPreparer(Action<string> activity, Action<string> log,
        Func<string, Task<string>> mountIso, Func<string, Task> dismountIso)
    {
        _activity = activity;
        _log = log;
        _mountIso = mountIso;
        _dismountIso = dismountIso;
    }

    public async Task<PreparedWindowsMedia> PrepareAsync(string isoPath, BootableIsoInfo info, WindowsIsoSelection selection)
    {
        if (selection.AddDrivers)
        {
            foreach (var folder in selection.DriverFolders)
                if (!Directory.Exists(folder)) throw new InvalidOperationException($"A selected drivers folder is no longer available: {folder}");
            foreach (var file in selection.DriverFiles)
                if (!File.Exists(file)) throw new InvalidOperationException($"A selected INF driver is no longer available: {file}");
            ValidateDriverPackages(selection);
        }
        var cacheKey = await CreateCacheKeyAsync(isoPath, selection);
        var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaptopQAUsbBuilder", "MediaCache", cacheKey);
        var cachedMedia = Path.Combine(cacheRoot, "media");
        var completeMarker = Path.Combine(cacheRoot, ".complete");
        var rejectionReport = Path.Combine(cacheRoot, "driver-rejections.json");
        if (File.Exists(completeMarker) && IsCompleteWindowsMedia(cachedMedia))
        {
            _activity($"Using cached {selection.EditionName} Windows media.");
            _log($"Windows media cache hit: {cacheKey}");
            return new PreparedWindowsMedia(cachedMedia, CalculateDirectoryBytes(cachedMedia), true, cacheKey,
                LoadDriverRejections(rejectionReport));
        }

        var stagingParent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaptopQAUsbBuilder", "Staging");
        Directory.CreateDirectory(stagingParent);
        EnsureStagingSpace(stagingParent, info);
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

            DeleteDirectoryTree(cacheRoot);
            Directory.CreateDirectory(cacheRoot);
            Directory.Move(mediaRoot, cachedMedia);
            File.WriteAllText(completeMarker,
                $"Edition={selection.EditionName}{Environment.NewLine}Created={DateTimeOffset.Now:O}{Environment.NewLine}DriverFolders={selection.DriverFolders.Count}{Environment.NewLine}DriverFiles={selection.DriverFiles.Count}{Environment.NewLine}ForceUnsigned={selection.ForceUnsigned}");
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
        var useful = rejectedDrivers.Count > 0 && process.ExitCode is 2 or 3
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
        var section = "";

        foreach (var rawLine in lines)
        {
            var line = StripInfComment(rawLine).Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            if (!IsApplicableInfSection(section, "SourceDisksNames") || !TrySplitInfEntry(line, out var diskId, out var value)) continue;
            var fields = value.Split(',');
            if (fields.Length > 3)
            {
                var diskPath = CleanInfValue(fields[3]).TrimStart('\\');
                if (!diskPath.Contains('%')) diskPaths[diskId] = diskPath;
            }
        }

        section = "";
        foreach (var rawLine in lines)
        {
            var line = StripInfComment(rawLine).Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            if (!TrySplitInfEntry(line, out var name, out var value)) continue;
            if (section.Equals("Version", StringComparison.OrdinalIgnoreCase) &&
                name.StartsWith("CatalogFile", StringComparison.OrdinalIgnoreCase) && IsApplicableCatalogEntry(name))
            {
                var catalog = CleanInfValue(value.Split(',')[0]);
                if (!catalog.Contains('%')) requiredPaths.Add(Path.Combine(packageRoot, catalog));
                continue;
            }
            if (!IsApplicableInfSection(section, "SourceDisksFiles")) continue;
            var fileName = CleanInfValue(name);
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
            requiredPaths.Add(relativeParts.Aggregate(packageRoot, Path.Combine));
        }

        var missing = requiredPaths.Where(path => !File.Exists(path))
            .Select(path => Path.GetRelativePath(packageRoot, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new IncompleteDriverPackage(infPath, missing);
    }

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

    private static void EnsureStagingSpace(string stagingRoot, BootableIsoInfo info)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(stagingRoot))!;
        var available = new DriveInfo(root).AvailableFreeSpace;
        var estimated = info.TotalBytes * 2 + 8L * 1024 * 1024 * 1024;
        if (available < estimated)
            throw new InvalidOperationException($"Local staging needs approximately {FormatBytes(estimated)}, but only {FormatBytes(available)} is available on {root}.");
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
