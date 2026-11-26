using BuffStrap.Accounts;
using BuffStrap.Favorites;
using System.Windows;

namespace Bloxstrap
{
    internal static class FavoritesLauncher
    {
        public static FavoritesStore Store => new(Path.Combine(Paths.Base, "Favorites.json"));

        public static void Launch(Guid id)
        {
            try
            {
                FavoriteLaunchPlan plan = FavoriteLaunchPlan.Resolve(Store.Load(), id);
                if (plan.AccountId is Guid accountId)
                {
                    // Never replace a missing, expired, or rejected account with the
                    // current Roblox session. A fresh ticket is requested at launch.
                    AccountSessions.RequireSaved(accountId);
                    App.LaunchSettings.SelectedAccountId = accountId;
                    App.LaunchSettings.SelectedAccountPlaceId = plan.PlaceId;
                    App.LaunchSettings.RobloxLaunchArgs = "";
                }
                else
                {
                    App.LaunchSettings.RobloxLaunchArgs = plan.PlayerUrl;
                }
                LaunchHandler.LaunchRoblox(LaunchMode.Player);
            }
            catch (Exception ex)
            {
                ShowLaunchError(ex);
            }
        }

        public static void LaunchAccount(Guid accountId, long placeId)
        {
            try
            {
                if (accountId == Guid.Empty || placeId <= 0) throw new RobloxAccountException("The account launch request is invalid.");
                AccountSessions.RequireSaved(accountId);
                App.LaunchSettings.SelectedAccountId = accountId;
                App.LaunchSettings.SelectedAccountPlaceId = placeId;
                App.LaunchSettings.RobloxLaunchArgs = "";
                LaunchHandler.LaunchRoblox(LaunchMode.Player);
            }
            catch (Exception ex) { ShowLaunchError(ex); }
        }

        private static void ShowLaunchError(Exception ex)
        {
            // Never log arbitrary exceptions from an authenticated path.
            string detail = ex is RobloxAccountException ? ex.Message : "Check that the favorite and account still exist, then try again.";
            Frontend.ShowMessageBox("Could not start the game.\n\n" + detail, MessageBoxImage.Warning);
            App.Terminate();
        }

        public static void StartFavorite(Guid id)
        {
            var start = new ProcessStartInfo { FileName = Paths.Application, UseShellExecute = false };
            start.ArgumentList.Add("-favorite");
            start.ArgumentList.Add(id.ToString("D"));
            if (Process.Start(start) is null) throw new InvalidOperationException("Windows could not start BuffStrap.");
        }

        public static void StartAccountLaunch(Guid accountId, long placeId)
        {
            if (accountId == Guid.Empty || placeId <= 0) throw new ArgumentException("Invalid account or place ID.");
            var start = new ProcessStartInfo { FileName = Paths.Application, UseShellExecute = false };
            start.ArgumentList.Add("-account");
            start.ArgumentList.Add(accountId.ToString("D"));
            start.ArgumentList.Add("-accountplace");
            start.ArgumentList.Add(placeId.ToString(CultureInfo.InvariantCulture));
            if (Process.Start(start) is null) throw new InvalidOperationException("Windows could not start BuffStrap.");
        }

        public static void StartPlace(long placeId)
        {
            if (placeId <= 0) throw new ArgumentOutOfRangeException(nameof(placeId));
            var start = new ProcessStartInfo { FileName = Paths.Application, UseShellExecute = false };
            start.ArgumentList.Add($"roblox://experiences/start?placeId={placeId}");
            if (Process.Start(start) is null) throw new InvalidOperationException("Windows could not start BuffStrap.");
        }
    }
}
