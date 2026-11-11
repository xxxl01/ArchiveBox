## ArchiveBox v0.3.1

The primary runtime remains **.NET Framework 4.8**. .NET 10 is not required. The application UI remains in Chinese.

### Responsive expiry pages

- Fixed Expired and Expiring Soon traversing the entire archive even when no relevant expiry rules exist. These pages now scan only relevant rule scopes and return immediately when there are none.
- Indexed expiry rules and archive records to remove repeated full-list searches for each file.
- Stream directory metadata and show results in small batches, with checked-file and folder counts. Inaccessible locations are counted and skipped.
- Scanning no longer locks navigation. Cancel and leaving the page return immediately, even while a background filesystem call is blocked. Results from an abandoned scan cannot overwrite the new page.
- Suppress the empty-results message while loading. Sorting becomes available when loading finishes.

### Hover and status feedback

- Removed custom path hover tooltips from folder trees and file rows.
- Removed floating notification banners. Operation results appear inline below the page title.
- Removed per-record filesystem checks from the task history page.

### Upgrade and validation

Extract `ArchiveBox-v0.3.1-win-x64.zip` into a new application folder and run `ArchiveBox.exe`. Existing archive files, settings, favorites, expiry rules, and the database remain in their previous locations.

Windows CI runs core tests including a 10,000-file/6,000-rule scenario, expiry overrides, branch pruning, result batching, and cancellation. Desktop interaction checks deliberately block filesystem reads and verify that navigation and cancellation remain responsive and old results are discarded. Startup and interaction checks run on Windows Server 2022 and 2025; the exact Windows 11 21H2 build 22000 environment is not available in CI.

If startup fails, use `Diagnose-ArchiveBox.cmd` and share the resulting logs. `SHA256SUMS.txt` contains the package checksum.
