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
                FavoritesData data = Store.Load();
                FavoriteGame? game = data.Games.FirstOrDefault(x => x.Id == id);
                if (game is null)
                    throw new InvalidOperationException("This favorite no longer exists. Its shortcut can be removed.");

                if (game.AccountId is Guid accountId)
                {
                    var account = data.Accounts.FirstOrDefault(x => x.Id == accountId)
                        ?? throw new InvalidOperationException("The account label assigned to this favorite is missing.");
                    // Roblox does not offer a documented API for a launcher to select a
                    // web account. Never silently launch using a different account.
                    var answer = Frontend.ShowMessageBox(
                        $"This favorite is assigned to {account.Username}. Roblox controls sign-in.\n\n" +
                        "Open the official game page, sign in or switch to that account, and press Play there. " +
                        "BuffStrap cannot verify or switch the browser account for you.",
                        MessageBoxImage.Information, MessageBoxButton.OKCancel);
                    if (answer == MessageBoxResult.OK)
                        Utilities.ShellExecute($"https://www.roblox.com/games/{game.PlaceId}/");
                    App.Terminate();
                    return;
                }

                App.LaunchSettings.RobloxLaunchArgs = $"roblox://experiences/start?placeId={game.PlaceId}";
                LaunchHandler.LaunchRoblox(LaunchMode.Player);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("FavoritesLauncher::Launch", ex);
                Frontend.ShowMessageBox("Could not open this favorite.\n\n" + ex.Message, MessageBoxImage.Warning);
                App.Terminate();
            }
        }

        public static void StartFavorite(Guid id)
        {
            var start = new ProcessStartInfo { FileName = Paths.Application, UseShellExecute = false };
            start.ArgumentList.Add("-favorite");
            start.ArgumentList.Add(id.ToString("D"));
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
