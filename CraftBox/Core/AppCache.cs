using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace CraftBox.Core
{
    public class AppCache
    {
        readonly string dir;
        readonly long maxBytes;
        readonly Dictionary<string, CacheEntry> index = new Dictionary<string, CacheEntry>();
        bool indexDirty;
        Timer flushTimer;
        List<PlayRecord> historyCache;
        List<PinnedTitle> pinnedCache;

        class CacheEntry
        {
            public string Key;
            public string File;
            public long Size;
            public DateTime AccessedUtc;
        }

        public AppCache(string dataDir, long maxBytes = 500L * 1024 * 1024)
        {
            dir = Path.Combine(dataDir, "app-cache");
            this.maxBytes = maxBytes;
            Directory.CreateDirectory(dir);
            Load();
        }

        public string GetText(string key)
        {
            lock (index)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (!index.TryGetValue(key, out var e)) return null;
                var path = Path.Combine(dir, e.File);
                if (!File.Exists(path)) { index.Remove(key); indexDirty = true; return null; }
                e.AccessedUtc = DateTime.UtcNow;
                indexDirty = true;
                var text = File.ReadAllText(path);
                var elapsed = sw.ElapsedMilliseconds;
                if (elapsed > 5)
                    StreamEngine.Log?.Invoke($"CACHE GetText key={key} len={text?.Length ?? 0} time={elapsed}ms");
                return text;
            }
        }

        public void PutText(string key, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (index)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var fileName = SafeName(key) + ".json";
                var path = Path.Combine(dir, fileName);
                File.WriteAllText(path, text);
                var tWrite = sw.ElapsedMilliseconds;
                index[key] = new CacheEntry
                {
                    Key = key, File = fileName,
                    Size = (long)text.Length,
                    AccessedUtc = DateTime.UtcNow,
                };
                Evict();
                var tEvict = sw.ElapsedMilliseconds;
                indexDirty = true;
                ScheduleFlush();
                StreamEngine.Log?.Invoke($"CACHE PutText key={key} len={text.Length} write={tWrite}ms evict={tEvict-tWrite}ms total={tEvict}ms entries={index.Count}");
            }
        }

        public string GetFile(string key)
        {
            lock (index)
            {
                if (!index.TryGetValue(key, out var e)) return null;
                var path = Path.Combine(dir, e.File);
                if (!File.Exists(path)) { index.Remove(key); indexDirty = true; return null; }
                e.AccessedUtc = DateTime.UtcNow;
                indexDirty = true;
                return path;
            }
        }

        public string PutFile(string key, byte[] data, string ext)
        {
            if (data == null || data.Length == 0) return null;
            lock (index)
            {
                var fileName = SafeName(key) + ext;
                var path = Path.Combine(dir, fileName);
                File.WriteAllBytes(path, data);
                index[key] = new CacheEntry
                {
                    Key = key, File = fileName,
                    Size = data.LongLength,
                    AccessedUtc = DateTime.UtcNow,
                };
                Evict();
                indexDirty = true;
                ScheduleFlush();
                return path;
            }
        }

        public long TotalBytes
        {
            get { lock (index) return index.Values.Sum(e => e.Size); }
        }

        public int Count
        {
            get { lock (index) return index.Count; }
        }

        public void FlushIndex()
        {
            lock (index)
            {
                if (!indexDirty) return;
                SaveIndex();
                indexDirty = false;
            }
        }

        // ── Torrent results ──────────────────────────────────────────

        public List<Release> GetReleases(string key)
        {
            var json = GetText("rel:" + key);
            if (json == null) return null;
            try { return DeserializeReleases(json); }
            catch { return null; }
        }

        public void PutReleases(string key, List<Release> releases)
        {
            if (releases == null || releases.Count == 0) return;
            PutText("rel:" + key, SerializeReleases(releases));
        }

        // ── TMDB TV info ─────────────────────────────────────────────

        public (List<TmdbSeason> Seasons, string ImdbId, string Genres, float Rating)? GetTvInfo(int tmdbId)
        {
            if (tmdbId <= 0) return null;
            var json = GetText($"tv:{tmdbId}");
            if (json == null) return null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    var seasons = new List<TmdbSeason>();
                    if (root.TryGetProperty("seasons", out var sarr))
                        foreach (var s in sarr.EnumerateArray())
                            seasons.Add(new TmdbSeason
                            {
                                Number = Int(s, "n"),
                                EpisodeCount = Int(s, "c"),
                                Name = Str(s, "name"),
                            });
                    return (seasons, Str(root, "imdb"), Str(root, "genres"),
                        root.TryGetProperty("rating", out var rv) && rv.ValueKind == JsonValueKind.Number ? (float)rv.GetDouble() : 0);
                }
            }
            catch { return null; }
        }

        public void PutTvInfo(int tmdbId, List<TmdbSeason> seasons, string imdbId, string genres, float rating)
        {
            if (tmdbId <= 0 || seasons == null) return;
            var obj = new Dictionary<string, object>
            {
                ["imdb"] = imdbId ?? "", ["genres"] = genres ?? "", ["rating"] = rating,
                ["seasons"] = seasons.Select(s => new Dictionary<string, object>
                    { ["n"] = s.Number, ["c"] = s.EpisodeCount, ["name"] = s.Name ?? "" }).ToList(),
            };
            PutText($"tv:{tmdbId}", JsonSerializer.Serialize(obj));
        }

        // ── TMDB season episodes ─────────────────────────────────────

        public List<TmdbEpisode> GetSeasonEpisodes(int tmdbId, int seasonNum)
        {
            if (tmdbId <= 0) return null;
            var json = GetText($"tvs:{tmdbId}:{seasonNum}");
            if (json == null) return null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var list = new List<TmdbEpisode>();
                    bool hasStillKey = false;
                    foreach (var ep in doc.RootElement.EnumerateArray())
                    {
                        DateTime airDate = default;
                        var ad = Str(ep, "air");
                        if (ad.Length >= 10) DateTime.TryParse(ad, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out airDate);
                        if (ep.TryGetProperty("still", out _)) hasStillKey = true;
                        list.Add(new TmdbEpisode { Number = Int(ep, "n"), Name = Str(ep, "name"), AirDate = airDate, Overview = Str(ep, "ov"), StillPath = Str(ep, "still") });
                    }
                    if (list.Count == 0) return null;
                    if (!hasStillKey)
                    {
                        var cacheKey = $"tvs:{tmdbId}:{seasonNum}";
                        if (index.TryGetValue(cacheKey, out var stale))
                        {
                            try { File.Delete(Path.Combine(dir, stale.File)); } catch { }
                            index.Remove(cacheKey);
                            indexDirty = true;
                            ScheduleFlush();
                        }
                        return null;
                    }
                    return list;
                }
            }
            catch { return null; }
        }

        public void PutSeasonEpisodes(int tmdbId, int seasonNum, List<TmdbEpisode> episodes)
        {
            if (tmdbId <= 0 || episodes == null || episodes.Count == 0) return;
            var arr = episodes.Select(e => new Dictionary<string, object>
            {
                ["n"] = e.Number, ["name"] = e.Name ?? "",
                ["air"] = e.AirDate != default ? e.AirDate.ToString("yyyy-MM-dd") : "",
                ["ov"] = e.Overview ?? "", ["still"] = e.StillPath ?? "",
            }).ToList();
            PutText($"tvs:{tmdbId}:{seasonNum}", JsonSerializer.Serialize(arr));
        }

        // ── TMDB catalog ─────────────────────────────────────────────

        readonly Dictionary<string, (List<Title> titles, DateTime written)> catalogMem =
            new Dictionary<string, (List<Title>, DateTime)>();

        public List<Title> GetCatalog(string key, TimeSpan maxAge = default)
        {
            return GetCatalog(key, maxAge, out _);
        }

        public List<Title> GetCatalog(string key, TimeSpan maxAge, out bool stale)
        {
            stale = false;
            if (maxAge == default) maxAge = TimeSpan.FromHours(24);

            lock (catalogMem)
            {
                if (catalogMem.TryGetValue(key, out var mem))
                {
                    stale = DateTime.UtcNow - mem.written > maxAge;
                    return mem.titles;
                }
            }

            var json = GetText("cat:" + key);
            if (json == null) return null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    DateTime written = DateTime.UtcNow;
                    if (root.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out written))
                    { }
                    bool expired = DateTime.UtcNow - written > maxAge;
                    if (!root.TryGetProperty("titles", out var tarr)) return null;
                    var list = new List<Title>();
                    foreach (var t in tarr.EnumerateArray())
                    {
                        DateTime releaseDate = default;
                        var rd = Str(t, "release");
                        if (rd.Length >= 10) DateTime.TryParse(rd, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out releaseDate);
                        list.Add(new Title
                        {
                            Kind = (TitleKind)Int(t, "kind"), Name = Str(t, "name"), Year = Int(t, "year"),
                            Key = Str(t, "key"), TmdbId = Int(t, "tmdb"), ImdbId = Str(t, "imdb"),
                            PosterUrl = Str(t, "poster"), BackdropUrl = Str(t, "backdrop"),
                            Overview = Str(t, "overview"), Genres = Str(t, "genres"), ReleaseDate = releaseDate,
                            Rating = t.TryGetProperty("rating", out var rv) && rv.ValueKind == JsonValueKind.Number ? (float)rv.GetDouble() : 0,
                            Certification = Str(t, "cert"),
                        });
                        if (list[list.Count - 1].Certification == "") list[list.Count - 1].Certification = null;
                    }
                    if (list.Count == 0) return null;
                    stale = expired;
                    lock (catalogMem) { catalogMem[key] = (list, written); }
                    return list;
                }
            }
            catch { return null; }
        }

        public void ClearAllCatalogs()
        {
            lock (catalogMem) { catalogMem.Clear(); }
            lock (index)
            {
                var catKeys = index.Keys.Where(k => k.StartsWith("cat:")).ToList();
                foreach (var k in catKeys)
                {
                    if (index.TryGetValue(k, out var e))
                    {
                        try { File.Delete(Path.Combine(dir, e.File)); } catch { }
                        index.Remove(k);
                    }
                }
                if (catKeys.Count > 0) indexDirty = true;
            }
            StreamEngine.Log?.Invoke($"CACHE ClearAllCatalogs done");
        }

        public void PutCatalog(string key, List<Title> titles)
        {
            if (titles == null || titles.Count == 0) return;
            lock (catalogMem) { catalogMem[key] = (titles, DateTime.UtcNow); }
            var arr = titles.Select(t => new Dictionary<string, object>
            {
                ["kind"] = (int)t.Kind, ["name"] = t.Name ?? "", ["year"] = t.Year,
                ["key"] = t.Key ?? "", ["tmdb"] = t.TmdbId, ["imdb"] = t.ImdbId ?? "",
                ["poster"] = t.PosterUrl ?? "", ["backdrop"] = t.BackdropUrl ?? "",
                ["overview"] = t.Overview ?? "", ["rating"] = t.Rating, ["genres"] = t.Genres ?? "",
                ["release"] = t.ReleaseDate != default ? t.ReleaseDate.ToString("yyyy-MM-dd") : "",
                ["cert"] = t.Certification ?? "",
            }).ToList();
            PutText("cat:" + key, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["titles"] = arr, ["ts"] = DateTime.UtcNow.ToString("o"),
            }));
        }

        // ── Posters ──────────────────────────────────────────────────

        public string GetPoster(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            return GetFile("poster:" + url.GetHashCode().ToString("x8"));
        }

        public string PutPoster(string url, byte[] data)
        {
            if (string.IsNullOrEmpty(url) || data == null || data.Length < 100) return null;
            var ext = url.Contains(".png") ? ".png" : ".jpg";
            return PutFile("poster:" + url.GetHashCode().ToString("x8"), data, ext);
        }

        // ── Subtitles ────────────────────────────────────────────────

        public string GetSubtitle(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return GetFile("sub:" + id);
        }

        public string PutSubtitle(string id, byte[] data)
        {
            if (string.IsNullOrEmpty(id) || data == null || data.Length == 0) return null;
            return PutFile("sub:" + id, data, ".srt");
        }

        // ── Per-account scoping ─────────────────────────────────────

        string accountId = "default";

        static bool migrated;

        public void SetAccountId(string id)
        {
            accountId = id ?? "default";
            historyCache = null;
            pinnedCache = null;
            likesCache = null;
            dislikesCache = null;
            if (!migrated)
            {
                migrated = true;
                System.Threading.ThreadPool.QueueUserWorkItem(_ => MigrateOldKeys());
            }
        }

        void MigrateOldKeys()
        {
            lock (index)
            {
                if (index.ContainsKey("play_history") && !index.ContainsKey(HistoryKey))
                {
                    var old = GetText("play_history");
                    if (old != null) PutText(HistoryKey, old);
                }
                if (index.ContainsKey("pinned_titles") && !index.ContainsKey(PinnedKey))
                {
                    var old = GetText("pinned_titles");
                    if (old != null) PutText(PinnedKey, old);
                }
            }
        }

        // ── Play history (continue watching) ─────────────────────────

        string HistoryKey => $"play_history_{accountId}";

        public class PlayRecord
        {
            public string TitleKey;
            public string TitleName;
            public string PosterUrl;
            public string BackdropUrl;
            public string ImdbId;
            public int TmdbId;
            public TitleKind Kind;
            public int Season;
            public int Episode;
            public string ReleaseName;
            public string MagnetLink;
            public long PositionMs;
            public long DurationMs;
            public DateTime PlayedUtc;
        }

        public List<PlayRecord> GetHistory()
        {
            if (historyCache != null) return historyCache;
            var json = GetText(HistoryKey);
            if (json == null) return historyCache = new List<PlayRecord>();
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var list = new List<PlayRecord>();
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        DateTime played = default;
                        var ps = Str(e, "played");
                        if (ps.Length > 0) DateTime.TryParse(ps, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out played);
                        list.Add(new PlayRecord
                        {
                            TitleKey = Str(e, "key"), TitleName = Str(e, "name"),
                            PosterUrl = Str(e, "poster"), BackdropUrl = Str(e, "backdrop"),
                            ImdbId = Str(e, "imdb"), TmdbId = Int(e, "tmdb"),
                            Kind = (TitleKind)Int(e, "kind"),
                            Season = Int(e, "s"), Episode = Int(e, "e"),
                            ReleaseName = Str(e, "release"), MagnetLink = Str(e, "magnet"),
                            PositionMs = Long(e, "pos"), DurationMs = Long(e, "dur"),
                            PlayedUtc = played,
                        });
                    }
                    return historyCache = list;
                }
            }
            catch { return historyCache = new List<PlayRecord>(); }
        }

        public void SaveHistory(List<PlayRecord> records)
        {
            if (records == null) return;
            historyCache = records;
            var arr = records.Select(r => new Dictionary<string, object>
            {
                ["key"] = r.TitleKey ?? "", ["name"] = r.TitleName ?? "",
                ["poster"] = r.PosterUrl ?? "", ["backdrop"] = r.BackdropUrl ?? "",
                ["imdb"] = r.ImdbId ?? "", ["tmdb"] = r.TmdbId, ["kind"] = (int)r.Kind,
                ["s"] = r.Season, ["e"] = r.Episode,
                ["release"] = r.ReleaseName ?? "", ["magnet"] = r.MagnetLink ?? "",
                ["pos"] = r.PositionMs, ["dur"] = r.DurationMs,
                ["played"] = r.PlayedUtc.ToString("o"),
            }).ToList();
            PutText(HistoryKey, JsonSerializer.Serialize(arr));
        }

        public void RecordPlay(PlayRecord record)
        {
            var history = GetHistory();
            history.RemoveAll(r => r.TitleKey == record.TitleKey && r.Season == record.Season && r.Episode == record.Episode);
            history.Insert(0, record);
            if (history.Count > 50) history.RemoveRange(50, history.Count - 50);
            SaveHistory(history);
        }

        public void RemoveFromHistory(string titleKey, int season, int episode)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var history = GetHistory();
            var t0 = sw.ElapsedMilliseconds;
            history.RemoveAll(r => r.TitleKey == titleKey && r.Season == season && r.Episode == episode);
            SaveHistory(history);
            StreamEngine.Log?.Invoke($"CACHE RemoveFromHistory get={t0}ms save={sw.ElapsedMilliseconds-t0}ms total={sw.ElapsedMilliseconds}ms count={history.Count}");
        }

        // ── Pinned titles (My List) ──────────────────────────────────

        string PinnedKey => $"pinned_titles_{accountId}";

        public class PinnedTitle
        {
            public string Key;
            public string Name;
            public string PosterUrl;
            public string BackdropUrl;
            public string ImdbId;
            public int TmdbId;
            public TitleKind Kind;
            public int Year;
            public string Genres;
            public float Rating;
            public DateTime PinnedUtc;
        }

        public List<PinnedTitle> GetPinned()
        {
            if (pinnedCache != null) return pinnedCache;
            var json = GetText(PinnedKey);
            if (json == null) return pinnedCache = new List<PinnedTitle>();
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var list = new List<PinnedTitle>();
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        DateTime pinned = default;
                        var ps = Str(e, "pinned");
                        if (ps.Length > 0) DateTime.TryParse(ps, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out pinned);
                        list.Add(new PinnedTitle
                        {
                            Key = Str(e, "key"), Name = Str(e, "name"),
                            PosterUrl = Str(e, "poster"), BackdropUrl = Str(e, "backdrop"),
                            ImdbId = Str(e, "imdb"), TmdbId = Int(e, "tmdb"),
                            Kind = (TitleKind)Int(e, "kind"), Year = Int(e, "year"),
                            Genres = Str(e, "genres"),
                            Rating = e.TryGetProperty("rating", out var rv) && rv.ValueKind == JsonValueKind.Number ? (float)rv.GetDouble() : 0,
                            PinnedUtc = pinned,
                        });
                    }
                    return pinnedCache = list;
                }
            }
            catch { return pinnedCache = new List<PinnedTitle>(); }
        }

        public void SavePinned(List<PinnedTitle> pinned)
        {
            if (pinned == null) return;
            pinnedCache = pinned;
            var arr = pinned.Select(p => new Dictionary<string, object>
            {
                ["key"] = p.Key ?? "", ["name"] = p.Name ?? "",
                ["poster"] = p.PosterUrl ?? "", ["backdrop"] = p.BackdropUrl ?? "",
                ["imdb"] = p.ImdbId ?? "", ["tmdb"] = p.TmdbId, ["kind"] = (int)p.Kind,
                ["year"] = p.Year, ["genres"] = p.Genres ?? "", ["rating"] = p.Rating,
                ["pinned"] = p.PinnedUtc.ToString("o"),
            }).ToList();
            PutText(PinnedKey, JsonSerializer.Serialize(arr));
        }

        public bool IsPinned(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return GetPinned().Any(p => p.Key == key);
        }

        public void TogglePin(Title title)
        {
            if (title == null || string.IsNullOrEmpty(title.Key)) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pinned = GetPinned();
            var t0 = sw.ElapsedMilliseconds;
            var existing = pinned.FindIndex(p => p.Key == title.Key);
            if (existing >= 0)
                pinned.RemoveAt(existing);
            else
            {
                pinned.Insert(0, new PinnedTitle
                {
                    Key = title.Key, Name = title.Name,
                    PosterUrl = title.PosterUrl, BackdropUrl = title.BackdropUrl,
                    ImdbId = title.ImdbId, TmdbId = title.TmdbId,
                    Kind = title.Kind, Year = title.Year,
                    Genres = title.Genres, Rating = title.Rating,
                    PinnedUtc = DateTime.UtcNow,
                });
                if (pinned.Count > 100) pinned.RemoveRange(100, pinned.Count - 100);
            }
            SavePinned(pinned);
            StreamEngine.Log?.Invoke($"CACHE TogglePin get={t0}ms save={sw.ElapsedMilliseconds-t0}ms total={sw.ElapsedMilliseconds}ms");
        }

        // ── Likes / Dislikes ─────────────────────────────────────────

        string LikesKey => $"likes_{accountId}";
        string DislikesKey => $"dislikes_{accountId}";
        HashSet<string> likesCache;
        HashSet<string> dislikesCache;

        HashSet<string> LoadSet(string key)
        {
            var json = GetText(key);
            if (json == null) return new HashSet<string>();
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var set = new HashSet<string>();
                    foreach (var e in doc.RootElement.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String) set.Add(e.GetString());
                    return set;
                }
            }
            catch { return new HashSet<string>(); }
        }

        void SaveSet(string key, HashSet<string> set)
        {
            PutText(key, JsonSerializer.Serialize(set.ToArray()));
        }

        public HashSet<string> GetLikes()
        {
            if (likesCache != null) return likesCache;
            return likesCache = LoadSet(LikesKey);
        }

        public HashSet<string> GetDislikes()
        {
            if (dislikesCache != null) return dislikesCache;
            return dislikesCache = LoadSet(DislikesKey);
        }

        public bool IsLiked(string key) => !string.IsNullOrEmpty(key) && GetLikes().Contains(key);
        public bool IsDisliked(string key) => !string.IsNullOrEmpty(key) && GetDislikes().Contains(key);

        public void ToggleLike(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var likes = GetLikes();
            var dislikes = GetDislikes();
            if (likes.Contains(key))
                likes.Remove(key);
            else
            {
                likes.Add(key);
                if (dislikes.Remove(key))
                    SaveSet(DislikesKey, dislikes);
            }
            SaveSet(LikesKey, likes);
        }

        public void ToggleDislike(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var likes = GetLikes();
            var dislikes = GetDislikes();
            if (dislikes.Contains(key))
                dislikes.Remove(key);
            else
            {
                dislikes.Add(key);
                if (likes.Remove(key))
                    SaveSet(LikesKey, likes);
            }
            SaveSet(DislikesKey, dislikes);
        }

        // ── Internal ─────────────────────────────────────────────────

        void Evict()
        {
            long total = 0;
            foreach (var e in index.Values) total += e.Size;
            while (total > maxBytes && index.Count > 1)
            {
                CacheEntry oldest = null;
                foreach (var e in index.Values)
                    if (oldest == null || e.AccessedUtc < oldest.AccessedUtc) oldest = e;
                if (oldest == null) break;
                total -= oldest.Size;
                try { File.Delete(Path.Combine(dir, oldest.File)); } catch { }
                index.Remove(oldest.Key);
            }
        }

        void Load()
        {
            var idxPath = Path.Combine(dir, "_index.json");
            if (!File.Exists(idxPath)) return;
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(idxPath)))
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var key = Str(item, "k");
                        var file = Str(item, "f");
                        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(file)) continue;
                        if (!File.Exists(Path.Combine(dir, file))) continue;
                        index[key] = new CacheEntry
                        {
                            Key = key, File = file,
                            Size = item.TryGetProperty("s", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt64() : 0,
                            AccessedUtc = item.TryGetProperty("a", out var av) && av.ValueKind == JsonValueKind.String
                                ? (DateTime.TryParse(av.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt) ? dt : DateTime.UtcNow)
                                : DateTime.UtcNow,
                        };
                    }
            }
            catch { }
        }

        void ScheduleFlush()
        {
            if (flushTimer != null) return;
            flushTimer = new Timer(_ =>
            {
                flushTimer = null;
                FlushIndex();
            }, null, 3000, Timeout.Infinite);
        }

        void SaveIndex()
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var entries = index.Values.Select(e => new { k = e.Key, f = e.File, s = e.Size, a = e.AccessedUtc.ToString("o") });
                File.WriteAllText(Path.Combine(dir, "_index.json"), JsonSerializer.Serialize(entries));
                StreamEngine.Log?.Invoke($"CACHE SaveIndex entries={index.Count} time={sw.ElapsedMilliseconds}ms");
            }
            catch { }
        }

        static string SerializeReleases(List<Release> releases) =>
            JsonSerializer.Serialize(releases.Select(r => new Dictionary<string, object>
            {
                ["t"] = r.Title ?? "", ["x"] = r.Indexer ?? "", ["l"] = r.Link ?? "",
                ["s"] = r.Size, ["se"] = r.Seeders, ["p"] = r.Peers, ["c"] = r.Categories,
            }).ToList());

        static List<Release> DeserializeReleases(string json)
        {
            var list = new List<Release>();
            using (var doc = JsonDocument.Parse(json))
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var r = new Release
                    {
                        Title = Str(item, "t"), Indexer = Str(item, "x"), Link = Str(item, "l"),
                        Size = Long(item, "s"), Seeders = (int)Long(item, "se"), Peers = (int)Long(item, "p"),
                    };
                    if (item.TryGetProperty("c", out var cats))
                        foreach (var c in cats.EnumerateArray())
                            r.Categories.Add(c.GetInt32());
                    r.Info = ReleaseInfo.Parse(r.Title);
                    list.Add(r);
                }
            return list;
        }

        static string SafeName(string key)
        {
            var sb = new System.Text.StringBuilder(key.Length);
            foreach (var c in key)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            if (sb.Length > 100) sb.Length = 100;
            return sb.ToString();
        }

        static string Str(JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        static int Int(JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetInt64() : 0;
        static long Long(JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }
}
