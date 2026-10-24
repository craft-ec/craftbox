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
    class DetailScreen : Screen
    {
        readonly Title title;
        readonly FocusList seasons, episodes, releases;
        readonly List<FocusList> columns = new List<FocusList>();
        int column;
        List<int> seasonNums = new List<int>();
        List<int> episodeNums = new List<int>();
        List<Release> shown = new List<Release>();
        SortedDictionary<int, List<Release>> episodeMap;
        TextLabel loadingLabel;
        CancellationTokenSource fetchCts;

        bool tmdbMode;
        Dictionary<int, List<TmdbEpisode>> tmdbEpisodeCache = new Dictionary<int, List<TmdbEpisode>>();
        readonly Dictionary<string, List<Release>> torrentCache = new Dictionary<string, List<Release>>();
        CancellationTokenSource searchCts;
        Task eztvPreFetch;
        bool showingAutoPlay;
        Release autoSelected;
        TextLabel metaLabel;
        List<StreamResult> streamResults;
        bool autoIsStream;
        static readonly Dictionary<string, List<StreamResult>> streamCache = new Dictionary<string, List<StreamResult>>();
        static readonly HttpClient imgHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        ImageView detailBg;
        View favBtn, likeBtn, dislikeBtn;
        readonly int resumeSeason, resumeEpisode;
        readonly long resumePositionMs;
        bool resumePending;

        public DetailScreen(App app, Title t, int resumeSeason = -1, int resumeEpisode = -1, long resumePositionMs = 0) : base(app)
        {
            title = t;
            this.resumeSeason = resumeSeason;
            this.resumeEpisode = resumeEpisode;
            this.resumePositionMs = resumePositionMs;
            this.resumePending = resumePositionMs > 0;

            // ── Netflix-style backdrop with gradient overlays ────────────
            detailBg = new ImageView
            {
                Position = new Position(0, 0),
                Size = new Size(Theme.W, Theme.H),
                FittingMode = FittingModeType.ScaleToFill,
                BackgroundColor = Theme.Surface,
            };
            Add(detailBg);
            // Dark overlay
            Add(Theme.Box(0, 0, Theme.W, Theme.H, Theme.Overlay));
            // Bottom gradient (heavier darkness toward the bottom where lists sit)
            var bottomGrad = new View
            {
                Position = new Position(0, Theme.H / 2),
                Size = new Size(Theme.W, Theme.H / 2),
                BackgroundColor = new Color(0, 0, 0, 0.7f),
            };
            Add(bottomGrad);
            // Left-side gradient for text readability
            var leftGrad = new View
            {
                Position = new Position(0, 0),
                Size = new Size(Theme.W / 2, Theme.H),
                BackgroundColor = new Color(0, 0, 0, 0.4f),
            };
            Add(leftGrad);

            // ── Title / meta / overview ──────────────────────────────────
            var head = t.Name + (t.Year > 0 ? $" ({t.Year})" : "");
            Add(Theme.Label(head, 64, Theme.Text, Theme.Margin, 36, Theme.W - 2 * Theme.Margin, 86));
            metaLabel = Theme.Label("", 28, Theme.Accent, Theme.Margin, 126, Theme.W - 2 * Theme.Margin, 40);
            Add(metaLabel);
            UpdateMeta();
            if (!string.IsNullOrEmpty(t.Overview))
                Add(Theme.Label(t.Overview, 26, Theme.Muted, Theme.Margin, 172, (Theme.W - 2 * Theme.Margin) * 0.6f, 120, true));

            // ── Action buttons (Yellow=My List, Green=Like, Red=Dislike) ─
            float btnY = 298, btnH = 36, btnGap = 16;
            float btnX = Theme.Margin;

            favBtn = MakeActionButton(ref btnX, btnY, btnH, btnGap, "♥ My List", app.Cache.IsPinned(t.Key));
            likeBtn = MakeActionButton(ref btnX, btnY, btnH, btnGap, "▲ Like", app.Cache.IsLiked(t.Key));
            dislikeBtn = MakeActionButton(ref btnX, btnY, btnH, btnGap, "▼ Dislike", app.Cache.IsDisliked(t.Key));

            var btnHint = Theme.Label("Yellow=My List  Green=Like  Red=Dislike", 16, Theme.Muted,
                btnX + 12, btnY + 4, 500, 28);
            Add(btnHint);

            // ── Continue watching indicator ──────────────────────────────
            var history = app.Cache.GetHistory();
            var lastWatch = history.Where(h => h.TitleKey == t.Key && h.DurationMs > 0 && h.PositionMs > 0)
                .OrderByDescending(h => h.PlayedUtc).FirstOrDefault();
            if (lastWatch != null)
            {
                var pct = (int)(100.0 * lastWatch.PositionMs / lastWatch.DurationMs);
                var pos = FormatMs(lastWatch.PositionMs);
                var dur = FormatMs(lastWatch.DurationMs);
                string resumeText;
                if (lastWatch.Kind == TitleKind.Show && lastWatch.Season > 0 && lastWatch.Episode > 0)
                    resumeText = $"▶ Continue S{lastWatch.Season:00}E{lastWatch.Episode:00} at {pos}/{dur} ({pct}%)";
                else
                    resumeText = $"▶ Continue at {pos}/{dur} ({pct}%)";

                var resumeBar = new View
                {
                    Position = new Position(Theme.Margin, btnY + btnH + 8),
                    Size = new Size(500, 32),
                    BackgroundColor = new Color(0.15f, 0.15f, 0.15f, 0.9f),
                };
                var progressFill = Theme.Box(0, 28, 500 * pct / 100f, 4, Theme.Accent);
                resumeBar.Add(progressFill);
                var resumeLbl = Theme.Label(resumeText, 18, Theme.Accent, 10, 0, 480, 28);
                resumeBar.Add(resumeLbl);
                Add(resumeBar);
            }

            // ── Content area ─────────────────────────────────────────────
            float top = 348, h = Theme.H - top - 50;
            if (t.Kind == TitleKind.Show)
            {
                seasons = new FocusList(Theme.Margin, top, 260, h);
                episodes = new FocusList(Theme.Margin + 290, top, 520, h, app.Tmdb.Enabled ? 108 : 72);
                releases = new FocusList(Theme.Margin + 840, top, Theme.W - 2 * Theme.Margin - 840, h);
                columns.AddRange(new[] { seasons, episodes, releases });
                Add(seasons); Add(episodes); Add(releases);
                seasons.Moved += _ => ShowSeason();
                seasons.Activated += _ => Focus(1);
                episodes.Moved += _ => ShowEpisode();
                episodes.Activated += _ => { if (tmdbMode) ActivateEpisode(); else Focus(2); };
                releases.Activated += i => Play(i);

                if (app.Tmdb.Enabled)
                {
                    tmdbMode = true;
                    loadingLabel = Theme.Label("Loading series info…", 24, Theme.Muted,
                        Theme.Margin, top + h - 40, 500, 36);
                    Add(loadingLabel);
                    _ = InitTmdbShowAsync(app);
                }
                else
                {
                    seasonNums = t.Seasons.ToList();
                    seasons.SetItems(seasonNums.Select(s => ($"Season {s}", (string)null, (Color)null)));
                    if (seasonNums.Count > 0) { ShowSeason(); Focus(seasonNums.Count > 1 ? 0 : 1); }
                    else Focus(0);
                    if (!string.IsNullOrEmpty(app.Settings.EztvUrl))
                    {
                        loadingLabel = Theme.Label("Loading episodes…", 24, Theme.Muted,
                            Theme.Margin + 290, top + h - 40, 500, 36);
                        Add(loadingLabel);
                        _ = FetchAllEpisodesAsync(app);
                    }
                }
            }
            else
            {
                releases = new FocusList(Theme.Margin, top, Theme.W - 2 * Theme.Margin, h);
                columns.Add(releases);
                Add(releases);
                releases.Activated += i => Play(i);
                if (t.IsUpcoming)
                {
                    var dateStr = t.ReleaseDate != default ? t.ReleaseDate.ToString("d MMM yyyy") : "TBA";
                    releases.SetItems(new[] { ($"Not yet released — {dateStr}", (string)null, (Color)null) });
                }
                else if (t.Releases.Count > 0)
                {
                    shown = t.RankedReleases();
                    ShowAutoPlay(shown);
                }
                else
                {
                    var persisted = app.Cache.GetReleases(PersistKey("movie"));
                    if (persisted != null && persisted.Count > 0)
                    {
                        shown = persisted;
                        ShowAutoPlay(shown);
                    }
                    else
                    {
                        releases.SetItems(new[] { ("Finding sources…", (string)null, (Color)null) });
                        _ = FetchMovieTorrentsAsync(app);
                    }
                }
                if (t.TmdbId > 0)
                {
                    var sk = StreamCacheKey(t.TmdbId, false, 0, 0);
                    if (streamCache.TryGetValue(sk, out var cached))
                        streamResults = cached;
                    _ = ResolveStreamAsync(t.TmdbId, false, 0, 0);
                }
                Focus(0);
            }

            // Load backdrop from cache or network
            var bgUrl = !string.IsNullOrEmpty(t.BackdropUrl) ? t.BackdropUrl : t.PosterUrl;
            _ = LoadBackdropAsync(bgUrl, fetchCts?.Token ?? CancellationToken.None);
        }

        async Task LoadBackdropAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                var cached = App.Cache.GetPoster(url);
                if (cached == null)
                {
                    byte[] data;
                    if (new Uri(url).Host.Contains("tmdb.org"))
                        data = await imgHttp.GetByteArrayAsync(url).ConfigureAwait(false);
                    else
                        data = await DohResolver.FetchBytesAsync(url, ct).ConfigureAwait(false);
                    if (data.Length >= 100 && data.Length <= 2_000_000)
                        cached = App.Cache.PutPoster(url, data);
                }
                if (cached != null)
                    App.Post(() => { if (detailBg != null) detailBg.ResourceUrl = cached; });
            }
            catch { }
        }

        // ── TMDB-driven show flow ──────────────────────────────────────

        async Task InitTmdbShowAsync(App app)
        {
            fetchCts = new CancellationTokenSource();
            try
            {
                int tmdbId = title.TmdbId;

                // Try disk cache synchronously first — renders instantly
                if (tmdbId > 0)
                {
                    var cached = app.Cache.GetTvInfo(tmdbId);
                    if (cached.HasValue && cached.Value.Seasons != null && cached.Value.Seasons.Count > 0)
                    {
                        var tmdbSeasons = cached.Value.Seasons;
                        if (!string.IsNullOrEmpty(cached.Value.ImdbId) && string.IsNullOrEmpty(title.ImdbId))
                            title.ImdbId = cached.Value.ImdbId;
                        if (!string.IsNullOrEmpty(cached.Value.Genres) && string.IsNullOrEmpty(title.Genres))
                            title.Genres = cached.Value.Genres;
                        if (cached.Value.Rating > 0 && title.Rating == 0) title.Rating = cached.Value.Rating;

                        seasonNums = tmdbSeasons.Select(s => s.Number).ToList();
                        seasons.SetItems(seasonNums.Select(s =>
                        {
                            var info = tmdbSeasons.FirstOrDefault(ts => ts.Number == s);
                            return ($"Season {s}", $"{info?.EpisodeCount ?? 0} episodes", (Color)null);
                        }));
                        RemoveLoading();
                        UpdateMeta();
                        if (seasonNums.Count > 0)
                        {
                            Focus(0);
                            _ = LoadTmdbSeasonAsync(app, seasonNums[0]);
                        }
                        app.Log($"DETAIL: TMDB tv info FROM CACHE (instant) -> {tmdbSeasons.Count} seasons");

                        if (!string.IsNullOrEmpty(title.ImdbId))
                            eztvPreFetch = FetchAllEpisodesAsync(app);
                        return;
                    }
                }

                // Cache miss — go async
                if (tmdbId <= 0 && app.Tmdb.Enabled)
                {
                    tmdbId = await app.Tmdb.FindTvIdAsync(title.Name, fetchCts.Token);
                    if (tmdbId > 0) title.TmdbId = tmdbId;
                    app.Log($"DETAIL: TMDB lookup '{title.Name}' -> tmdbId={tmdbId}");
                }
                if (tmdbId <= 0)
                {
                    app.Post(() => FallbackToReleaseMode(app));
                    return;
                }

                var (tmdbSeasonsNet, imdbId, genres, rating) = await app.Tmdb.GetTvInfoAsync(tmdbId, fetchCts.Token);
                if (tmdbSeasonsNet != null && tmdbSeasonsNet.Count > 0)
                    app.Cache.PutTvInfo(tmdbId, tmdbSeasonsNet, imdbId, genres, rating);
                app.Log($"DETAIL: TMDB tv info -> {tmdbSeasonsNet?.Count ?? 0} seasons, imdb={imdbId}");

                if (!string.IsNullOrEmpty(imdbId) && string.IsNullOrEmpty(title.ImdbId))
                    title.ImdbId = imdbId;
                if (!string.IsNullOrEmpty(genres) && string.IsNullOrEmpty(title.Genres)) title.Genres = genres;
                if (rating > 0 && title.Rating == 0) title.Rating = rating;

                if (!string.IsNullOrEmpty(title.ImdbId))
                    eztvPreFetch = FetchAllEpisodesAsync(app);

                if (tmdbSeasonsNet == null || tmdbSeasonsNet.Count == 0)
                {
                    app.Post(() => FallbackToReleaseMode(app));
                    return;
                }

                app.Post(() =>
                {
                    seasonNums = tmdbSeasonsNet.Select(s => s.Number).ToList();
                    seasons.SetItems(seasonNums.Select(s =>
                    {
                        var info = tmdbSeasonsNet.FirstOrDefault(ts => ts.Number == s);
                        return ($"Season {s}", $"{info?.EpisodeCount ?? 0} episodes", (Color)null);
                    }));
                    RemoveLoading();
                    UpdateMeta();
                    if (seasonNums.Count > 0)
                    {
                        Focus(0);
                        _ = LoadTmdbSeasonAsync(app, seasonNums[0]);
                    }
                });
            }
            catch (Exception e)
            {
                app.Log($"DETAIL: TMDB show init failed: {e.Message}");
                app.Post(() => FallbackToReleaseMode(app));
            }
        }

        async Task LoadTmdbSeasonAsync(App app, int seasonNum)
        {
            if (tmdbEpisodeCache.ContainsKey(seasonNum))
            {
                app.Post(() => PopulateEpisodes(seasonNum));
                return;
            }

            var cached = app.Cache.GetSeasonEpisodes(title.TmdbId, seasonNum);
            if (cached != null)
            {
                tmdbEpisodeCache[seasonNum] = cached;
                app.Log($"DETAIL: TMDB S{seasonNum} FROM CACHE -> {cached.Count} episodes");
                app.Post(() => PopulateEpisodes(seasonNum));
                return;
            }

            app.Post(() => episodes.SetItems(new[] { ("Loading…", (string)null, (Color)null) }));

            var eps = await app.Tmdb.GetSeasonEpisodesAsync(title.TmdbId, seasonNum, fetchCts?.Token ?? CancellationToken.None);
            if (eps != null && eps.Count > 0)
            {
                tmdbEpisodeCache[seasonNum] = eps;
                app.Cache.PutSeasonEpisodes(title.TmdbId, seasonNum, eps);
            }
            app.Log($"DETAIL: TMDB S{seasonNum} -> {eps?.Count ?? 0} episodes");
            app.Post(() => PopulateEpisodes(seasonNum));
        }

        void PopulateEpisodes(int seasonNum)
        {
            if (tmdbEpisodeCache.TryGetValue(seasonNum, out var eps) && eps.Count > 0)
            {
                episodeNums = eps.Select(e => e.Number).ToList();
                var items = eps.Select(e =>
                {
                    var label = $"E{e.Number:00}";
                    if (!string.IsNullOrEmpty(e.Name)) label += $"  {e.Name}";
                    string sub = null;
                    if (e.AirDate != default)
                    {
                        sub = e.AirDate > DateTime.UtcNow
                            ? $"Airs {e.AirDate:d MMM yyyy}"
                            : e.AirDate.ToString("d MMM yyyy");
                    }
                    return (label, sub, e.AirDate > DateTime.UtcNow ? Theme.Muted : (Color)null);
                });
                episodes.SetImageItems(items, 160, 90);
                _ = LoadEpisodeStillsAsync(eps);
                if (episodeNums.Count > 0)
                    ShowEpisodePreview();
            }
            else
            {
                episodeNums = new List<int>();
                episodes.SetItems(new[] { ("No episodes found", (string)null, (Color)null) });
                releases.SetItems(new (string, string, Color)[0]);
            }
        }

        async Task LoadEpisodeStillsAsync(List<TmdbEpisode> eps)
        {
            var ct = fetchCts?.Token ?? CancellationToken.None;
            for (int i = 0; i < eps.Count; i++)
            {
                if (ct.IsCancellationRequested) return;
                var url = Tmdb.StillUrl(eps[i].StillPath);
                if (url == null) continue;
                int idx = i;
                try
                {
                    var cached = App.Cache.GetPoster(url);
                    if (cached == null)
                    {
                        using (var resp = await imgHttp.GetAsync(url, ct).ConfigureAwait(false))
                        {
                            resp.EnsureSuccessStatusCode();
                            var data = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            if (data.Length >= 100 && data.Length <= 2_000_000)
                                cached = App.Cache.PutPoster(url, data);
                        }
                    }
                    if (cached != null && !ct.IsCancellationRequested)
                        App.Post(() => episodes.SetRowImage(idx, cached));
                }
                catch (OperationCanceledException) { return; }
                catch { }
            }
        }

        string PersistKey(string suffix)
        {
            var id = !string.IsNullOrEmpty(title.ImdbId) ? title.ImdbId
                   : title.TmdbId > 0 ? $"tmdb{title.TmdbId}"
                   : title.Name?.Replace(' ', '-') ?? "unknown";
            return $"{id}_{suffix}";
        }

        async Task SearchEpisodeTorrentsAsync(int season, int episode)
        {
            var key = $"S{season:00}E{episode:00}";
            if (torrentCache.TryGetValue(key, out var cached))
            {
                App.Post(() => ShowTorrentResults(cached));
                return;
            }
            var persisted = App.Cache.GetReleases(PersistKey(key));
            if (persisted != null)
            {
                torrentCache[key] = persisted;
                App.Post(() => ShowTorrentResults(persisted));
                return;
            }

            searchCts?.Cancel();
            searchCts = new CancellationTokenSource();
            var ct = searchCts.Token;

            App.Post(() => releases.SetItems(new[] { ($"Searching {key}…", (string)null, (Color)null) }));

            if (eztvPreFetch != null)
            {
                try { await eztvPreFetch.ConfigureAwait(false); } catch { }
            }

            var results = new List<Release>();
            var fast = new List<Task>();
            var slow = new List<Task>();

            var existing = title.Releases
                .Where(r => r.Info.Season == season && (r.Info.Episode == episode || r.Info.Episode == -1))
                .ToList();
            lock (results) results.AddRange(existing);

            // Fast: Torrentio (instant, aggregates TPB/1337x/RARBG)
            fast.Add(Task.Run(async () =>
            {
                try
                {
                    var imdb = title.ImdbId;
                    if (string.IsNullOrEmpty(imdb)) return;
                    var found = await App.Torrentio.SearchEpisodeAsync(imdb, season, episode, ct);
                    App.Log($"DETAIL: Torrentio {key} '{imdb}' -> {found.Count} releases");
                    lock (results) results.AddRange(found);
                }
                catch (Exception e) { App.Log($"DETAIL: Torrentio {key} fail: {e.Message}"); }
            }));

            // Fast: Torznab indexers
            var indexers = App.Settings.Indexers;
            if (indexers.Count > 0)
            {
                fast.Add(Task.Run(async () =>
                {
                    try
                    {
                        var (found, errors) = await App.Torznab.SearchAllAsync(
                            indexers, $"{title.Name} {key}", Cat.Tv, ct);
                        var matched = found.Where(r => r.Info.Season == season &&
                                                        (r.Info.Episode == episode || r.Info.Episode == -1)).ToList();
                        App.Log($"DETAIL: Torznab {key} -> {matched.Count} matches from {found.Count} total");
                        lock (results) results.AddRange(matched);
                    }
                    catch (Exception e) { App.Log($"DETAIL: Torznab {key} fail: {e.Message}"); }
                }));
            }

            // Fast: Nyaa (anime only)
            if (IsAnime())
            {
                fast.Add(Task.Run(async () =>
                {
                    try
                    {
                        var found = await App.Nyaa.SearchAsync($"{title.Name} {key}", 30, ct);
                        var matched = found.Where(r => r.Info.Season == season &&
                                                        (r.Info.Episode == episode || r.Info.Episode == -1)).ToList();
                        App.Log($"DETAIL: Nyaa {key} -> {matched.Count} matches from {found.Count} total");
                        lock (results) results.AddRange(matched);
                    }
                    catch (Exception e) { App.Log($"DETAIL: Nyaa {key} fail: {e.Message}"); }
                }));
            }

            // Slow: EZTV
            if (!string.IsNullOrEmpty(title.ImdbId))
            {
                slow.Add(Task.Run(async () =>
                {
                    try
                    {
                        var found = App.EztvCache.Snapshot
                            .Where(r => r.ImdbId == title.ImdbId && r.Info.Season == season &&
                                        (r.Info.Episode == episode || r.Info.Episode == -1))
                            .ToList();
                        if (found.Count == 0)
                        {
                            foreach (var apiUrl in new[] { App.Settings.EztvUrl, "https://eztvx.to/api/get-torrents" })
                            {
                                if (string.IsNullOrEmpty(apiUrl)) continue;
                                try
                                {
                                    var all = await App.Eztv.SearchByImdbAsync(apiUrl, title.ImdbId, 100, ct);
                                    found = all.Where(r => r.Info.Season == season &&
                                                           (r.Info.Episode == episode || r.Info.Episode == -1)).ToList();
                                    App.Log($"DETAIL: EZTV {key} ({apiUrl}) -> {found.Count} matches from {all.Count} total");
                                    if (found.Count > 0) break;
                                }
                                catch (Exception e) { App.Log($"DETAIL: EZTV {key} fail: {e.Message}"); }
                            }
                        }
                        else
                            App.Log($"DETAIL: EZTV cache {key} -> {found.Count}");
                        lock (results) results.AddRange(found);
                    }
                    catch { }
                }));
            }

            // Slow: TPB (single query, 8s cap — Torrentio already covers TPB)
            if (!string.IsNullOrEmpty(App.Settings.PirateBayUrl))
            {
                slow.Add(Task.Run(async () =>
                {
                    try
                    {
                        using (var tpbCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            tpbCts.CancelAfter(TimeSpan.FromSeconds(8));
                            var epQuery = $"{title.Name} {key}";
                            var tpb = await App.PirateBay.SearchAsync(App.Settings.PirateBayUrl, epQuery, 40, tpbCts.Token);
                            var matched = tpb.Where(r => r.Info.Season == season &&
                                                          (r.Info.Episode == episode || r.Info.Episode == -1)).ToList();
                            App.Log($"DETAIL: TPB '{epQuery}' -> {matched.Count} matches from {tpb.Count} total");
                            lock (results) results.AddRange(matched);
                        }
                    }
                    catch (Exception e) { App.Log($"DETAIL: TPB {key} fail: {e.Message}"); }
                }));
            }

            // Wait for fast sources, show results immediately
            await Task.WhenAll(fast).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            var deduped = Dedup(results);
            Title.Rank(deduped);
            torrentCache[key] = deduped;
            App.Cache.PutReleases(PersistKey(key), deduped);
            App.Post(() => ShowTorrentResults(deduped));

            // Let slow sources finish in background, merge if they find more
            if (slow.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try { await Task.WhenAll(slow).ConfigureAwait(false); }
                    catch { }
                    if (ct.IsCancellationRequested) return;
                    var all = Dedup(results);
                    if (all.Count <= deduped.Count) return;
                    Title.Rank(all);
                    torrentCache[key] = all;
                    App.Cache.PutReleases(PersistKey(key), all);
                    App.Post(() => ShowTorrentResults(all));
                    App.Log($"DETAIL: slow sources added {all.Count - deduped.Count} more for {key}");
                });
            }
        }

        void ShowTorrentResults(List<Release> results)
        {
            ShowAutoPlay(results);
        }

        void FallbackToReleaseMode(App app)
        {
            tmdbMode = false;
            RemoveLoading();
            seasonNums = title.Seasons.ToList();
            seasons.SetItems(seasonNums.Select(s => ($"Season {s}", (string)null, (Color)null)));
            if (seasonNums.Count > 0)
            {
                ShowSeasonFromReleases();
                Focus(seasonNums.Count > 1 ? 0 : 1);
            }
            if (!string.IsNullOrEmpty(app.Settings.EztvUrl))
                _ = FetchAllEpisodesAsync(app);
        }

        // ── Legacy release-driven flow ─────────────────────────────────

        async Task FetchAllEpisodesAsync(App app)
        {
            fetchCts = fetchCts ?? new CancellationTokenSource();
            try
            {
                if (string.IsNullOrEmpty(title.ImdbId) && title.TmdbId > 0)
                {
                    title.ImdbId = await app.Tmdb.GetImdbIdAsync(title.TmdbId, true, fetchCts.Token);
                    app.Log($"DETAIL: resolved IMDB from TMDB {title.TmdbId} -> {title.ImdbId}");
                }
                if (string.IsNullOrEmpty(title.ImdbId)) { app.Post(RemoveLoading); return; }

                var cached = app.EztvCache.Snapshot;
                var all = cached.Where(r => r.ImdbId == title.ImdbId).ToList();
                app.Log($"DETAIL: EZTV cache filter -> {all.Count} releases for {title.ImdbId}");

                foreach (var apiUrl in new[] { app.Settings.EztvUrl, "https://eztvx.to/api/get-torrents" })
                {
                    if (string.IsNullOrEmpty(apiUrl)) continue;
                    try
                    {
                        var api = await app.Eztv.SearchByImdbAsync(apiUrl, title.ImdbId, 100, fetchCts.Token).ConfigureAwait(false);
                        app.Log($"DETAIL: EZTV API search ({apiUrl}) -> {api.Count} releases");
                        if (api.Count > 0) { all.AddRange(api); break; }
                    }
                    catch (Exception e) { app.Log($"DETAIL: EZTV API search ({apiUrl}) failed: {e.Message}"); }
                }
                if (fetchCts.IsCancellationRequested) return;
                app.Post(() => MergeEpisodes(all));
            }
            catch { }
            finally { app.Post(RemoveLoading); }
        }

        async Task FetchMovieTorrentsAsync(App app)
        {
            fetchCts = new CancellationTokenSource();
            try
            {
                var results = new List<Release>();
                var fast = new List<Task>();
                var slow = new List<Task>();

                // Fast: Torrentio (aggregates TPB, 1337x, RARBG — usually returns 100+ results)
                fast.Add(Task.Run(async () =>
                {
                    try
                    {
                        var imdb = title.ImdbId;
                        if (string.IsNullOrEmpty(imdb) && title.TmdbId > 0 && app.Tmdb.Enabled)
                            imdb = await app.Tmdb.GetImdbIdAsync(title.TmdbId, false, fetchCts.Token);
                        if (string.IsNullOrEmpty(imdb)) return;
                        if (string.IsNullOrEmpty(title.ImdbId)) title.ImdbId = imdb;
                        var found = await app.Torrentio.SearchMovieAsync(imdb, fetchCts.Token);
                        var matched = found.Where(r => !r.Info.IsSeries).ToList();
                        app.Log($"DETAIL: Torrentio movie '{imdb}' -> {matched.Count} releases");
                        lock (results) results.AddRange(matched);
                    }
                    catch (Exception e) { app.Log($"DETAIL: Torrentio movie fail: {e.Message}"); }
                }));

                // Fast: YTS
                if (!string.IsNullOrEmpty(app.Settings.YtsUrl))
                {
                    fast.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var yts = await app.Yts.SearchAsync(app.Settings.YtsUrl, title.Name, 50, fetchCts.Token);
                            app.Log($"DETAIL: YTS search '{title.Name}' -> {yts.Count} releases");
                            lock (results) results.AddRange(yts);
                        }
                        catch (Exception e) { app.Log($"DETAIL: YTS fail: {e.Message}"); }
                    }));
                }

                // Fast: Torznab indexers
                var indexers = app.Settings.Indexers;
                if (indexers.Count > 0)
                {
                    fast.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var query = title.Year > 0 ? $"{title.Name} {title.Year}" : title.Name;
                            var (found, errors) = await app.Torznab.SearchAllAsync(
                                indexers, query, Cat.Movies, fetchCts.Token);
                            var matched = found.Where(r => !r.Info.IsSeries).ToList();
                            app.Log($"DETAIL: Torznab movie '{query}' -> {matched.Count} matches from {found.Count} total");
                            lock (results) results.AddRange(matched);
                        }
                        catch (Exception e) { app.Log($"DETAIL: Torznab movie fail: {e.Message}"); }
                    }));
                }

                // Fast: Nyaa (anime only)
                if (IsAnime())
                {
                    fast.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var query = title.Year > 0 ? $"{title.Name} {title.Year}" : title.Name;
                            var found = await app.Nyaa.SearchAsync(query, 30, fetchCts.Token);
                            var matched = found.Where(r => !r.Info.IsSeries).ToList();
                            app.Log($"DETAIL: Nyaa movie '{query}' -> {matched.Count} matches from {found.Count} total");
                            lock (results) results.AddRange(matched);
                        }
                        catch (Exception e) { app.Log($"DETAIL: Nyaa movie fail: {e.Message}"); }
                    }));
                }

                // Slow: PirateBay (rate-limited, 8s cap — Torrentio already covers TPB)
                if (!string.IsNullOrEmpty(app.Settings.PirateBayUrl))
                {
                    slow.Add(Task.Run(async () =>
                    {
                        try
                        {
                            using (var tpbCts = CancellationTokenSource.CreateLinkedTokenSource(fetchCts.Token))
                            {
                                tpbCts.CancelAfter(TimeSpan.FromSeconds(8));
                                var tpb = await app.PirateBay.SearchAsync(app.Settings.PirateBayUrl, title.Name, 30, tpbCts.Token);
                                var matched = tpb.Where(r => !r.Info.IsSeries).ToList();
                                app.Log($"DETAIL: TPB movie '{title.Name}' -> {matched.Count} matches from {tpb.Count} total");
                                lock (results) results.AddRange(matched);
                            }
                        }
                        catch (Exception e) { app.Log($"DETAIL: TPB movie fail: {e.Message}"); }
                    }));
                }

                // Show results as soon as fast sources finish
                await Task.WhenAll(fast).ConfigureAwait(false);
                if (fetchCts.IsCancellationRequested) return;

                void MergeAndShow(List<Release> pool)
                {
                    var deduped = Dedup(pool);
                    foreach (var r in deduped)
                    {
                        r.Info = r.Info ?? ReleaseInfo.Parse(r.Title);
                        if (!title.Releases.Any(x => x.Link == r.Link))
                            title.Releases.Add(r);
                    }
                    shown = title.RankedReleases();
                    app.Cache.PutReleases(PersistKey("movie"), shown);
                    ShowAutoPlay(shown);
                }

                app.Post(() => MergeAndShow(results));

                // Slow sources merge in when ready
                if (slow.Count > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await Task.WhenAll(slow).ConfigureAwait(false); }
                        catch { }
                        if (fetchCts.IsCancellationRequested) return;
                        int before = Dedup(results).Count;
                        var afterDedup = Dedup(results);
                        if (afterDedup.Count <= before) return;
                        app.Post(() => MergeAndShow(results));
                        app.Log($"DETAIL: slow sources added {afterDedup.Count - before} more movies");
                    });
                }
            }
            catch (Exception e)
            {
                app.Log($"DETAIL: movie torrent fetch failed: {e.Message}");
                app.Post(() => releases.SetItems(new[] { ($"Search failed: {e.Message}", (string)null, (Color)null) }));
            }
        }

        void MergeEpisodes(List<Release> fetched)
        {
            var existingLinks = new HashSet<string>(title.Releases.Select(r => r.Link));
            int added = 0;
            foreach (var r in fetched)
            {
                if (existingLinks.Contains(r.Link)) continue;
                r.Info = r.Info ?? ReleaseInfo.Parse(r.Title);
                if (!r.Info.IsSeries) continue;
                title.Releases.Add(r);
                existingLinks.Add(r.Link);
                added++;
            }
            if (tmdbMode) return;
            if (added > 0 || seasonNums.Count == 0)
            {
                seasonNums = title.Seasons.ToList();
                seasons.SetItems(seasonNums.Select(s => ($"Season {s}", (string)null, (Color)null)));
                if (seasonNums.Count > 0)
                {
                    ShowSeasonFromReleases();
                    if (column == 0 && seasonNums.Count == 1) Focus(1);
                }
            }
        }

        // ── Shared UI helpers ──────────────────────────────────────────

        void ShowSeason()
        {
            if (tmdbMode)
            {
                if (seasonNums.Count == 0) return;
                _ = LoadTmdbSeasonAsync(App, seasonNums[seasons.Index]);
                return;
            }
            ShowSeasonFromReleases();
        }

        void ShowSeasonFromReleases()
        {
            if (seasonNums.Count == 0) return;
            episodeMap = title.Episodes(seasonNums[seasons.Index]);
            episodeNums = episodeMap.Keys.Where(e => e >= 0).Concat(episodeMap.Keys.Where(e => e < 0)).ToList();
            episodes.SetItems(episodeNums.Select(e =>
            {
                var best = episodeMap[e][0];
                var label = e < 0 ? "Full season pack" : $"Episode {e}";
                return (label, $"{episodeMap[e].Count} release(s) · best {best.Info.Quality} · {best.Seeders} seeds", (Color)null);
            }));
            ShowEpisodeFromReleases();
        }

        void ShowEpisode()
        {
            if (tmdbMode) { ShowEpisodePreview(); return; }
            ShowEpisodeFromReleases();
        }

        void ShowEpisodePreview()
        {
            if (episodeNums.Count == 0 || seasonNums.Count == 0)
            {
                releases.SetItems(new (string, string, Color)[0]);
                shown = new List<Release>();
                showingAutoPlay = false;
                return;
            }
            int sn = seasonNums[seasons.Index];
            int en = episodeNums[episodes.Index];
            var cacheKey = $"S{sn:00}E{en:00}";
            if (torrentCache.TryGetValue(cacheKey, out var cached))
            {
                ShowAutoPlay(cached);
                return;
            }
            var persisted = App.Cache.GetReleases(PersistKey(cacheKey));
            if (persisted != null)
            {
                torrentCache[cacheKey] = persisted;
                ShowAutoPlay(persisted);
                return;
            }
            var items = new List<(string, string, Color)>();
            if (tmdbEpisodeCache.TryGetValue(sn, out var eps))
            {
                var ep = eps.FirstOrDefault(e => e.Number == en);
                if (ep != null)
                {
                    if (ep.AirDate > DateTime.UtcNow)
                    {
                        items.Add(($"Not yet aired — {ep.AirDate:d MMM yyyy}", (string)null, Theme.Muted));
                        releases.SetItems(items);
                        shown = new List<Release>();
                        showingAutoPlay = false;
                        return;
                    }
                    if (!string.IsNullOrEmpty(ep.Overview))
                        items.Add((ep.Overview, ep.AirDate != default ? ep.AirDate.ToString("d MMM yyyy") : null, (Color)null));
                }
            }
            items.Add(("Press OK to find torrents", (string)null, Theme.Muted));
            releases.SetItems(items);
            shown = new List<Release>();
            showingAutoPlay = false;
        }

        void ActivateEpisode()
        {
            if (episodeNums.Count == 0 || seasonNums.Count == 0) return;
            int sn = seasonNums[seasons.Index];
            int en = episodeNums[episodes.Index];
            if (tmdbEpisodeCache.TryGetValue(sn, out var eps))
            {
                var ep = eps.FirstOrDefault(e => e.Number == en);
                if (ep != null && ep.AirDate > DateTime.UtcNow) return;
            }
            var cacheKey = $"S{sn:00}E{en:00}";
            if (torrentCache.TryGetValue(cacheKey, out var cached))
            {
                ShowAutoPlay(cached);
                Focus(2);
                return;
            }
            var persisted = App.Cache.GetReleases(PersistKey(cacheKey));
            if (persisted != null)
            {
                torrentCache[cacheKey] = persisted;
                ShowAutoPlay(persisted);
                Focus(2);
                return;
            }
            _ = SearchEpisodeTorrentsAsync(sn, en);
            if (title.TmdbId > 0)
            {
                var sk = StreamCacheKey(title.TmdbId, true, sn, en);
                if (streamCache.TryGetValue(sk, out var cachedStream))
                    streamResults = cachedStream;
                else
                    streamResults = null;
                _ = ResolveStreamAsync(title.TmdbId, true, sn, en);
            }
            Focus(2);
        }

        void UpdateMeta()
        {
            var parts = new List<string>();
            if (title.Rating > 0) parts.Add($"★ {title.Rating:0.0}");
            if (!string.IsNullOrEmpty(title.Genres)) parts.Add(title.Genres);
            if (title.Kind == TitleKind.Show && seasonNums.Count > 0)
                parts.Add($"{seasonNums.Count} Season{(seasonNums.Count != 1 ? "s" : "")}");
            metaLabel.Text = string.Join("  ·  ", parts);
        }

        void ShowEpisodeFromReleases()
        {
            if (episodeNums.Count == 0) { shown = new List<Release>(); releases.SetItems(new (string, string, Color)[0]); return; }
            var e = episodeNums[episodes.Index];
            shown = episodeMap[e].ToList();
            if (e >= 0 && episodeMap.TryGetValue(-1, out var packs)) shown.AddRange(packs);
            Title.Rank(shown);
            ShowAutoPlay(shown);
        }

        static (string, string, Color) Describe(Release r)
        {
            var parts = new List<string>();
            if (r.Info.Quality.Length > 0) parts.Add(r.Info.Quality);
            if (r.Info.Source.Length > 0) parts.Add(r.Info.Source == "WEB" ? "WEB-DL" : r.Info.Source);
            if (r.Info.Hevc) parts.Add("HEVC");
            if (r.Size > 0) parts.Add(Theme.Bytes(r.Size));
            parts.Add($"{r.Seeders} seeds");
            parts.Add(r.Indexer);
            if (r.Info.Dts) parts.Add("DTS: no audio");
            if (r.Info.IsLowQuality) parts.Add("low quality");
            return (r.Title, string.Join(" · ", parts),
                r.Info.IsLowQuality || r.Info.Dts || r.Seeders == 0 ? Theme.Warn : null);
        }

        static (string, string, Color) DescribeStream(StreamResult s)
        {
            var parts = new List<string>();
            parts.Add("Stream");
            if (!string.IsNullOrEmpty(s.Quality)) parts.Add(s.Quality);
            if (!string.IsNullOrEmpty(s.Format)) parts.Add(s.Format.ToUpperInvariant());
            var srv = StreamServerLabel(s.Server);
            if (srv.Length > 0) parts.Add(srv.TrimStart(' ', '·', ' '));
            parts.Add("Instant");
            return ($"⚡ {s.Server ?? "Stream"}", string.Join(" · ", parts), Theme.Accent);
        }

        void Focus(int c)
        {
            column = Math.Max(0, Math.Min(columns.Count - 1, c));
            for (int i = 0; i < columns.Count; i++) columns[i].SetActive(i == column);
        }

        void Play(int index)
        {
            if (showingAutoPlay)
            {
                if (index == 0)
                {
                    if (autoIsStream)
                        PlayStream();
                    else if (autoSelected != null)
                        PlayRelease(autoSelected);
                    return;
                }
                // "Show all" — torrents first (ranked), then streams
                showingAutoPlay = false;
                autoSelected = null;
                var allItems = new List<(string, string, Color)>();
                var ranked = shown.OrderByDescending(r => ReleaseScore(r)).ToList();
                allItems.AddRange(ranked.Select(Describe));
                if (streamResults != null)
                    allItems.AddRange(streamResults.Select(DescribeStream));
                releases.SetItems(allItems);
                return;
            }
            int torrentCount = shown?.Count ?? 0;
            var sortedTorrents = shown.OrderByDescending(r => ReleaseScore(r)).ToList();
            if (index < torrentCount)
            {
                if (index < sortedTorrents.Count)
                    PlayRelease(sortedTorrents[index]);
                return;
            }
            int streamIdx = index - torrentCount;
            if (streamResults != null && streamIdx >= 0 && streamIdx < streamResults.Count)
            {
                var picked = streamResults[streamIdx];
                int sn = -1, ep = -1;
                string playName = title.Name;
                if (title.Kind == TitleKind.Show)
                {
                    sn = seasonNums.Count > 0 ? seasonNums[seasons.Index] : -1;
                    ep = episodeNums.Count > 0 ? episodeNums[episodes.Index] : -1;
                    playName += ep >= 0 ? $" · S{sn:00}E{ep:00}" : $" · Season {sn}";
                }
                App.Push(new PlayerScreen(App, null, sn, ep, playName, title, 0, picked.Url,
                    streamFallbacks: streamResults, releaseFallbacks: shown));
                return;
            }
        }

        static int ReleaseScore(Release r)
        {
            int score = 0;
            if (r.Info.Source == "WEB") score += 200;
            else if (r.Info.Source == "BluRay") score += 100;
            if (r.Info.Quality == "1080p") score += 80;
            else if (r.Info.Quality == "2160p") score += 70;
            else if (r.Info.Quality == "720p") score += 40;
            if (!r.Info.Dts) score += 30;
            if (!r.Info.IsLowQuality) score += 20;
            if (r.Info.Hevc) score += 5;
            score += Math.Min(r.Seeders, 50);
            return score;
        }

        void PlayRelease(Release r)
        {
            if (string.IsNullOrEmpty(r.Link)) return;
            int season = -1, episode = -1;
            string name = title.Name;
            if (title.Kind == TitleKind.Show)
            {
                season = seasonNums.Count > 0 ? seasonNums[seasons.Index] : -1;
                episode = episodeNums.Count > 0 ? episodeNums[episodes.Index] : -1;
                name += episode >= 0 ? $" · S{season:00}E{episode:00}" : $" · Season {season}";
            }
            long pos = ConsumeResumePosition();
            App.Push(new PlayerScreen(App, r, season, episode, name, title, pos,
                streamFallbacks: streamResults, releaseFallbacks: shown));
        }

        static bool IsWebDl(Release r) => r.Info.Source == "WEB";

        static Release AutoSelect(List<Release> ranked)
        {
            // Priority: WEB-DL 1080p > WEB-DL any > 1080p any > any with seeds
            var candidates = ranked.Where(r => r.Info.Quality == "1080p" && IsWebDl(r) && r.Seeders >= 3 && !r.Info.Dts && !r.Info.IsLowQuality).ToList();
            if (candidates.Count == 0)
                candidates = ranked.Where(r => IsWebDl(r) && r.Seeders >= 3 && !r.Info.Dts && !r.Info.IsLowQuality).ToList();
            if (candidates.Count == 0)
                candidates = ranked.Where(r => r.Info.Quality == "1080p" && r.Seeders >= 3 && !r.Info.Dts && !r.Info.IsLowQuality).ToList();
            if (candidates.Count == 0)
                candidates = ranked.Where(r => r.Info.Quality == "1080p" && r.Seeders > 0 && !r.Info.IsLowQuality).ToList();
            if (candidates.Count == 0)
                candidates = ranked.Where(r => r.Seeders >= 3 && !r.Info.Dts && !r.Info.IsLowQuality).ToList();
            if (candidates.Count == 0)
                candidates = ranked.Where(r => r.Seeders > 0 && !r.Info.IsLowQuality).ToList();
            if (candidates.Count == 0)
                candidates = ranked.Where(r => r.Seeders > 0).ToList();
            if (candidates.Count == 0)
                return ranked.FirstOrDefault();
            return candidates.OrderBy(r => r.Size > 0 ? r.Size : long.MaxValue).First();
        }

        void ShowAutoPlay(List<Release> results)
        {
            shown = results;
            var bestStream = BestStream();
            bool hasStream1080 = bestStream != null && (bestStream.Quality == "1080p" || bestStream.Quality == "auto");
            bool hasTorrent = results.Count > 0;

            if (!hasStream1080 && !hasTorrent && bestStream == null)
            {
                showingAutoPlay = false;
                autoSelected = null;
                autoIsStream = false;
                releases.SetItems(new[] { ("No sources found", (string)null, (Color)null) });
                return;
            }

            autoSelected = hasTorrent ? AutoSelect(results) : null;
            showingAutoPlay = true;
            var items = new List<(string, string, Color)>();

            // Priority: torrent first (better quality + subtitle match), stream as fallback
            if (autoSelected != null)
            {
                autoIsStream = false;
                var parts = new List<string>();
                if (autoSelected.Info.Quality.Length > 0) parts.Add(autoSelected.Info.Quality);
                if (autoSelected.Info.Source == "WEB") parts.Add("WEB-DL");
                else if (autoSelected.Info.Source.Length > 0) parts.Add(autoSelected.Info.Source);
                if (autoSelected.Info.Hevc) parts.Add("HEVC");
                if (autoSelected.Size > 0) parts.Add(Theme.Bytes(autoSelected.Size));
                parts.Add($"{autoSelected.Seeders} seeds");
                items.Add(($"▶  Play", string.Join(" · ", parts), Theme.Accent));
            }
            else if (bestStream != null)
            {
                autoIsStream = true;
                var q = bestStream.Quality == "auto" ? "Adaptive" : bestStream.Quality;
                var srv = StreamServerLabel(bestStream.Server);
                items.Add(($"▶  Play", $"Stream · {q}{srv} · Instant", Theme.Accent));
            }

            int totalSources = results.Count + (streamResults?.Count ?? 0);
            if (totalSources > 1)
                items.Add(($"Show all {totalSources} sources", (string)null, (Color)null));
            releases.SetItems(items);

            // Auto-play on resume from Continue Watching
            if (resumePending && (autoIsStream || autoSelected != null))
            {
                if (autoIsStream) PlayStream();
                else PlayRelease(autoSelected);
            }
        }

        async Task ResolveStreamAsync(int tmdbId, bool isTv, int season, int episode)
        {
            try
            {
                var results = await StreamProvider.ResolveAsync(tmdbId, isTv, season, episode).ConfigureAwait(false);
                if (results == null || results.Count == 0) return;
                var sk = StreamCacheKey(tmdbId, isTv, season, episode);
                streamCache[sk] = results;
                App.Log($"DETAIL: Stream resolved {results.Count} sources for tmdb={tmdbId} (qualities: {string.Join(", ", results.Select(r => r.Quality))})");
                App.Post(() =>
                {
                    streamResults = results;
                    ShowAutoPlay(shown ?? new List<Release>());
                });
            }
            catch (Exception e)
            {
                App.Log($"DETAIL: Stream resolve failed: {e.Message}");
            }
        }

        static string StreamCacheKey(int tmdbId, bool isTv, int season, int episode)
            => isTv ? $"stream_{tmdbId}_s{season}e{episode}" : $"stream_{tmdbId}";

        StreamResult BestStream()
        {
            if (streamResults == null || streamResults.Count == 0) return null;
            // Prefer explicit 1080p > 720p > auto (adaptive) > anything else
            return streamResults.FirstOrDefault(s => s.Quality == "1080p")
                ?? streamResults.FirstOrDefault(s => s.Quality == "auto")
                ?? streamResults.FirstOrDefault(s => s.Quality == "720p")
                ?? streamResults[0];
        }

        void RefreshAutoPlayWithStream()
        {
            ShowAutoPlay(shown ?? new List<Release>());
        }

        void ShowStreamOnly()
        {
            ShowAutoPlay(shown ?? new List<Release>());
        }

        void PlayStream()
        {
            var best = BestStream();
            if (best == null) return;
            App.Log($"DETAIL: Playing stream ({best.Quality}): {best.Url}");
            int sn = -1, ep = -1;
            string playName = title.Name;
            if (title.Kind == TitleKind.Show)
            {
                sn = seasonNums.Count > 0 ? seasonNums[seasons.Index] : -1;
                ep = episodeNums.Count > 0 ? episodeNums[episodes.Index] : -1;
                playName += ep >= 0 ? $" · S{sn:00}E{ep:00}" : $" · Season {sn}";
            }
            long pos = ConsumeResumePosition();
            App.Push(new PlayerScreen(App, null, sn, ep, playName, title, pos, best.Url,
                streamFallbacks: streamResults, releaseFallbacks: shown));
        }

        long ConsumeResumePosition()
        {
            if (!resumePending) return 0;
            resumePending = false;
            return resumePositionMs;
        }

        static string StreamServerLabel(string server)
        {
            if (string.IsNullOrEmpty(server) || server == "primary") return "";
            if (server.StartsWith("quality:")) return "";
            if (server.StartsWith("alt:")) return " · " + server.Substring(4);
            return " · " + server;
        }

        bool IsAnime()
        {
            var g = title.Genres ?? "";
            return g.Contains("Animation") || g.Contains("Anime");
        }

        void RemoveLoading()
        {
            if (loadingLabel != null) { Remove(loadingLabel); loadingLabel = null; }
        }

        static List<Release> Dedup(List<Release> results)
        {
            var seen = new HashSet<string>();
            var deduped = new List<Release>();
            foreach (var r in results)
            {
                var hash = ExtractHash(r.Link);
                if (hash != null && !seen.Add(hash)) continue;
                deduped.Add(r);
            }
            return deduped;
        }

        static string ExtractHash(string magnet)
        {
            if (string.IsNullOrEmpty(magnet)) return null;
            var idx = magnet.IndexOf("btih:");
            if (idx < 0) return null;
            var start = idx + 5;
            var end = magnet.IndexOf('&', start);
            return end > start ? magnet.Substring(start, end - start).ToUpperInvariant() : magnet.Substring(start).ToUpperInvariant();
        }

        public override void OnClosed()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            fetchCts?.Cancel();
            var t0 = sw.ElapsedMilliseconds;
            searchCts?.Cancel();
            var t1 = sw.ElapsedMilliseconds;
            App.Log($"DETAIL OnClosed fetchCts.Cancel={t0}ms searchCts.Cancel={t1-t0}ms total={t1}ms");
        }

        public override bool OnKey(string key)
        {
            switch (key)
            {
                case "XF86Yellow":
                    if (!string.IsNullOrEmpty(title.Key))
                    {
                        App.Cache.TogglePin(title);
                        bool pinned = App.Cache.IsPinned(title.Key);
                        UpdateActionButton(favBtn, pinned);
                        App.Toast(pinned ? "Added to My List" : "Removed from My List");
                    }
                    return true;
                case "XF86Green":
                    if (!string.IsNullOrEmpty(title.Key))
                    {
                        App.Cache.ToggleLike(title.Key);
                        bool liked = App.Cache.IsLiked(title.Key);
                        UpdateActionButton(likeBtn, liked);
                        UpdateActionButton(dislikeBtn, App.Cache.IsDisliked(title.Key));
                        App.Toast(liked ? "Liked" : "Like removed");
                    }
                    return true;
                case "XF86Red":
                    if (!string.IsNullOrEmpty(title.Key))
                    {
                        App.Cache.ToggleDislike(title.Key);
                        bool disliked = App.Cache.IsDisliked(title.Key);
                        UpdateActionButton(dislikeBtn, disliked);
                        UpdateActionButton(likeBtn, App.Cache.IsLiked(title.Key));
                        App.Toast(disliked ? "Disliked" : "Dislike removed");
                    }
                    return true;
            }
            if (columns[column].OnKey(key)) return true;
            if (key == "Left" && column > 0) { Focus(column - 1); return true; }
            if (key == "Right" && column < columns.Count - 1) { Focus(column + 1); return true; }
            return false;
        }

        View MakeActionButton(ref float x, float y, float h, float gap, string label, bool active)
        {
            float w = label.Length * 14 + 28;
            var btn = new View
            {
                Position = new Position(x, y),
                Size = new Size(w, h),
                BackgroundColor = active ? Theme.Accent : Theme.Panel,
            };
            var lbl = Theme.Label(label, 20, active ? Theme.Text : Theme.Muted, 0, 0, w, h);
            lbl.HorizontalAlignment = HorizontalAlignment.Center;
            lbl.Name = "lbl";
            btn.Add(lbl);
            Add(btn);
            x += w + gap;
            return btn;
        }

        void UpdateActionButton(View btn, bool active)
        {
            if (btn == null) return;
            btn.BackgroundColor = active ? Theme.Accent : Theme.Panel;
            var lbl = btn.FindChildByName("lbl") as TextLabel;
            if (lbl != null) lbl.TextColor = active ? Theme.Text : Theme.Muted;
        }

        static string FormatMs(long ms)
        {
            var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
                                      : $"{ts.Minutes}:{ts.Seconds:00}";
        }
    }
}

