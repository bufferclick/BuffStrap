using Microsoft.Win32;

namespace Bloxstrap
{
    internal sealed class RepairFindings
    {
        public bool AppMissingOrInvalid { get; init; }
        public bool PlayerProtocol { get; init; }
        public bool ApiRegistration { get; init; }
        public bool UninstallRegistration { get; init; }
        public bool DesktopShortcut { get; init; }
        public bool StartMenuShortcut { get; init; }
        public bool PlayerMissing { get; init; }
        public bool PlayerNeverInstalled { get; init; }
    }

    internal static class InstallationRepair
    {
        private static string DesktopShortcut => Path.Combine(Paths.Desktop, App.ProjectName + ".lnk");
        private static string StartShortcut => Path.Combine(Paths.WindowsStartMenu, App.ProjectName + ".lnk");
        private static readonly string[] PlayerSchemes = { "roblox", "roblox-player" };

        public static RepairFindings Scan()
        {
            bool appInvalid = !File.Exists(Paths.Application);
            if (!appInvalid)
            {
                try { appInvalid = FileVersionInfo.GetVersionInfo(Paths.Application).ProductName != "BuffStrap"; }
                catch { appInvalid = true; }
            }
            string expectedCommand = $"\"{Paths.Application}\" -player \"%1\"";
            bool protocol = PlayerSchemes.Any(scheme =>
            {
                using var root = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{scheme}");
                using var key = root?.OpenSubKey(@"shell\open\command");
                return root?.GetValue("URL Protocol") is null ||
                    !String.Equals(key?.GetValue("") as string, expectedCommand, StringComparison.OrdinalIgnoreCase);
            });
            using var apis = Registry.CurrentUser.OpenSubKey(App.ApisKey);
            bool api = !String.Equals(apis?.GetValue("ApplicationPath") as string, Paths.Application, StringComparison.OrdinalIgnoreCase) ||
                       !String.Equals(apis?.GetValue("InstallationPath") as string, Paths.Base, StringComparison.OrdinalIgnoreCase);
            using var uninstall = Registry.CurrentUser.OpenSubKey(App.UninstallKey);
            bool install = !String.Equals(uninstall?.GetValue("InstallLocation") as string, Paths.Base, StringComparison.OrdinalIgnoreCase) ||
                           !String.Equals(uninstall?.GetValue("DisplayIcon") as string, Paths.Application + ",0", StringComparison.OrdinalIgnoreCase);
            bool neverInstalled = String.IsNullOrEmpty(App.Distribution.RobloxPlayerData.State.VersionGuid);
            return new RepairFindings
            {
                AppMissingOrInvalid = appInvalid,
                PlayerProtocol = protocol,
                ApiRegistration = api,
                UninstallRegistration = install,
                DesktopShortcut = ShortcutNeedsRepair(DesktopShortcut),
                StartMenuShortcut = ShortcutNeedsRepair(StartShortcut),
                PlayerNeverInstalled = neverInstalled,
                PlayerMissing = !neverInstalled && !File.Exists(App.Distribution.RobloxPlayerData.ExecutablePath)
            };
        }

        private static bool ShortcutNeedsRepair(string path)
        {
            if (!File.Exists(path)) return true; // optional shortcut, clearly described in the UI
            try
            {
                var link = ShellLink.Shortcut.ReadFromFile(path);
                string? target = link.ExtraData.EnvironmentVariableDataBlock?.TargetUnicode;
                return !String.Equals(target, Paths.Application, StringComparison.OrdinalIgnoreCase) ||
                    !String.IsNullOrEmpty(link.StringData?.CommandLineArguments);
            }
            catch { return true; }
        }

        public static void RestorePlayerProtocols()
        {
            RequireApp();
            WindowsRegistry.RegisterPlayer();
            foreach (string scheme in PlayerSchemes)
            {
                using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
                root.SetValue("URL Protocol", "");
            }
        }

        public static void RestoreApiRegistration()
        {
            RequireApp();
            using var key = Registry.CurrentUser.CreateSubKey(App.ApisKey);
            key.SetValue("ApplicationPath", Paths.Application);
            key.SetValue("InstallationPath", Paths.Base);
        }

        public static void RestoreUninstallRegistration()
        {
            RequireApp();
            using var key = Registry.CurrentUser.CreateSubKey(App.UninstallKey);
            key.SetValue("DisplayIcon", Paths.Application + ",0");
            key.SetValue("DisplayName", App.ProjectName);
            key.SetValue("DisplayVersion", App.Version);
            key.SetValue("InstallLocation", Paths.Base);
            key.SetValue("Publisher", App.ProjectOwner);
            key.SetValue("ModifyPath", $"\"{Paths.Application}\" -settings");
            key.SetValue("UninstallString", $"\"{Paths.Application}\" -uninstall");
            key.SetValue("QuietUninstallString", $"\"{Paths.Application}\" -uninstall -quiet");
        }

        public static void RestoreShortcut(bool desktop)
        {
            RequireApp();
            string target = desktop ? DesktopShortcut : StartShortcut;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ShellLink.Shortcut.CreateShortcut(Paths.Application, "", Paths.Application, 0).WriteToFile(temporary);
                File.Move(temporary, target, true);
                if (ShortcutNeedsRepair(target)) throw new IOException("The restored shortcut could not be verified.");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static void SchedulePlayerReinstall()
        {
            App.State.Prop.ForceReinstall = true;
            App.State.Save();
            var saved = JsonSerializer.Deserialize<State>(File.ReadAllText(App.State.FileLocation));
            if (saved is null || !saved.ForceReinstall)
                throw new IOException("Could not save the scheduled reinstall.");
        }

        private static void RequireApp()
        {
            if (!File.Exists(Paths.Application) || FileVersionInfo.GetVersionInfo(Paths.Application).ProductName != "BuffStrap")
                throw new IOException("The installed BuffStrap executable is missing or invalid. Reinstall BuffStrap first.");
        }
    }
}
