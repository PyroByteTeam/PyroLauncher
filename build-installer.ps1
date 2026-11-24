# Builds a self-contained PyroLauncher and wraps it into a single-file setup:
#   artifacts\PyroLauncher-Setup-<version>.exe
# Usage:  .\build-installer.ps1 [-Runtime win-x64|win-arm64]
param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$app = Join-Path $root "artifacts\app"
$installerDir = Join-Path $root "src\PyroLauncher.Installer"
$payload = Join-Path $installerDir "payload.zip"

[xml]$proj = Get-Content (Join-Path $root "src\PyroLauncher\PyroLauncher.csproj")
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Building PyroLauncher $version ($Runtime)…"

if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish (Join-Path $root "src\PyroLauncher\PyroLauncher.csproj") -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $app
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed" }

Get-ChildItem $app -Filter *.xml | Remove-Item -Force   # NuGet API docs, not needed at runtime
Set-Content -Path (Join-Path $app "version.txt") -Value $version -NoNewline
if (Test-Path $payload) { Remove-Item $payload -Force }
Compress-Archive -Path (Join-Path $app "*") -DestinationPath $payload -CompressionLevel Optimal

$out = Join-Path $root "artifacts\setup"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $installerDir "PyroLauncher.Installer.csproj") -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$version -o $out
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed" }

$final = Join-Path $root "artifacts\PyroLauncher-Setup-$version.exe"
Move-Item (Join-Path $out "PyroLauncher.Setup.exe") $final -Force
Remove-Item $payload -Force
Write-Host "Done: $final ($([math]::Round((Get-Item $final).Length / 1MB, 1)) MB)"
