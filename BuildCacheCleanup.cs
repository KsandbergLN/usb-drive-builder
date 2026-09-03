using System.Diagnostics;
using System.IO;
using System.Text;

namespace LaptopQaUsbBuilder;

internal static class BuildCacheCleanup
{
    internal static readonly string[] DirectoryNames =
        ["MediaCache", "DriverPackCache", "DriverPayloadCache", "Staging"];

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaptopQAUsbBuilder");

    private static string RequestPath => Path.Combine(Root, ".clear-cache-on-exit");

    internal static IEnumerable<string> Paths => DirectoryNames.Select(name => Path.Combine(Root, name));

    internal static long GetSize()
    {
        long total = 0;
        foreach (var path in Paths)
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

    internal static void RequestCleanupOnExit()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(RequestPath, DateTimeOffset.Now.ToString("O"));
    }

    internal static bool IsCleanupRequested => File.Exists(RequestPath);

    internal static long ClearBestEffort()
    {
        foreach (var path in Paths) DeleteTreeBestEffort(path);
        return GetSize();
    }

    internal static bool ClearStagingBestEffort()
    {
        var staging = Path.Combine(Root, "Staging");
        DeleteTreeBestEffort(staging);
        return !Directory.Exists(staging);
    }

    internal static void CompleteRequestIfEmpty()
    {
        if (GetSize() != 0) return;
        try { File.Delete(RequestPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void StartCleanupAfterExit(int processId)
    {
        if (!IsCleanupRequested) return;
        var quotedPaths = string.Join(",", Paths.Select(path => $"'{PsQuote(path)}'"));
        var request = PsQuote(RequestPath);
        var script = $"$current=Get-Process -Id {processId} -ErrorAction SilentlyContinue;if($current){{$current|Wait-Process -ErrorAction SilentlyContinue}};" +
                     "$limit=(Get-Date).AddHours(2);while((Get-Process -ErrorAction SilentlyContinue|Where-Object ProcessName -like 'USB Drive Builder v*') -and (Get-Date) -lt $limit){Start-Sleep -Seconds 2};" +
                     "if(Get-Process -ErrorAction SilentlyContinue|Where-Object ProcessName -like 'USB Drive Builder v*'){exit};" +
                     $"$paths=@({quotedPaths});foreach($path in $paths){{for($i=0;$i -lt 20 -and (Test-Path -LiteralPath $path);$i++){{Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue;Start-Sleep -Milliseconds 500}}}};" +
                     $"if(-not ($paths|Where-Object{{Test-Path -LiteralPath $_}})){{Remove-Item -LiteralPath '{request}' -Force -ErrorAction SilentlyContinue}}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }

    private static void DeleteTreeBestEffort(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                         .OrderByDescending(directory => directory.Length))
            {
                try { Directory.Delete(directory, false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Directory.Delete(path, false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string PsQuote(string value) => value.Replace("'", "''");
}
