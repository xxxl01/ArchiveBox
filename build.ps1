param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'Install Visual Studio 2022 with the .NET desktop development workload.' }
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'MSBuild was not found.' }
    & $msbuild ArchiveBox.sln /restore /m /verbosity:minimal /p:Configuration=Release /p:DebugType=None /p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        $vstest = & $vswhere -latest -products * -find '**\vstest.console.exe' | Select-Object -First 1
        if (-not $vstest) { throw 'Visual Studio Test Platform was not found.' }
        $tests = Join-Path $PSScriptRoot 'tests/ArchiveBox.Core.Tests/bin/Release/net48'
        & $vstest (Join-Path $tests 'ArchiveBox.Core.Tests.dll') /Platform:x64 "/TestAdapterPath:$tests" '/Logger:console;verbosity=normal'
        if ($LASTEXITCODE -ne 0) { throw '.NET Framework 4.8 tests failed.' }
    }
    $project = [xml](Get-Content src/ArchiveBox.App/ArchiveBox.App.csproj -Raw)
    $version = $project.Project.PropertyGroup.Version
    $destination = Join-Path $PSScriptRoot "artifacts/ArchiveBox-v$version-win-x64"
    if (Test-Path $destination) { Remove-Item -Recurse -Force $destination }
    $null = New-Item -ItemType Directory -Path $destination -Force
    Copy-Item src/ArchiveBox.App/bin/Release/net48/* $destination -Recurse
    Copy-Item README.md $destination
    Copy-Item scripts/Diagnose-ArchiveBox.cmd $destination
    # Windows PowerShell 5.1 needs a BOM to reliably read UTF-8 scripts.
    [IO.File]::WriteAllText((Join-Path $destination 'Diagnose-ArchiveBox.ps1'),
        [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'scripts/Diagnose-ArchiveBox.ps1')), [Text.UTF8Encoding]::new($true))
    if (Test-Path (Join-Path $destination 'ArchiveBox.runtimeconfig.json')) { throw 'Unexpected modern .NET runtime configuration in Framework package.' }
    if (-not (Select-String -Path (Join-Path $destination 'ArchiveBox.exe.config') -SimpleMatch '.NETFramework,Version=v4.8' -Quiet)) { throw 'Framework 4.8 startup configuration is missing.' }
    if (-not (Test-Path (Join-Path $destination 'e_sqlite3.dll'))) { throw 'Windows x64 SQLite native library is missing.' }
    $archive = "$destination.zip"
    if (Test-Path $archive) { Remove-Item $archive }
    Compress-Archive -Path $destination -DestinationPath $archive -CompressionLevel Optimal
    Write-Host "Created: $archive"
}
finally { Pop-Location }
