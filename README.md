![USB Drive Builder application](docs/images/laptop-qa-usb-drive-builder.png)

# USB Drive Builder

**USB Drive Builder** is a Windows desktop tool for IT technicians who need to turn one or more USB drives into consistent, ready-to-use laptop support media. It replaces repetitive manual disk preparation with a guided workflow that erases approved USB disks, creates a configurable MBR partition layout, formats each volume, and copies the correct diagnostic, Windows setup, and support content to its destination.

The app is designed for repeatable bench workflows. Technicians can save a standard layout, preview how it will fit each selected drive, attach different files and folders to individual partitions, and build several USB drives in a sequential queue. Before and after every build, the app validates the target disk and resulting partition layout, while activity and file logs provide a clear record for troubleshooting.

## Highlights

- Configure 1-4 MBR partitions using FAT32, NTFS, or exFAT, including one partition that consumes all remaining space.
- Add files and folders per partition, with `Autounattend.xml` support, Windows edition selection, and optional offline driver injection into bootable 64-bit UEFI Windows media.
- Build multiple USB drives in sequence without one failed drive stopping the rest of the queue.
- Preview proportional partition layouts for every selected drive before anything is erased.
- Estimate selected content sizes, highlight partitions whose content will not fit, and block the build before erasure.
- Revalidate USB identity and reject boot, system, non-USB, changed, or unsafe source/target disks.
- Save default layouts, switch among Light, Dark, and AMOLED themes, and use the interface in 12 languages.

## Default layout

The factory defaults are:

| Partition | Size | File system |
|---|---:|---|
| `DELL DIAG` | 50 MB | FAT32 |
| `Win11 Boot` | 20 GB | NTFS |
| `IT SUPP` | `*` (all remaining space) | exFAT |

`*` may be used on exactly one partition in any position. Fixed sizes accept MB or GB values, such as `50 MB` and `20 GB`.
Size fields turn red when an entry does not contain a valid `MB`, `GB`, or `*` value.

## Partition configuration

Open the three-bar menu in the upper-left corner to manage the default partition layout. Defaults can contain 1-4 partitions with configurable volume labels, sizes, and FAT32, NTFS, or exFAT formats.

Default editing is locked initially to prevent accidental changes. Unlock it with the lock icon, make the required changes, and choose **Save**. The main-screen **Defaults** button restores the saved default layout without changing the factory fallback values.

Partitions can be added with the green `+`, removed with their red `-`, and reordered using the two-bar drag handles. Config removal and reordering controls are disabled while defaults are locked.

### Generated Windows Setup defaults

The **Generated Windows Setup defaults** section is used only when scripts are selected and no `Autounattend.xml` was supplied. If you provide an XML file, its existing Windows Setup settings are preserved and these generated-file fields are not used. The fields mean:

| Setting | Meaning |
|---|---|
| Target disk | Internal disk number passed to DiskPart and Windows Setup. This is normally `0`, but it must match the disk that should be erased on the computer being installed. |
| Install partition | Partition number receiving the Windows image. With the standard generated layout, partition `3` is the Windows partition. |
| EFI MB | Size of the EFI System Partition, in MB. |
| MSR MB | Size of the Microsoft Reserved partition, in MB. |
| Shrink MB | Space reserved from the Windows partition for the Recovery partition, in MB. |
| EFI / Windows / Recovery label | Volume labels assigned to those internal-disk partitions. |
| EFI / Win / Rec letters | Temporary drive letters used by Windows Setup while it creates and formats the partitions. |
| Edition | Windows image name requested from the selected ISO; it should match one of that ISO's available editions. |
| Prompt before erasing/installing | Adds a Yes/No prompt in Windows Setup before the internal disk is erased and Windows is installed. |

The **Allow unsigned drivers** option applies only to optional DISM driver injection and adds `/ForceUnsigned`; it does not override Windows or Secure Boot policy. The lock icon protects the default partition rows and generated settings from accidental edits until unlocked.

## Adding content

Every partition row has a muted, theme-aware green **Add** button stacked above a muted red **Clear** button with no content heading. Add opens an app-themed content manager that remains open while multiple content types are selected, sizes itself to the actions available for that file system, and closes only when **Close** is chosen. Clear removes all content assigned to that partition. Green text to their right summarizes attached types—`AUXML`, `ISO`, `Folder`, `Files`, `Drivers`, and `Scripts`—without expanding the row into separate buttons. Folder contents are merged into the destination partition root, while selected files are copied directly to that root.

Every content-manager action changes from grey to green immediately after that content type has a successful selection. Cancelling a picker leaves its button unchanged.

