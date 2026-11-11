param(
    [ValidateSet("win-x64", "win-arm64")][string]$Runtime = "win-x64",
    [switch]$SelfContained
)
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    dotnet test tests/ArchiveBox.Core.Tests/ArchiveBox.Core.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    $flavor = if ($SelfContained) { "standalone" } else { "lite" }
    $destination = Join-Path $PSScriptRoot "artifacts/ArchiveBox-$Runtime-$flavor"
    if (Test-Path $destination) { Remove-Item -Recurse -Force $destination }
    $standalone = if ($SelfContained) { "true" } else { "false" }
    dotnet publish src/ArchiveBox.App/ArchiveBox.App.csproj -c Release -r $Runtime --self-contained $standalone -p:PublishReadyToRun=false -p:DebugType=None -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
    Copy-Item README.md (Join-Path $destination "README.md")
    $archive = "$destination.zip"
    if (Test-Path $archive) { Remove-Item $archive }
    Compress-Archive -Path $destination -DestinationPath $archive -CompressionLevel Optimal
    Write-Host "Created: $archive"
}
finally { Pop-Location }
