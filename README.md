<div align="center">

<img src="src/PyroLauncher/Assets/logo.png" width="96" alt="PyroLauncher logo">

# PyroLauncher

A Minecraft launcher for people who play with mods.

[![License: GPL v3](https://img.shields.io/badge/license-GPLv3-1f1f1f?style=flat-square)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-1f1f1f?style=flat-square)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-1f1f1f?style=flat-square)](#requirements)
[![Latest release](https://img.shields.io/github/v/release/PyroByteTeam/PyroLauncher?style=flat-square&color=1f1f1f&label=release)](https://github.com/PyroByteTeam/PyroLauncher/releases)

<img src=".github/assets/home.png" width="860" alt="Instances on the home page">

</div>

## Why another launcher

We wanted something between the official launcher and the power-user tools: every instance
isolated, mods and modpacks from both Modrinth and CurseForge in one place, and no fiddling
with Java. PyroLauncher is that, wrapped in a quiet black-and-white interface.

## What it does

**Instances.** Vanilla, Fabric, Forge or NeoForge, each with its own folder, settings and
play time. Group them into categories by dragging cards around. A new instance shows an
**Install** button first, which downloads the game, mod loader and Java without launching.

**Mods, resource packs and shaders.** Search Modrinth and CurseForge side by side, read
descriptions, galleries and changelogs, pick a version, and install with dependencies. Files
you already have are recognised by hash, so updates work for mods you dropped in by hand.
Turn resource packs on and pick a shader pack before you start the game.

**Modpacks.** Browse packs on the Discover page, install one as a new instance, and update or
roll it back later from its Modpack tab. Your worlds, screenshots and anything you added
yourself are left alone. Import `.mrpack` and CurseForge `.zip` files, or download a pack's
server files.

<p align="center">
  <img src=".github/assets/discover.png" width="49%" alt="Discover page">
  <img src=".github/assets/instance.png" width="49%" alt="Mods of an instance">
</p>

**Export.** Share an instance as a Modrinth pack, a CurseForge pack or a `.pyropack`
(everything bundled, including the instance's game settings). You choose which files go in.

**Presets.** Save a list of mods, resource packs and shaders once and apply it to any
instance. The launcher finds versions that fit that instance's Minecraft version and loader,
tells you what is missing, and lets you untick anything before installing.

**Java.** Eclipse Temurin builds from Adoptium are downloaded on demand, one per major version,
and the right one is picked for each Minecraft version. Bring your own if you prefer.

**Accounts.** Microsoft sign-in with several accounts, a 3D skin preview, skin upload and cape
switching. Offline accounts are available behind developer mode.

**Everything else.** A live, colour-coded game console. Screenshot and server-list managers.
A downloads page with pause, cancel and a list of every domain the launcher talked to. English
and Russian interface.

<p align="center">
  <img src=".github/assets/presets.png" width="70%" alt="Applying a preset to an instance">
</p>

## Requirements

- Windows 10 or 11, x64 or ARM64
- Nothing else: the installer ships its own .NET runtime, and Java is downloaded when needed

macOS and Linux aren't supported yet. The interface is built on WPF, which is Windows-only.

## Installing

Grab `PyroLauncher-Setup-<version>-win-x64.exe` (or `win-arm64`) from
[Releases](https://github.com/PyroByteTeam/PyroLauncher/releases) and run it. There's also a
portable zip if you'd rather not install anything. It installs for
your user only, so no administrator prompt. Running the same setup again updates or repairs
the install, and it also handles uninstalling. You choose whether to keep your instances and
settings.

Launcher data lives in `%APPDATA%\PyroLauncher`. Set `PYROLAUNCHER_HOME` to keep it somewhere
else, e.g. on another drive or next to a portable copy.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/PyroByteTeam/PyroLauncher.git
cd PyroLauncher
dotnet run --project src/PyroLauncher
```

To build release files:

```powershell
.\build-installer.ps1                     # x64 and ARM64
.\build-installer.ps1 -Runtime win-x64    # just one
```

For each runtime you get a setup (`PyroLauncher-Setup-<version>-<runtime>.exe`) and a portable
zip (`PyroLauncher-<version>-<runtime>-portable.zip`) in `artifacts/`. Both are self-contained.

### CurseForge

CurseForge's API needs a key. Get one for free at
[console.curseforge.com](https://console.curseforge.com/) and paste it into
**Settings → Launcher**. Without it everything still works, just with Modrinth only.

## Project layout

```
src/
  PyroLauncher/              the launcher (WPF)
    Accounts/                Microsoft sign-in, profiles, skins
    Content/                 Modrinth & CurseForge APIs, modpacks, presets, export
    Minecraft/               version resolving, loaders, Java, launching
    Instances/               instance model and storage
    UI/                      views, view models, controls, translations
    Lang/                    translation files
  PyroLauncher.Installer/    the setup / uninstaller
```

Adding a language means adding `Lang/<code>.json` (English text → translation) and listing it
in `UI/Loc.cs`. Anything without a translation falls back to English.

## Contributing

Bug reports and pull requests are welcome. For anything bigger than a fix, open an issue first
so we can agree on the approach. Please keep changes focused and test them against a real
game launch.

## License

[GPL-3.0](LICENSE). Made by the PyroByte team.

PyroLauncher is not an official Minecraft product and is not approved by or associated with
Mojang or Microsoft.
