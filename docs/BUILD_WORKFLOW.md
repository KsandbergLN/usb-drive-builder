# Build workflow and safety

## Queue

Select one or more disks reported by Windows as USB. Each target is revalidated immediately before `clean`, partitioning, copying, and verification. Drives run sequentially; one failure is logged without stopping later drives. Build preflight checks source existence, capacity, media identity, and content compatibility before any target is erased.

Enter `ERASE` to enable **Build USB Queue**. The app runs DiskPart `clean`, refreshes storage state, rediscovers the device by persistent ID if its disk number changes, initializes MBR only when needed, creates the requested layout, and verifies labels and file systems. All target data is permanently removed. Windows media is UEFI-only and intended for supported removable USB flash sticks with native NTFS UEFI support.

## Progress and cancellation

The Activity bar shows the current operation. The queue tracker shares Windows-media preparation across selected drives, then advances the active drive through partitioning, copying, and verification. Progress estimates are intentionally omitted. Cancellation is available from preflight through USB writing; active DISM is stopped safely, mounted WIM state is discarded, and an ISO is dismounted.

## Cache and recovery

Windows media, driver packs, expanded payloads, and private staging are cached under `%LOCALAPPDATA%\\LaptopQAUsbBuilder`. Cache identity includes source content, edition, driver manifest, unsigned-driver choice, compression mode, and staging algorithm. A matching later build can reuse prepared media. After the queue completes, **Keep cache** (default) or **Clear cache** is offered once for the whole queue. USB contents, sources, logs, and profiles are unaffected.

If preparation succeeds but a later USB operation fails, retain the cache and build log for retry. Clear Cache removes available data and schedules locked remnants for cleanup after exit. Do not remove active cache directories while DISM is servicing an image.

## Logs and safety rules

Logs are saved under `%LOCALAPPDATA%\\LaptopQAUsbBuilder\\Logs`. PowerShell CLIXML errors are decoded into readable messages and exception paths are sanitized. Sources on a queued target are rejected, protected metadata is skipped when a drive root is used as a source, and targets reported as boot, system, non-USB, read-only, changed, or unsafe are rejected.

For troubleshooting, record the app version, target disk number/name/capacity, queue size, source types, exact message, and relevant build or crash log. Do not send ISO images, source folders, or answer files unless specifically approved.
