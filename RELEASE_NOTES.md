## .NET Framework 4.8 is now the primary runtime

ArchiveBox now targets **.NET Framework 4.8**. This is the main application release, not an optional compatibility build. The previous .NET 10 standalone/lite packages have been replaced by one Windows x64 package. **.NET 10 is not required to run this version.**

The deployment target includes **Windows 11 21H2, build 22000**. Windows 11 normally includes .NET Framework 4.8 or a compatible in-place update. The exact build 22000 environment has not been available for direct testing; Windows CI results do not substitute for testing that specific Windows version.

## Download and run

Download `ArchiveBox-v0.2.0-win-x64.zip`, extract the **entire folder**, and run `ArchiveBox.exe`. Keep the EXE, configuration file, DLLs, and native SQLite library together. Do not reuse DLLs from v0.1.0.

`SHA256SUMS.txt` contains the package checksum. The application UI remains in Chinese.

## Changes

- Retargeted the WPF desktop application and Windows tests to .NET Framework 4.8.
- Adapted file copying, hashing, path handling, directory selection, and dependencies for Framework 4.8.
- Added an explicit startup entry point that captures failures before WPF resources are loaded.
- Added fatal UI and background exception logging, with a visible error message and log location for managed UI failures.
- Added `Diagnose-ArchiveBox.cmd` to record the Windows build, Framework release, process output, and exit code when startup fails before application logging starts.
- Kept the existing archive database format and file locations. No database reset is required.

## Validation and limitations

The release workflow runs the core tests under the .NET Framework test host and checks packaged WPF startup, a nonempty directory, refresh, and view switching on Windows Server 2022. The regular build also checks Windows Server 2025. Neither runner is Windows 11 21H2.

If the application still exits on your computer, run `Diagnose-ArchiveBox.cmd` and share the generated `launch-*.log` and `error-*.log` from `%LocalAppData%\ArchiveBox` (or `%TEMP%\ArchiveBox` if the normal log directory is unavailable).

Expiry remains a reminder only. Files are recycled only after confirmation, and copied source files are unaffected by archive expiry.
