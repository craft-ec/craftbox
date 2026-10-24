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
    class CategoryScreen : Screen
    {
        static readonly HttpClient imgHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        const float CardW = 220, CardH = 330, CardGap = 28, GridRowH = 430;

        static readonly string[] SortLabels = { "Popular", "Top Rated", "Newest" };
        static readonly string[] SortValues = { "popularity.desc", "vote_average.desc", "primary_release_date.desc" };
        static readonly string[] TimeLabels = { "All Time", "This Week", "This Month", "This Year", "Past Year" };

        readonly string mediaType, genreId, language, extra;
        readonly View sortBox, timeBox;
        readonly TextLabel sortLabel, timeLabel, status;
        readonly View gridClip, grid;
        readonly List<Title> titles = new List<Title>();
        readonly List<View> cards = new List<View>();
        readonly int cols, visibleRows;

        int sortIdx, timeIdx;
        int focusArea; // 0=sort, 1=time, 2=grid
        int gridIdx;
        CancellationTokenSource loadCts;

        public CategoryScreen(App app, string name, string mediaType, string genreId,
            string language, string extra, int defaultSort = 0) : base(app)
        {
            this.mediaType = mediaType;
            this.genreId = genreId;
            this.language = language;
            this.extra = extra;
            sortIdx = defaultSort;

            cols = (int)((Theme.W - 2 * Theme.Margin) / (CardW + CardGap));
            visibleRows = Math.Max(1, (int)((Theme.H - 200) / GridRowH));

            Add(Theme.Label(name, 48, Theme.Accent, Theme.Margin, 30, 800, 60));

            sortBox = Theme.Box(Theme.Margin, 100, 280, 52, Theme.FocusBg);
            sortLabel = Theme.Label($"Sort ▸ {SortLabels[sortIdx]}", 26, Theme.Text, 20, 6, 240, 40);
            sortBox.Add(sortLabel);
            Add(sortBox);

            timeBox = Theme.Box(Theme.Margin + 300, 100, 300, 52, Theme.Panel);
            timeLabel = Theme.Label($"Time ▸ {TimeLabels[0]}", 26, Theme.Text, 20, 6, 260, 40);
            timeBox.Add(timeLabel);
            Add(timeBox);

            status = Theme.Label("", 28, Theme.Muted, Theme.Margin, 200, Theme.W - 2 * Theme.Margin, 60);
            Add(status);

            gridClip = new View
            {
                Position = new Position(Theme.Margin, 180),
                Size = new Size(Theme.W - 2 * Theme.Margin, Theme.H - 200),
                ClippingMode = ClippingModeType.ClipChildren,
            };
            grid = new View();
            gridClip.Add(grid);
            Add(gridClip);

            focusArea = 2;
            Reload();
        }

        public override void OnShown() => Refresh();
        public override void OnClosed() => loadCts?.Cancel();

        void Reload()
        {
            loadCts?.Cancel();
            loadCts = new CancellationTokenSource();
            _ = LoadAsync(loadCts.Token);
        }

        async Task LoadAsync(CancellationToken ct)
        {
            titles.Clear();
            ClearGrid();
            status.Text = "Loading…";

            try
            {
                bool tv = mediaType == "tv";
                var baseUrl = BuildUrl();
                var tasks = new[]
                {
                    App.Tmdb.ListAsync(baseUrl + "&page=1", tv, ct),
                    App.Tmdb.ListAsync(baseUrl + "&page=2", tv, ct),
                    App.Tmdb.ListAsync(baseUrl + "&page=3", tv, ct),
                };
                await Task.WhenAll(tasks);
                if (ct.IsCancellationRequested) return;
                foreach (var t in tasks) titles.AddRange(t.Result);
                await App.Tmdb.FetchCertsAsync(titles, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                status.Text = "Error: " + e.Message;
                App.Log("CAT ERROR: " + e);
                return;
            }

            status.Text = titles.Count == 0 ? "Nothing found." : "";
            LayoutGrid();
            gridIdx = 0;
            Refresh();
            _ = LoadPostersAsync(ct);
        }

        string BuildUrl()
        {
            var sort = SortValues[sortIdx];
            if (mediaType == "tv" && sort.StartsWith("primary_release_date"))
                sort = sort.Replace("primary_release_date", "first_air_date");
            var path = $"/discover/{mediaType}?sort_by={sort}";
            if (genreId != null) path += $"&with_genres={genreId}";
            if (language != null) path += $"&with_original_language={language}";
            if (sort.StartsWith("vote_average") && (extra == null || !extra.Contains("vote_count.gte")))
                path += "&vote_count.gte=200";
            if (extra != null) path += $"&{extra}";
            var (gte, lte) = TimeRange();
            if (gte != null)
            {
                var dp = mediaType == "tv" ? "first_air_date" : "primary_release_date";
                path += $"&{dp}.gte={gte}";
                if (lte != null) path += $"&{dp}.lte={lte}";
            }
            return path;
        }

        (string gte, string lte) TimeRange()
        {
            var now = DateTime.UtcNow;
            switch (timeIdx)
            {
                case 1: return (now.AddDays(-7).ToString("yyyy-MM-dd"), now.ToString("yyyy-MM-dd"));
                case 2: return (now.AddDays(-30).ToString("yyyy-MM-dd"), now.ToString("yyyy-MM-dd"));
                case 3: return ($"{now.Year}-01-01", now.ToString("yyyy-MM-dd"));
                case 4: return ($"{now.Year - 1}-01-01", $"{now.Year - 1}-12-31");
                default: return (null, null);
            }
        }

        void ClearGrid()
        {
            foreach (var c in cards) { grid.Remove(c); c.Dispose(); }
            cards.Clear();
        }

        void LayoutGrid()
        {
            ClearGrid();
            for (int i = 0; i < titles.Count; i++)
            {
                var cert = titles[i].Certification ?? App.Tmdb.GetCachedCert(titles[i].TmdbId);
                var card = Theme.MakeCard(titles[i], CardW, CardH, 0, -1, cert, 70);
                int r = i / cols, c = i % cols;
                card.Position = new Position(c * (CardW + CardGap), r * GridRowH);
                grid.Add(card);
                cards.Add(card);
            }
        }

        async Task LoadPostersAsync(CancellationToken ct)
        {
            // Pass 1: set already-cached posters instantly
            for (int i = 0; i < titles.Count; i++)
            {
                var t = titles[i];
                if (string.IsNullOrEmpty(t.PosterUrl)) continue;
                var cached = App.Cache.GetPoster(t.PosterUrl);
                if (cached != null && i < cards.Count)
                    Theme.SetCardPoster(cards[i], cached, CardW, CardH);
            }

            // Pass 2: download missing posters in parallel batches of 6
            var missing = new List<(int idx, Title t)>();
            for (int i = 0; i < titles.Count; i++)
            {
                var t = titles[i];
                if (!string.IsNullOrEmpty(t.PosterUrl) && App.Cache.GetPoster(t.PosterUrl) != null) continue;
                missing.Add((i, t));
            }

            int failed = 0;
            const int batch = 6;
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
                        if (localPath != null && item.idx < cards.Count)
                            App.Post(() => Theme.SetCardPoster(cards[item.idx], localPath, CardW, CardH));
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref failed);
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
                byte[] data;
                if (new Uri(url).Host.Contains("tmdb.org"))
                    data = await imgHttp.GetByteArrayAsync(url).ConfigureAwait(false);
                else
                    data = await DohResolver.FetchBytesAsync(url, ct).ConfigureAwait(false);
                if (data.Length < 100 || data.Length > 500_000) return null;
                return App.Cache.PutPoster(url, data);
            }
            catch { return null; }
        }

        void Refresh()
        {
            sortBox.BackgroundColor = focusArea == 0 ? Theme.FocusBg : Theme.Panel;
            timeBox.BackgroundColor = focusArea == 1 ? Theme.FocusBg : Theme.Panel;

            for (int i = 0; i < cards.Count; i++)
            {
                bool f = focusArea == 2 && i == gridIdx;
                var frame = cards[i].FindChildByName("frame");
                cards[i].Scale = f ? new Vector3(1.08f, 1.08f, 1f) : Vector3.One;
                frame.BackgroundColor = f ? Theme.Accent : Theme.Panel;
            }

            if (focusArea == 2 && cards.Count > 0)
            {
                int row = gridIdx / cols;
                int topVisible = (int)Math.Round(-grid.PositionY / GridRowH);
                if (row < topVisible) topVisible = row;
                if (row >= topVisible + visibleRows) topVisible = row - visibleRows + 1;
                if (topVisible < 0) topVisible = 0;
                grid.PositionY = -topVisible * GridRowH;
            }
        }

        public override bool OnKey(string key)
        {
            switch (focusArea)
            {
                case 0:
                    switch (key)
                    {
                        case "Right": focusArea = 1; Refresh(); return true;
                        case "Down":
                            focusArea = 2;
                            if (gridIdx < 0 || gridIdx >= cards.Count) gridIdx = 0;
                            Refresh(); return true;
                        case "Return": case "KP_Enter": case "Select":
                            sortIdx = (sortIdx + 1) % SortLabels.Length;
                            sortLabel.Text = $"Sort ▸ {SortLabels[sortIdx]}";
                            Reload(); return true;
                        case "XF86Back": case "Escape": return false;
                    }
                    return true;
                case 1:
                    switch (key)
                    {
                        case "Left": focusArea = 0; Refresh(); return true;
                        case "Down":
                            focusArea = 2;
                            if (gridIdx < 0 || gridIdx >= cards.Count) gridIdx = 0;
                            Refresh(); return true;
                        case "Return": case "KP_Enter": case "Select":
                            timeIdx = (timeIdx + 1) % TimeLabels.Length;
                            timeLabel.Text = $"Time ▸ {TimeLabels[timeIdx]}";
                            Reload(); return true;
                        case "XF86Back": case "Escape": return false;
                    }
                    return true;
                case 2:
                    if (cards.Count == 0)
                        return key != "XF86Back" && key != "Escape";
                    int col = gridIdx % cols;
                    switch (key)
                    {
                        case "Up":
                            if (gridIdx < cols) { focusArea = 0; Refresh(); return true; }
                            gridIdx -= cols; break;
                        case "Down":
                            if (gridIdx + cols < cards.Count) gridIdx += cols;
                            else gridIdx = cards.Count - 1;
                            break;
                        case "Left":
                            if (col > 0) gridIdx--;
                            break;
                        case "Right":
                            if (col < cols - 1 && gridIdx + 1 < cards.Count) gridIdx++;
                            break;
                        case "Return": case "KP_Enter": case "Select":
                            if (gridIdx < titles.Count)
                                App.Push(new DetailScreen(App, titles[gridIdx]));
                            return true;
                        case "XF86Back": case "Escape": return false;
                        default: return false;
                    }
                    Refresh();
                    return true;
            }
            return false;
        }
    }
}
