using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CraftBox.Core
{
    /// One Torznab endpoint, e.g. a Prowlarr indexer (http://host:9696/1/api)
    /// or Jackett's aggregate (http://host:9117/api/v2.0/indexers/all/results/torznab/api).
    public class IndexerConfig
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string ApiKey { get; set; } = "";
    }

    public class Settings
    {
        public string ProwlarrUrl { get; set; } = "";
        public string ProwlarrApiKey { get; set; } = "";
        public string EztvUrl { get; set; } = "";
        public string YtsUrl { get; set; } = "https://yts.rs/api/v2/list_movies.json";
        public List<IndexerConfig> Indexers { get; set; } = new List<IndexerConfig>();
        public string TmdbKey { get; set; } = "";
        public int BufferLimitMb { get; set; } = 3000;
        public string PirateBayUrl { get; set; } = "https://apibay.org/q.php";
        public string SyncUrl { get; set; } = "";
        public string SyncToken { get; set; } = "";
        public string VidLinkKey { get; set; } = "";
        public string VidFastDigest { get; set; } = "";

        static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public static Settings Load(string path)
        {
            Settings s = null;
            try
            {
                if (File.Exists(path))
                    s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            }
            catch (JsonException) { }
            s = s ?? new Settings();

            var secretsPath = Path.Combine(Path.GetDirectoryName(path), "secrets.json");
            try
            {
                if (File.Exists(secretsPath))
                {
                    var sec = JsonSerializer.Deserialize<Settings>(File.ReadAllText(secretsPath));
                    if (sec != null)
                    {
                        if (!string.IsNullOrEmpty(sec.TmdbKey)) s.TmdbKey = sec.TmdbKey;
                        if (!string.IsNullOrEmpty(sec.SyncUrl)) s.SyncUrl = sec.SyncUrl;
                        if (!string.IsNullOrEmpty(sec.SyncToken)) s.SyncToken = sec.SyncToken;
                        if (!string.IsNullOrEmpty(sec.VidLinkKey)) s.VidLinkKey = sec.VidLinkKey;
                        if (!string.IsNullOrEmpty(sec.VidFastDigest)) s.VidFastDigest = sec.VidFastDigest;
                    }
                }
            }
            catch { }

            return s;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
    }
}
