## ArchiveBox v0.3.2

The primary runtime remains **.NET Framework 4.8**. .NET 10 is not required. The application UI remains in Chinese.

### Application icon

- Replaced the generic executable icon with the owner-provided green archive artwork.
- Applied the same icon to application windows, the taskbar, and the sidebar logo.
- Preserved the original transparent PNG and embedded a multi-resolution Windows icon, from 16 to 256 pixels, for different display scales.
- Embedded all icon assets in the executable; displaying the icon does not access the network.

This release retains the expiry scan responsiveness and tooltip removal fixes from v0.3.1.

### Upgrade and validation

Extract `ArchiveBox-v0.3.2-win-x64.zip` into a new application folder and run `ArchiveBox.exe`. Existing archive files, settings, favorites, expiry rules, and the database remain in their previous locations.

Windows CI runs 50 core tests and desktop startup/interaction checks on Windows Server 2022 and 2025, and renders screenshots for review. The exact Windows 11 21H2 build 22000 environment is not available in CI.

`SHA256SUMS.txt` contains the package checksum.
