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
