using System.IO;

namespace LaptopQaUsbBuilder;

internal static class WindowsMediaFolderSource
{
    internal static readonly string[] RequiredBootFiles =
    [
        "setup.exe", "bootmgr", "boot/bcd", "efi/boot/bootx64.efi",
        "efi/microsoft/boot/bcd", "sources/boot.wim"
    ];

    internal static bool IsInstaller(string root) => Directory.Exists(root)
        && RequiredBootFiles.All(file => File.Exists(Path.Combine(root, file)))
        && (File.Exists(Path.Combine(root, "sources", "install.wim"))
            || File.Exists(Path.Combine(root, "sources", "install.esd")));

    // OneDrive placeholders have ReparsePoint set too. Only skip actual links,
    // not cloud-backed files/directories that Windows can hydrate on access.
    internal static bool IsDirectoryLink(string path) => new DirectoryInfo(path).LinkTarget is not null;

    internal static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (new FileInfo(file).LinkTarget is not null)
                    throw new InvalidOperationException($"Windows media contains a linked file. Use a complete local copy: {file}");
                yield return file;
            }
            foreach (var folder in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(folder);
                if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsDirectoryLink(folder))
                    throw new InvalidOperationException($"Windows media contains a linked folder. Use a complete local copy: {folder}");
                pending.Push(folder);
            }
        }
    }
}