Positive and destructive controls use the same theme-aware palette throughout the app. Light and Dark use the desaturated `#D7F3E5` green and `#D8A2A3` red; AMOLED uses higher-saturation equivalents with contrasting text.

For NTFS partitions, Add also offers **XML** and **ISO**. FAT32 and exFAT partitions accept regular file and folder content but do not offer ISO selection. XML selects an answer file that is copied to the partition root as `Autounattend.xml`.

ISO accepts one supported 64-bit Windows installer ISO per USB drive. After inspection, an app-themed options window lists only the editions in the image and selects Windows 11 Pro by default when available. The chosen edition is exported as the only install edition, reducing later servicing and copy work.

On every NTFS partition, the Add chooser always shows **Drivers** and **Scripts** alongside XML and ISO. Drivers and Scripts may be selected before or after the ISO, so content can be configured in any order; a build is blocked with a clear validation message if those selections remain without a Windows ISO. Drivers opens a themed manager with **Add Folder** plus one **Add Driver Files** picker for individual INF packages and compressed ZIP/CAB driver packs. Multiple INF files and driver packs can be selected together. Compressed packs are safely extracted into a SHA-256-addressed cache under `%LOCALAPPDATA%\LaptopQAUsbBuilder\DriverPackCache`, then scanned and validated exactly like extracted folders. Legacy underscore-compressed payloads referenced by an INF—such as `.sy_`, `.dl_`, and `.ca_`—are expanded into `DriverPayloadCache`; the original driver source is never changed. Unsafe ZIP paths, damaged/password-protected archives, and packs containing no INF files stop before USB erasure. Entries can be removed one at a time or cleared without affecting the ISO or other content, and the Drivers button remains green while any sources are active. Before ISO hashing or servicing, the app checks the effective x64 catalog plus package-owned payloads referenced by applicable CopyFiles and service definitions. Windows inbox dependencies brought in through `Include`/`Needs`, externally mapped vendor payloads, and unused inventory entries do not create false missing-file warnings. An incomplete package stops preflight with a themed **Incomplete driver package** warning that names the INF and missing files; every incomplete package is written to the build log. For DISM servicing failures, every rejected INF is logged individually with its reported HRESULT before the app continues or stops; fatal messages include the total failed count and direct technicians to the complete build-log list. Complete packages with DISM-invalid or incompatible data can still be skipped without a blocking prompt. The app mounts the installed image once, processes every source, and commits once; `boot.wim` is not serviced. **Allow unsigned drivers** in Config adds DISM `/ForceUnsigned`; it is off by default, and Windows or Secure Boot can still reject an unsigned driver.

Preparation happens once on fast local storage before any USB is erased. The app uses DISM maximum WIM compression for the selected-edition export, mounts the installed Windows image once, commits once, and caches the result under `%LOCALAPPDATA%\LaptopQAUsbBuilder\MediaCache`. Compressed archive extraction is cached under `DriverPackCache`, and expansion of legacy underscore-compressed payloads is cached under `DriverPayloadCache`. Cache publication retries temporary access-denied locks caused by scanners or indexers; completed driver staging can continue from its unique working directory if that directory cannot be renamed after all retries. Maximum compression reduces prepared-media size but makes a first-time export slower than fast compression. Every USB in the queue reuses that prepared media, and a later build reuses it when the ISO, edition, complete driver-source manifest, and unsigned-driver setting match. Delete these caches with the app closed if local disk space must be reclaimed or a clean preparation must be forced.

The destination must be a fixed-size NTFS partition of at least 5 GB and large enough for the prepared media. Windows boot does not use or require the FAT32 `DELL DIAG` partition; that volume remains available only for diagnostics. Bootable ISO support targets Dell-compatible removable USB flash sticks with native NTFS UEFI support, not fixed-media external hard disks such as WD My Passport. Select the USB's UEFI entry on the Dell boot menu so Windows Setup installs to a GPT system disk. An explicitly selected `Autounattend.xml` is copied afterward.

The always-visible **Scripts** action opens a themed source manager that retains the current list while **Add Files** is used repeatedly, allowing scripts and supporting resources to be collected from multiple folders or drives before choosing **Close**. It accepts every file type; entries can be removed individually or cleared together. Everything is copied after ISO preparation into `sources\$OEM$\$$\Setup\Scripts` on the finished USB. Only CMD, BAT, PowerShell, VBS, JS, and WSF files are automatically executed; other files remain available to those scripts through `%~dp0`. Duplicate filenames from different locations and app-reserved helper names are rejected, and all selected files participate in existence, target-disk, and partition-capacity safety checks.

