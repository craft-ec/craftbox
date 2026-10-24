using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using CraftBox.Core;

namespace CraftBox.UI
{
    class HomeScreen : Screen
    {
        static readonly HttpClient imgHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        class Row
        {
            public string Name;
            public List<Title> Titles = new List<Title>();
            public View View;
            public View Strip;
            public List<View> Cards = new List<View>();
            public int Col;
            public string CatMediaType;
            public string CatGenreId;
            public string CatLanguage;
            public string CatExtra;
            public int CatDefaultSort;
            public bool IsContinueWatching;
            public List<AppCache.PlayRecord> PlayRecords;
        }

        // Hero banner
        View hero;
        ImageView heroBg;
        TextLabel heroTitle, heroOverview, heroMeta;
        View heroGradient;
        int heroIdx;
        List<Title> heroTitles = new List<Title>();

        readonly List<Row> rows = new List<Row>();
        readonly View rowsView = new View();
        readonly TextField search;
        readonly View searchBox;
        readonly TextLabel status;
        int focusRow = 0;
        int prevFocusRow = -1, prevFocusCol = -1;
        bool editing;
        string lastQuery = "";
        CancellationTokenSource loadCts;
        bool focusOnHero = true;
        Dictionary<string, int> watchProgressMap;

        public HomeScreen(App app) : base(app)
        {
            // Hero area
            hero = new View { Position = new Position(0, 0), Size = new Size(Theme.W, Theme.HeroH) };
            hero.ClippingMode = ClippingModeType.ClipChildren;
            heroBg = new ImageView { Size = new Size(Theme.W, Theme.HeroH), FittingMode = FittingModeType.ScaleToFill };
            hero.Add(heroBg);
            heroGradient = new View
            {
                Position = new Position(0, 0),
                Size = new Size(Theme.W, Theme.HeroH),
                BackgroundColor = Color.Transparent,
            };
            hero.Add(heroGradient);
            // Dark gradient overlay
            var gradTop = Theme.Box(0, 0, Theme.W, 120, new Color(0.078f, 0.078f, 0.078f, 0.6f));
            hero.Add(gradTop);
            var gradBot = Theme.Box(0, Theme.HeroH - 200, Theme.W, 200, new Color(0.078f, 0.078f, 0.078f, 0.95f));
            hero.Add(gradBot);
            var gradMid = Theme.Box(0, Theme.HeroH - 300, Theme.W, 100, new Color(0.078f, 0.078f, 0.078f, 0.4f));
            hero.Add(gradMid);

            heroTitle = Theme.Label("", 48, Theme.Text, Theme.Margin, Theme.HeroH - 190, Theme.W * 0.5f, 64);
            heroTitle.FontStyle = new PropertyMap().Add("weight", new PropertyValue("bold"));
            hero.Add(heroTitle);
            heroMeta = Theme.Label("", 18, Theme.Muted, Theme.Margin, Theme.HeroH - 128, Theme.W * 0.5f, 28);
            hero.Add(heroMeta);
            heroOverview = Theme.Label("", 20, new Color(0.88f, 0.88f, 0.88f, 1f), Theme.Margin, Theme.HeroH - 96, Theme.W * 0.55f, 80, true);
            hero.Add(heroOverview);
            Add(hero);

            // Search bar overlay on hero top-right
            searchBox = Theme.RoundBox(Theme.W - Theme.Margin - 460, 36, 460, 52, Theme.Panel, 8);
            search = new TextField
            {
                PlaceholderText = "Search movies & shows…",
                PixelSize = 22,
                TextColor = Theme.Text,
                Position = new Position(18, 0),
                Size = new Size(420, 52),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = true,
            };
            search.KeyEvent += (s, e) =>
            {
                if (!editing || e.Key.State != Key.StateType.Down) return false;
                var k = e.Key.KeyPressedName;
                return (k == "Return" || k == "KP_Enter" || k == "Select" || k == "Up" || k == "Down" || k == "XF86Back" || k == "Escape")
                       && OnKey(k);
            };
            searchBox.Add(search);
            Add(searchBox);

            var acct = app.Accounts?.Active;
            if (acct != null)
            {
                var acctColor = new Color(acct.ColorR, acct.ColorG, acct.ColorB, 1f);
                var acctBadge = new View
                {
                    Position = new Position(Theme.W - Theme.Margin - 460 - 180, 40),
                    Size = new Size(160, 44),
                    BackgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.8f),
                };
                var acctDot = Theme.Box(10, 10, 24, 24, acctColor);
                acctBadge.Add(acctDot);
                var acctName = Theme.Label(acct.Name, 22, Theme.Text, 42, 0, 110, 44);
                acctBadge.Add(acctName);
                Add(acctBadge);
            }

            status = Theme.Label("", 24, Theme.Muted, Theme.Margin, Theme.HeroH + 20, Theme.W - 2 * Theme.Margin, 200, true);
            rowsView.Position = new Position(0, Theme.HeroH);
            Add(rowsView);
            Add(status);
            app.SettingsChanged += () => Reload();
            Reload();
        }

        public override void OnShown() => Refresh();

        public override void OnClosed() => loadCts?.Cancel();

