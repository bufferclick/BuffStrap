<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="Bloxstrap/Resources/BuffStrapWordmark-Dark.png">
  <source media="(prefers-color-scheme: light)" srcset="Bloxstrap/Resources/BuffStrapWordmark-Light.png">
  <img alt="BuffStrap" src="Bloxstrap/Resources/BuffStrapWordmark-Light.png" width="540">
</picture>

**Made by Buffer**

A Roblox bootstrapper based on [Fishstrap](https://github.com/fishstrap/fishstrap), which is based on [Bloxstrap](https://github.com/bloxstraplabs/bloxstrap).

[Download the latest release](https://github.com/bufferclick/BuffStrap/releases/latest)

</div>

## Install

BuffStrap is for **64-bit Windows 10 and newer**. Download **BuffStrapSetup.exe** from the [official releases page](https://github.com/bufferclick/BuffStrap/releases/latest) and run it. Setup downloads the latest stable BuffStrap, then opens the installation wizard, where you can choose your install folder and whether to create Desktop and Start menu shortcuts. The release executables include the required .NET runtime.

Roblox itself is downloaded or updated when you first launch it with BuffStrap. BuffStrap retains Fishstrap's Roblox Player and Studio support, FastFlags and settings editors, appearance and mod options, Discord integration, server details, and other existing features. Roblox's FastFlag allowlist still applies.

## Updates

By default BuffStrap checks its own GitHub releases when you open the app or launch Roblox. It downloads a newer stable version automatically and keeps your settings and shortcuts. The existing auto-update setting can be turned off. If you are offline or another BuffStrap process is still running, it continues with the installed version and can try again on a later launch. Roblox's own update mechanism remains separate and unchanged.

Setup and the updater select **BuffStrap.exe** by name, check the expected download size and, when provided by GitHub, its SHA-256 digest, and verify the executable's product and version before running it. They never use an incomplete cached download.

**Settings → Update Center** shows the installed version, the last successful check, release notes, and a list of validated stable releases. You can reinstall a release or select an older one. Back up first: older releases might not understand newer settings. A deliberate rollback turns off automatic updates before starting the old release, so it will not immediately update itself again; re-enable updates in Settings when you are ready. An older release may also ask you to confirm the downgrade itself.

## Favorites and accounts

Open **Favorites** from the launch menu. Add a game using its Roblox game link or place ID, optionally give it a folder and an account label, and search or edit your list. Choose **Shortcut** to create a shortcut on the Desktop, in the Start menu, or both. Shortcuts reference the saved favorite, so later edits and account assignments still apply. Recently Played records confirmed joins while BuffStrap activity tracking is enabled; it stores at most 50 entries locally.

Account entries are **username labels, not saved logins**. Roblox's documented [account switcher](https://en.help.roblox.com/hc/en-us/articles/21037888001044-Account-Switching) operates on the web and does not provide a supported launcher API for selecting a session. For an assigned favorite, BuffStrap opens the official game page and asks you to sign in or switch to the indicated account and press Play there. It cannot verify the browser account or perform that extra Play click. Unassigned favorites launch using the current Roblox session. BuffStrap does not ask for or save passwords or account session tokens in Favorites.

## Backup, restore and repair

**Settings → Update Center** can back up last-saved settings, FastFlags, mods, Favorites and account labels, profiles, and custom launcher themes. The archive excludes Roblox's login/session storage and skips links and files with sensitive names. Custom mods and integration settings can contain user-entered information, so inspect them before sharing a backup. Restore validates paths, sizes and checksums, replaces the files in the archive, keeps extra files, and closes BuffStrap so the restored configuration loads on restart. Make a backup of your current data first and close Roblox and other BuffStrap processes before restoring.

The repair dialog checks common installation registrations, Player launch links, shortcuts and the Roblox Player executable, displays its findings, and asks before applying selected fixes. A selected Roblox file repair is scheduled for the next launch and may need a network connection and close a running player. It keeps settings, favorites, mods and Roblox login data. This is not a full integrity scan; if BuffStrap's own executable is missing or invalid, reinstall BuffStrap using the official setup or Update Center.

## Roblox app themes

BuffStrap's existing Roblox light/dark setting is retained. Set it to **Default** to leave Roblox's own theme selection alone, including any curated themes Roblox makes available to your account. Roblox's [desktop theme settings](https://en.help.roblox.com/hc/en-us/articles/360022922852-Changing-Your-App-Theme-Light-Dark-and-System) do not document an API for arbitrary custom themes, so BuffStrap does not claim to apply a custom theme inside Roblox itself. Custom bootstrapper themes are separate and affect BuffStrap's launch window only.

## Building and releasing

The solution uses the .NET 6 SDK and the WPF UI submodule. On Windows:

```powershell
git submodule update --init --recursive
dotnet restore .\BuffStrap.sln
dotnet build .\BuffStrap.sln -c Release
dotnet run --project .\Tests\ReleaseClient.Tests\ReleaseClient.Tests.csproj -c Release
dotnet publish .\Bloxstrap\Bloxstrap.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
dotnet publish .\BuffStrapSetup\BuffStrapSetup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The shared version is in `BuffStrap.Version.props`. The first release is **v1.0.0**. Use small patch increments for fixes (for example, v1.0.1 then v1.0.2), and increment the minor version when appropriate. A stable tag such as `v1.0.1` must match the version in that file. The release workflow builds both self-contained executables and publishes them as GitHub release assets; the installer and updater use the latest published stable release.

## Credits

BuffStrap is made by Buffer and builds on the work of the Fishstrap and Bloxstrap contributors. Their upstream services, including Fishstrap's configuration and game-invite services, are still used for the features that depend on them. See [LICENSE](LICENSE) and [LICENSE.Bloxstrap](LICENSE.Bloxstrap) for the original license notices. BuffStrap is not affiliated with Roblox Corporation.
