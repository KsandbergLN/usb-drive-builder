# Laptop QA USB Drive Builder — Technician Handoff

## Purpose

Laptop QA USB Drive Builder prepares standardized support USB media for laptop QA and support work. It creates an MBR-partitioned USB drive with UEFI-only Windows boot media, formats the requested partitions, copies selected content, and verifies the resulting labels and file systems.

This is an operations handoff for technicians. It does not describe how the application is built or maintained.

## Current release

- Application: Laptop QA USB Drive Builder
- Release: 1.4.52
- Platform: Windows 10/11, 64-bit
- Launch: Run the newest `Laptop QA USB Drive Builder vX.Y.Z.exe` from the `dist` folder
- Permissions: Windows administrator approval is required

## Standard output

The built drive uses MBR and, by default, has this visible layout:

| Partition | Size | Format | Typical contents |
|---|---:|---|---|
| `DELL DIAG` | 50 MB | FAT32 | Diagnostic tools |
| `Win11 Boot` | 20 GB | NTFS | Windows setup/support files, optional `Autounattend.xml`, and optional bootable UEFI Windows media |
| `IT SUPP` | `*` (remaining space) | exFAT | Support tools and other technician content |

The default layout can be changed in Settings. A valid MBR layout must contain 1–4 partitions and exactly one `*` remaining-space partition.

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
5. For each partition that needs content, use **Files** and/or **Folders**. Folder contents are merged into the root of that partition in selection order.
6. Use **XML** if an answer file is needed. The selected file is copied to that partition's root as `Autounattend.xml`. The XML button also turns green automatically when an XML file is detected at the root of a selected folder.
7. Use **ISO** on a fixed-size NTFS partition of at least 5 GB to create bootable 64-bit Windows installer media. The app retains and verifies the complete ISO boot set. The FAT32 `DELL DIAG` partition is diagnostics-only and is not involved in Windows boot. Bootable ISO support targets supported Dell-compatible removable USB flash sticks with native NTFS UEFI support, not fixed-media external hard disks. Only one ISO partition is supported.
8. Review any capacity warning in the Partition Layout card. Warning-colored segments identify partitions whose selected content is estimated not to fit; hover to see the required and available sizes.
9. Check the warning panel. Type `ERASE` exactly in the confirmation box.
10. Select **Build USB Queue** and approve the final confirmation.
11. Wait for the queue to finish. Do not remove a drive or close the application while a build is active.
12. Review the completion message. It reports successful and failed drives and gives the build-log path.

The Activity card displays the current operation and byte-based completion percentage during transfers. ETA and total-queue time estimates are intentionally omitted because formatting, ISO mounting, small-file overhead, antivirus scanning, and changing USB speed made them unreliable.

All application-generated confirmations, warnings, errors, and completion dialogs follow the active Light, Dark, or AMOLED theme. Native Windows file and folder pickers are intentionally unchanged.

Each drive is rechecked immediately before it is erased, then built and verified before the next queued drive starts. If one drive fails, later queued drives still run.

After `Clear-Disk`, the app refreshes Windows storage state and initializes only a genuinely RAW disk. If a cleared USB stick already remains empty MBR, partition creation continues without issuing a redundant initialization command. Storage errors serialized by PowerShell as CLIXML are decoded into readable build-log messages.

Selected files and folders are measured when attached and checked again during preflight. The preflight combines their sizes with validated extracted ISO contents and a filesystem safety allowance. If any partition is estimated to be too small, the build stops before the destructive confirmation or erasure.

## What “successful” means

For each successful drive, the app has:

- erased and initialized the USB target as MBR while leaving every partition inactive for UEFI-only startup;
- created and formatted the configured partitions;
- copied the selected files and folders;
- copied an explicitly selected `Autounattend.xml`, prepared NTFS UEFI media, and extracted validated Windows ISO contents;
- verified the expected partition labels and file systems.

The app does not modify the source ISO. Bootable media targets supported removable USB flash sticks and 64-bit UEFI Windows Setup, not fixed-media external hard disks or legacy BIOS. It relies on the Dell firmware's native NTFS UEFI support and does not require a FAT32 boot partition. Secure Boot acceptance depends on the ISO signatures and target firmware policy.

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
