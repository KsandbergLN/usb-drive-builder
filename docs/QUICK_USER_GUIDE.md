# USB Drive Builder — Quick User Guide

Use this guide when you need to create one or more standardized QA/support USB drives.

## Safety first

**Building permanently erases every selected USB drive.** Check the drive name, number, and capacity before you type `ERASE`.

## 1. Start the app

Open the newest `USB Drive Builder vX.Y.Z.exe` in the `dist` folder. Approve the Windows administrator prompt.

## 2. Select the target drive(s)

Insert the USB drive(s), then:

1. Select **Refresh** if needed.
2. Click each USB drive card you want to build.
3. Hover over a card to see its capacity, serial number, and unique ID.

Any currently assigned drive letters appear beside the disk number, such as `Disk 2 | E:, F:`.

Selected cards form a queue and are processed in order.

## 3. Check the partition layout

The normal **Defaults** layout is:

- `DELL DIAG` — `50 MB` — `FAT32`
- `Win11 Boot` — `20 GB` — `NTFS`
- `IT SUPP` — `*` — `exFAT`

`*` means “use all remaining space.” Exactly one partition must use `*`.

Every fixed size must include `MB` or `GB`. A missing or malformed unit turns the size field red; `*` is the only unit-free entry.

To change the layout, edit the partition rows on the main screen. Use **+** to add a row, **−** to remove one, and the drag handle to reorder rows. Use the three-bar menu in the upper-left to edit and save the defaults restored by **Defaults**.

## 4. Add content

On the partition row that should receive content, select **Add**. The themed chooser provides:

- Select **Files** for individual files.
- Select **Folder** for a folder whose contents should be merged into the partition root.
- Select **XML** on NTFS for an answer file.
- Select **ISO** on an NTFS partition to create bootable 64-bit UEFI Windows installation media.
- On NTFS, **Drivers** is always visible and can manage multiple driver folders and individually selected INF packages before or after ISO selection.
- On NTFS, **Scripts** is always visible and opens a themed manager where **Add Files** can be used repeatedly to collect scripts and supporting files from multiple locations before or after ISO selection.

Select **Clear** on the partition row to remove all its content selections.

The theme-matched muted green Add button and muted red Clear button are stacked vertically. Add opens a content manager that stays open while you make multiple selections; choose **Close** when finished. Each Files, Folder, XML, ISO, Drivers, or Scripts action changes from grey to green immediately after that content type is selected; cancelling a picker does not change its color. Drivers and Scripts are always shown for NTFS and do not require a selected XML. They can be selected before the ISO, but the build requires an ISO before either can be applied. When scripts are present without an XML, the app generates the required `Autounattend.xml` automatically. Green summary text beside the controls shows which content types are currently attached: `AUXML`, `ISO`, `Folder`, `Files`, `Drivers`, and `Scripts`. Hover over this area to see the selected paths.

Folder contents are copied into the destination partition and merged in the order selected. An answer file is renamed to `Autounattend.xml` at the partition root. The XML button also turns green automatically when an XML file is present at the root of a selected folder.

After files or folders are selected, the app estimates how much partition space they require. If they will not fit, it shows a warning and highlights the affected segment in the Partition Layout card. Hover over that segment to compare its capacity with the estimated requirement.

When you select an ISO, choose the Windows edition. Windows 11 Pro is selected automatically when present. ISO selection does not ask about drivers because the always-visible **Drivers** action manages them independently before or after ISO selection. **Add Folder** retains previous folders and recursively adds another. **Add Driver Files** is one combined picker for any mixture of INF, ZIP, and CAB files. The app safely extracts archive packs into its local content-addressed cache before scanning them for INFs. If an extracted folder contains legacy compressed payloads such as `.sy_` in place of an INF-required `.sys`, the app expands them into a separate cache without altering the source. Remove deletes the highlighted source, Clear removes all driver sources, and Close applies the list without changing the ISO; the title-bar × has the same behavior. Drivers are added only to the installed Windows image so accepted packages are available during OOBE; `boot.wim` is unchanged. Before ISO preparation, the app checks the effective x64 catalog and payloads actually referenced by applicable CopyFiles or service definitions; unused INF inventory entries are ignored. Incomplete packages stop before servicing and identify the INF and missing filenames; the full list is saved in the build log. Damaged/password-protected archives, unsafe ZIP paths, and archives without INFs also stop before the USB is erased. If DISM rejects drivers during servicing, every failed INF and its reported error code are written individually to the build log, including when servicing ultimately stops. Complete but DISM-invalid packages can still be skipped and logged. **Allow unsigned drivers** in Config applies DISM `/ForceUnsigned`; it does not guarantee Windows or Secure Boot acceptance.