        void Reload()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            loadCts?.Cancel();
            loadCts = new CancellationTokenSource();
            App.Log($"RELOAD start cancel={sw.ElapsedMilliseconds}ms");
            _ = LoadAsync(loadCts.Token);
        }

        async Task LoadAsync(CancellationToken ct)
        {
            var loadSw = System.Diagnostics.Stopwatch.StartNew();
            ClearRows();
            var tClear = loadSw.ElapsedMilliseconds;
            var indexers = App.Settings.Indexers;
            var hasEztv = !string.IsNullOrEmpty(App.Settings.EztvUrl);
            var hasYts = !string.IsNullOrEmpty(App.Settings.YtsUrl);
            var hasTmdb = App.Tmdb.Enabled;
            App.Log($"HOME LoadAsync clear={tClear}ms hasTmdb={hasTmdb} hasEztv={hasEztv} hasYts={hasYts} indexers={indexers.Count}");

            if (!hasTmdb && indexers.Count == 0 && !hasEztv && !hasYts)
            {
                var urls = App.Config.Urls().ToList();
                status.Text = "No indexer configured yet.\nOn your phone, open " +
                              (urls.Count > 0 ? string.Join("  or  ", urls) : "http://<this TV's IP>:8099/") +
                              "\nand add your Prowlarr, EZTV, or indexer URL.";
                focusRow = -1;
                Refresh();
                return;
            }
            status.Text = "Loading…";
            var errors = new List<string>();
            var searching = lastQuery.Length > 0;

            var allHistory = App.Cache.GetHistory();
            watchProgressMap = new Dictionary<string, int>();
            foreach (var h in allHistory)
            {
                if (!string.IsNullOrEmpty(h.TitleKey) && h.DurationMs > 0 && h.PositionMs > 0)
                {
                    var pct = (int)(100.0 * h.PositionMs / h.DurationMs);
                    if (!watchProgressMap.ContainsKey(h.TitleKey))
                        watchProgressMap[h.TitleKey] = pct;
                }
            }

            if (!searching)
            {
                var history = allHistory;
                var resumable = history.Where(h => h.DurationMs > 0 && h.PositionMs > 0 &&
                    (double)h.PositionMs / h.DurationMs < 0.92).ToList();
                if (resumable.Count > 0)
                {
                    var records = resumable.Take(20).ToList();
                    var titles = records.Select(h =>
                    {
                        var label = h.TitleName;
                        if (h.Kind == TitleKind.Show && h.Season > 0 && h.Episode > 0)
                            label += $" · S{h.Season:00}E{h.Episode:00}";
                        var pct = h.DurationMs > 0 ? (int)(100.0 * h.PositionMs / h.DurationMs) : 0;
                        return new Title
                        {
                            Kind = h.Kind, Name = label, Key = h.TitleKey,
                            PosterUrl = h.PosterUrl, BackdropUrl = h.BackdropUrl,
                            ImdbId = h.ImdbId, TmdbId = h.TmdbId,
                            Overview = $"{pct}% watched · {FormatTime(h.PositionMs)}/{FormatTime(h.DurationMs)}",
                        };
                    }).ToList();
                    AddRow("Continue Watching", titles, isContinueWatching: true, playRecords: records);
                }

                var pinned = App.Cache.GetPinned();
                if (pinned.Count > 0)
                {
                    var pinnedTitles = pinned.Select(p => new Title
                    {
                        Kind = p.Kind, Name = p.Name, Key = p.Key, Year = p.Year,
                        PosterUrl = p.PosterUrl, BackdropUrl = p.BackdropUrl,
                        ImdbId = p.ImdbId, TmdbId = p.TmdbId,
                        Genres = p.Genres, Rating = p.Rating,
                    }).ToList();
                    AddRow("My List", pinnedTitles);
                }
            }

            var acctSuffix = "_" + (App.Accounts?.Active?.Id ?? "default");
            if (hasTmdb)
            {
                try
                {
                    if (searching)
                    {
                        App.Log($"HOME: TMDB searching '{lastQuery}'...");
                        var results = await App.Tmdb.SearchCatalogAsync(lastQuery, ct);
                        App.Log($"HOME: TMDB search returned {results.Count} results");
                        if (ct.IsCancellationRequested) return;
                        var movies = results.Where(t => t.Kind == TitleKind.Movie).Take(20).ToList();
                        var shows = results.Where(t => t.Kind == TitleKind.Show).Take(20).ToList();
                        if (movies.Count > 0) AddRow("Movies", movies);
                        if (shows.Count > 0) AddRow("Series", shows);
                    }
                    else
                    {
                        var catDefs = new (string name, string cacheKey,
                            string media, string genre, string lang, string extra, int defSort)[]
                        {
                            ("Trending Movies", "trending_movie", "movie", null, null, null, 0),
                            ("Trending Series", "trending_tv", "tv", null, null, null, 0),
                            ("Now Playing", "now_playing", "movie", null, null, null, 0),
                            ("Upcoming Movies", "upcoming", "movie", null, null, null, 0),
                            ("Animated Movies", "discover_movie_16", "movie", "16", null, null, 0),
                            ("Animated Series", "discover_tv_16", "tv", "16", null, null, 0),
                            ("Anime", "discover_tv_16_ja", "tv", "16", "ja", null, 0),
                            ("K-Drama", "discover_tv__ko", "tv", null, "ko", null, 0),
                            ("Critically Acclaimed", "acclaimed", "movie", null, null, "vote_average.gte=8&vote_count.gte=1000", 1),
                            ("Hidden Gems", "hidden_gems", "movie", null, null, "vote_average.gte=7&vote_count.gte=50&vote_count.lte=500", 1),
                            ("Top Rated Series", "top_rated_tv", "tv", null, null, null, 1),
                            ("Action", "discover_movie_28", "movie", "28", null, null, 0),
                            ("Comedy", "discover_movie_35", "movie", "35", null, null, 0),
                            ("Sci-Fi & Fantasy", "discover_tv_10765", "tv", "10765", null, null, 0),
                            ("Horror", "discover_movie_27", "movie", "27", null, null, 0),
                            ("Documentary", "discover_movie_99", "movie", "99", null, null, 0),
                            ("Romance", "discover_movie_10749", "movie", "10749", null, null, 0),
                            ("Mystery", "discover_tv_9648", "tv", "9648", null, null, 0),
                            ("Thriller", "discover_movie_53", "movie", "53", null, null, 0),
                            ("Netflix Exclusives", "provider_netflix", "tv", null, null, "with_watch_providers=8&watch_region=US", 0),
                            ("Disney+ Exclusives", "provider_disney", "tv", null, null, "with_watch_providers=337&watch_region=US", 0),
                            ("Apple TV+ Originals", "provider_apple", "tv", null, null, "with_watch_providers=350&watch_region=US", 0),
                            ("Prime Video", "provider_prime", "tv", null, null, "with_watch_providers=9&watch_region=US", 0),
                            ("HBO Originals", "provider_hbo", "tv", null, null, "with_watch_providers=384&watch_region=US", 0),
                        };

                        var readSw = System.Diagnostics.Stopwatch.StartNew();
                        var preRead = await Task.Run(() =>
                        {
                            var list = new List<(string name, List<Title> titles,
                                string media, string genre, string lang, string extra, int defSort, bool stale, string ck)>();
                            foreach (var (n, ck, m, g, l, ex, ds) in catDefs)
                            {
                                var fullKey = ck + acctSuffix;
                                var c = App.Cache.GetCatalog(fullKey, default, out bool st);
                                if (c != null) list.Add((n, c.Take(20).ToList(), m, g, l, ex, ds, st, fullKey));
                            }
                            return list;
                        });
                        var tPreRead = readSw.ElapsedMilliseconds;
                        if (ct.IsCancellationRequested) return;

                        bool anyShown = preRead.Count > 0;
                        bool anyStale = preRead.Any(r => r.stale);

                        readSw.Restart();
                        foreach (var r in preRead)
                            AddRow(r.name, r.titles, r.media, r.genre, r.lang, r.extra, r.defSort);
                        var tAddRows = readSw.ElapsedMilliseconds;
                        App.Log($"HOME: preRead={tPreRead}ms addRows={tAddRows}ms count={preRead.Count}");

                        if (anyShown)
                        {
                            App.Log($"HOME: showed cached catalog (stale={anyStale}), refreshing in background");
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var tasks = BuildCatalogTasks(ct);
                                    await Task.WhenAll(tasks.Select(t => t.task)).ConfigureAwait(false);
                                    var allTitles = new List<Title>();
                                    foreach (var (cacheKey2, task) in tasks)
                                    {
                                        if (task.IsCompleted && !task.IsFaulted && task.Result.Count > 0)
                                        {
                                            var take = task.Result.Take(16).ToList();
                                            allTitles.AddRange(take);
                                        }
                                    }
                                    await App.Tmdb.FetchCertsAsync(allTitles, ct).ConfigureAwait(false);
                                    foreach (var (cacheKey2, task) in tasks)
                                    {
                                        if (task.IsCompleted && !task.IsFaulted && task.Result.Count > 0)
                                            App.Cache.PutCatalog(cacheKey2 + acctSuffix, task.Result.Take(16).ToList());
                                    }
                                }
                                catch { }
                            });
                            _ = LoadRecommendationsAsync(ct);
                            _ = LoadTopicShelvesAsync(ct);
                            return;
                        }

                        // No cache — load recommendations first, then fetch all
                        await LoadRecommendationsAsync(ct);
                        App.Log("HOME: no cached catalog, fetching all...");
                        var fetchTasks = BuildCatalogTasks(ct);
                        var categories = fetchTasks.Select(t => (t.cacheKey, t.task)).ToArray();
                        await Task.WhenAll(categories.Select(c => c.task));
                        if (ct.IsCancellationRequested) return;

                        var allFresh = new List<Title>();
                        foreach (var (ck, tsk) in categories)
                        {
                            if (tsk.IsCompleted && !tsk.IsFaulted && tsk.Result.Count > 0)
                                allFresh.AddRange(tsk.Result.Take(16));
                        }
                        await App.Tmdb.FetchCertsAsync(allFresh, ct);
                        if (ct.IsCancellationRequested) return;

                        foreach (var (name, cacheKey, media, genre, lang, extra, defSort) in catDefs)
                        {
                            var match = categories.FirstOrDefault(c => c.cacheKey == cacheKey);
                            if (match.task != null && match.task.IsCompleted && !match.task.IsFaulted)
                            {
                                var result = match.task.Result;
                                if (result.Count > 0)
                                {
                                    AddRow(name, result.Take(16).ToList(), media, genre, lang, extra, defSort);
                                    App.Cache.PutCatalog(cacheKey + acctSuffix, result.Take(16).ToList());
                                }
                            }
                        }

                        _ = LoadTopicShelvesAsync(ct);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { errors.Add("TMDB: " + e.Message); App.Log("HOME TMDB ERROR: " + e); }
            }

            if (!hasTmdb)
            {
                var searchLower = lastQuery.ToLowerInvariant();

                if (hasEztv)
                {
                    try
                    {
                        var eztvReleases = await App.EztvCache.GetAsync(App.Settings.EztvUrl, ct);
                        if (ct.IsCancellationRequested) return;
                        var allShows = Catalog.Build(eztvReleases).Where(t => t.Kind == TitleKind.Show).ToList();
                        if (searching)
                        {
                            var matches = allShows.Where(t => t.Name.ToLowerInvariant().Contains(searchLower))
                                .OrderByDescending(t => t.BestSeeders).Take(30).ToList();
                            if (matches.Count > 0) AddRow("Series: " + lastQuery, matches);
                        }
                        else
                        {
                            var popular = allShows.OrderByDescending(t => t.BestSeeders).Take(30).ToList();
                            if (popular.Count > 0) AddRow("Popular Series", popular);
                            var recent = allShows.OrderByDescending(t => t.Latest).Take(30).ToList();
                            if (recent.Count > 0) AddRow("Recently Added", recent);
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception e) { errors.Add("EZTV: " + e.Message); App.Log("HOME EZTV ERROR: " + e); }
                }

                if (hasYts)
                {
                    try
                    {
                        if (searching)
                        {
                            var searchResults = await App.Yts.SearchAsync(App.Settings.YtsUrl, lastQuery, 50, ct);
                            if (ct.IsCancellationRequested) return;
                            var searchTitles = Catalog.Build(searchResults)
                                .Where(t => t.Kind == TitleKind.Movie)
                                .OrderByDescending(t => t.BestSeeders).Take(30).ToList();
                            if (searchTitles.Count > 0) AddRow("Movies: " + lastQuery, searchTitles);
                        }
                        else
                        {
                            var ytsReleases = await App.YtsCache.GetAsync(App.Settings.YtsUrl, ct);
                            if (ct.IsCancellationRequested) return;
                            var allMovies = Catalog.Build(ytsReleases).Where(t => t.Kind == TitleKind.Movie).ToList();
                            var popular = allMovies.OrderByDescending(t => t.BestSeeders).Take(30).ToList();
                            if (popular.Count > 0) AddRow("Popular Movies", popular);
                            var recent = allMovies.OrderByDescending(t => t.Latest).Take(30).ToList();
                            if (recent.Count > 0) AddRow("New Movies", recent);
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception e) { errors.Add("YTS: " + e.Message); App.Log("HOME YTS ERROR: " + e); }
                }
            }

            if (indexers.Count > 0)
            {
                var queries = new List<(string name, string q, int cat)>();
                if (lastQuery.Length > 0) queries.Add(("Results for " + lastQuery, lastQuery, 0));
                queries.Add(("Latest movies", "", Cat.Movies));
                queries.Add(("Latest TV shows", "", Cat.Tv));

                foreach (var (name, q, cat) in queries)
                {
                    List<Release> results;
                    try
                    {
                        var r = await App.Torznab.SearchAllAsync(indexers, q, cat, ct);
                        results = r.results;
                        errors.AddRange(r.errors);
                    }
                    catch (OperationCanceledException) { return; }
                    if (ct.IsCancellationRequested) return;
                    var titles = Catalog.Build(results);
                    if (cat == Cat.Movies) titles = titles.Where(t => t.Kind == TitleKind.Movie).ToList();
                    if (cat == Cat.Tv) titles = titles.Where(t => t.Kind == TitleKind.Show).ToList();
                    if (titles.Count > 0) AddRow(name, titles.Take(40).ToList());
                }
            }

            status.Text = rows.Count == 0
                ? (searching ? $"No results for \"{lastQuery}\"." : "Nothing found.")
                  + (errors.Count > 0 ? "\n" + string.Join("\n", errors.Distinct().Take(4)) : "")
                : "";
            if (errors.Count > 0 && rows.Count > 0) App.Toast(string.Join("\n", errors.Distinct().Take(3)));
            if (focusRow >= rows.Count) focusRow = rows.Count - 1;
            if (focusRow < 0 && rows.Count > 0 && lastQuery.Length > 0) focusRow = 0;
            Refresh();
            _ = LoadPostersAsync(ct);
        }

        // ── Hero banner ──────────────────────────────────────────────────

        void UpdateHero()
        {
            if (heroTitles.Count == 0)
            {
                heroTitle.Text = "";
                heroMeta.Text = "";
                heroOverview.Text = "";
                return;
            }
            var t = heroTitles[heroIdx % heroTitles.Count];
            heroTitle.Text = t.Name;
            var parts = new List<string>();
            if (t.Year > 0) parts.Add(t.Year.ToString());
            parts.Add(t.Kind == TitleKind.Show ? "Series" : "Movie");
            if (t.Rating > 0) parts.Add($"★ {t.Rating:0.0}");
            if (!string.IsNullOrEmpty(t.Genres)) parts.Add(t.Genres);
            heroMeta.Text = string.Join("  ·  ", parts);
            heroOverview.Text = t.Overview ?? "";
            if (!string.IsNullOrEmpty(t.BackdropUrl))
            {
                _ = SetHeroBgAsync(t.BackdropUrl);
            }
            else if (!string.IsNullOrEmpty(t.PosterUrl))
            {
                _ = SetHeroBgAsync(t.PosterUrl);
            }
        }

        async Task SetHeroBgAsync(string url)
        {
            try
            {
                var path = await DownloadPosterAsync(url, loadCts?.Token ?? CancellationToken.None);
                if (path != null)
                    App.Post(() => heroBg.ResourceUrl = path);
            }
            catch { }
        }

        // ── Poster loading ───────────────────────────────────────────────

        async Task LoadPostersAsync(CancellationToken ct)
        {
            var visibleRows = rows.ToList();

            // Pass 1: set already-cached posters instantly (no network, no disk write)
            foreach (var row in visibleRows)
                for (int i = 0; i < row.Titles.Count; i++)
                {
                    var t = row.Titles[i];
                    if (string.IsNullOrEmpty(t.PosterUrl)) continue;
                    var cached = App.Cache.GetPoster(t.PosterUrl);
                    if (cached != null && i < row.Cards.Count)
                        SetPoster(row.Cards[i], cached);
                }

            // Set hero from first trending row with backdrops
            if (heroTitles.Count == 0)
            {
                foreach (var row in visibleRows)
                {
                    var withBg = row.Titles.Where(t => !string.IsNullOrEmpty(t.BackdropUrl)).Take(7).ToList();
                    if (withBg.Count >= 3)
                    {
                        heroTitles = withBg;
                        heroIdx = 0;
                        App.Post(UpdateHero);
                        break;
                    }
                }
            }

            // Pass 2: download missing posters in parallel batches of 6
            var missing = new List<(Row row, int idx, Title t)>();
            foreach (var row in visibleRows)
                for (int i = 0; i < row.Titles.Count; i++)
                {
                    var t = row.Titles[i];
                    if (!string.IsNullOrEmpty(t.PosterUrl) && App.Cache.GetPoster(t.PosterUrl) != null) continue;
                    missing.Add((row, i, t));
                }

            int failed = 0;
            const int batch = 12;
            for (int b = 0; b < missing.Count && !ct.IsCancellationRequested; b += batch)
            {
                var chunk = missing.Skip(b).Take(batch).ToList();
                var tasks = chunk.Select(async item =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(item.t.PosterUrl) && App.Tmdb.Enabled)
                            await App.Tmdb.EnrichAsync(item.t, ct);
                        if (ct.IsCancellationRequested || string.IsNullOrEmpty(item.t.PosterUrl)) return;
                        var localPath = await DownloadPosterAsync(item.t.PosterUrl, ct);
                        if (localPath != null && item.idx < item.row.Cards.Count)
                            App.Post(() => SetPoster(item.row.Cards[item.idx], localPath));
                    }
                    catch (Exception e)
                    {
                        if (Interlocked.Increment(ref failed) <= 3)
                            App.Log($"HOME: poster fail [{item.t.Name}]: {e.Message}");
                    }
                }).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            App.Cache.FlushIndex();
        }

        async Task<string> DownloadPosterAsync(string url, CancellationToken ct)
        {
            var existing = App.Cache.GetPoster(url);
            if (existing != null) return existing;
            try
            {
                var uri = new Uri(url);
                byte[] data;
                if (uri.Host.Contains("tmdb.org"))
                    data = await imgHttp.GetByteArrayAsync(url).ConfigureAwait(false);
                else
                    data = await DohResolver.FetchBytesAsync(url, ct).ConfigureAwait(false);
                if (data.Length < 100 || data.Length > 500_000) return null;
                return App.Cache.PutPoster(url, data);
            }
            catch { return null; }
        }

        // ── Row management ───────────────────────────────────────────────

        void RemoveCardFromRow(int rowIdx, int col)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var row = rows[rowIdx];
            if (col < 0 || col >= row.Cards.Count) return;
            StyleCard(row.Cards[col], false);
            row.Strip.Remove(row.Cards[col]);
            row.Cards[col].Dispose();
            row.Cards.RemoveAt(col);
            if (col < row.Titles.Count) row.Titles.RemoveAt(col);
            if (row.PlayRecords != null && col < row.PlayRecords.Count) row.PlayRecords.RemoveAt(col);
            var t0 = sw.ElapsedMilliseconds;
            if (row.Cards.Count == 0)
            {
                rowsView.Remove(row.View);
                row.View.Dispose();
                rows.RemoveAt(rowIdx);
                for (int i = rowIdx; i < rows.Count; i++)
                    rows[i].View.PositionY = i * Theme.RowH;
                if (focusRow >= rows.Count) focusRow = rows.Count - 1;
                if (focusRow < 0) { focusOnHero = true; focusRow = -1; }
            }
            else
            {
                if (row.Col >= row.Cards.Count) row.Col = row.Cards.Count - 1;
                for (int i = 0; i < row.Cards.Count; i++)
                    row.Cards[i].PositionX = i * (Theme.CardW + Theme.CardGap);
            }
            var t1 = sw.ElapsedMilliseconds;
            prevFocusRow = -1;
            prevFocusCol = -1;
            Refresh();
            App.Log($"REMOVE_CARD remove={t0}ms reflow={t1-t0}ms total={sw.ElapsedMilliseconds}ms");
        }

        void ClearRows()
        {
            foreach (var r in rows) { rowsView.Remove(r.View); r.View.Dispose(); }
            rows.Clear();
            prevFocusRow = -1;
            prevFocusCol = -1;
        }

        void AddRow(string name, List<Title> titles,
            string catMedia = null, string catGenre = null, string catLang = null,
            string catExtra = null, int catSort = 0,
            bool isContinueWatching = false, List<AppCache.PlayRecord> playRecords = null)
        {
            var row = new Row
            {
                Name = name, Titles = titles,
                CatMediaType = catMedia, CatGenreId = catGenre,
                CatLanguage = catLang, CatExtra = catExtra, CatDefaultSort = catSort,
                IsContinueWatching = isContinueWatching, PlayRecords = playRecords,
            };
            float rowY = rows.Count * Theme.RowH;
            row.View = new View { Position = new Position(0, rowY), Size = new Size(Theme.W, Theme.RowH) };
            row.View.Add(Theme.Label(name, 28, Theme.Text, Theme.Margin, 0, 1200, Theme.RowLabelH));
            row.Strip = new View { Position = new Position(Theme.Margin, Theme.RowLabelH + Theme.RowPadTop) };
            row.View.Add(row.Strip);
            for (int i = 0; i < titles.Count; i++)
            {
                int pct = -1;
                if (isContinueWatching && playRecords != null && i < playRecords.Count)
                {
                    var pr = playRecords[i];
                    if (pr.DurationMs > 0) pct = (int)(100.0 * pr.PositionMs / pr.DurationMs);
                }
                else
                {
                    var hist = watchProgressMap;
                    if (hist != null && hist.TryGetValue(titles[i].Key ?? "", out pct)) { }
                    else pct = -1;
                }
                var cert = titles[i].Certification ?? App.Tmdb.GetCachedCert(titles[i].TmdbId);
                var card = Theme.MakeCard(titles[i], Theme.CardW, Theme.CardH, Theme.CardRadius, pct, cert);
                card.Position = new Position(i * (Theme.CardW + Theme.CardGap), 0);
                row.Strip.Add(card);
                row.Cards.Add(card);
            }
            if (catMedia != null)
            {
                var more = MakeMoreCard();
                more.Position = new Position(titles.Count * (Theme.CardW + Theme.CardGap), 0);
                row.Strip.Add(more);
                row.Cards.Add(more);
            }
            rows.Add(row);
            rowsView.Add(row.View);
        }

        static View MakeMoreCard()
        {
            var card = new View { Size = new Size(Theme.CardW, Theme.CardH + 44) };
            var frame = Theme.RoundBox(0, 0, Theme.CardW, Theme.CardH, Theme.Panel, Theme.CardRadius);
            frame.Name = "frame";
            var label = Theme.Label("More ▸", 30, Theme.Accent, 0, Theme.CardH / 2 - 20, Theme.CardW, 40);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            frame.Add(label);
            card.Add(frame);
            card.Add(Theme.Label("", 20, Theme.Text, 0, Theme.CardH + 6, Theme.CardW, 28));
            return card;
        }

        static void SetPoster(View card, string url) =>
            Theme.SetCardPoster(card, url, Theme.CardW, Theme.CardH);

        // ── Focus & navigation ───────────────────────────────────────────

        void StyleCard(View card, bool focused)
        {
            card.Scale = focused ? new Vector3(1.08f, 1.08f, 1f) : Vector3.One;
            var ring = card.FindChildByName("ring");
            if (ring != null) { if (focused) ring.Show(); else ring.Hide(); }
            var tl = card.FindChildByName("title") as TextLabel;
            if (tl != null) tl.TextColor = focused ? Color.White : Theme.Text;
        }

        void UpdateRowStrip(Row row)
        {
            int perScreen = Math.Max(1, (int)((Theme.W - 2 * Theme.Margin) / (Theme.CardW + Theme.CardGap)));
            int page = row.Col / perScreen;
            int first = page * perScreen;
            row.Strip.PositionX = Theme.Margin - first * (Theme.CardW + Theme.CardGap);
        }

        void Refresh()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            searchBox.BackgroundColor = editing ? Theme.PanelHover : Theme.Panel;

            var heroActive = focusOnHero && !editing && focusRow == -1;
            hero.BackgroundColor = heroActive ? new Color(0.1f, 0.1f, 0.1f, 1f) : Theme.Bg;

            if (prevFocusRow >= 0 && prevFocusRow < rows.Count)
            {
                var prev = rows[prevFocusRow];
                if (prevFocusCol >= 0 && prevFocusCol < prev.Cards.Count)
                    StyleCard(prev.Cards[prevFocusCol], false);
                UpdateRowStrip(prev);
            }
            var t0 = sw.ElapsedMilliseconds;

            if (!focusOnHero && focusRow >= 0 && focusRow < rows.Count)
            {
                var cur = rows[focusRow];
                if (cur.Col >= 0 && cur.Col < cur.Cards.Count)
                    StyleCard(cur.Cards[cur.Col], true);
                UpdateRowStrip(cur);
            }
            var t1 = sw.ElapsedMilliseconds;

            prevFocusRow = focusRow;
            prevFocusCol = (focusRow >= 0 && focusRow < rows.Count) ? rows[focusRow].Col : -1;
            App.Log($"REFRESH prev={t0}ms cur={t1-t0}ms rows={rows.Count} focusRow={focusRow} total={sw.ElapsedMilliseconds}ms");
            float scrollY;
            if (focusOnHero || focusRow <= 0)
                scrollY = 0;
            else
            {
                int adj = focusRow - 1;
                int vPage = adj / 2;
                float vPad = (Theme.H - 2 * Theme.RowH) / 2f;
                scrollY = Theme.HeroH + (1 + vPage * 2) * Theme.RowH - vPad;
            }
            hero.PositionY = -scrollY;
            rowsView.PositionY = Theme.HeroH - scrollY;
        }

        public override bool OnKey(string key)
        {
            if (editing)
            {
                switch (key)
                {
                    case "Return": case "KP_Enter": case "Select":
                        StopEditing();
                        lastQuery = search.Text.Trim();
                        Reload();
                        return true;
                    case "XF86Back": case "Escape": case "Up": case "Down":
                        StopEditing();
                        return true;
                }
                return false;
            }

            var keySw = System.Diagnostics.Stopwatch.StartNew();
            switch (key)
            {
                case "Up":
                    if (focusOnHero)
                    {
                        focusOnHero = false;
                        focusRow = -1;
                        StartEditing();
                        return true;
                    }
                    if (focusRow <= 0)
                    {
                        focusOnHero = true;
                        focusRow = -1;
                    }
                    else
                        focusRow--;
                    break;
                case "Down":
                    if (focusOnHero)
                    {
                        focusOnHero = false;
                        focusRow = 0;
                    }
                    else if (focusRow < rows.Count - 1)
                        focusRow++;
                    break;
                case "Left":
                    if (focusOnHero)
                    {
                        if (heroTitles.Count > 0)
                        {
                            heroIdx = (heroIdx - 1 + heroTitles.Count) % heroTitles.Count;
                            UpdateHero();
                        }
                        return true;
                    }
                    if (focusRow >= 0 && rows[focusRow].Col > 0)
                        rows[focusRow].Col--;
                    break;
                case "Right":
                    if (focusOnHero)
                    {
                        if (heroTitles.Count > 0)
                        {
                            heroIdx = (heroIdx + 1) % heroTitles.Count;
                            UpdateHero();
                        }
                        return true;
                    }
                    if (focusRow >= 0 && rows[focusRow].Col < rows[focusRow].Cards.Count - 1)
                        rows[focusRow].Col++;
                    break;
                case "Return": case "KP_Enter":
                    if (focusOnHero)
                    {
                        if (heroTitles.Count > 0)
                        {
                            var ht = heroTitles[heroIdx % heroTitles.Count];
                            App.Push(new DetailScreen(App, ht));
                        }
                        return true;
                    }
                    if (focusRow == -1) StartEditing();
                    else
                    {
                        var row = rows[focusRow];
                        if (row.CatMediaType != null && row.Col == row.Cards.Count - 1)
                            App.Push(new CategoryScreen(App, row.Name, row.CatMediaType,
                                row.CatGenreId, row.CatLanguage, row.CatExtra, row.CatDefaultSort));
                        else if (row.IsContinueWatching && row.PlayRecords != null && row.Col < row.PlayRecords.Count)
                        {
                            var rec = row.PlayRecords[row.Col];
                            var t = new Title
                            {
                                Key = rec.TitleKey, Name = rec.TitleName, Kind = rec.Kind,
                                PosterUrl = rec.PosterUrl, BackdropUrl = rec.BackdropUrl,
                                ImdbId = rec.ImdbId, TmdbId = rec.TmdbId,
                            };
                            if (!string.IsNullOrEmpty(rec.MagnetLink))
                            {
                                var rel = new Release { Title = rec.ReleaseName ?? rec.TitleName, Link = rec.MagnetLink };
                                rel.Info = ReleaseInfo.Parse(rel.Title);
                                t.Releases.Add(rel);
                            }
                            App.Push(new DetailScreen(App, t, rec.Season, rec.Episode, rec.PositionMs));
                        }
                        else if (row.Col < row.Titles.Count)
                            App.Push(new DetailScreen(App, row.Titles[row.Col]));
                    }
                    return true;
                case "XF86Red":
                    if (focusRow >= 0 && focusRow < rows.Count)
                    {
                        var rr = rows[focusRow];
                        if (rr.IsContinueWatching && rr.PlayRecords != null && rr.Col < rr.PlayRecords.Count)
                        {
                            var rec = rr.PlayRecords[rr.Col];
                            App.Cache.RemoveFromHistory(rec.TitleKey, rec.Season, rec.Episode);
                            App.Toast("Removed from Continue Watching");
                            RemoveCardFromRow(focusRow, rr.Col);
                        }
                        else if (rr.Col < rr.Titles.Count)
                        {
                            var t = rr.Titles[rr.Col];
                            if (!string.IsNullOrEmpty(t.Key))
                            {
                                App.Cache.ToggleDislike(t.Key);
                                bool disliked = App.Cache.IsDisliked(t.Key);
                                App.Toast(disliked ? "Disliked" : "Dislike removed");
                            }
                        }
                    }
                    return true;
                case "XF86Green":
                    if (focusRow >= 0 && focusRow < rows.Count)
                    {
                        var lr = rows[focusRow];
                        if (lr.Col < lr.Titles.Count)
                        {
                            var t = lr.Titles[lr.Col];
                            if (!string.IsNullOrEmpty(t.Key))
                            {
                                App.Cache.ToggleLike(t.Key);
                                bool liked = App.Cache.IsLiked(t.Key);
                                App.Toast(liked ? "Liked" : "Like removed");
                            }
                        }
                    }
                    return true;
                case "XF86Yellow":
                    if (focusRow >= 0 && focusRow < rows.Count)
                    {
                        var pr = rows[focusRow];
                        if (pr.Col < pr.Titles.Count)
                        {
                            var t = pr.Titles[pr.Col];
                            if (!string.IsNullOrEmpty(t.Key))
                            {
                                bool wasPinned = App.Cache.IsPinned(t.Key);
                                App.Cache.TogglePin(t);
                                App.Toast(wasPinned ? "Removed from My List" : "Added to My List");
                                if (wasPinned && pr.Name == "My List")
                                    RemoveCardFromRow(focusRow, pr.Col);
                            }
                        }
                    }
                    return true;
                case "XF86Blue":
                    App.SwitchAccount();
                    return true;
                case "XF86Back": case "Escape":
                    if (lastQuery.Length > 0) { lastQuery = ""; search.Text = ""; Reload(); return true; }
                    return false;
                default:
                    return false;
            }
            Refresh();
            App.Log($"KEY {key} total={keySw.ElapsedMilliseconds}ms");
            return true;
        }

        void StartEditing()
        {
            editing = true;
            focusOnHero = false;
            FocusManager.Instance.SetCurrentFocusView(search);
            Refresh();
        }

        void StopEditing()
        {
            editing = false;
            FocusManager.Instance.ClearFocus();
            Refresh();
        }

        // ── Catalog tasks ────────────────────────────────────────────────

        (string cacheKey, Task<List<Title>> task)[] BuildCatalogTasks(CancellationToken ct)
        {
            return new (string, Task<List<Title>>)[]
            {
                ("trending_movie", App.Tmdb.TrendingAsync("movie", 1, ct)),
                ("trending_tv", App.Tmdb.TrendingAsync("tv", 1, ct)),
                ("now_playing", App.Tmdb.ListAsync("/movie/now_playing?language=en-US", false, ct)),
                ("upcoming", App.Tmdb.ListAsync("/movie/upcoming?language=en-US", false, ct)),
                ("discover_movie_16", App.Tmdb.DiscoverAsync("movie", "16", null, ct)),
                ("discover_tv_16", App.Tmdb.DiscoverAsync("tv", "16", null, ct)),
                ("discover_tv_16_ja", App.Tmdb.DiscoverAsync("tv", "16", "ja", ct)),
                ("discover_tv__ko", App.Tmdb.DiscoverAsync("tv", null, "ko", ct)),
                ("acclaimed", App.Tmdb.ListAsync("/discover/movie?sort_by=vote_average.desc&vote_count.gte=1000&vote_average.gte=8", false, ct)),
                ("hidden_gems", App.Tmdb.ListAsync("/discover/movie?sort_by=vote_average.desc&vote_count.gte=50&vote_count.lte=500&vote_average.gte=7", false, ct)),
                ("top_rated_tv", App.Tmdb.ListAsync("/tv/top_rated", true, ct)),
                ("discover_movie_28", App.Tmdb.DiscoverAsync("movie", "28", null, ct)),
                ("discover_movie_35", App.Tmdb.DiscoverAsync("movie", "35", null, ct)),
                ("discover_tv_10765", App.Tmdb.DiscoverAsync("tv", "10765", null, ct)),
                ("discover_movie_27", App.Tmdb.DiscoverAsync("movie", "27", null, ct)),
                ("discover_movie_99", App.Tmdb.DiscoverAsync("movie", "99", null, ct)),
                ("discover_movie_10749", App.Tmdb.DiscoverAsync("movie", "10749", null, ct)),
                ("discover_tv_9648", App.Tmdb.DiscoverAsync("tv", "9648", null, ct)),
                ("discover_movie_53", App.Tmdb.DiscoverAsync("movie", "53", null, ct)),
                ("provider_netflix", App.Tmdb.ListAsync("/discover/tv?sort_by=popularity.desc&with_watch_providers=8&watch_region=US", true, ct)),
                ("provider_disney", App.Tmdb.ListAsync("/discover/tv?sort_by=popularity.desc&with_watch_providers=337&watch_region=US", true, ct)),
                ("provider_apple", App.Tmdb.ListAsync("/discover/tv?sort_by=popularity.desc&with_watch_providers=350&watch_region=US", true, ct)),
                ("provider_prime", App.Tmdb.ListAsync("/discover/tv?sort_by=popularity.desc&with_watch_providers=9&watch_region=US", true, ct)),
                ("provider_hbo", App.Tmdb.ListAsync("/discover/tv?sort_by=popularity.desc&with_watch_providers=384&watch_region=US", true, ct)),
            };
        }

        async Task LoadRecommendationsAsync(CancellationToken ct)
        {
            if (!App.Tmdb.Enabled) return;
            var dislikes = App.Cache.GetDislikes();

            var history = App.Cache.GetHistory();
            var historySeeds = history
                .Where(h => h.TmdbId > 0 && h.DurationMs > 0 && (double)h.PositionMs / h.DurationMs > 0.25)
                .GroupBy(h => h.TmdbId)
                .Select(g => g.OrderByDescending(h => h.PlayedUtc).First())
                .OrderByDescending(h => h.PlayedUtc)
                .Take(5)
                .ToList();

            var likedKeys = App.Cache.GetLikes();
            var pinned = App.Cache.GetPinned();
            var likedSeeds = pinned
                .Where(p => p.TmdbId > 0 && likedKeys.Contains(p.Key))
                .Take(5)
                .Select(p => new AppCache.PlayRecord
                {
                    TmdbId = p.TmdbId, Kind = p.Kind, TitleName = p.Name, TitleKey = p.Key,
                    PlayedUtc = p.PinnedUtc,
                })
                .ToList();

            var allSeeds = new List<AppCache.PlayRecord>();
            var seenTmdb = new HashSet<int>();
            foreach (var s in historySeeds)
                if (seenTmdb.Add(s.TmdbId)) allSeeds.Add(s);
            foreach (var s in likedSeeds)
                if (seenTmdb.Add(s.TmdbId)) allSeeds.Add(s);

            if (allSeeds.Count == 0) return;

            var recSuffix = "_" + (App.Accounts?.Active?.Id ?? "default");
            var tasks = allSeeds.Select(s =>
            {
                var tv = s.Kind == TitleKind.Show;
                var cacheKey = $"rec_{(tv ? "tv" : "movie")}_{s.TmdbId}{recSuffix}";
                var cached = App.Cache.GetCatalog(cacheKey);
                if (cached != null)
                    return (seed: s, cacheKey, task: Task.FromResult(cached));
                return (seed: s, cacheKey, task: App.Tmdb.RecommendationsAsync(s.TmdbId, tv, ct));
            }).ToList();

            try
            {
                await Task.WhenAll(tasks.Select(t => t.task)).ConfigureAwait(false);
            }
            catch { }
            if (ct.IsCancellationRequested) return;

            var seen = new HashSet<string>();
            foreach (var (seed, cacheKey, task) in tasks)
            {
                if (!task.IsCompleted || task.IsFaulted) continue;
                var recs = task.Result;
                if (recs == null || recs.Count == 0) continue;
                var filtered = recs.Where(r => !string.IsNullOrEmpty(r.PosterUrl)
                    && !dislikes.Contains(r.Key ?? "") && seen.Add(r.Key)).Take(10).ToList();
                if (filtered.Count < 3) continue;

                var seedName = seed.TitleName;
                if (seedName.Length > 30) seedName = seedName.Substring(0, 27) + "…";
                App.Post(() => { AddRow($"Because you watched {seedName}", filtered); Refresh(); _ = LoadPostersAsync(ct); });
                App.Cache.PutCatalog(cacheKey, filtered);
            }
        }

        async Task LoadTopicShelvesAsync(CancellationToken ct)
        {
            if (!App.Tmdb.Enabled || ct.IsCancellationRequested) return;

            var topics = new (string name, string cacheKey, string media, string genres, string keyword, string extra)[]
            {
                ("Romantic Comedies", "topic_romcom", "movie", "35,10749", null, null),
                ("Heist Movies", "topic_heists", "movie", null, "heist", null),
                ("Time Travel", "topic_timetravel", "movie", null, "time travel", null),
                ("Animated Adventures", "topic_animated", "movie", "16,12", null, null),
                ("Historical Dramas", "topic_historical", "movie", "18,36", null, null),
                ("Under 100 Minutes", "topic_short", "movie", null, null, "with_runtime.gte=1&with_runtime.lte=99"),
            };

            var topicSuffix = "_" + (App.Accounts?.Active?.Id ?? "default");
            foreach (var (name, cacheKey, media, genres, keyword, extra) in topics)
            {
                if (ct.IsCancellationRequested) return;
                var fullKey = cacheKey + topicSuffix;
                var cached = App.Cache.GetCatalog(fullKey);
                if (cached != null && cached.Count > 0)
                {
                    var take = cached.Take(10).ToList();
                    App.Post(() => { AddRow(name, take); Refresh(); });
                    continue;
                }

                try
                {
                    string keywords = null;
                    if (keyword != null)
                    {
                        var kwId = await App.Tmdb.FindKeywordIdAsync(keyword, ct).ConfigureAwait(false);
                        if (kwId <= 0) continue;
                        keywords = kwId.ToString();
                    }
                    var results = await App.Tmdb.TopicDiscoverAsync(media, genres, keywords, extra, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return;
                    if (results.Count > 0)
                    {
                        var take = results.Take(10).ToList();
                        App.Cache.PutCatalog(fullKey, take);
                        App.Post(() => { AddRow(name, take); Refresh(); _ = LoadPostersAsync(ct); });
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { }
            }
        }

        static string FormatTime(long ms)
        {
            var ts = TimeSpan.FromMilliseconds(ms);
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                                      : $"{ts.Minutes}:{ts.Seconds:00}";
        }
    }
}
