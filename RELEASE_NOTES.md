## ArchiveBox v0.3.0

The primary runtime remains **.NET Framework 4.8**. .NET 10 is not required. The application UI remains in Chinese.

### Navigation and layout

- Added Home with persistent quick-folder cards. Use **Add to Home** in the browser, or add a folder from Home.
- Renamed All Archives to Archive Files.
- Added Settings as the single place to select or change the one active archive root.
- Removed the permanent bottom status/details area so file lists use the available height.
- Removed the outer scrolling destination panel and the inline new-folder/expiry forms.

### Copy and move

- The browser destination panel contains only a folder tree and fixed **Copy**, **Move**, and **New Folder** buttons.
- Select an existing folder in the tree, then click Copy or Move. Both actions show a confirmation dialog with the destination and collision handling before executing.
- New Folder opens its own dialog; typing a folder name is never required to select an existing destination.
- Selected directories can be copied or moved with their hierarchy and empty directories preserved. Moving removes source files only after content verification and only removes empty source directories.

### Archive Files and expiry

- Archive Files now shows the archive root's real contents, including files that were already there before using the app.
- Folders are on the left and files are on the right. The unrelated destination panel is removed from this page.
- Set expiry for a selected folder using its folder action, or for selected files using the file toolbar.
- The nearest ancestor folder rule applies to descendants and newly archived files. A file-specific expiry overrides that rule; an explicit no-expiry setting also overrides it. Clear the override to restore inheritance.
- Expiring Soon and Expired evaluate the effective rules. Expiry remains a reminder; recycling still requires confirmation.

### Upgrade and validation

Extract `ArchiveBox-v0.3.0-win-x64.zip` into a new application folder and run `ArchiveBox.exe`. Existing archive files and the database remain in their previous locations. The database update adds favorites and expiry rules without resetting existing records; existing explicit expiry dates are migrated.

Windows CI runs the core tests and interaction checks for Home persistence, confirmation cancellation, copy/move, directory copy, new folders, existing archive files, expiry inheritance/overrides, and small-window button visibility. It also renders Home, Browser, Archive Files and Settings screenshots for review. The exact Windows 11 21H2 build 22000 environment is not available in CI.

If startup fails, use `Diagnose-ArchiveBox.cmd` and share the resulting logs. `SHA256SUMS.txt` contains the package checksum.