The Scripts manager accepts all file types and keeps its list open for repeated additions from different locations. Remove deletes the highlighted entry, Clear empties the list, and Close applies it; duplicate filenames from separate locations are not allowed because all files share one destination. All selected files are copied into `sources\$OEM$\$$\Setup\Scripts` after the Windows media is copied. CMD, BAT, PowerShell, VBS, JS, and WSF files execute sequentially in listed order; XML and every other format are treated as supporting files that scripts can reference with `%~dp0`. The app modifies only the USB copy of a selected `Autounattend.xml`, adding a synchronous `specialize` command after any existing commands. If no XML was selected, it creates a minimal `Autounattend.xml`. Windows Setup runs the recognized scripts as `SYSTEM` before OOBE. After they finish, every selected file and the generated runner/cleanup files delete automatically. Original source files are not changed.

The ISO partition must be NTFS, use a fixed size of at least 5 GB, and be large enough for the prepared contents. The app keeps only the chosen Windows edition, exports it with maximum WIM compression, prepares it on local storage, and caches the result so all queued USBs—and matching later builds—reuse the same media. Maximum compression saves USB and cache space but can make the initial preparation take longer. Cached media is stored at `%LOCALAPPDATA%\LaptopQAUsbBuilder\MediaCache` and may be deleted while the app is closed. The FAT32 `DELL DIAG` partition remains diagnostics-only and is not required for Windows boot. Bootable ISO support is intended for supported Dell-compatible removable USB flash sticks with native NTFS UEFI support; fixed-media external hard disks are not supported as boot targets. Only one bootable ISO partition is supported per USB drive.

The USB itself uses MBR, with no active partition or installed legacy MBR boot program. Select its UEFI entry in the Dell boot menu; Windows Setup then installs Windows to a GPT internal system disk. Secure Boot still depends on the selected ISO's signatures and laptop firmware policy.

## 5. Build

1. Review the drive cards, partition rows, preview, and selected sources.
2. Type `ERASE` in the confirmation box.
3. Select **Build USB Queue**.

The app immediately shows **Preparing build** with an indeterminate progress bar while it checks the selected disks, source paths, capacity, and prepares or retrieves cached Windows media. If preflight succeeds, it proceeds directly into the USB queue without another confirmation or driver-report prompt. Safety and preparation failures still stop before anything is erased.

The build is blocked before erasure if the complete selected content—including extracted ISO files and other files or folders assigned to that partition—is estimated not to fit.

The app rechecks each drive before erasing it, then formats, copies, and verifies it. Do not disconnect drives or close the app while the status shows **Building**.

Below the progress bar, **Current activity** identifies the active operation and shows byte-based completion percentage during copying. The app intentionally does not display time estimates because formatting, ISO mounting, small-file overhead, antivirus scanning, and changing USB speeds make them unreliable.

Confirmations, warnings, errors, completion messages, and tooltips follow the selected Light, Dark, or AMOLED theme. File and folder selection windows remain standard Windows Explorer dialogs.

## 6. Finish and verify

When the queue completes, the dialog shows how many drives succeeded or failed. A failed drive does not stop later queued drives from running.

For a successful drive, safely eject it in Windows before removing it. If a drive fails, keep it connected, note the message, and collect the build log before retrying.

## Common fixes

| Problem | What to do |
|---|---|
| USB drive missing | Select **Refresh**, try another USB port, and confirm Windows sees the drive. |
| Build button disabled | Select a drive and type `ERASE` exactly. |
| Drive too small | Use a larger drive or reduce fixed partition sizes. |
| Source not found | Reconnect the source or choose the file/folder again. |
| Invalid partition settings | Use sizes such as `50 MB` or `20 GB`; ensure exactly one row uses `*`. |
| Incomplete driver package | Re-extract the original driver package and preserve every file referenced by its INF. The warning names the INF and missing files discovered during the current preflight. |
| Compressed driver pack cannot be extracted | Use an intact, non-password-protected ZIP or CAB containing INF driver packages. The source archive is never modified. |
| Driver injection fails | Confirm the folder contains extracted INF packages, ensure enough free space exists on the Windows system drive, and review the build log for the DISM error. |
| DISM error `0x80070070` | The Windows system drive ran out of space. Close the app and remove unneeded `MediaCache` or other disposable cache folders under `%LOCALAPPDATA%\LaptopQAUsbBuilder`, then retry. |

## Logs

Build and crash logs are stored at:

`%LOCALAPPDATA%\LaptopQAUsbBuilder\Logs`

Logs may include source filenames and line numbers for troubleshooting, but they do not include the developer's local build or OneDrive path.
