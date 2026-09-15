namespace LaptopQaUsbBuilder;

internal static class WindowsSetupDiskGuard
{
    // Preserve the original binary units without CInt rounding at the boundaries.
    internal static string[] ScriptLines() =>
    [
        "Option Explicit",
        "Sub Fail(message)",
        "  WScript.Echo message",
        "  WScript.Quit 1",
        "End Sub",
        "Sub CheckTargetDisk()",
        "  Dim wmi, drives, drive, count, sizeGiB",
        "  Set wmi = GetObject(\"winmgmts:\\\\.\\root\\cimv2\")",
        "  Set drives = wmi.ExecQuery(\"SELECT * FROM Win32_DiskDrive WHERE Index = 0\")",
        "  count = 0",
        "  For Each drive In drives",
        "    count = count + 1",
        "    If IsNull(drive.Size) Then Fail \"Disk size information is unavailable.\"",
        "    sizeGiB = CDbl(drive.Size) / 1024 / 1024 / 1024",
        "    If sizeGiB < 200 Or sizeGiB > 4000 Then Fail \"Target disk must be between 200 and 4000 GiB.\"",
        "  Next",
        "  If count <> 1 Then Fail \"Exactly one matching target disk is required.\"",
        "End Sub",
        // An error inside the sub returns here; it must never allow a successful exit.
        "On Error Resume Next",
        "CheckTargetDisk",
        "If Err.Number <> 0 Then Fail \"Could not validate target disk: \" & Err.Description",
        "On Error GoTo 0",
        "WScript.Echo \"Disk 0 passed the disk check. Existing partitions may be erased.\"",
        "WScript.Quit 0"
    ];

    internal static string[] PartitionRunnerLines =>
    [
        "@echo off",
        "cscript.exe //nologo //E:vbscript X:\\check-target-disk.vbs > X:\\check-target-disk.log 2>&1",
        "if errorlevel 1 goto blocked",
        "if not errorlevel 0 goto blocked",
        "diskpart.exe /s X:\\diskpart.txt > X:\\diskpart.log 2>&1",
        "exit /b",
        ":blocked",
        "echo Windows installation blocked. The target disk did not pass validation.",
        "type X:\\check-target-disk.log",
        "echo Close Windows Setup and restart after reviewing the target disk or configuration.",
        // Keep the synchronous command active so Setup cannot continue to ImageInstall,
        // including when VBScript/WMI is missing or the check fails unexpectedly.
        "pause",
        "goto blocked"
    ];
}
