# Builds PyroLauncher for every supported runtime. For each one you get:
#   artifacts\PyroLauncher-Setup-<version>-<runtime>.exe     installer / updater / uninstaller
#   artifacts\PyroLauncher-<version>-<runtime>-portable.zip  unzip and run, no install
#
# Usage:
#   .\build-installer.ps1                         # everything (win-x64 and win-arm64)
#   .\build-installer.ps1 -Runtime win-x64        # one runtime
#   .\build-installer.ps1 -NoPortable             # installers only
param(
    [string[]]$Runtime = @("win-x64", "win-arm64"),
    [switch]$NoPortable
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$installerDir = Join-Path $root "src\PyroLauncher.Installer"
$payload = Join-Path $installerDir "payload.zip"

[xml]$proj = Get-Content (Join-Path $root "src\PyroLauncher\PyroLauncher.csproj")
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
New-Item -ItemType Directory -Force $artifacts | Out-Null

$singleFile = @("-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none")
$built = @()

foreach ($rid in $Runtime) {
    Write-Host "`n=== PyroLauncher $version ($rid) ===" -ForegroundColor Cyan
    $app = Join-Path $artifacts "app-$rid"
    if (Test-Path $app) { Remove-Item $app -Recurse -Force }

    dotnet publish (Join-Path $root "src\PyroLauncher\PyroLauncher.csproj") -c Release -r $rid --self-contained true @singleFile -o $app
    if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed ($rid)" }
    Get-ChildItem $app -Filter *.xml | Remove-Item -Force   # NuGet API docs, not needed at runtime
    Set-Content -Path (Join-Path $app "version.txt") -Value $version -NoNewline

    # Portable zip
    if (-not $NoPortable) {
        $zip = Join-Path $artifacts "PyroLauncher-$version-$rid-portable.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path (Join-Path $app "*") -DestinationPath $zip -CompressionLevel Optimal
        $built += $zip
    }

    # Installer with the launcher embedded as payload.zip
    if (Test-Path $payload) { Remove-Item $payload -Force }
    Compress-Archive -Path (Join-Path $app "*") -DestinationPath $payload -CompressionLevel Optimal
    $out = Join-Path $artifacts "setup-$rid"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish (Join-Path $installerDir "PyroLauncher.Installer.csproj") -c Release -r $rid --self-contained true @singleFile `
        -p:Version=$version -o $out
    $failed = $LASTEXITCODE -ne 0
    Remove-Item $payload -Force
    if ($failed) { throw "Installer publish failed ($rid)" }

    $setup = Join-Path $artifacts "PyroLauncher-Setup-$version-$rid.exe"
    Move-Item (Join-Path $out "PyroLauncher.Setup.exe") $setup -Force
    Remove-Item $out, $app -Recurse -Force
    $built += $setup
}

Write-Host "`nDone:" -ForegroundColor Green
foreach ($f in $built) { Write-Host ("  {0}  ({1} MB)" -f (Split-Path $f -Leaf), [math]::Round((Get-Item $f).Length / 1MB, 1)) }
