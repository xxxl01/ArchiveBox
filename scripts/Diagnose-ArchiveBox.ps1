param([switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
$folder = if ($SmokeTest) { Join-Path $PSScriptRoot 'diagnostic-smoke-test' } else { Join-Path $env:LOCALAPPDATA 'ArchiveBox' }
try { $null = New-Item -ItemType Directory -Path $folder -Force }
catch {
    $folder = Join-Path $env:TEMP 'ArchiveBox'
    $null = New-Item -ItemType Directory -Path $folder -Force
}
$log = Join-Path $folder ('launch-{0}.log' -f (Get-Date -Format 'yyyy-MM-dd-HHmmss'))
try {
    $framework = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
    $windows = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    @(
        'ArchiveBox .NET Framework 4.8 launch diagnostics'
        ('Time: {0:o}' -f (Get-Date))
        ('Windows build: {0}.{1}' -f $windows.CurrentBuildNumber, $windows.UBR)
        ('OS architecture: {0}' -f $env:PROCESSOR_ARCHITECTURE)
        ('.NET Framework release: {0}' -f $framework.Release)
    ) | Out-File $log -Encoding utf8
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'This package requires a 64-bit Windows system.' }
    if (-not $framework -or $framework.Release -lt 528040) { throw '.NET Framework 4.8 or later is required. Windows 11 normally includes it.' }
    $exe = Join-Path $PSScriptRoot 'ArchiveBox.exe'
    if (-not (Test-Path $exe)) { throw 'ArchiveBox.exe is missing. Extract the complete ZIP before starting the application.' }
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.WorkingDirectory = $PSScriptRoot
    $info.UseShellExecute = $false
    $info.RedirectStandardError = $true
    $info.RedirectStandardOutput = $true
    if ($SmokeTest) {
        $info.Arguments = '--smoke-test'
        $info.EnvironmentVariables['ARCHIVEBOX_SMOKE_DATA'] = Join-Path $folder 'appdata'
    }
    $process = [System.Diagnostics.Process]::Start($info)
    $stderr = $process.StandardError.ReadToEndAsync()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    if ($SmokeTest) {
        if (-not $process.WaitForExit(120000)) { $process.Kill(); throw 'Diagnostic startup check timed out.' }
    }
    else { $process.WaitForExit() }
    $exitCode = $process.ExitCode
    $stderr.Result | Out-File $log -Append -Encoding utf8
    $stdout.Result | Out-File $log -Append -Encoding utf8
    ('Exit code: {0} (0x{1:X8})' -f $exitCode, ($exitCode -band 0xffffffffL)) | Out-File $log -Append -Encoding utf8
    $process.Dispose()
    Write-Host ('Launch log: ' + $log)
    if ($exitCode -ne 0) { throw ('The application exited unexpectedly. Log: ' + $log) }
    exit 0
}
catch {
    $_.Exception.ToString() | Out-File $log -Append -Encoding utf8
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ('Launch log: ' + $log)
    exit 1
}
