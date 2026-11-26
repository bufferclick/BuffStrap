using BuffStrap.Favorites;
using System.Windows.Media.Imaging;

namespace Bloxstrap.Utility
{
    /// <summary>Small, public-only preview cache shared by the favorite editor and list.</summary>
    internal static class GamePreviews
    {
        private static readonly HttpClient Http = RobloxGamePreviewClient.CreateHttpClient();
        private static readonly RobloxGamePreviewClient Client = new(Http);
        private static readonly SemaphoreSlim Slots = new(4);
        private static readonly Dictionary<long, (RobloxGamePreview Preview, DateTime At)> Cache = new();
        private static readonly object Sync = new();
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

        public static async Task<RobloxGamePreview?> GetAsync(long placeId, CancellationToken token = default)
        {
            if (TryCached(placeId, out var cached)) return cached;
            await Slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (TryCached(placeId, out cached)) return cached;
                var preview = await Client.GetAsync(placeId, token).ConfigureAwait(false);
                // Don't cache partial failures: reopening Favorites can retry them.
                if (preview is not null && preview.DetailsAvailable && preview.IconPng is not null)
                {
                    lock (Sync)
                    {
                        Cache[placeId] = (preview, DateTime.UtcNow);
                        if (Cache.Count > 200)
                        {
                            long oldest = Cache.OrderBy(x => x.Value.At).First().Key;
                            Cache.Remove(oldest);
                        }
                    }
                }
                return preview;
            }
            finally { Slots.Release(); }
        }

        private static bool TryCached(long id, out RobloxGamePreview? preview)
        {
            lock (Sync)
            {
                if (Cache.TryGetValue(id, out var item) && DateTime.UtcNow - item.At < Lifetime)
                {
                    preview = item.Preview;
                    return true;
                }
                Cache.Remove(id);
                preview = null;
                return false;
            }
        }

        public static BitmapSource? MakeImage(byte[]? png)
        {
            if (png is null) return null;
            try
            {
                using var stream = new MemoryStream(png);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 256;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
        }
    }
}
