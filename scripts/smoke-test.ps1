param([string]$Executable = "artifacts/ArchiveBox-v0.2.0-win-x64/ArchiveBox.exe")
$ErrorActionPreference = "Stop"
$exe = (Resolve-Path $Executable).Path
$data = Join-Path $PSScriptRoot "../artifacts/smoke-test"
$null = New-Item -ItemType Directory -Path $data -Force
$data = (Resolve-Path $data).Path
$marker = Join-Path $data 'smoke-success.txt'
if (Test-Path $marker) { Remove-Item $marker }
$info = [System.Diagnostics.ProcessStartInfo]::new($exe)
$info.UseShellExecute = $false
$info.Arguments = '--smoke-test'
$info.EnvironmentVariables['ARCHIVEBOX_SMOKE_DATA'] = $data
$process = [System.Diagnostics.Process]::Start($info)
try {
    if (-not $process.WaitForExit(45000)) { throw 'Window initialization did not finish within 45 seconds.' }
    if ($process.ExitCode -ne 0) { throw "Application startup failed with exit code $($process.ExitCode)." }
    if (-not (Test-Path $marker)) { throw 'Application exited without completing window initialization.' }
    Write-Host (Get-Content $marker -Raw)
}
catch {
    Get-ChildItem $data -Filter '*.log' | ForEach-Object { Write-Host (Get-Content $_.FullName -Raw) }
    throw
}
finally {
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
