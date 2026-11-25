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

Open **Accounts** from the launch menu. Select **Add account**, sign in on Roblox's page (including any verification), then click **I've signed in**. Cards show the account's public avatar, display name and username. Enter a Roblox game link or place ID and press **Play** on the desired account. The sign-in window requires Windows 10 version 1809 or newer and the Microsoft Edge WebView2 Runtime; if the runtime is missing, install it from [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/). The isolated sign-in window only navigates to Roblox domains; some external sign-in methods may not work there.

Open **Favorites** to organize games into folders, search and edit them, assign saved accounts, and create Desktop or Start menu shortcuts. Shortcuts reference the saved favorite, so later edits and account assignments still apply. An assigned favorite uses that account; a missing, expired or rejected session results in an error, not a silent switch to the current account. Unassigned favorites use the current Roblox session. Recently Played records confirmed joins while activity tracking is enabled, up to 50 locally. Username-only entries created by v1.0.1 remain in place, including their favorite assignments, but must be signed in before use. Removing an account deletes its local session and, after confirmation, unassigns its favorites; it does not revoke that session on Roblox's servers.

The account manager uses an **unofficial** Roblox web-to-player flow: it checks the saved session and requests a short-lived launch ticket immediately before starting the unmodified Roblox Player. Roblox can expire, rotate, bind or reject sessions and change this flow at any time; sign in again if prompted. **Launching a selected account may change Roblox's shared last-signed-in account**, including for launches outside BuffStrap. Roblox's official [account switcher](https://en.help.roblox.com/hc/en-us/articles/21037888001044-Account-Switching) is a separate web feature. BuffStrap does not capture passwords or bypass verification. Sessions are encrypted with Windows DPAPI for the current Windows user; other programs running as that user may still be able to access them. A one-time ticket is passed to Roblox in its process command line and Roblox may write it to its own logs; do not share those logs indiscriminately.

## Backup, restore and repair

**Settings → Update Center** can back up last-saved settings, FastFlags, mods, Favorites, public account names/display names/user IDs, profiles, and custom launcher themes. **Account sessions and sign-in browser profiles are never included.** On another Windows user or PC, reconnect accounts after restoring; restoring on the same PC leaves existing local sessions untouched. The archive also excludes Roblox's login storage, links, files with sensitive names, and files or settings with recognizable Roblox session markers. These filters cannot identify every possible secret in arbitrary custom mods or integration arguments; inspect the backup before sharing it. Restore validates paths, sizes and checksums, replaces the files in the archive, keeps extra files, and closes BuffStrap so the restored configuration loads on restart. Make a backup of your current data first and close Roblox and other BuffStrap processes before restoring.

The repair dialog checks common installation registrations, Player launch links, shortcuts and the Roblox Player executable, displays its findings, and asks before applying selected fixes. A selected Roblox Player file repair is scheduled for the next Player launch and may need a network connection. Close Roblox and other BuffStrap processes before scheduling it. It keeps settings, favorites, mods and Roblox login data. This is not a full integrity scan; if BuffStrap's own executable is missing or invalid, reinstall BuffStrap using the official setup or Update Center.

## Roblox app themes

BuffStrap's existing Roblox light/dark setting is retained. Set it to **Default** to leave Roblox's own theme selection alone, including any curated themes Roblox makes available to your account. Roblox's [desktop theme settings](https://en.help.roblox.com/hc/en-us/articles/360022922852-Changing-Your-App-Theme-Light-Dark-and-System) do not document an API for arbitrary custom themes. Custom bootstrapper themes affect BuffStrap's launch window only.

We also investigated non-launcher options. **Mods** can override some local files such as cursors, sounds or fonts; their effect depends on the current Roblox version and does not uniformly recolor the menus of every experience. A separate, non-injecting screen overlay could tint the whole image, but it cannot selectively restyle Roblox controls and may not appear over full-screen windows. Injected shaders or code can modify the running client; Roblox [warns that modified clients can lead to enforcement](https://devforum.roblox.com/t/an-update-on-automated-action-against-modified-clients/3640609). BuffStrap does not inject such code or promise that third-party overlays are approved. Experience creators can style UI they control in [Roblox Studio](https://create.roblox.com/docs/ui/styling), which is different from changing other people's games.

## Building and releasing

The solution uses the .NET 6 SDK and the WPF UI submodule. On Windows:

```powershell
git submodule update --init --recursive
dotnet restore .\BuffStrap.sln
dotnet build .\BuffStrap.sln -c Release
dotnet run --project .\Tests\ReleaseClient.Tests\ReleaseClient.Tests.csproj -c Release
dotnet run --project .\Tests\UiSmoke.Tests\UiSmoke.Tests.csproj -c Release
# On an interactive Windows desktop with WebView2 installed, also exercise the isolated profiles:
$env:BUFFSTRAP_TEST_WEBVIEW2 = '1'
dotnet run --project .\Tests\UiSmoke.Tests\UiSmoke.Tests.csproj -c Release
Remove-Item Env:\BUFFSTRAP_TEST_WEBVIEW2
dotnet publish .\Bloxstrap\Bloxstrap.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
dotnet publish .\BuffStrapSetup\BuffStrapSetup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The standard CI smoke test does not open a live WebView2 window; the optional profile test above needs a usable Windows desktop. It uses synthetic cookies and does not sign in or launch a game. Real sign-in and selected-account launches must be verified separately with accounts you own.

The shared version is in `BuffStrap.Version.props`. The first release is **v1.0.0**. Use small patch increments for fixes (for example, v1.0.1 then v1.0.2), and increment the minor version when appropriate. A stable tag such as `v1.0.1` must match the version in that file. The release workflow builds both self-contained executables and publishes them as GitHub release assets; the installer and updater use the latest published stable release.

## Credits

BuffStrap is made by Buffer and builds on the work of the Fishstrap and Bloxstrap contributors. Their upstream services, including Fishstrap's configuration and game-invite services, are still used for the features that depend on them. See [LICENSE](LICENSE) and [LICENSE.Bloxstrap](LICENSE.Bloxstrap) for the original license notices. BuffStrap is not affiliated with Roblox Corporation.
