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
                PrepareFavoriteLaunch(id);
                LaunchHandler.LaunchRoblox(LaunchMode.Player);
            }
            catch (Exception ex) { ShowLaunchError(ex); }
        }

        internal static void PrepareFavoriteLaunch(Guid id)
        {
            FavoriteLaunchPlan plan = FavoriteLaunchPlan.Resolve(Store.Load(), id);
            if (plan.AccountId is Guid accountId)
            {
                PrepareAccountLaunch(accountId, plan.PlaceId);
            }
            else
            {
                App.LaunchSettings.SelectedAccountId = null;
                App.LaunchSettings.SelectedAccountPlaceId = null;
                App.LaunchSettings.RobloxLaunchArgs = plan.PlayerUrl;
            }
        }

        public static void LaunchAccount(Guid accountId, long placeId)
        {
            try
            {
                PrepareAccountLaunch(accountId, placeId);
                LaunchHandler.LaunchRoblox(LaunchMode.Player);
            }
            catch (Exception ex) { ShowLaunchError(ex); }
        }

        private static void PrepareAccountLaunch(Guid accountId, long placeId)
        {
            if (accountId == Guid.Empty || placeId <= 0) throw new RobloxAccountException("The account launch request is invalid.");
            // Never replace a missing, expired, or rejected account with the
            // current Roblox session. A fresh ticket is requested at launch.
            AccountSessions.RequireSaved(accountId);
            App.LaunchSettings.SelectedAccountId = accountId;
            App.LaunchSettings.SelectedAccountPlaceId = placeId;
            App.LaunchSettings.RobloxLaunchArgs = "";
        }

        private static void ShowLaunchError(Exception ex)
        {
            // Never log arbitrary exceptions from an authenticated path.
            string detail = ex is RobloxAccountException ? ex.Message : "Check that the favorite and account still exist, then try again.";
            Frontend.ShowMessageBox("Could not start the game.\n\n" + detail, MessageBoxImage.Warning);
            App.Terminate();
        }

        // A test build can be running beside an older installed build. Start the
        // running executable, not Paths.Application, so Play uses this same code.
        // Only public IDs travel on the child command line; the session and the
        // one-time ticket are obtained inside the child.
        private static ProcessStartInfo CreateChildStartInfo(params string[] arguments)
        {
            var start = new ProcessStartInfo { FileName = Paths.Process, UseShellExecute = false };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("-childlaunch");
            return start;
        }

        internal static ProcessStartInfo FavoriteStartInfo(Guid id)
        {
            if (id == Guid.Empty) throw new ArgumentException("Invalid favorite ID.", nameof(id));
            return CreateChildStartInfo("-favorite", id.ToString("D"));
        }

        internal static ProcessStartInfo AccountStartInfo(Guid accountId, long placeId)
        {
            if (accountId == Guid.Empty || placeId <= 0) throw new ArgumentException("Invalid account or place ID.");
            return CreateChildStartInfo("-account", accountId.ToString("D"), "-accountplace", placeId.ToString(CultureInfo.InvariantCulture));
        }

        internal static ProcessStartInfo PlaceStartInfo(long placeId)
        {
            if (placeId <= 0) throw new ArgumentOutOfRangeException(nameof(placeId));
            return CreateChildStartInfo($"roblox://experiences/start?placeId={placeId.ToString(CultureInfo.InvariantCulture)}");
        }

        private static void Start(ProcessStartInfo info)
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Windows could not start BuffStrap.");
        }

        public static void StartFavorite(Guid id) => Start(FavoriteStartInfo(id));

        public static void StartAccountLaunch(Guid accountId, long placeId) => Start(AccountStartInfo(accountId, placeId));

        public static void StartPlace(long placeId) => Start(PlaceStartInfo(placeId));
    }
}
