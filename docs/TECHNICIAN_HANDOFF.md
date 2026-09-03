# USB Drive Builder — Technician Handoff

## Purpose

USB Drive Builder prepares standardized support USB media for laptop QA and support work. It creates an MBR-partitioned USB drive with UEFI-only Windows boot media, formats the requested partitions, copies selected content, and verifies the resulting labels and file systems.

This is an operations handoff for technicians. It does not describe how the application is built or maintained.

## Current release

- Application: USB Drive Builder
- Release: 2.0.92
- Platform: Windows 10/11, 64-bit
- Launch: Run the newest `USB Drive Builder vX.Y.Z.exe` from the `dist` folder
- Permissions: Windows administrator approval is required

## Standard output

The built drive uses MBR and, by default, has this visible layout:

| Partition | Size | Format | Typical contents |
|---|---:|---|---|
| `DELL DIAG` | 50 MB | FAT32 | Diagnostic tools |
| `Win11 Boot` | 20 GB | NTFS | Windows setup/support files, optional `Autounattend.xml`, and optional bootable UEFI Windows media |
| `IT SUPP` | `*` (remaining space) | exFAT | Support tools and other technician content |

The default layout can be changed in Config. A valid MBR layout must contain 1–4 partitions and exactly one `*` remaining-space partition.

### Generated Windows Setup defaults

The **Generated Windows Setup defaults** section is used only when scripts are selected without a supplied `Autounattend.xml`. When an XML is supplied, the app preserves that file's Windows Setup settings instead. **Target disk** is the internal disk number passed to DiskPart and Windows Setup. **Install partition** identifies the Windows image destination; the standard generated layout uses partition `3`. **EFI MB** and **MSR MB** set the EFI System and Microsoft Reserved partition sizes. **Shrink MB** is the amount reserved for the Recovery partition. The EFI, Windows, and Recovery label fields name those volumes; the EFI/Win/Rec letter fields provide temporary Setup drive letters. Select **Edition** from its list; it must match an available image name in the selected ISO. **Prompt before erasing/installing** shows the **Ready to Reimage** Yes/No dialog before internal-disk reimaging, with **No** selected by default. Choose **OOBE language** and **OOBE keyboard** from the drop-downs to set the Windows locale and input profile after Setup; the defaults are English (United States) and US keyboard. The chosen language must be present in the selected Windows ISO. These settings bypass the language and keyboard prompts. The settings visibly dim while locked. **Generate Autounattend.xml** is always available: it asks where to save a standalone answer file using the currently displayed values, without changing them. The generated file writes one DiskPart command per Windows Setup step so shell quoting cannot combine commands. Labels support letters, numbers, spaces, hyphens, and underscores; temporary drive letters must be one letter. **Allow unsigned drivers** applies only to DISM `/ForceUnsigned` and cannot override Windows or Secure Boot signature enforcement.


## Before starting a build

1. Confirm the source files and folders are available locally or on a separate drive.
2. Confirm every target USB drive can be erased. The selected drive(s) are completely erased and this cannot be undone.
3. Use a drive larger than the configured fixed partitions. For the standard layout, use a drive larger than 20.3 GB; larger capacity is recommended.
4. Close Explorer windows or applications that are using the target USB drive.
5. If building several drives, identify each target by its displayed disk number, name, and capacity before selecting it.

## Normal operating procedure

