using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CraftBox.Core
{
    public class YtsCache
    {
        readonly YtsClient client;
        readonly string cacheDir;
        readonly TimeSpan maxAge;
        readonly object sync = new object();

        List<Release> cached = new List<Release>();
        DateTime fetchedUtc;
        bool loading;

        public YtsCache(YtsClient client, string dataDir, TimeSpan? maxAge = null)
        {
            this.client = client;
            this.maxAge = maxAge ?? TimeSpan.FromMinutes(30);
            cacheDir = dataDir;
            LoadDisk();
        }

        public bool HasData
        {
            get { lock (sync) return cached.Count > 0; }
        }

        public List<Release> Snapshot
        {
            get { lock (sync) return cached.ToList(); }
        }

        public async Task<List<Release>> GetAsync(string baseUrl, CancellationToken ct)
        {
            List<Release> snap;
            bool stale;
            lock (sync)
            {
                snap = cached.ToList();
                stale = (DateTime.UtcNow - fetchedUtc) > maxAge;
            }

            if (snap.Count > 0 && !stale)
                return snap;

            if (snap.Count > 0 && stale)
            {
                RefreshInBackground(baseUrl);
                return snap;
            }

            return await FetchFresh(baseUrl, ct).ConfigureAwait(false);
        }

        public event Action CacheUpdated;

        async Task<List<Release>> FetchFresh(string baseUrl, CancellationToken ct)
        {
            lock (sync) { if (loading) return cached.ToList(); loading = true; }
            try
            {
                var all = new List<Release>();
                // Recent movies
                for (int p = 1; p <= 3; p++)
                {
                    var batch = await client.BrowseAsync(baseUrl, p, 50, "date_added", null, ct).ConfigureAwait(false);
                    if (batch.Count == 0) break;
                    all.AddRange(batch);
                }
                // Popular movies
                var popular = await client.BrowseAsync(baseUrl, 1, 50, "download_count", null, ct).ConfigureAwait(false);
                all.AddRange(popular);
                // Top rated
                var rated = await client.BrowseAsync(baseUrl, 1, 50, "rating", null, ct).ConfigureAwait(false);
                all.AddRange(rated);

                lock (sync) { cached = all; fetchedUtc = DateTime.UtcNow; }
                SaveDisk(all);
                CacheUpdated?.Invoke();
                return all;
            }
            finally { lock (sync) loading = false; }
        }

        void RefreshInBackground(string baseUrl)
        {
            lock (sync) { if (loading) return; }
            _ = Task.Run(async () =>
            {
                try { await FetchFresh(baseUrl, CancellationToken.None).ConfigureAwait(false); }
                catch { }
            });
        }

        string CachePath => Path.Combine(cacheDir, "yts_cache.txt");
        string TimePath => Path.Combine(cacheDir, "yts_time.txt");

        void LoadDisk()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                var lines = File.ReadAllLines(CachePath);
                var releases = new List<Release>();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var parts = line.Split('\t');
                        if (parts.Length < 7) continue;
                        var r = new Release
                        {
                            Title = parts[0],
                            Link = parts[1],
                            Seeders = int.TryParse(parts[2], out var s) ? s : 0,
                            Peers = int.TryParse(parts[3], out var p) ? p : 0,
                            Size = long.TryParse(parts[4], out var sz) ? sz : 0,
                            ImdbId = parts[5] == "-" ? null : parts[5],
                            Indexer = "YTS",
                        };
                        if (long.TryParse(parts[6], out var epoch) && epoch > 0)
                            r.Published = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                        if (parts.Length > 7 && parts[7] != "-")
                            r.PosterUrl = parts[7];
                        r.Categories.Add(Cat.Movies);
                        r.Info = ReleaseInfo.Parse(r.Title);
                        releases.Add(r);
                    }
                    catch { }
                }
                if (releases.Count > 0)
                {
                    lock (sync) cached = releases;
                    if (File.Exists(TimePath) && DateTime.TryParse(File.ReadAllText(TimePath).Trim(), out var dt))
                        lock (sync) fetchedUtc = dt;
                }
            }
            catch { }
        }

        void SaveDisk(List<Release> releases)
        {
            try
            {
                Directory.CreateDirectory(cacheDir);
                var lines = new List<string>();
                foreach (var r in releases)
                {
                    var epoch = r.Published > DateTime.MinValue
                        ? new DateTimeOffset(r.Published, TimeSpan.Zero).ToUnixTimeSeconds().ToString()
                        : "0";
                    lines.Add($"{r.Title}\t{r.Link}\t{r.Seeders}\t{r.Peers}\t{r.Size}\t{r.ImdbId ?? "-"}\t{epoch}\t{r.PosterUrl ?? "-"}");
                }
                File.WriteAllLines(CachePath, lines);
                File.WriteAllText(TimePath, DateTime.UtcNow.ToString("o"));
            }
            catch { }
        }
    }
}
