using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CraftBox.Core
{
    public class TmdbSeason { public int Number; public int EpisodeCount; public string Name; }
    public class TmdbEpisode { public int Number; public string Name; public DateTime AirDate; public string Overview; public string StillPath; }

    /// Optional poster/overview lookup. Without a key every call is a no-op.
    public class Tmdb
    {
        const string Api = "https://api.themoviedb.org/3";
        const string Img = "https://image.tmdb.org/t/p/";

        public static string StillUrl(string stillPath, string size = "w300")
            => string.IsNullOrEmpty(stillPath) ? null : Img + size + stillPath;
        readonly HttpClient http;
        readonly string key;
        readonly string cacheDir;
        readonly ConcurrentDictionary<string, Task> pending = new ConcurrentDictionary<string, Task>();
        readonly Dictionary<string, CachedMeta> diskCache = new Dictionary<string, CachedMeta>();

        class CachedMeta
        {
            public string PosterUrl;
            public string BackdropUrl;
            public string Overview;
        }

        public Tmdb(HttpClient http, string key, string dataDir = null)
        {
            this.http = http;
            this.key = (key ?? "").Trim();
            cacheDir = dataDir;
            LoadDiskCache();
        }

        volatile bool disabled;
        public bool Enabled => key.Length > 0 && !disabled;

        public int AgeLimit { get; set; }

        readonly ConcurrentDictionary<int, string> certCache = new ConcurrentDictionary<int, string>();

        public string GetCachedCert(int tmdbId) =>
            tmdbId > 0 && certCache.TryGetValue(tmdbId, out var c) && c.Length > 0 ? c : null;

        public string CertLabel(bool tv)
        {
            if (AgeLimit <= 0) return null;
            if (AgeLimit < 13) return tv ? "TV-PG" : "PG";
            if (AgeLimit < 17) return tv ? "TV-14" : "PG-13";
            return tv ? "TV-MA" : "R";
        }

        public string CertFilter(bool tv)
        {
            var cert = CertLabel(tv);
            if (cert == null) return "";
            return $"&certification_country=US&certification.lte={cert}&include_adult=false";
        }

        public async Task FetchCertsAsync(List<Title> titles, CancellationToken ct = default)
        {
            if (!Enabled || titles == null) return;
            foreach (var t in titles)
            {
                if (t.Certification == null && t.TmdbId > 0 && certCache.TryGetValue(t.TmdbId, out var cached))
                    t.Certification = cached.Length > 0 ? cached : null;
            }
            var need = titles.Where(t => t.TmdbId > 0 && t.Certification == null && !certCache.ContainsKey(t.TmdbId)).ToList();
            if (need.Count == 0) return;
            var tasks = need.Select(async t =>
            {
                try
                {
                    var tv = t.Kind == TitleKind.Show;
                    var path = tv
                        ? $"/tv/{t.TmdbId}/content_ratings"
                        : $"/movie/{t.TmdbId}/release_dates";
                    var doc = await GetAsync(path, ct).ConfigureAwait(false);
                    if (doc.TryGetProperty("results", out var arr))
                    {
                        foreach (var entry in arr.EnumerateArray())
                        {
                            var iso = entry.TryGetProperty("iso_3166_1", out var c) ? c.GetString() : "";
                            if (iso != "US") continue;
                            if (tv)
                            {
                                var r = entry.TryGetProperty("rating", out var rv) ? rv.GetString() : "";
                                if (!string.IsNullOrEmpty(r)) { t.Certification = r; break; }
                            }
                            else if (entry.TryGetProperty("release_dates", out var rds))
                            {
                                foreach (var rd in rds.EnumerateArray())
                                {
                                    var cert = rd.TryGetProperty("certification", out var cv) ? cv.GetString() : "";
                                    if (!string.IsNullOrEmpty(cert)) { t.Certification = cert; break; }
                                }
                                if (t.Certification != null) break;
                            }
                        }
                    }
                    t.Certification ??= "";
                    certCache[t.TmdbId] = t.Certification;
                }
                catch { t.Certification ??= ""; certCache[t.TmdbId] = ""; }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        static readonly Dictionary<int, string> GenreMap = new Dictionary<int, string>
        {
            {28,"Action"},{12,"Adventure"},{16,"Animation"},{35,"Comedy"},{80,"Crime"},
            {99,"Documentary"},{18,"Drama"},{10751,"Family"},{14,"Fantasy"},{36,"History"},
            {27,"Horror"},{10402,"Music"},{9648,"Mystery"},{10749,"Romance"},{878,"Sci-Fi"},
            {10770,"TV Movie"},{53,"Thriller"},{10752,"War"},{37,"Western"},
            {10759,"Action & Adventure"},{10762,"Kids"},{10763,"News"},{10764,"Reality"},
            {10765,"Sci-Fi & Fantasy"},{10766,"Soap"},{10767,"Talk"},{10768,"War & Politics"},
        };

        /// Get IMDB ID for a TMDB entry by its TMDB ID.
        public async Task<string> GetImdbIdAsync(int tmdbId, bool tv, CancellationToken ct = default)
        {
            if (!Enabled || tmdbId <= 0) return null;
            try
            {
                var type = tv ? "tv" : "movie";
                var doc = await GetAsync($"/{type}/{tmdbId}/external_ids", ct).ConfigureAwait(false);
                if (doc.TryGetProperty("imdb_id", out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var id = prop.GetString();
                    if (!string.IsNullOrEmpty(id)) return id;
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                StreamEngine.Log?.Invoke($"TMDB external_ids fail [{tmdbId}]: {e.Message}");
            }
            return null;
        }

        /// Get TV show seasons and IMDB ID in one call.
        public async Task<(List<TmdbSeason> Seasons, string ImdbId, string Genres, float Rating)> GetTvInfoAsync(int tmdbId, CancellationToken ct = default)
        {
            if (!Enabled || tmdbId <= 0) return (null, null, null, 0);
            try
            {
                var doc = await GetAsync($"/tv/{tmdbId}?append_to_response=external_ids", ct).ConfigureAwait(false);
                string imdbId = null;
                if (doc.TryGetProperty("external_ids", out var ext))
                    if (ext.TryGetProperty("imdb_id", out var ip) && ip.ValueKind == JsonValueKind.String)
                        imdbId = ip.GetString();
                string genres = "";
                if (doc.TryGetProperty("genres", out var garr))
                {
                    var names = new List<string>();
                    foreach (var g in garr.EnumerateArray())
                    {
                        var gn = Str(g, "name");
                        if (gn.Length > 0) names.Add(gn);
                    }
                    genres = string.Join(", ", names.Take(3));
                }
                float rating = doc.TryGetProperty("vote_average", out var va) && va.ValueKind == JsonValueKind.Number ? (float)va.GetDouble() : 0;
                var list = new List<TmdbSeason>();
                if (doc.TryGetProperty("seasons", out var seasons))
                    foreach (var s in seasons.EnumerateArray())
                    {
                        int num = s.TryGetProperty("season_number", out var sn) && sn.ValueKind == JsonValueKind.Number ? (int)sn.GetInt64() : -1;
                        if (num <= 0) continue;
                        list.Add(new TmdbSeason
                        {
                            Number = num,
                            EpisodeCount = s.TryGetProperty("episode_count", out var ec) && ec.ValueKind == JsonValueKind.Number ? (int)ec.GetInt64() : 0,
                            Name = Str(s, "name"),
                        });
                    }
                return (list, imdbId, genres, rating);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                StreamEngine.Log?.Invoke($"TMDB tv info fail [{tmdbId}]: {e.Message}");
                return (null, null, null, 0);
            }
        }

        /// Get episodes for a specific season.
        public async Task<List<TmdbEpisode>> GetSeasonEpisodesAsync(int tmdbId, int seasonNum, CancellationToken ct = default)
        {
            if (!Enabled || tmdbId <= 0) return null;
            try
            {
                var doc = await GetAsync($"/tv/{tmdbId}/season/{seasonNum}", ct).ConfigureAwait(false);
                var list = new List<TmdbEpisode>();
                if (doc.TryGetProperty("episodes", out var episodes))
                    foreach (var ep in episodes.EnumerateArray())
                    {
                        int num = ep.TryGetProperty("episode_number", out var en) && en.ValueKind == JsonValueKind.Number ? (int)en.GetInt64() : -1;
                        if (num < 0) continue;
                        DateTime airDate = default;
                        var ad = Str(ep, "air_date");
                        if (ad.Length >= 10) DateTime.TryParse(ad, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out airDate);
                        list.Add(new TmdbEpisode { Number = num, Name = Str(ep, "name"), AirDate = airDate, Overview = Str(ep, "overview"), StillPath = Str(ep, "still_path") });
                    }
                return list;
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                StreamEngine.Log?.Invoke($"TMDB season fail [{tmdbId}/S{seasonNum}]: {e.Message}");
                return null;
            }
        }

        /// Find TMDB ID for a TV show by name.
        public async Task<int> FindTvIdAsync(string name, CancellationToken ct = default)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(name)) return 0;
            try
            {
                var doc = await GetAsync($"/search/tv?query={Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
                if (doc.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                {
                    var first = results[0];
                    if (first.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                        return (int)id.GetInt64();
                }
            }
            catch { }
            return 0;
        }

        /// Fetch trending movies or TV shows from TMDB.
        public async Task<List<Title>> TrendingAsync(string mediaType, int page = 1, CancellationToken ct = default)
        {
            if (!Enabled) return new List<Title>();
            try
            {
                if (AgeLimit > 0)
                    return await DiscoverAsync(mediaType, null, null, ct).ConfigureAwait(false);
                var doc = await GetAsync($"/trending/{mediaType}/week?page={page}", ct).ConfigureAwait(false);
                return ParseCatalog(doc, mediaType == "tv");
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                StreamEngine.Log?.Invoke($"TMDB trending fail: {e.Message}");
                return new List<Title>();
            }
        }

        /// Search TMDB for movies and TV shows by name.
        public async Task<List<Title>> SearchCatalogAsync(string query, CancellationToken ct = default)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(query)) return new List<Title>();
            try
            {
                var adult = AgeLimit > 0 ? "&include_adult=false" : "";
                var doc = await GetAsync($"/search/multi?query={Uri.EscapeDataString(query)}{adult}", ct).ConfigureAwait(false);
                return ParseCatalog(doc, null);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                StreamEngine.Log?.Invoke($"TMDB search fail [{query}]: {e.Message}");
                return new List<Title>();
            }
        }

        List<Title> ParseCatalog(JsonElement doc, bool? forceTv)
        {
            if (!doc.TryGetProperty("results", out var results)) return new List<Title>();
            var list = new List<Title>();
            foreach (var r in results.EnumerateArray())
            {
                string mt;
                if (forceTv.HasValue)
                    mt = forceTv.Value ? "tv" : "movie";
                else
                    mt = Str(r, "media_type");
                if (mt != "movie" && mt != "tv") continue;
                bool tv = mt == "tv";
                var name = Str(r, tv ? "name" : "title");
                if (string.IsNullOrEmpty(name)) continue;
                var dateStr = Str(r, tv ? "first_air_date" : "release_date");
                int year = 0;
                if (dateStr.Length >= 4) int.TryParse(dateStr.Substring(0, 4), out year);
                DateTime releaseDate = default;
                if (dateStr.Length >= 10) DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out releaseDate);
                var genreNames = new List<string>();
                if (r.TryGetProperty("genre_ids", out var gids))
                    foreach (var g in gids.EnumerateArray())
                        if (g.ValueKind == JsonValueKind.Number && GenreMap.TryGetValue((int)g.GetInt64(), out var gn))
                            genreNames.Add(gn);
                list.Add(new Title
                {
                    Kind = tv ? TitleKind.Show : TitleKind.Movie,
                    Name = name,
                    Year = year,
                    Key = (tv ? "show:" : "movie:") + ReleaseInfo.Key(name) + (tv ? "" : ":" + year),
                    TmdbId = r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? (int)id.GetInt64() : 0,
                    PosterUrl = Image(r, "poster_path", "w342"),
                    BackdropUrl = Image(r, "backdrop_path", "w1280"),
                    Overview = Str(r, "overview"),
                    ReleaseDate = releaseDate,
                    Rating = r.TryGetProperty("vote_average", out var va) && va.ValueKind == JsonValueKind.Number ? (float)va.GetDouble() : 0,
                    Genres = string.Join(", ", genreNames.Take(3)),
                });
            }
            return list;
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        public async Task<List<Title>> DiscoverAsync(string mediaType, string genres = null, string language = null, CancellationToken ct = default)
        {
            if (!Enabled) return new List<Title>();
            try
            {
                var path = $"/discover/{mediaType}?sort_by=popularity.desc{CertFilter(mediaType == "tv")}";
                if (genres != null) path += $"&with_genres={genres}";
                if (language != null) path += $"&with_original_language={language}";
                var doc = await GetAsync(path, ct).ConfigureAwait(false);
                return ParseCatalog(doc, mediaType == "tv");
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                StreamEngine.Log?.Invoke($"TMDB discover fail: {e.Message}");
                return new List<Title>();
            }
        }

        public async Task<List<Title>> ListAsync(string path, bool tv, CancellationToken ct = default)
        {
            if (!Enabled) return new List<Title>();
            try
            {
                if (AgeLimit > 0 && !path.StartsWith("/discover/"))
                {
                    var discoverPath = $"/discover/{(tv ? "tv" : "movie")}?sort_by=popularity.desc{CertFilter(tv)}";
                    if (path.Contains("now_playing"))
                    {
                        var from = DateTime.UtcNow.AddDays(-28).ToString("yyyy-MM-dd");
                        var to = DateTime.UtcNow.ToString("yyyy-MM-dd");
                        discoverPath += $"&with_release_type=2|3&release_date.gte={from}&release_date.lte={to}";
                    }
                    else if (path.Contains("upcoming"))
                    {
                        var from = DateTime.UtcNow.ToString("yyyy-MM-dd");
                        var to = DateTime.UtcNow.AddDays(42).ToString("yyyy-MM-dd");
                        discoverPath += $"&primary_release_date.gte={from}&primary_release_date.lte={to}";
                    }
                    else if (path.Contains("top_rated"))
                        discoverPath += "&vote_count.gte=200&sort_by=vote_average.desc";
                    var doc2 = await GetAsync(discoverPath, ct).ConfigureAwait(false);
                    return ParseCatalog(doc2, tv);
                }
                var filtered = path.Contains("?") ? path + CertFilter(tv) : path + "?" + CertFilter(tv).TrimStart('&');
                var doc = await GetAsync(filtered, ct).ConfigureAwait(false);
                return ParseCatalog(doc, tv);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                StreamEngine.Log?.Invoke($"TMDB list fail: {e.Message}");
                return new List<Title>();
            }
        }

        /// Fills PosterUrl/BackdropUrl/Overview. Concurrent calls for the same title share one request.
        public Task EnrichAsync(Title t, CancellationToken ct = default)
        {
            if (t.PosterUrl != null) return Task.CompletedTask;
            if (TryApplyCache(t)) return Task.CompletedTask;
            if (!Enabled) return Task.CompletedTask;
            return pending.GetOrAdd(t.Key, _ => LookupAsync(t, ct));
        }

        bool TryApplyCache(Title t)
        {
            lock (diskCache)
            {
                if (!diskCache.TryGetValue(t.Key, out var c)) return false;
                t.PosterUrl = c.PosterUrl;
                t.BackdropUrl = c.BackdropUrl;
                if (string.IsNullOrEmpty(t.Overview)) t.Overview = c.Overview;
                return true;
            }
        }

        async Task LookupAsync(Title t, CancellationToken ct)
        {
            try
            {
                var tv = t.Kind == TitleKind.Show;
                JsonElement? hit = null;
                if (t.ImdbId != null)
                {
                    var doc = await GetAsync($"/find/{t.ImdbId}?external_source=imdb_id", ct);
                    var arr = doc.GetProperty(tv ? "tv_results" : "movie_results");
                    if (arr.GetArrayLength() > 0) hit = arr[0];
                }
                if (hit == null && t.TmdbId > 0 && !tv)
                    hit = await GetAsync($"/movie/{t.TmdbId}", ct);
                if (hit == null)
                {
                    var path = tv
                        ? $"/search/tv?query={Uri.EscapeDataString(t.Name)}" + (t.Year > 0 ? $"&first_air_date_year={t.Year}" : "")
                        : $"/search/movie?query={Uri.EscapeDataString(t.Name)}" + (t.Year > 0 ? $"&year={t.Year}" : "");
                    var arr = (await GetAsync(path, ct)).GetProperty("results");
                    if (arr.GetArrayLength() > 0) hit = arr[0];
                }
                if (hit == null) { t.PosterUrl = ""; SaveToCache(t); return; }
                var h = hit.Value;
                t.PosterUrl = Image(h, "poster_path", "w342");
                t.BackdropUrl = Image(h, "backdrop_path", "w1280");
                if (h.TryGetProperty("overview", out var o)) t.Overview = o.GetString();
                SaveToCache(t);
            }
            catch (HttpRequestException e)
            {
                StreamEngine.Log?.Invoke($"TMDB lookup fail [{t.Name}]: {e.GetType().Name}: {e.Message}");
                if (e.Message.Contains("401"))
                {
                    disabled = true;
                    StreamEngine.Log?.Invoke("TMDB: key invalid (401), disabling all lookups");
                }
                t.PosterUrl = "";
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                StreamEngine.Log?.Invoke($"TMDB lookup fail [{t.Name}]: {e.GetType().Name}: {e.Message}");
                t.PosterUrl = "";
            }
            finally
            {
                pending.TryRemove(t.Key, out _);
            }
        }

        void SaveToCache(Title t)
        {
            lock (diskCache)
            {
                diskCache[t.Key] = new CachedMeta
                {
                    PosterUrl = t.PosterUrl ?? "",
                    BackdropUrl = t.BackdropUrl ?? "",
                    Overview = t.Overview ?? "",
                };
            }
            FlushDiskCache();
        }

        string CachePath => cacheDir != null ? Path.Combine(cacheDir, "tmdb_cache.txt") : null;

        void LoadDiskCache()
        {
            try
            {
                var path = CachePath;
                if (path == null || !File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('\t');
                    if (parts.Length < 4) continue;
                    diskCache[parts[0]] = new CachedMeta
                    {
                        PosterUrl = parts[1] == "-" ? "" : parts[1],
                        BackdropUrl = parts[2] == "-" ? "" : parts[2],
                        Overview = parts.Length > 3 ? parts[3].Replace("\\n", "\n") : "",
                    };
                }
            }
            catch { }
        }

        void FlushDiskCache()
        {
            try
            {
                var path = CachePath;
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines;
                lock (diskCache)
                {
                    lines = diskCache.Select(kv =>
                        $"{kv.Key}\t{(kv.Value.PosterUrl?.Length > 0 ? kv.Value.PosterUrl : "-")}\t{(kv.Value.BackdropUrl?.Length > 0 ? kv.Value.BackdropUrl : "-")}\t{(kv.Value.Overview ?? "").Replace("\n", "\\n")}")
                        .ToList();
                }
                File.WriteAllLines(path, lines);
            }
            catch { }
        }

        public async Task<List<Title>> RecommendationsAsync(int tmdbId, bool tv, CancellationToken ct = default)
        {
            if (!Enabled || tmdbId <= 0) return new List<Title>();
            try
            {
                var type = tv ? "tv" : "movie";
                var doc = await GetAsync($"/{type}/{tmdbId}/recommendations?language=en-US", ct).ConfigureAwait(false);
                return ParseCatalog(doc, tv);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                StreamEngine.Log?.Invoke($"TMDB recommendations fail [{tmdbId}]: {e.Message}");
                return new List<Title>();
            }
        }

        public async Task<List<Title>> SimilarAsync(int tmdbId, bool tv, CancellationToken ct = default)
        {
            if (!Enabled || tmdbId <= 0) return new List<Title>();
            try
            {
                var type = tv ? "tv" : "movie";
                var doc = await GetAsync($"/{type}/{tmdbId}/similar?language=en-US", ct).ConfigureAwait(false);
                return ParseCatalog(doc, tv);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                StreamEngine.Log?.Invoke($"TMDB similar fail [{tmdbId}]: {e.Message}");
                return new List<Title>();
            }
        }

        public async Task<List<Title>> TopicDiscoverAsync(string mediaType, string genres = null,
            string keywords = null, string extra = null, CancellationToken ct = default)
        {
            if (!Enabled) return new List<Title>();
            try
            {
                var path = $"/discover/{mediaType}?sort_by=popularity.desc&vote_count.gte=50&language=en-US&include_adult=false{CertFilter(mediaType == "tv")}";
                if (genres != null) path += $"&with_genres={genres}";
                if (keywords != null) path += $"&with_keywords={keywords}";
                if (extra != null) path += "&" + extra;
                var doc = await GetAsync(path, ct).ConfigureAwait(false);
                return ParseCatalog(doc, mediaType == "tv");
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                StreamEngine.Log?.Invoke($"TMDB topic discover fail: {e.Message}");
                return new List<Title>();
            }
        }

        public async Task<int> FindKeywordIdAsync(string keyword, CancellationToken ct = default)
        {
            if (!Enabled) return 0;
            try
            {
                var doc = await GetAsync($"/search/keyword?query={Uri.EscapeDataString(keyword)}", ct).ConfigureAwait(false);
                if (doc.TryGetProperty("results", out var results))
                    foreach (var r in results.EnumerateArray())
                        if (Str(r, "name").Equals(keyword, StringComparison.OrdinalIgnoreCase))
                            return r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? (int)id.GetInt64() : 0;
            }
            catch { }
            return 0;
        }

        public async Task PreWarmCatalogAsync(AppCache cache, CancellationToken ct = default)
        {
            if (!Enabled) return;
            var tasks = new (string key, Task<List<Title>> task)[]
            {
                ("trending_movie", TrendingAsync("movie", 1, ct)),
                ("trending_tv", TrendingAsync("tv", 1, ct)),
                ("now_playing", ListAsync("/movie/now_playing?language=en-US", false, ct)),
                ("upcoming", ListAsync("/movie/upcoming?language=en-US", false, ct)),
                ("discover_movie_16", DiscoverAsync("movie", "16", null, ct)),
                ("discover_tv_16", DiscoverAsync("tv", "16", null, ct)),
                ("discover_tv_16_ja", DiscoverAsync("tv", "16", "ja", ct)),
                ("discover_tv__ko", DiscoverAsync("tv", null, "ko", ct)),
                ("acclaimed", ListAsync("/discover/movie?sort_by=vote_average.desc&vote_count.gte=1000&vote_average.gte=8", false, ct)),
                ("hidden_gems", ListAsync("/discover/movie?sort_by=vote_average.desc&vote_count.gte=50&vote_count.lte=500&vote_average.gte=7", false, ct)),
                ("top_rated_tv", ListAsync("/tv/top_rated", true, ct)),
                ("discover_movie_28", DiscoverAsync("movie", "28", null, ct)),
                ("discover_movie_35", DiscoverAsync("movie", "35", null, ct)),
                ("discover_tv_10765", DiscoverAsync("tv", "10765", null, ct)),
                ("discover_movie_27", DiscoverAsync("movie", "27", null, ct)),
                ("discover_movie_99", DiscoverAsync("movie", "99", null, ct)),
                ("discover_movie_10749", DiscoverAsync("movie", "10749", null, ct)),
                ("discover_tv_9648", DiscoverAsync("tv", "9648", null, ct)),
                ("discover_movie_53", DiscoverAsync("movie", "53", null, ct)),
                ("provider_netflix", ListAsync("/discover/tv?sort_by=popularity.desc&with_watch_providers=8&watch_region=US", true, ct)),
                ("provider_disney", ListAsync("/discover/tv?sort_by=popularity.desc&with_watch_providers=337&watch_region=US", true, ct)),
                ("provider_apple", ListAsync("/discover/tv?sort_by=popularity.desc&with_watch_providers=350&watch_region=US", true, ct)),
                ("provider_prime", ListAsync("/discover/tv?sort_by=popularity.desc&with_watch_providers=9&watch_region=US", true, ct)),
                ("provider_hbo", ListAsync("/discover/tv?sort_by=popularity.desc&with_watch_providers=384&watch_region=US", true, ct)),
            };
            try { await Task.WhenAll(tasks.Select(t => t.task)).ConfigureAwait(false); }
            catch { }
            var allTitles = new List<Title>();
            foreach (var (key, task) in tasks)
            {
                if (task.IsCompleted && !task.IsFaulted && task.Result?.Count > 0)
                    allTitles.AddRange(task.Result.Take(20));
            }
            try { await FetchCertsAsync(allTitles, ct).ConfigureAwait(false); }
            catch { }
            foreach (var (key, task) in tasks)
            {
                if (task.IsCompleted && !task.IsFaulted && task.Result?.Count > 0)
                    cache.PutCatalog(key, task.Result.Take(20).ToList());
            }
        }

        static string Image(JsonElement e, string prop, string size) =>
            e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? Img + size + p.GetString() : "";

        async Task<JsonElement> GetAsync(string path, CancellationToken ct)
        {
            // A v4 read token is a long JWT; a v3 key is 32 hex chars.
            var v4 = key.Length > 40;
            var url = Api + path + (v4 ? "" : (path.Contains("?") ? "&" : "?") + "api_key=" + key);
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (v4) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using (var res = await http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    res.EnsureSuccessStatusCode();
                    var s = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return JsonDocument.Parse(s).RootElement.Clone();
                }
            }
        }
    }
}