1. Launch the newest executable and approve the administrator prompt.
2. Insert the target USB drive(s). Select **Refresh** if a newly inserted drive is not listed.
3. Select one or more USB drive cards. Assigned drive letters appear beside each disk number; multiple selections are queued and processed one at a time.
4. Review **Partition Settings** and the **Partition Layout** preview.
5. For each partition that needs content, select **Add**. On FAT32 and exFAT, Add opens the **Files / folders** managed-source dialog directly: select files and folders repeatedly, remove an individual selection, clear the list, and choose Close to apply it. On NTFS, Add also provides XML, ISO, Drivers, and Scripts actions. Drivers and Scripts are always visible on NTFS, can be selected before or after the ISO, and do not require XML; script builds generate `Autounattend.xml` when none was supplied. A build is blocked if Drivers or Scripts remain selected without an ISO. Add and Clear are stacked vertically; green text beside them summarizes `AUXML`, `ISO`, `Folder`, `Files`, `Drivers`, and `Scripts` selections. Folder contents are merged into the partition root in selection order. The Scripts manager displays each file name and source folder, including recursively found files from Add Folder. Hover over the control area for full paths.
6. On NTFS, the Add chooser also offers **XML** and **ISO**. XML is copied to that partition's root as `Autounattend.xml`. Once an ISO is selected, check **Generate Autounattend.xml** in the same Add dialog to create a new answer file from the Generated Windows Setup defaults in Config. A supplied XML takes precedence, so clear it before using the generated file.
7. Use **ISO** on a fixed-size NTFS partition of at least 5 GB and choose the edition; Windows 11 Pro is preferred. Driver selection is separate and may happen before or after ISO selection through the always-visible **Drivers** action. Its themed manager uses **Add Folder** for recursively scanned directories and one **Add Driver Files** picker for INF, ZIP, and CAB selections. Archives are extracted before validation into `%LOCALAPPDATA%\LaptopQAUsbBuilder\DriverPackCache\<SHA-256>`. INF-referenced legacy compressed payloads (`.sy_`, `.dl_`, `.ca_`, and equivalent final-character underscore names) are expanded into `DriverPayloadCache`; sources remain unchanged. ZIP extraction rejects paths outside its cache directory; damaged/password-protected packs and packs without INFs stop before target erasure. Drivers are added only to the installed image for OOBE; `boot.wim` is not serviced. Driver preflight resolves decorated x64 catalogs and checks payloads referenced by applicable CopyFiles and ServiceBinary definitions rather than treating every SourceDisksFiles inventory entry as mandatory. Missing required files stop with a specific themed warning; the dialog identifies the first INF and up to 12 missing files, while the build log lists all incomplete packages. Every INF rejected by DISM servicing is also logged individually with its reported HRESULT before servicing continues or stops, so the build log always retains the complete failure list. Complete packages rejected for invalid DISM data remain non-blocking. **Scripts** opens a themed manager that retains selections across repeated **Add Files** actions, allowing files of any type to be gathered from multiple locations, individually removed, or cleared before **Close** applies them. Duplicate destination filenames and reserved helper names are rejected. CMD, BAT, PowerShell, VBS, JS, and WSF execute; XML and other formats are supporting files. The app merges a synchronous `specialize` command into the USB copy of a selected `Autounattend.xml`, or generates one. Recognized scripts run as `SYSTEM` before OOBE, after which all selected files and helpers are deleted. Source files remain unchanged. The FAT32 `DELL DIAG` partition is diagnostics-only. Bootable ISO support targets supported Dell-compatible removable USB flash sticks with native NTFS UEFI support, not fixed-media external hard disks. Only one ISO partition is supported.
8. Review any capacity warning in the Partition Layout card. Warning-colored segments identify partitions whose selected content is estimated not to fit; hover to see the required and available sizes.
9. Check the warning panel. Type `ERASE` exactly in the confirmation box.
10. Select **Build USB Queue**. A valid preflight proceeds directly into the queue without another confirmation.
11. Wait for the queue to finish. Do not remove a drive or close the application while a build is active.
12. Review the completion message. It reports successful and failed drives and gives the build-log path.

The upper Activity progress bar displays only the current operation and byte-based completion percentage during transfers. The lower queue tracker begins empty, creates one equal-width segment per selected USB, and fills each segment progressively as its drive is partitioned, copied, and verified. The selected drive card is pale green; a writing drive becomes dark green with diagonal green stripes, a completed drive is solid dark green, and a failed drive is red. ETA and total-queue time estimates are intentionally omitted because formatting, ISO mounting, small-file overhead, antivirus scanning, and changing USB speed made them unreliable.

All application-generated confirmations, warnings, errors, and completion dialogs follow the active Light, Dark, or AMOLED theme. Native Windows file and folder pickers are intentionally unchanged.

Each drive is rechecked immediately before it is erased, then built and verified before the next queued drive starts. If one drive fails, later queued drives still run.

After `Clear-Disk`, the app refreshes Windows storage state and explicitly removes any partition that a removable drive leaves behind, then initializes only a genuinely RAW disk. If a cleared USB stick already remains empty MBR, partition creation continues without issuing a redundant initialization command. Storage errors serialized by PowerShell as CLIXML are decoded into readable build-log messages.

Selected files, folders, and Setup-script sources are measured when attached and checked again during preflight. The preflight prepares the selected Windows edition locally, optionally services it with DISM, and combines the exact cached-media size with other content and a filesystem safety allowance. If preparation fails or any partition is estimated to be too small, the build stops before erasure.

Windows media is staged and cached under `%LOCALAPPDATA%\LaptopQAUsbBuilder`. Driver sources are copied into the active build's private staging folder before DISM starts, so cache cleanup or a second app instance cannot remove files that servicing is using. The staging copy hashes same-size candidates, represents byte-identical payloads with hard links while preserving every path, and suppresses redundant INF entries only when their complete package directories are byte-identical. The build log reports linked file count, temporary bytes saved, and suppressed INF count. Driver staging overlaps the ISO copy; exact-duplicate hashing uses up to four workers, package preflight uses up to six workers, and ZIP/CAB extraction uses at most two concurrent workers. DISM operations against the mounted WIM remain serialized. Each DISM operation displays elapsed time and a real percentage when DISM supplies one. The app samples DISM CPU, process I/O, watched paths, and output; after five quiet seconds it confirms DISM is still working, and after ten uninterrupted inactive minutes it records a non-blocking warning while continuing to wait. If preparation completes but USB partitioning, copying, or verification later fails, all completed media and supporting driver caches are retained across app closure for the next retry; successful completion restores normal close-time cleanup. Selected source folders are never modified or reorganized, and newly added folders or packages are included automatically on the next recursive scan. Maximum-compression WIM export, one installed-image mount/commit, and queue-wide cache reuse reduce output size and repeated DISM work. Maximum compression can increase first-time export duration. A matching later build also reuses the cache. Cache identity includes the source ISO hash, edition, driver-folder manifest, unsigned-driver choice, and staging algorithm version. Remove the cache subfolders only while the app is closed when space must be reclaimed or preparation must be forced from scratch. Ensure the system drive has ample free space before first-time preparation.

