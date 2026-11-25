using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace BuffStrap.Favorites;

// These records contain only game details and account labels. They never contain
// Roblox passwords, cookies, authentication tickets, or other session material.
public sealed class FavoriteGame
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long PlaceId { get; set; }
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public Guid? AccountId { get; set; }
}

public sealed class FavoriteAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public override string ToString() => Username;
}

public sealed class RecentGame
{
    public long PlaceId { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset PlayedAtUtc { get; set; }
}

public sealed class FavoritesData
{
    public int SchemaVersion { get; set; } = 1;
    public List<FavoriteGame> Games { get; set; } = new();
    public List<FavoriteAccount> Accounts { get; set; } = new();
    public List<RecentGame> Recent { get; set; } = new();
}

/// <summary>An assigned account must never silently fall back to another session.</summary>
public sealed record FavoriteLaunchPlan(long PlaceId, string? AssignedUsername)
{
    public bool RequiresBrowserSignIn => AssignedUsername is not null;
    public string WebsiteUrl => $"https://www.roblox.com/games/{PlaceId}/";
    public string PlayerUrl => $"roblox://experiences/start?placeId={PlaceId}";

    public static FavoriteLaunchPlan Resolve(FavoritesData data, Guid id)
    {
        FavoritesStore.Validate(data);
        var game = data.Games.FirstOrDefault(x => x.Id == id)
            ?? throw new InvalidOperationException("This favorite no longer exists. Its shortcut can be removed.");
        string? username = game.AccountId is Guid accountId
            ? data.Accounts.Single(x => x.Id == accountId).Username : null;
        return new FavoriteLaunchPlan(game.PlaceId, username);
    }
}

/// <summary>
/// Reload-modify-save under a cross-process mutex, so an activity watcher and a
/// favorites window cannot silently overwrite each other's changes.
/// </summary>
public sealed class FavoritesStore
{
    public const int MaxRecent = 50;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _mutexName;

    public FavoritesStore(string path)
    {
        _path = Path.GetFullPath(path);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..24];
        _mutexName = "BuffStrap-Favorites-" + hash;
    }

    public FavoritesData Load() => Locked(LoadUnlocked);

    // Used by restore while replacing Favorites.json alongside the other files.
    public T WithExclusiveLock<T>(Func<T> action) => Locked(action);

    public void Update(Action<FavoritesData> change) => Locked(() =>
    {
        var data = LoadUnlocked();
        change(data);
        Validate(data);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return true;
    });

    public void RecordJoin(long placeId, DateTimeOffset time)
    {
        if (placeId <= 0) return;
        Update(data =>
        {
            string name = data.Games.FirstOrDefault(x => x.PlaceId == placeId)?.Name ?? $"Place {placeId}";
            data.Recent.RemoveAll(x => x.PlaceId == placeId);
            data.Recent.Insert(0, new RecentGame { PlaceId = placeId, Name = name, PlayedAtUtc = time.ToUniversalTime() });
            if (data.Recent.Count > MaxRecent)
                data.Recent.RemoveRange(MaxRecent, data.Recent.Count - MaxRecent);
        });
    }

    public static FavoritesData Parse(string json)
    {
        var data = JsonSerializer.Deserialize<FavoritesData>(json) ?? throw new InvalidDataException("Favorites data is empty.");
        Validate(data);
        return data;
    }

    public static string Serialize(FavoritesData data)
    {
        Validate(data);
        return JsonSerializer.Serialize(data, JsonOptions);
    }

    public static bool IsValidUsername(string? username) => username is not null &&
        Regex.IsMatch(username, "^[A-Za-z0-9_]{3,20}$", RegexOptions.CultureInvariant);

    public static void Validate(FavoritesData data)
    {
        if (data.SchemaVersion != 1 || data.Games is null || data.Accounts is null || data.Recent is null ||
            data.Games.Count > 10000 || data.Accounts.Count > 500 || data.Recent.Count > MaxRecent)
            throw new InvalidDataException("Favorites data has an unsupported format or size.");

        var accountIds = new HashSet<Guid>();
        var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in data.Accounts)
        {
            if (account is null || account.Id == Guid.Empty || !accountIds.Add(account.Id) ||
                !IsValidUsername(account.Username) || !usernames.Add(account.Username))
                throw new InvalidDataException("A saved account label is invalid or duplicated.");
        }

        var gameIds = new HashSet<Guid>();
        foreach (var game in data.Games)
        {
            if (game is null || game.Id == Guid.Empty || !gameIds.Add(game.Id) || game.PlaceId <= 0 ||
                String.IsNullOrWhiteSpace(game.Name) || game.Name.Length > 120 || game.Folder is null || game.Folder.Length > 80 ||
                (game.AccountId.HasValue && !accountIds.Contains(game.AccountId.Value)))
                throw new InvalidDataException("A saved favorite is invalid.");
        }

        foreach (var game in data.Recent)
        {
            if (game is null || game.PlaceId <= 0 || String.IsNullOrWhiteSpace(game.Name) || game.Name.Length > 120)
                throw new InvalidDataException("A recently played game is invalid.");
        }
    }

    private FavoritesData LoadUnlocked()
    {
        if (!File.Exists(_path)) return new FavoritesData();
        if (new FileInfo(_path).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Favorites file is too large.");
        return Parse(File.ReadAllText(_path));
    }

    private T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Favorites are in use by another BuffStrap process.");
            return action();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }
}