The app automatically adds a synchronous `specialize` command to the copy of `Autounattend.xml` written to the USB; the technician's source XML is never modified. If no XML was selected, the app generates a minimal answer file containing the command. During Windows Setup, the command runs recognized scripts sequentially as `SYSTEM` before OOBE. After the last script exits, a generated cleanup helper removes every selected script or support file and both generated helper files. Existing unattended settings and existing specialize commands are preserved, and the new command is placed after them.

Content selections stay with their partition when the partition is reordered. Hover over the content controls to review the selected paths, or use **Clear** to remove all content selections from that partition.

## Progress and activity

The Activity card shows a determinate current-activity progress bar plus total queue progress. During a file or folder transfer the current bar displays byte-based completion percentage for that operation. Time estimates are intentionally omitted because formatting, ISO mounting, antivirus activity, small-file overhead, and changing USB speeds made them unreliable.

## Selecting and building USB drives

The drive picker shows disks that Windows reports with a USB bus type, with any assigned drive letters beside the disk number. Select one or more drive cards to create a sequential build queue. Each selected drive is revalidated immediately before it is erased, partitioned, populated, and verified. A failure on one drive is logged without preventing later queued drives from running.

After **Build USB Queue** is selected, the app immediately enters a visible **Preparing build** state while it checks targets, source paths, capacity, and prepares or retrieves cached Windows media. Initial DISM preparation can take several minutes. Once `ERASE` has enabled the Build button, a valid preflight flows directly into the USB queue without another confirmation or skipped-driver prompt. A safety or preparation failure still stops before erasure and explains what must be corrected.

Before building, enter `ERASE` in the confirmation field. Every partition and file on each selected target is permanently removed.

The Partition Layout card remains blank until a drive is selected. It then displays proportional, color-coded partition segments using each drive's calculated capacity. Multiple selected drives share the available height dynamically. Hover over a segment to see its drive number, label, calculated size, and file system.

When files or folders are selected, the app scans their logical sizes and allows additional filesystem working space. A partition that is too small is shown with warning colors in the layout, and its hover bubble shows the estimated required space. The app checks again during preflight—including extracted ISO contents and other content assigned to the same partition—and will not erase a drive while any selected content is estimated not to fit.

## Appearance and language

The configuration menu includes Light, Dark, and AMOLED themes and the same 12-language set used by Laptop QA V2. Theme changes preview live, and saved theme and language preferences persist between launches.

Application confirmations, warnings, errors, completion messages, and tooltips use the active app theme instead of the standard Windows message-box appearance. Windows file and folder selection dialogs remain native so they retain normal Explorer navigation and shell integration.

## Run

Double-click the newest **USB Drive Builder vX.Y.Z.exe** in the `dist` folder and accept the administrator prompt. Disk partitioning requires elevation. The WPF application performs storage operations without displaying a PowerShell window.

The version appears in the app footer and executable metadata. Historical versioned executables can coexist in the shared `dist` folder.

For operating instructions, see the [Quick User Guide](docs/QUICK_USER_GUIDE.md). For support ownership, troubleshooting, and escalation details, see the [Technician Handoff](docs/TECHNICIAN_HANDOFF.md).

## Build and publish

The project targets .NET 8 for Windows:

```powershell
dotnet build .\LaptopQaUsbBuilder.csproj -c Release
```

For a versioned release, update `AppVersion`, `AssemblyVersion`, and `FileVersion` in `LaptopQaUsbBuilder.csproj`, then run:

```powershell
.\publish.cmd
```

The publish script uses a staging directory and places the versioned executable in `dist` without deleting historical builds.

## Safety and logs

- The app initializes every selected USB target as MBR. Windows installation media is UEFI-only so the laptop's internal Windows system disk is GPT.
- The selected USB disks are completely erased; this cannot be undone.
- Targets are checked again before erasure and rejected if Windows reports them as boot, system, non-USB, or changed since selection.
- After erasure, the app refreshes Windows' storage state and initializes the disk only when it is actually RAW, avoiding redundant initialization failures on USB sticks that remain MBR.
- Sources stored on a queued target disk are rejected before building.
- Protected metadata such as `System Volume Information` and `$RECYCLE.BIN` is skipped when a drive root is used as a source.
- FAT32 sizes and volume-label lengths are validated against Windows limits.
- Copy, build, and crash logs are saved under `%LOCALAPPDATA%\LaptopQAUsbBuilder\Logs`.
- PowerShell CLIXML errors are decoded before logging so Windows storage failures retain their useful error message. Exception stack traces retain source filenames and line numbers while removing the developer's local build path.
- Bootable ISO preparation targets supported removable USB flash sticks with native NTFS UEFI support. It does not create FAT32 or legacy-BIOS boot media, and fixed-media external hard disks are not supported as boot targets. Secure Boot acceptance still depends on the ISO and injected-driver signatures and target firmware policy.