The segmented total-progress tracker includes shared preparation instead of waiting for USB writes. For Windows-media builds, validation, driver/archive preparation, ISO hashing and staging, WIM export, driver injection, and commit advance the first 55 percent of every selected-drive segment together. The remaining 45 percent of each segment advances independently while that USB is partitioned, copied, and verified. The current-activity bar continues to restart for each individual operation.

USB initialization uses native DiskPart `clean` instead of deleting partitions individually, zero-filling the device, or writing through custom raw-disk handles. Immediately before the destructive operation, the app revalidates the disk number, persistent unique ID, USB bus type, boot/system flags, and read-only state. It then refreshes Windows storage state for up to 60 seconds, rediscovers the same device by persistent ID if its disk number changed, and verifies zero partitions. If the empty disk is not already MBR, `Set-Disk -PartitionStyle MBR` performs the conversion before partition creation; this avoids issuing a redundant DiskPart conversion on an already-MBR disk. DiskPart exit `-2147024463` is Windows error 433 and is reported as a device disconnect during the partition-table write; repeated failures point to the USB device or physical connection rather than partition-removal sequencing.

Configuration displays the combined size of `MediaCache`, `DriverPackCache`, and `DriverPayloadCache`. **Clear Cache** deletes those three folders after confirmation while preserving logs and settings. A named cross-instance guard prevents manual or close-time cache cleanup from racing active media preparation.

DISM error `0x80070070` means the local system drive ran out of space; it does not mean the driver paths named at the end of the DISM log are invalid. The app includes selected driver content in its preflight estimate and reports the capacity issue before mounting when possible. Close the app before removing disposable folders under `%LOCALAPPDATA%\LaptopQAUsbBuilder`.

Config includes **Allow unsigned drivers (DISM /ForceUnsigned)**, disabled by default. Enabling it only changes DISM acceptance during optional injection; Windows and Secure Boot can still reject unsigned drivers.

## What “successful” means

For each successful drive, the app has:

- erased and initialized the USB target as MBR while leaving every partition inactive for UEFI-only startup;
- created and formatted the configured partitions;
- copied the selected files and folders;
- copied or generated `Autounattend.xml`, preserving selected answer-file settings while adding any requested pre-OOBE script runner, and prepared NTFS UEFI media containing the selected Windows edition and any requested offline drivers;
- verified the expected partition labels and file systems.

The app does not modify the source ISO. Bootable media targets supported removable USB flash sticks and 64-bit UEFI Windows Setup, not fixed-media external hard disks or legacy BIOS. It relies on the Dell firmware's native NTFS UEFI support and does not require a FAT32 boot partition. Secure Boot acceptance depends on the ISO and injected-driver signatures and target firmware policy.

## Troubleshooting

### Drive is not listed

- Confirm it is connected directly and appears in Windows Disk Management.
- Select **Refresh**.
- Only disks Windows reports as USB are shown.
- If it still does not appear, try another USB port or drive enclosure.

### Build button is disabled

- At least one USB drive must be selected.
- The confirmation box must contain `ERASE`.
- Correct any invalid partition settings shown by the app.

### Drive is too small

Use a larger drive or reduce the fixed-size partitions. The remaining partition receives all capacity left after the fixed partitions and required overhead.

### Source file or folder is missing

Reconnect the source drive or correct the source path, then select the source again. Do not use a queued target drive as a source; it will be erased.

### Incomplete driver package

The app derives this warning from the driver sources selected for the current build; it does not use a hardcoded package list. Re-extract the original vendor package and retain every catalog and payload file referenced by the INF. The warning identifies the first affected INF and missing files found during preflight, and the build log lists every incomplete package discovered in that run.

### A queued drive fails

Leave the other drives connected until the queue completes. Record the failed disk number and exact error, then collect the build log. Do not retry until the target identity and source paths have been checked.

### Where to find logs

Logs are saved under:

`%LOCALAPPDATA%\LaptopQAUsbBuilder\Logs`

The completion dialog shows the current build-log path. Crash logs are saved in the same folder. Stack traces retain useful source filenames and line numbers but sanitize the developer's local build and OneDrive path.

## Escalation handoff

When escalating, provide:

- app version;
- target disk number, displayed name, and capacity;
- whether one drive or a queue was used;
- the partition layout and source types selected, including XML or ISO sources;
- the exact message shown by the app; and
- the relevant `Build-YYYYMMDD-HHMMSS.log` or `Crash-YYYYMMDD-HHMMSS.log` file.

Do not send source content, answer files, or ISO images unless they are specifically requested and approved.

undefined
