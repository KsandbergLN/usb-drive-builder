# Laptop QA USB Drive Builder — Quick User Guide

Use this guide when you need to create one or more standardized QA/support USB drives.

## Safety first

**Building permanently erases every selected USB drive.** Check the drive name, number, and capacity before you type `ERASE`.

## 1. Start the app

Open the newest `Laptop QA USB Drive Builder vX.Y.Z.exe` in the `dist` folder. Approve the Windows administrator prompt.

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

On the partition row that should receive content:

- Select **Files** for individual files.
- Select **Folders** for one or more folders.
- Select **XML** for an answer file.
- Select **ISO** on an NTFS partition to create bootable 64-bit UEFI Windows installation media.
- Select **Clear** to remove all content selections for that partition.

Folder contents are copied into the destination partition and merged in the order selected. An answer file is renamed to `Autounattend.xml` at the partition root. The XML button also turns green automatically when an XML file is present at the root of a selected folder.

After files or folders are selected, the app estimates how much partition space they require. If they will not fit, it shows a warning and highlights the affected segment in the Partition Layout card. Hover over that segment to compare its capacity with the estimated requirement.

The ISO partition must be NTFS, use a fixed size of at least 5 GB, and be large enough for the ISO contents. The app copies and verifies the complete boot set. The FAT32 `DELL DIAG` partition remains diagnostics-only and is not required for Windows boot. Bootable ISO support is intended for supported Dell-compatible removable USB flash sticks with native NTFS UEFI support; fixed-media external hard disks are not supported as boot targets. Only one bootable ISO partition is supported per USB drive.

The USB itself uses MBR, with no active partition or installed legacy MBR boot program. Select its UEFI entry in the Dell boot menu; Windows Setup then installs Windows to a GPT internal system disk. Secure Boot still depends on the selected ISO's signatures and laptop firmware policy.

## 5. Build

1. Review the drive cards, partition rows, preview, and selected sources.
2. Type `ERASE` in the confirmation box.
3. Select **Build USB Queue**.
4. Read the final confirmation and select **Yes** only when the targets are correct.

The app immediately shows **Preparing build** with an indeterminate progress bar while it checks the selected disks, source paths, capacity, and ISO contents. The final erase confirmation appears after these safety checks finish.

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

## Logs

Build and crash logs are stored at:

`%LOCALAPPDATA%\LaptopQAUsbBuilder\Logs`

Logs may include source filenames and line numbers for troubleshooting, but they do not include the developer's local build or OneDrive path.
