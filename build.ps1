# Builds PyroLauncher release files into artifacts\. Runs on Windows, macOS and Linux (PowerShell 7+).
#
#   Windows   PyroLauncher-Setup-<version>-win-<arch>.exe      installer / updater / uninstaller
#             PyroLauncher-<version>-win-<arch>-portable.zip   unzip and run
#   macOS     PyroLauncher-<version>-osx-<arch>.dmg            disk image (built on a Mac only)
#             PyroLauncher-<version>-osx-<arch>.zip            PyroLauncher.app
#   Linux     PyroLauncher-<version>-linux-<arch>.deb          Debian, Ubuntu, Mint...
#             PyroLauncher-<version>-linux-<arch>.rpm          Fedora, openSUSE...
#             PyroLauncher-<version>-linux-<arch>.AppImage     any distro (built on Linux only)
#             PyroLauncher-<version>-linux-<arch>.tar.gz       extract and run ./PyroLauncher
#
# Packaging tools (nFPM for .deb/.rpm, appimagetool) are downloaded into .tools/ on first use.
#
# Usage:
#   ./build.ps1                                  # everything
#   ./build.ps1 -Platform windows                # windows | macos | linux, one or more
#   ./build.ps1 -Runtime win-x64, linux-x64      # exact runtimes
#   ./build.ps1 -NoPortable                      # Windows: installers only
#
# The Windows setup is a WPF app, so it can only be built on Windows. Elsewhere, Windows runtimes
# produce the portable zip only.
param(
    [ValidateSet("windows", "macos", "linux")]
    [string[]]$Platform = @("windows", "macos", "linux"),
    [string[]]$Runtime,
    [switch]$NoPortable
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$appProject = Join-Path $root "src/PyroLauncher.Desktop/PyroLauncher.Desktop.csproj"
$installerDir = Join-Path $root "src/PyroLauncher.Installer"
$payload = Join-Path $installerDir "payload.zip"
$logo = Join-Path $root "src/PyroLauncher.Desktop/Assets/logo.png"

if (-not $Runtime) {
    $Runtime = @()
    if ($Platform -contains "windows") { $Runtime += "win-x64", "win-arm64" }
    if ($Platform -contains "macos")   { $Runtime += "osx-x64", "osx-arm64" }
    if ($Platform -contains "linux")   { $Runtime += "linux-x64", "linux-arm64" }
}

[xml]$proj = Get-Content $appProject
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
New-Item -ItemType Directory -Force $artifacts | Out-Null

$singleFile = @("-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none")
$built = @()

# ---------------------------------------------------------------- helpers

# Zip a folder, marking the given files executable (Compress-Archive drops Unix permissions).
function New-UnixZip([string]$Source, [string]$Zip, [string]$Prefix, [string[]]$Executables) {
    Add-Type -AssemblyName System.IO.Compression
    if (Test-Path $Zip) { Remove-Item $Zip -Force }
    $fs = [IO.File]::Create($Zip)
    $archive = [IO.Compression.ZipArchive]::new($fs, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($dir in Get-ChildItem $Source -Recurse -Directory) {
            $rel = [IO.Path]::GetRelativePath($Source, $dir.FullName).Replace('\', '/')
            $e = $archive.CreateEntry("$Prefix$rel/")
            $e.ExternalAttributes = (0x41ED -shl 16)                     # drwxr-xr-x
        }
        foreach ($file in Get-ChildItem $Source -Recurse -File) {
            $rel = [IO.Path]::GetRelativePath($Source, $file.FullName).Replace('\', '/')
            $e = $archive.CreateEntry("$Prefix$rel", [IO.Compression.CompressionLevel]::Optimal)
            $mode = if ($Executables -contains $rel) { 0x81ED } else { 0x81A4 }   # -rwxr-xr-x / -rw-r--r--
            $e.ExternalAttributes = ($mode -shl 16)
            $out = $e.Open(); $in = [IO.File]::OpenRead($file.FullName)
            try { $in.CopyTo($out) } finally { $in.Dispose(); $out.Dispose() }
        }
    } finally { $archive.Dispose(); $fs.Dispose() }

    # .NET records Windows as the creating OS, so unzip tools ignore the Unix modes. Patch every
    # central directory entry's "version made by" host byte to 3 (Unix).
    $bytes = [IO.File]::ReadAllBytes($Zip)
    $eocd = $bytes.Length - 22
    while ([BitConverter]::ToUInt32($bytes, $eocd) -ne 0x06054b50) { $eocd-- }
    $count = [BitConverter]::ToUInt16($bytes, $eocd + 10)
    $p = [BitConverter]::ToUInt32($bytes, $eocd + 16)
    for ($i = 0; $i -lt $count; $i++) {
        if ([BitConverter]::ToUInt32($bytes, $p) -ne 0x02014b50) { throw "Bad zip central directory in $Zip" }
        $bytes[$p + 5] = 3
        $p += 46 + [BitConverter]::ToUInt16($bytes, $p + 28) + [BitConverter]::ToUInt16($bytes, $p + 30) + [BitConverter]::ToUInt16($bytes, $p + 32)
    }
    [IO.File]::WriteAllBytes($Zip, $bytes)
}

# tar.gz a folder under a top-level directory, marking the given files executable.
function New-TarGz([string]$Source, [string]$Archive, [string]$Prefix, [string[]]$Executables) {
    if (Test-Path $Archive) { Remove-Item $Archive -Force }
    $fs = [IO.File]::Create($Archive)
    $gz = [IO.Compression.GZipStream]::new($fs, [IO.Compression.CompressionLevel]::Optimal)
    $tar = [Formats.Tar.TarWriter]::new($gz, [Formats.Tar.TarEntryFormat]::Pax, $false)
    try {
        $d = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::Directory, "$Prefix/")
        $d.Mode = [IO.UnixFileMode]0x1ED
        $tar.WriteEntry($d)
        foreach ($file in Get-ChildItem $Source -Recurse -File) {
            $rel = [IO.Path]::GetRelativePath($Source, $file.FullName).Replace('\', '/')
            $e = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, "$Prefix/$rel")
            $e.Mode = if ($Executables -contains $rel) { [IO.UnixFileMode]0x1ED } else { [IO.UnixFileMode]0x1A4 }
            $data = [IO.File]::OpenRead($file.FullName)
            try { $e.DataStream = $data; $tar.WriteEntry($e) } finally { $data.Dispose() }
        }
    } finally { $tar.Dispose(); $gz.Dispose(); $fs.Dispose() }
}

# Resize the logo to a square PNG; returns $null when no image tool is available.
function Get-LogoPng([int]$Size) {
    $out = Join-Path ([IO.Path]::GetTempPath()) "pyro-logo-$Size.png"
    if ($IsWindows) {
        Add-Type -AssemblyName System.Drawing
        $src = [Drawing.Image]::FromFile($logo)
        $bmp = [Drawing.Bitmap]::new($Size, $Size)
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($src, 0, 0, $Size, $Size)
        $g.Dispose(); $src.Dispose()
        $bmp.Save($out, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        return $out
    }
    if (Get-Command sips -ErrorAction SilentlyContinue) {
        sips -z $Size $Size $logo --out $out | Out-Null
        return $out
    }
    return $null
}

# Apple icon file holding PNG images (ic07 = 128, ic08 = 256, ic09 = 512 px).
function New-Icns([string]$Path) {
    $chunks = @()
    foreach ($pair in @(@("ic07", 128), @("ic08", 256), @("ic09", 512))) {
        $png = Get-LogoPng $pair[1]
        if (-not $png) { return $false }
        $chunks += , @([Text.Encoding]::ASCII.GetBytes($pair[0]), [IO.File]::ReadAllBytes($png))
        Remove-Item $png -Force
    }
    $ms = [IO.MemoryStream]::new()
    function Write-BE([int]$v) { $b = [BitConverter]::GetBytes($v); [Array]::Reverse($b); $ms.Write($b, 0, 4) }
    $total = 8; foreach ($c in $chunks) { $total += 8 + $c[1].Length }
    $ms.Write([Text.Encoding]::ASCII.GetBytes("icns"), 0, 4); Write-BE $total
    foreach ($c in $chunks) { $ms.Write($c[0], 0, 4); Write-BE (8 + $c[1].Length); $ms.Write($c[1], 0, $c[1].Length) }
    [IO.File]::WriteAllBytes($Path, $ms.ToArray())
    return $true
}

$tools = Join-Path $root ".tools"
$nfpmVersion = "2.49.0"

function Get-Download([string]$Url, [string]$Path) {
    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing
}

# nFPM builds .deb and .rpm on any OS. The archive is checked against the release's checksums.txt.
function Get-Nfpm {
    $dir = Join-Path $tools "nfpm-$nfpmVersion"
    $exe = Join-Path $dir ($(if ($IsWindows) { "nfpm.exe" } else { "nfpm" }))
    if (Test-Path $exe) { return $exe }
    $os = if ($IsWindows) { "Windows" } elseif ($IsMacOS) { "Darwin" } else { "Linux" }
    $arch = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq "Arm64") { "arm64" } else { "x86_64" }
    $name = "nfpm_${nfpmVersion}_${os}_${arch}" + $(if ($IsWindows) { ".zip" } else { ".tar.gz" })
    $base = "https://github.com/goreleaser/nfpm/releases/download/v$nfpmVersion"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $archive = Join-Path $dir $name
    $sums = Join-Path $dir "checksums.txt"
    Get-Download "$base/$name" $archive
    Get-Download "$base/checksums.txt" $sums
    $expected = (Get-Content $sums | Where-Object { $_ -match "\s$([regex]::Escape($name))$" }) -split '\s+' | Select-Object -First 1
    $actual = (Get-FileHash $archive -Algorithm SHA256).Hash
    if (-not $expected -or $actual -ne $expected.ToUpper()) { Remove-Item $dir -Recurse -Force; throw "nFPM checksum mismatch for $name" }
    if ($IsWindows) { Expand-Archive $archive $dir -Force } else { tar -xzf $archive -C $dir nfpm }
    Remove-Item $archive, $sums -Force
    Start-Sleep -Seconds 2                                          # give antivirus a moment to act
    if (-not (Test-Path $exe)) { return $null }
    return $exe
}

# Runs nFPM; returns $false when the OS refuses to start it (Windows Defender sometimes
# quarantines nfpm.exe as a false positive).
function Invoke-Nfpm([string]$Exe, [string[]]$Arguments) {
    if (-not $Exe -or -not (Test-Path $Exe)) { return $false }
    try { & $Exe @Arguments | Out-Host } catch { return $false }
    if ($LASTEXITCODE -ne 0) { throw "nFPM failed: $Arguments" }
    return $true
}

function Get-AppImageTool {
    $exe = Join-Path $tools "appimagetool-x86_64.AppImage"
    if (-not (Test-Path $exe)) {
        New-Item -ItemType Directory -Force $tools | Out-Null
        Get-Download "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" $exe
        chmod +x $exe
    }
    return $exe
}

$desktopEntry = @"
[Desktop Entry]
Type=Application
Name=PyroLauncher
GenericName=Minecraft Launcher
Comment=Minecraft launcher for modded instances
Exec={0}
Icon=pyrolauncher
Terminal=false
Categories=Game;
Keywords=minecraft;mods;modpack;launcher;
StartupWMClass=PyroLauncher

"@

function New-LinuxPackages([string]$Rid, [string]$App) {
    $arch = if ($Rid -eq "linux-arm64") { "arm64" } else { "amd64" }
    $work = Join-Path $artifacts "pkg-$Rid"
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Force $work | Out-Null
    $desktop = Join-Path $work "pyrolauncher.desktop"
    ($desktopEntry -f "/opt/pyrolauncher/PyroLauncher %U").Replace("`r`n", "`n") | Set-Content $desktop -NoNewline
    $q = { param($p) "'" + $p.Replace('\', '/') + "'" }
    $config = Join-Path $work "nfpm.yaml"
    @"
name: pyrolauncher
arch: $arch
platform: linux
version: $version
release: 1
section: games
priority: optional
maintainer: PyroByte Team
description: Minecraft launcher for modded instances (Modrinth, CurseForge, Fabric, Forge, NeoForge).
vendor: PyroByte
homepage: https://github.com/PyroByteTeam/PyroLauncher
license: GPL-3.0
contents:
  - src: $(& $q (Join-Path $App "PyroLauncher"))
    dst: /opt/pyrolauncher/PyroLauncher
    file_info: { mode: 0755 }
  - src: $(& $q (Join-Path $App "version.txt"))
    dst: /opt/pyrolauncher/version.txt
    file_info: { mode: 0644 }
  - src: /opt/pyrolauncher/PyroLauncher
    dst: /usr/bin/pyrolauncher
    type: symlink
  - src: $(& $q $desktop)
    dst: /usr/share/applications/pyrolauncher.desktop
    file_info: { mode: 0644 }
  - src: $(& $q $logo)
    dst: /usr/share/pixmaps/pyrolauncher.png
    file_info: { mode: 0644 }
overrides:
  deb:
    depends: [libc6, libfontconfig1, libx11-6, libice6, libsm6, "libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu70 | libicu67"]
  rpm:
    depends: [fontconfig, libX11, libICE, libSM, libicu]
"@ | Set-Content $config
    $nfpm = Get-Nfpm
    $out = @()
    foreach ($kind in "deb", "rpm") {
        $target = Join-Path $artifacts "PyroLauncher-$version-$Rid.$kind"
        if (Test-Path $target) { Remove-Item $target -Force }
        if (-not (Invoke-Nfpm $nfpm @("package", "--config", $config, "--packager", $kind, "--target", $target))) {
            Write-Warning ("Skipping the $Rid .deb/.rpm: nFPM could not be started. If your antivirus removed it, " +
                "build these on Linux or with the GitHub Actions workflow instead.")
            break
        }
        $out += $target
    }
    Remove-Item $work -Recurse -Force
    return $out
}

function New-AppImage([string]$Rid, [string]$App) {
    $arch = if ($Rid -eq "linux-arm64") { "aarch64" } else { "x86_64" }
    $dir = Join-Path $artifacts "AppDir-$Rid"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    $bin = Join-Path $dir "usr/bin"
    New-Item -ItemType Directory -Force $bin | Out-Null
    Copy-Item (Join-Path $App "PyroLauncher"), (Join-Path $App "version.txt") $bin
    Copy-Item $logo (Join-Path $dir "pyrolauncher.png")
    ($desktopEntry -f "PyroLauncher %U") | Set-Content (Join-Path $dir "pyrolauncher.desktop") -NoNewline
    "#!/bin/sh`nHERE=`"`$(dirname `"`$(readlink -f `"`$0`")`")`"`nexec `"`$HERE/usr/bin/PyroLauncher`" `"`$@`"`n" |
        Set-Content (Join-Path $dir "AppRun") -NoNewline
    chmod +x (Join-Path $dir "AppRun") (Join-Path $bin "PyroLauncher")
    $target = Join-Path $artifacts "PyroLauncher-$version-$Rid.AppImage"
    if (Test-Path $target) { Remove-Item $target -Force }
    $tool = Get-AppImageTool
    # Extract-and-run avoids needing FUSE (CI runners and containers usually lack it).
    $env:APPIMAGE_EXTRACT_AND_RUN = "1"; $env:ARCH = $arch
    & $tool $dir $target | Out-Host
    $failed = $LASTEXITCODE -ne 0
    Remove-Item Env:ARCH
    Remove-Item $dir -Recurse -Force
    if ($failed) { throw "appimagetool failed ($Rid)" }
    return $target
}

function Publish-App([string]$Rid, [string]$Out) {
    if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
    dotnet publish $appProject -c Release -r $Rid --self-contained true @singleFile -o $Out
    if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed ($Rid)" }
    Get-ChildItem $Out -Filter *.xml | Remove-Item -Force       # NuGet API docs, not needed at runtime
    Get-ChildItem $Out -Filter *.pdb | Remove-Item -Force
    Set-Content -Path (Join-Path $Out "version.txt") -Value $version -NoNewline
}

# ---------------------------------------------------------------- per runtime

foreach ($rid in $Runtime) {
    Write-Host "`n=== PyroLauncher $version ($rid) ===" -ForegroundColor Cyan
    $app = Join-Path $artifacts "app-$rid"
    Publish-App $rid $app

    switch -Wildcard ($rid) {
        "win-*" {
            if (-not $NoPortable -or -not $IsWindows) {
                $zip = Join-Path $artifacts "PyroLauncher-$version-$rid-portable.zip"
                if (Test-Path $zip) { Remove-Item $zip -Force }
                Compress-Archive -Path (Join-Path $app "*") -DestinationPath $zip -CompressionLevel Optimal
                $built += $zip
            }
            if (-not $IsWindows) { Write-Warning "Skipping the $rid setup: the installer can only be built on Windows."; break }

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
            Remove-Item $out -Recurse -Force
            $built += $setup
        }
        "osx-*" {
            $bundle = Join-Path $artifacts "bundle-$rid/PyroLauncher.app"
            if (Test-Path (Split-Path $bundle)) { Remove-Item (Split-Path $bundle) -Recurse -Force }
            $macos = Join-Path $bundle "Contents/MacOS"
            $resources = Join-Path $bundle "Contents/Resources"
            New-Item -ItemType Directory -Force $macos, $resources | Out-Null
            Copy-Item (Join-Path $app "*") $macos -Recurse
            $iconKey = ""
            if (New-Icns (Join-Path $resources "PyroLauncher.icns")) {
                $iconKey = "`n    <key>CFBundleIconFile</key>`n    <string>PyroLauncher</string>"
            } else { Write-Warning "No image tool found; the app bundle has no icon." }
            @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>PyroLauncher</string>
    <key>CFBundleDisplayName</key>
    <string>PyroLauncher</string>
    <key>CFBundleIdentifier</key>
    <string>com.pyrobyte.pyrolauncher</string>
    <key>CFBundleExecutable</key>
    <string>PyroLauncher</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>$iconKey
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.games</string>
</dict>
</plist>
"@ | Set-Content -Path (Join-Path $bundle "Contents/Info.plist") -NoNewline

            # Apple Silicon refuses to run unsigned code. An ad-hoc signature is enough for local use;
            # it can only be applied on a Mac, otherwise users run `codesign` themselves (see README).
            if ($IsMacOS) {
                codesign --force --deep --sign - $bundle
                if ($LASTEXITCODE -ne 0) { throw "codesign failed ($rid)" }
            }

            $zip = Join-Path $artifacts "PyroLauncher-$version-$rid.zip"
            if ($IsMacOS) {
                if (Test-Path $zip) { Remove-Item $zip -Force }
                ditto -c -k --keepParent $bundle $zip                    # keeps the signature and permissions

                # Disk image: the app next to an Applications shortcut to drag it onto.
                $stage = Join-Path $artifacts "dmg-$rid"
                if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
                New-Item -ItemType Directory -Force $stage | Out-Null
                ditto $bundle (Join-Path $stage "PyroLauncher.app")
                ln -s /Applications (Join-Path $stage "Applications")
                $dmg = Join-Path $artifacts "PyroLauncher-$version-$rid.dmg"
                if (Test-Path $dmg) { Remove-Item $dmg -Force }
                hdiutil create -volname "PyroLauncher" -srcfolder $stage -ov -format UDZO $dmg
                $failed = $LASTEXITCODE -ne 0
                Remove-Item $stage -Recurse -Force
                if ($failed) { throw "hdiutil failed ($rid)" }
                $built += $dmg
            } else {
                Write-Warning "Skipping the $rid .dmg: disk images can only be built on a Mac."
                New-UnixZip (Split-Path $bundle) $zip "" @("PyroLauncher.app/Contents/MacOS/PyroLauncher")
            }
            Remove-Item (Split-Path $bundle) -Recurse -Force
            $built += $zip
        }
        "linux-*" {
            $built += New-LinuxPackages $rid $app
            if ($IsLinux) { $built += New-AppImage $rid $app }
            else { Write-Warning "Skipping the $rid AppImage: appimagetool only runs on Linux." }

            Copy-Item $logo (Join-Path $app "pyrolauncher.png")
            $tgz = Join-Path $artifacts "PyroLauncher-$version-$rid.tar.gz"
            New-TarGz $app $tgz "PyroLauncher" @("PyroLauncher")
            $built += $tgz
        }
        default { throw "Unknown runtime $rid" }
    }
    Remove-Item $app -Recurse -Force
}

Write-Host "`nDone:" -ForegroundColor Green
foreach ($f in $built) { Write-Host ("  {0}  ({1} MB)" -f (Split-Path $f -Leaf), [math]::Round((Get-Item $f).Length / 1MB, 1)) }
