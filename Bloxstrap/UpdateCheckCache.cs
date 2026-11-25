using BuffStrap.Release;

namespace Bloxstrap
{
    internal sealed class UpdateCheckCache
    {
        public DateTimeOffset CheckedAtUtc { get; set; }
        public string TagName { get; set; } = "";
        public string Notes { get; set; } = "";

        private static string FilePath => Path.Combine(Paths.Base, "UpdateCheck.json");

        public static UpdateCheckCache? Load()
        {
            try
            {
                if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 65536) return null;
                return JsonSerializer.Deserialize<UpdateCheckCache>(File.ReadAllText(FilePath));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("UpdateCheckCache::Load", ex);
                return null;
            }
        }

        public static void Record(ReleaseInfo release)
        {
            try
            {
                var cache = new UpdateCheckCache { CheckedAtUtc = DateTimeOffset.UtcNow, TagName = release.TagName, Notes = release.Notes };
                string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, JsonSerializer.Serialize(cache));
                    File.Move(temp, FilePath, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch (Exception ex) { App.Logger.WriteException("UpdateCheckCache::Record", ex); }
        }
    }
}
