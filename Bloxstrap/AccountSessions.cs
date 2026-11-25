using BuffStrap.Accounts;
using BuffStrap.Favorites;
using System.Security.Cryptography;

namespace Bloxstrap
{
    /// <summary>Per-Windows-user encrypted sessions, deliberately outside backup's allowlist.</summary>
    internal sealed class AccountSessionVault
    {
        private readonly string _directory;
        public AccountSessionVault(string baseDirectory) => _directory = Path.Combine(baseDirectory, "AccountSessions");

        private string PathFor(Guid id)
        {
            if (id == Guid.Empty) throw new ArgumentException("Invalid account ID.");
            return Path.Combine(_directory, id.ToString("N") + ".session");
        }

        public bool Exists(Guid id) => id != Guid.Empty && File.Exists(PathFor(id));

        // Serialize each account's read/rotation/write across BuffStrap processes.
        // An open file, unlike a named Mutex, can safely be held across awaits.
        public async Task<FileStream> LockAsync(Guid id, CancellationToken cancellationToken = default)
        {
            string path = PathFor(id) + ".lock";
            Directory.CreateDirectory(_directory);
            var start = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (start.Elapsed < TimeSpan.FromSeconds(30))
                {
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public void Save(Guid id, string session)
        {
            if (!RobloxAccountClient.IsValidSession(session)) throw new RobloxAccountException("The Roblox session is invalid. Sign in again.");
            string path = PathFor(id);
            Directory.CreateDirectory(_directory);
            byte[] plain = Encoding.UTF8.GetBytes(session);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, id.ToByteArray(), DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, encrypted);
                File.Move(temporary, path, true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public string Read(Guid id)
        {
            string path = PathFor(id);
            if (!File.Exists(path)) throw new RobloxAccountException("This account needs to be signed in on this PC. Open Account Manager and sign in again.");
            if (new FileInfo(path).Length > 65536) throw new RobloxAccountException("The saved account session is invalid. Sign in again.");
            byte[] encrypted = File.ReadAllBytes(path);
            byte[] plain;
            try { plain = ProtectedData.Unprotect(encrypted, id.ToByteArray(), DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { throw new RobloxAccountException("This session belongs to another Windows user or PC, or is damaged. Sign in again."); }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
            try
            {
                string session = Encoding.UTF8.GetString(plain);
                if (!RobloxAccountClient.IsValidSession(session)) throw new RobloxAccountException("The saved account session is invalid. Sign in again.");
                return session;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        public void Delete(Guid id)
        {
            string path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    internal static class AccountSessions
    {
        private static readonly HttpClient Http = RobloxAccountClient.CreateHttpClient();
        private static RobloxAccountClient Api => new(Http);
        public static AccountSessionVault Vault => new(Paths.Base);
        public static bool IsSaved(FavoriteAccount account) => account.UserId.HasValue && Vault.Exists(account.Id);

        /// <summary>Add or reconnect a public profile without ever writing its session to Favorites.json.</summary>
        public static async Task ConnectAsync(string session, Guid? expectedId = null, CancellationToken cancellationToken = default)
        {
            var identity = await Api.GetIdentityAsync(session, cancellationToken);
            var existing = FavoritesLauncher.Store.Load().Accounts;
            FavoriteAccount? match = expectedId.HasValue
                ? existing.FirstOrDefault(x => x.Id == expectedId.Value)
                : existing.FirstOrDefault(x => x.UserId == identity.UserId) ??
                  existing.FirstOrDefault(x => x.Username.Equals(identity.Username, StringComparison.OrdinalIgnoreCase));
            if (expectedId.HasValue && match is null)
                throw new RobloxAccountException("That account was removed. Add it again.");
            bool hadMatch = match is not null;
            Guid accountId = match?.Id ?? Guid.NewGuid();
            using var accountLock = await Vault.LockAsync(accountId, cancellationToken);
            FavoritesLauncher.Store.Update(data =>
            {
                FavoriteAccount? account = data.Accounts.FirstOrDefault(x => x.Id == accountId);
                if (hadMatch && account is null)
                    throw new RobloxAccountException("That account was removed. Add it again.");
                if (account is not null && (account.UserId.HasValue ? account.UserId.Value != identity.UserId :
                    !account.Username.Equals(identity.Username, StringComparison.OrdinalIgnoreCase)))
                    throw new RobloxAccountException("You signed in to a different Roblox account. Sign in to the account shown on the card.");
                if (data.Accounts.Any(x => x.Id != accountId && (x.UserId == identity.UserId ||
                    x.Username.Equals(identity.Username, StringComparison.OrdinalIgnoreCase))))
                    throw new RobloxAccountException("This Roblox account is already in the list. Refresh the list and try again.");
                if (account is null)
                {
                    account = new FavoriteAccount { Id = accountId };
                    data.Accounts.Add(account);
                }
                account.Username = identity.Username;
                account.DisplayName = identity.DisplayName;
                account.UserId = identity.UserId;
            });
            // If encrypted storage fails, a newly added profile remains disconnected.
            Vault.Save(accountId, identity.Session);
        }

        public static void RequireSaved(Guid accountId)
        {
            var account = FavoritesLauncher.Store.Load().Accounts.FirstOrDefault(x => x.Id == accountId)
                ?? throw new RobloxAccountException("That account no longer exists.");
            if (!IsSaved(account))
                throw new RobloxAccountException("Sign in to @" + account.Username + " in Account Manager before playing. Logins are not included in backups.");
        }

        // Called immediately before starting the player, not before an update/download.
        public static async Task<string> CreateLaunchTicketAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            using var accountLock = await Vault.LockAsync(accountId, cancellationToken);
            var account = FavoritesLauncher.Store.Load().Accounts.FirstOrDefault(x => x.Id == accountId)
                ?? throw new RobloxAccountException("That account no longer exists.");
            if (!IsSaved(account))
                throw new RobloxAccountException("Sign in to @" + account.Username + " in Account Manager before playing.");
            string session = Vault.Read(accountId);
            void SaveRotation(string rotated) => Vault.Save(accountId, rotated);
            var identity = await Api.GetIdentityAsync(session, cancellationToken, SaveRotation);
            if (identity.UserId != account.UserId)
                throw new RobloxAccountException("The saved session belongs to a different Roblox account. Sign in again.");
            var ticket = await Api.GetTicketAsync(identity.Session, cancellationToken, SaveRotation);
            return ticket.Value;
        }

        public static async Task<byte[]?> GetAvatarAsync(long userId, CancellationToken cancellationToken = default) =>
            await Api.GetAvatarAsync(userId, cancellationToken);

        public static async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            using var accountLock = await Vault.LockAsync(id, cancellationToken);
            // Removing locally does not revoke the session on Roblox's servers.
            Vault.Delete(id);
            FavoritesLauncher.Store.Update(data =>
            {
                data.Accounts.RemoveAll(x => x.Id == id);
                foreach (var game in data.Games.Where(x => x.AccountId == id)) game.AccountId = null;
            });
        }
    }
}
