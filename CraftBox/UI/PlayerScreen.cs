using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tizen.Multimedia;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using CraftBox.Core;
using Timer = Tizen.NUI.Timer;
using Display = Tizen.Multimedia.Display;
using Size = Tizen.NUI.Size;

namespace CraftBox.UI
{
    class PlayerScreen : Screen
    {
        readonly Release release;
        readonly int season, episode;
        readonly string name;
        readonly string imdbId;
        readonly string titleKey, posterUrl, backdropUrl;
        readonly int tmdbId;
        readonly TitleKind titleKind;
        readonly CancellationTokenSource cts = new CancellationTokenSource();

        // OSD
        readonly View osd;
        readonly TextLabel statusLabel, timeLabel, statsLabel;
        readonly View barBg, barBuf, barPlay;
        readonly View barCursor;
        readonly View[] buttons = new View[6];
        readonly TextLabel[] btnLabels = new TextLabel[6];
        static readonly string[] BtnText = { "◀◀ 1m", "◀ 10s", "▶❚❚", "10s ▶", "1m ▶▶", "CC" };

        Player player;
        StreamSession session;
        Timer tick;
        DateTime osdUntil;
        bool closed, started, osdVisible, focusOnBar;
        int focusIdx = 2;
        long pendingSeekMs = -1;
        long seekCursorMs = -1;
        int bufferingPct = 100;
        readonly long startPositionMs;
        long lastSaveUtcTicks;
        string streamUrl;

        // Smart failover
        readonly List<StreamResult> streamFallbacks;
        readonly List<Release> releaseFallbacks;
        List<string> sourceQueue = new List<string>();
        List<object> sourceObjects = new List<object>();
        int currentSourceIdx = -1;
        DateTime bufferStallStart;
        bool switchingSources;
        const int StallTimeoutSec = 20;

        // Subtitles
        string subtitlePath;
        TextLabel subtitleLabel;
        List<SrtCue> cues;
        int lastCueIdx = -1;
        bool subtitleViaEvent;
        List<SubtitleEntry> availableSubs;
        int activeSubIdx = -1;

        const int OsdH = 280;
        const int BtnW = 140, BtnH = 56, BtnGap = 16;

        public PlayerScreen(App app, Release r, int season, int episode, string name, Title title = null, long startPositionMs = 0, string streamUrl = null,
            List<StreamResult> streamFallbacks = null, List<Release> releaseFallbacks = null) : base(app)
        {
            release = r;
            this.season = season;
            this.episode = episode;
            this.name = name;
            this.imdbId = title?.ImdbId;
            this.titleKey = title?.Key;
            this.posterUrl = title?.PosterUrl;
            this.backdropUrl = title?.BackdropUrl;
            this.tmdbId = title?.TmdbId ?? 0;
            this.titleKind = title?.Kind ?? TitleKind.Movie;
            this.startPositionMs = startPositionMs;
            this.streamUrl = streamUrl;
            this.streamFallbacks = streamFallbacks;
            this.releaseFallbacks = releaseFallbacks;
            BuildSourceQueue(streamUrl, r, streamFallbacks, releaseFallbacks);

            statusLabel = Theme.Label("Starting…", 36, Theme.Text, Theme.Margin, Theme.H / 2 - 60, Theme.W - 2 * Theme.Margin, 120, true);
            statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
            Add(statusLabel);

            osd = new View { Position = new Position(0, Theme.H - OsdH), Size = new Size(Theme.W, OsdH), BackgroundColor = new Color(0.04f, 0.04f, 0.04f, 0.88f) };

            // Title
            osd.Add(Theme.Label(name, 32, Theme.Text, Theme.Margin, 16, Theme.W - 2 * Theme.Margin, 44));

            // Progress bar
            float barW = Theme.W - 2 * Theme.Margin;
            barBg = Theme.Box(Theme.Margin, 76, barW, 10, new Color(0.25f, 0.25f, 0.25f, 1f));
            osd.Add(barBg);
            barBuf = Theme.Box(Theme.Margin, 76, 0, 10, new Color(0.45f, 0.45f, 0.45f, 1f));
            osd.Add(barBuf);
            barPlay = Theme.Box(Theme.Margin, 76, 0, 10, Theme.Accent);
            osd.Add(barPlay);
            barCursor = Theme.Box(Theme.Margin, 70, 4, 22, Theme.Text);
            osd.Add(barCursor);
            barCursor.Hide();

            // Time
            timeLabel = Theme.Label("", 28, Theme.Text, Theme.Margin, 100, 600, 36);
            osd.Add(timeLabel);

            // Stats
            statsLabel = Theme.Label("", 24, Theme.Muted, Theme.W - Theme.Margin - 500, 100, 500, 36);
            statsLabel.HorizontalAlignment = HorizontalAlignment.End;
            osd.Add(statsLabel);

            // Control buttons (6: ◀◀1m, ◀10s, ▶❚❚, 10s▶, 1m▶▶, CC)
            float totalBtnW = 6 * BtnW + 5 * BtnGap;
            float btnX = (Theme.W - totalBtnW) / 2f;
            float btnY = 156;
            for (int i = 0; i < 6; i++)
            {
                var btn = new View
                {
                    Position = new Position(btnX + i * (BtnW + BtnGap), btnY),
                    Size = new Size(BtnW, BtnH),
                    BackgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f),
                };
                var lbl = Theme.Label(BtnText[i], 26, Theme.Text, 0, 0, BtnW, BtnH);
                lbl.HorizontalAlignment = HorizontalAlignment.Center;
                btn.Add(lbl);
                osd.Add(btn);
                buttons[i] = btn;
                btnLabels[i] = lbl;
            }

            // Hint
            osd.Add(Theme.Label("▲▼ timeline/controls  ·  ◀▶ seek/navigate  ·  Enter: select  ·  Back: hide", 20, Theme.Muted,
                Theme.Margin, OsdH - 36, Theme.W - 2 * Theme.Margin, 28));

            Add(osd);
            osd.Hide();

            subtitleLabel = new TextLabel
            {
                Position = new Position(Theme.Margin + 100, Theme.H - OsdH - 80),
                Size = new Size(Theme.W - 2 * Theme.Margin - 200, 160),
                Text = "",
                PointSize = 34,
                TextColor = Color.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                MultiLine = true,
            };
            var outline = new PropertyMap();
            outline.Add("color", new PropertyValue(new Vector4(0, 0, 0, 1)));
            outline.Add("width", new PropertyValue(2f));
            subtitleLabel.Outline = outline;
            var shadow = new PropertyMap();
            shadow.Add("color", new PropertyValue(new Vector4(0, 0, 0, 0.9f)));
            shadow.Add("offset", new PropertyValue(new Vector2(2, 2)));
            shadow.Add("blurRadius", new PropertyValue(4f));
            subtitleLabel.Shadow = shadow;
            subtitleLabel.Hide();
            Add(subtitleLabel);
        }

        public override void OnShown()
        {
            if (started || closed) return;
            started = true;
            _ = StartAsync();
        }

        async Task StartAsync()
        {
            string playUri;

            if (!string.IsNullOrEmpty(streamUrl))
            {
                statusLabel.Text = "Connecting to stream…";
                playUri = streamUrl;
                App.Log($"Player: streaming HLS directly: {streamUrl}");
            }
            else
            {
                if (App.Engine == null)
                {
                    statusLabel.Text = "The torrent engine is still starting. Try again in a moment.";
                    return;
                }
                var progress = new Progress<string>(s => { if (!closed) statusLabel.Text = s; });
                try
                {
                    session = await App.Engine.OpenAsync(release.Link, season, episode,
                        progress, ChooseFileAsync, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!closed) App.Pop();
                    return;
                }
                catch (Exception e)
                {
                    App.Log($"Player open failed: {e}");
                    if (!closed) statusLabel.Text = "Could not start: " + e.Message;
                    return;
                }
                if (closed)
                {
                    _ = App.Engine.CloseAsync(session);
                    return;
                }
                playUri = session.Uri;
                statusLabel.Text = "Opening " + session.FileName + "…";
            }

            _ = FetchSubtitlesAsync();

            try
            {
                player = new Player();
                player.ErrorOccurred += (s, e) => App.Post(() =>
                {
                    if (closed || switchingSources) return;
                    if (TryNextSource())
                    {
                        App.Log($"Player: error on source {currentSourceIdx}, switching to next");
                        return;
                    }
                    Fail("Playback error: " + e.Error +
                        (release != null && release.Info.Dts ? " (DTS audio is not supported by this TV)" : ""));
                });
                player.BufferingProgressChanged += (s, e) => App.Post(() =>
                {
                    if (!closed)
                    {
                        bufferingPct = e.Percent;
                        if (e.Percent < 100 && !osdVisible) ShowOsd();
                        if (e.Percent < 100 && e.Percent < 20)
                        {
                            if (bufferStallStart == default) bufferStallStart = DateTime.UtcNow;
                            else if ((DateTime.UtcNow - bufferStallStart).TotalSeconds > StallTimeoutSec && !switchingSources)
                            {
                                App.Log($"Player: buffer stall detected ({e.Percent}% for {StallTimeoutSec}s), switching source");
                                TryNextSource();
                            }
                        }
                        else
                            bufferStallStart = default;
                    }
                });
                player.PlaybackCompleted += (s, e) => App.Post(() => { if (!closed) App.Pop(); });
                player.SetSource(new MediaUriSource(playUri));
                player.Display = new Display(Window.Instance);
                player.SubtitleUpdated += OnSubtitleUpdated;
                await player.PrepareAsync();
                if (subtitlePath != null)
                {
                    try { player.SetSubtitle(subtitlePath); App.Log("Subtitle loaded: " + subtitlePath); }
                    catch (Exception se) { App.Log("SetSubtitle failed: " + se.Message); }
                }
                if (closed) return;
                BackgroundColor = Color.Transparent;
                Window.Instance.BackgroundColor = Color.Transparent;
                player.Start();
                if (startPositionMs > 0)
                {
                    try { await player.SetPlayPositionAsync((int)startPositionMs, false); }
                    catch { }
                }
                statusLabel.Text = "";
                statusLabel.Hide();
                ShowOsd();
                tick = new Timer(500);
                tick.Tick += (s, e) => { UpdateOsd(); return !closed; };
                tick.Start();
            }
            catch (Exception e)
            {
                App.Log($"Player playback failed: {e}");
                Fail("Could not play this file: " + e.Message);
            }
        }

        Task<int> ChooseFileAsync(IList<string> files)
        {
            var tcs = new TaskCompletionSource<int>();
            App.Post(() => App.Push(new ChooserScreen(App, "Choose an episode", files, tcs)));
            return tcs.Task;
        }

        void Fail(string message)
        {
            if (closed) return;
            BackgroundColor = Theme.Bg;
            Window.Instance.BackgroundColor = Theme.Bg;
            statusLabel.Show();
            statusLabel.Text = message;
        }

        void ShowOsd()
        {
            osdVisible = true;
            osdUntil = DateTime.UtcNow.AddSeconds(8);
            osd.Show();
            subtitleLabel.RaiseToTop();
            RefreshBarFocus();
            UpdateOsd();
        }

        void RefreshBarFocus()
        {
            float h = focusOnBar ? 16f : 10f;
            barBg.SizeHeight = h;
            barBuf.SizeHeight = h;
            barPlay.SizeHeight = h;
            barBg.BackgroundColor = focusOnBar ? new Color(0.35f, 0.35f, 0.35f, 1f) : new Color(0.2f, 0.2f, 0.2f, 1f);
            RefreshButtons();
        }

        void HideOsd()
        {
            osdVisible = false;
            osd.Hide();
        }

        void RefreshButtons()
        {
            bool paused = player != null && player.State == PlayerState.Paused;
            btnLabels[2].Text = paused ? "▶ Play" : "❚❚ Pause";
            btnLabels[5].Text = activeSubIdx >= 0 ? "CC ●" : "CC";

            for (int i = 0; i < 6; i++)
            {
                if (focusOnBar)
                {
                    buttons[i].BackgroundColor = Theme.Panel;
                    btnLabels[i].TextColor = Theme.Muted;
                }
                else
                {
                    buttons[i].BackgroundColor = i == focusIdx ? Theme.Accent : Theme.Panel;
                    btnLabels[i].TextColor = i == focusIdx ? new Color(0, 0, 0, 1f) : Theme.Text;
                }
            }
        }

        void ActivateButton()
        {
            switch (focusIdx)
            {
                case 0: Seek(-60000); break;
                case 1: Seek(-10000); break;
                case 2: TogglePause(); break;
                case 3: Seek(10000); break;
                case 4: Seek(60000); break;
                case 5: ShowSubtitlePicker(); break;
            }
        }

        void TogglePause()
        {
            if (player == null) return;
            try
            {
                if (player.State == PlayerState.Playing) player.Pause();
                else if (player.State == PlayerState.Paused) player.Start();
            }
            catch (Exception) { }
            ShowOsd();
        }

        void UpdateOsd()
        {
            if (closed || player == null) return;

            if (osdVisible && !focusOnBar && DateTime.UtcNow > osdUntil && player.State == PlayerState.Playing)
                HideOsd();

            try
            {
                long dur = player.StreamInfo.GetDuration();
                long pos = pendingSeekMs >= 0 ? pendingSeekMs : player.GetPlayPosition();

                // Manual subtitle sync (fallback when SubtitleUpdated doesn't fire)
                if (cues != null && !subtitleViaEvent)
                {
                    long realPos = pendingSeekMs >= 0 ? pendingSeekMs : pos;
                    int idx = FindCue(realPos);
                    if (idx != lastCueIdx)
                    {
                        lastCueIdx = idx;
                        if (idx >= 0)
                        {
                            subtitleLabel.Text = cues[idx].Text;
                            subtitleLabel.Show();
                            subtitleLabel.RaiseToTop();
                        }
                        else
                            subtitleLabel.Hide();
                    }
                }

                if (!osdVisible) return;

                float barW = Theme.W - 2 * Theme.Margin;
                barPlay.SizeWidth = dur > 0 ? barW * (float)pos / dur : 0;

                bool paused = player.State == PlayerState.Paused;

                if (focusOnBar && seekCursorMs >= 0 && dur > 0)
                {
                    barCursor.Show();
                    barCursor.PositionX = Theme.Margin + barW * (float)seekCursorMs / dur - 2;
                    timeLabel.Text = $"Seek to {Theme.Time(seekCursorMs)}  ({Theme.Time(pos)} / {Theme.Time(dur)})";
                }
                else if (pendingSeekMs >= 0)
                    timeLabel.Text = $"Seeking to {Theme.Time(pendingSeekMs)}…";
                else if (bufferingPct < 100)
                    timeLabel.Text = $"Buffering {bufferingPct}%  ·  {Theme.Time(pos)} / {Theme.Time(dur)}";
                else
                    timeLabel.Text = $"{Theme.Time(pos)} / {Theme.Time(dur)}" + (paused ? "   ❚❚ PAUSED" : "");

                if (session != null)
                {
                    double playPct = dur > 0 ? 100.0 * pos / dur : 0;
                    double ahead = session.BufferAhead(playPct);
                    statsLabel.Text = $"{session.Peers} peers · {Theme.Bytes(session.DownloadRate)}/s · {session.FileProgress:0}% dl · {ahead:0}% ahead";
                    float bufPct2 = (float)session.FileProgress / 100f;
                    barBuf.SizeWidth = barW * bufPct2;
                }
                else
                {
                    statsLabel.Text = "Streaming";
                    barBuf.SizeWidth = barW;
                }

                if (pos > 5000 && !string.IsNullOrEmpty(titleKey) &&
                    DateTime.UtcNow.Ticks - lastSaveUtcTicks > TimeSpan.TicksPerSecond * 30)
                {
                    lastSaveUtcTicks = DateTime.UtcNow.Ticks;
                    try
                    {
                        App.Cache.RecordPlay(new AppCache.PlayRecord
                        {
                            TitleKey = titleKey, TitleName = name,
                            PosterUrl = posterUrl, BackdropUrl = backdropUrl,
                            ImdbId = imdbId, TmdbId = tmdbId, Kind = titleKind,
                            Season = season, Episode = episode,
                            ReleaseName = release?.Title, MagnetLink = release?.Link,
                            PositionMs = pos, DurationMs = dur,
                            PlayedUtc = DateTime.UtcNow,
                        });
                    }
                    catch { }
                }

                RefreshButtons();
            }
            catch (Exception) { }
        }

        int FindCue(long posMs)
        {
            if (cues == null) return -1;
            int lo = 0, hi = cues.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (posMs < cues[mid].StartMs) hi = mid - 1;
                else if (posMs > cues[mid].EndMs) lo = mid + 1;
                else return mid;
            }
            return -1;
        }

        async void Seek(long deltaMs)
        {
            if (player == null || (player.State != PlayerState.Playing && player.State != PlayerState.Paused)) return;
            try
            {
                long dur = player.StreamInfo.GetDuration();
                long from = pendingSeekMs >= 0 ? pendingSeekMs : player.GetPlayPosition();
                long target = Math.Max(0, Math.Min(dur > 0 ? dur - 1000 : long.MaxValue, from + deltaMs));
                pendingSeekMs = target;
                ShowOsd();
                await player.SetPlayPositionAsync((int)target, false);
            }
            catch (Exception) { }
            finally { pendingSeekMs = -1; }
        }

        public override bool OnKey(string key)
        {
            switch (key)
            {
                case "XF86AudioPlay": case "XF86AudioPause": case "XF86PlayBack":
                    TogglePause();
                    return true;
                case "XF86AudioRewind": Seek(-10000); return true;
                case "XF86AudioForward": Seek(10000); return true;
                case "XF86AudioStop": App.Pop(); return true;
            }

            if (!osdVisible)
            {
                switch (key)
                {
                    case "Return": case "KP_Enter": case "Select":
                    case "Left": case "Right": case "Up": case "Down":
                        focusIdx = 2;
                        focusOnBar = false;
                        ShowOsd();
                        return true;
                    case "XF86Back": case "Escape": case "BackSpace":
                        return false;
                }
                return true;
            }

            switch (key)
            {
                case "Up":
                    osdUntil = DateTime.UtcNow.AddSeconds(8);
                    if (!focusOnBar)
                    {
                        focusOnBar = true;
                        try { seekCursorMs = pendingSeekMs >= 0 ? pendingSeekMs : player?.GetPlayPosition() ?? 0; }
                        catch { seekCursorMs = 0; }
                        RefreshBarFocus();
                    }
                    return true;
                case "Down":
                    osdUntil = DateTime.UtcNow.AddSeconds(8);
                    if (focusOnBar) { focusOnBar = false; seekCursorMs = -1; barCursor.Hide(); RefreshBarFocus(); }
                    return true;
                case "Left":
                    osdUntil = DateTime.UtcNow.AddSeconds(8);
                    if (focusOnBar)
                    {
                        long durL = 0;
                        try { durL = player?.StreamInfo?.GetDuration() ?? 0; } catch { }
                        long stepL = Math.Max(10000, durL / 50);
                        seekCursorMs = Math.Max(0, seekCursorMs - stepL);
                    }
                    else { if (focusIdx > 0) focusIdx--; RefreshButtons(); }
                    return true;
                case "Right":
                    osdUntil = DateTime.UtcNow.AddSeconds(8);
                    if (focusOnBar)
                    {
                        long durR = 0;
                        try { durR = player?.StreamInfo?.GetDuration() ?? 0; } catch { }
                        long stepR = Math.Max(10000, durR / 50);
                        seekCursorMs = Math.Min(durR > 0 ? durR - 1000 : long.MaxValue, seekCursorMs + stepR);
                    }
                    else { if (focusIdx < 5) focusIdx++; RefreshButtons(); }
                    return true;
                case "Return": case "KP_Enter": case "Select":
                    if (focusOnBar && seekCursorMs >= 0)
                    {
                        long fromPos = 0;
                        try { fromPos = player?.GetPlayPosition() ?? 0; } catch { }
                        Seek(seekCursorMs - fromPos);
                        focusOnBar = false; seekCursorMs = -1; barCursor.Hide();
                        RefreshBarFocus();
                    }
                    else if (!focusOnBar)
                        ActivateButton();
                    return true;
                case "XF86Back": case "Escape": case "BackSpace":
                    if (focusOnBar) { focusOnBar = false; seekCursorMs = -1; barCursor.Hide(); RefreshBarFocus(); }
                    else HideOsd();
                    return true;
            }
            return true;
        }

        // --- Subtitle management ---

        async Task FetchSubtitlesAsync()
        {
            if (string.IsNullOrEmpty(imdbId))
            {
                App.Log("Subtitle: no IMDB ID, skipping");
                return;
            }
            try
            {
                var client = new OpenSubtitlesClient();
                List<SubtitleEntry> subs;
                if (season > 0 && episode > 0)
                    subs = await client.SearchEpisodeAsync(imdbId, season, episode, cts.Token);
                else
                    subs = await client.SearchMovieAsync(imdbId, cts.Token);

                App.Log($"Subtitles: {subs.Count} English found for {imdbId} (SDH: {subs.Count(s => s.HearingImpaired)})");
                if (subs.Count == 0) return;

                App.Post(() => { availableSubs = subs; });

                // Auto-select: match release format, then SDH, then first
                var pick = MatchSubtitleToRelease(subs);
                var idx = subs.IndexOf(pick);
                await ApplySubtitleAsync(client, subs, idx);
            }
            catch (Exception e)
            {
                App.Log("Subtitle fetch failed: " + e.Message);
            }
        }

        async Task ApplySubtitleAsync(OpenSubtitlesClient client, List<SubtitleEntry> subs, int idx)
        {
            if (idx < 0 || idx >= subs.Count) return;
            var entry = subs[idx];
            try
            {
                var path = await client.DownloadAsync(entry, App.Cache, cts.Token);
                if (path == null) return;

                subtitlePath = path;
                cues = SrtCue.Parse(System.IO.File.ReadAllText(path));
                lastCueIdx = -1;
                subtitleViaEvent = false;
                App.Log($"Subtitle applied: [{(entry.HearingImpaired ? "SDH" : "STD")}] {entry.DisplayName} ({cues.Count} cues)");

                App.Post(() =>
                {
                    if (closed) return;
                    activeSubIdx = idx;
                    if (player != null)
                    {
                        try { player.SetSubtitle(subtitlePath); }
                        catch (Exception e) { App.Log("SetSubtitle failed: " + e.Message); }
                    }
                });
            }
            catch (Exception e)
            {
                App.Log($"Subtitle download failed: {e.Message}");
            }
        }

        void ShowSubtitlePicker()
        {
            if (availableSubs == null || availableSubs.Count == 0)
            {
                App.Toast("No subtitles available");
                return;
            }

            var items = new List<string> { "Off — No subtitles" };
            for (int i = 0; i < availableSubs.Count; i++)
            {
                var s = availableSubs[i];
                var prefix = i == activeSubIdx ? "● " : "  ";
                items.Add(prefix + s.DisplayName);
            }

            var tcs = new TaskCompletionSource<int>();
            App.Push(new ChooserScreen(App, "Subtitles", items, tcs));
            _ = HandleSubtitleChoiceAsync(tcs);
        }

        async Task HandleSubtitleChoiceAsync(TaskCompletionSource<int> tcs)
        {
            int choice;
            try { choice = await tcs.Task; }
            catch { return; }
            if (choice < 0) return;

            if (choice == 0)
            {
                // Turn off subtitles
                cues = null;
                subtitlePath = null;
                activeSubIdx = -1;
                lastCueIdx = -1;
                subtitleViaEvent = false;
                subtitleLabel.Hide();
                App.Log("Subtitles turned off");
                return;
            }

            int subIdx = choice - 1;
            if (subIdx == activeSubIdx) return;

            var client = new OpenSubtitlesClient();
            await ApplySubtitleAsync(client, availableSubs, subIdx);
        }

        // ── Smart source failover ─────────────────────────────────────

        void BuildSourceQueue(string initialStream, Release initialRelease,
            List<StreamResult> streams, List<Release> releases)
        {
            var usedStreamUrls = new HashSet<string>();
            var usedMagnets = new HashSet<string>();

            // Current source is index 0
            if (!string.IsNullOrEmpty(initialStream))
            {
                sourceQueue.Add("stream");
                sourceObjects.Add(initialStream);
                usedStreamUrls.Add(initialStream);
                currentSourceIdx = 0;
            }
            else if (initialRelease != null)
            {
                sourceQueue.Add("torrent");
                sourceObjects.Add(initialRelease);
                if (!string.IsNullOrEmpty(initialRelease.Link))
                    usedMagnets.Add(initialRelease.Link);
                currentSourceIdx = 0;
            }

            // Add remaining torrents first (prefer WEB-DL 1080p, then WEB-DL any, then 1080p, then rest)
            if (releases != null)
            {
                var ordered = releases
                    .Where(r => r.Seeders > 0 && !r.Info.IsLowQuality && !r.Info.Dts)
                    .OrderByDescending(r => (r.Info.Source == "WEB" ? 100 : 0) + (r.Info.Quality == "1080p" ? 50 : 0) + Math.Min(r.Seeders, 50));
                foreach (var r in ordered)
                {
                    if (!string.IsNullOrEmpty(r.Link) && usedMagnets.Add(r.Link))
                    {
                        sourceQueue.Add("torrent");
                        sourceObjects.Add(r);
                    }
                }
            }

            // Streams last — fallback only
            if (streams != null)
            {
                var ordered = streams
                    .OrderByDescending(s => s.Quality == "1080p" ? 3 : s.Quality == "auto" ? 2 : s.Quality == "720p" ? 1 : 0);
                foreach (var s in ordered)
                {
                    if (usedStreamUrls.Add(s.Url))
                    {
                        sourceQueue.Add("stream");
                        sourceObjects.Add(s.Url);
                    }
                }
            }

            App.Log($"Player: source queue built with {sourceQueue.Count} sources (current={currentSourceIdx})");
        }

        bool TryNextSource()
        {
            if (switchingSources) return false;
            int nextIdx = currentSourceIdx + 1;
            if (nextIdx >= sourceQueue.Count) return false;

            switchingSources = true;
            bufferStallStart = default;
            currentSourceIdx = nextIdx;

            var type = sourceQueue[nextIdx];
            var obj = sourceObjects[nextIdx];
            string label = type == "stream" ? "stream" : ((Release)obj).Info.Quality + " " + ((Release)obj).Info.Source;
            App.Log($"Player: switching to source {nextIdx}: {type} ({label})");

            _ = SwitchSourceAsync(type, obj);
            return true;
        }

        async Task SwitchSourceAsync(string type, object source)
        {
            try
            {
                // Stop current playback
                var p = player;
                player = null;
                if (p != null)
                {
                    try { if (p.State == PlayerState.Playing || p.State == PlayerState.Paused) p.Stop(); } catch { }
                    try { p.Unprepare(); } catch { }
                    p.Dispose();
                }
                if (session != null)
                {
                    _ = App.Engine?.CloseAsync(session);
                    session = null;
                }

                string playUri;
                if (type == "stream")
                {
                    playUri = (string)source;
                    streamUrl = playUri;
                    App.Post(() =>
                    {
                        statusLabel.Text = "Switching to next stream…";
                        statusLabel.Show();
                    });
                }
                else
                {
                    var r = (Release)source;
                    if (App.Engine == null) { switchingSources = false; return; }
                    App.Post(() =>
                    {
                        statusLabel.Text = $"Switching to {r.Info.Quality} {r.Info.Source}…";
                        statusLabel.Show();
                    });
                    var progress = new Progress<string>(s => { if (!closed) App.Post(() => statusLabel.Text = s); });
                    session = await App.Engine.OpenAsync(r.Link, season, episode,
                        progress, ChooseFileAsync, cts.Token);
                    if (closed) return;
                    playUri = session.Uri;
                }

                player = new Player();
                player.ErrorOccurred += (s, e) => App.Post(() =>
                {
                    if (closed || switchingSources) return;
                    if (TryNextSource()) return;
                    Fail("Playback error: " + e.Error);
                });
                player.BufferingProgressChanged += (s, e) => App.Post(() =>
                {
                    if (!closed)
                    {
                        bufferingPct = e.Percent;
                        if (e.Percent < 100 && !osdVisible) ShowOsd();
                        if (e.Percent < 100 && e.Percent < 20)
                        {
                            if (bufferStallStart == default) bufferStallStart = DateTime.UtcNow;
                            else if ((DateTime.UtcNow - bufferStallStart).TotalSeconds > StallTimeoutSec && !switchingSources)
                                TryNextSource();
                        }
                        else
                            bufferStallStart = default;
                    }
                });
                player.PlaybackCompleted += (s, e) => App.Post(() => { if (!closed) App.Pop(); });
                player.SetSource(new MediaUriSource(playUri));
                player.Display = new Display(Window.Instance);
                player.SubtitleUpdated += OnSubtitleUpdated;
                await player.PrepareAsync();
                if (subtitlePath != null)
                {
                    try { player.SetSubtitle(subtitlePath); } catch { }
                }
                if (closed) return;
                BackgroundColor = Color.Transparent;
                player.Start();
                App.Post(() =>
                {
                    statusLabel.Text = "";
                    statusLabel.Hide();
                    ShowOsd();
                });
                App.Log($"Player: switched to source {currentSourceIdx} successfully");
            }
            catch (Exception e)
            {
                App.Log($"Player: switch failed: {e.Message}");
                if (!TryNextSource())
                    App.Post(() => Fail("All sources failed: " + e.Message));
            }
            finally
            {
                switchingSources = false;
            }
        }

        // ── Subtitle format matching ─────────────────────────────────────

        SubtitleEntry MatchSubtitleToRelease(List<SubtitleEntry> subs)
        {
            if (release == null || string.IsNullOrEmpty(release.Title))
                return subs.FirstOrDefault(s => s.HearingImpaired) ?? subs[0];

            var relInfo = release.Info ?? ReleaseInfo.Parse(release.Title);
            var relName = release.Title.ToLowerInvariant();

            // Try exact release name match first
            var exact = subs.FirstOrDefault(s =>
                !string.IsNullOrEmpty(s.ReleaseName) &&
                s.ReleaseName.ToLowerInvariant().Contains(relInfo.Title.ToLowerInvariant()) &&
                (relInfo.Source.Length == 0 || s.ReleaseName.ToUpperInvariant().Contains(relInfo.Source.ToUpperInvariant())));

            if (exact != null) return exact;

            // Try quality + source match
            var qualityMatch = subs.Where(s =>
            {
                if (string.IsNullOrEmpty(s.ReleaseName)) return false;
                var subUpper = s.ReleaseName.ToUpperInvariant();
                bool qMatch = relInfo.Quality.Length == 0 || subUpper.Contains(relInfo.Quality.ToUpperInvariant());
                bool sMatch = relInfo.Source.Length == 0 || subUpper.Contains(relInfo.Source == "WEB" ? "WEB" : relInfo.Source.ToUpperInvariant());
                return qMatch && sMatch;
            }).ToList();

            if (qualityMatch.Count > 0)
                return qualityMatch.FirstOrDefault(s => s.HearingImpaired) ?? qualityMatch[0];

            // Fallback: SDH or first
            return subs.FirstOrDefault(s => s.HearingImpaired) ?? subs[0];
        }

        void OnSubtitleUpdated(object sender, SubtitleUpdatedEventArgs e)
        {
            subtitleViaEvent = true;
            App.Post(() =>
            {
                if (closed) return;
                var text = e.Text?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    subtitleLabel.Hide();
                }
                else
                {
                    text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
                    subtitleLabel.Text = text;
                    subtitleLabel.Show();
                    subtitleLabel.RaiseToTop();
                }
            });
        }

        public override void OnClosed()
        {
            closed = true;
            cts.Cancel();
            tick?.Stop();
            Window.Instance.BackgroundColor = Theme.Bg;
            var p = player;
            player = null;
            long posMs = 0, durMs = 0;
            if (p != null)
            {
                try { posMs = p.GetPlayPosition(); } catch { }
                try { durMs = p.StreamInfo.GetDuration(); } catch { }
                try { if (p.State == PlayerState.Playing || p.State == PlayerState.Paused) p.Stop(); } catch (Exception) { }
                try { p.Unprepare(); } catch (Exception) { }
                p.Dispose();
            }
            if (session != null) _ = App.Engine?.CloseAsync(session);

            if (posMs > 5000 && !string.IsNullOrEmpty(titleKey))
            {
                try
                {
                    App.Cache.RecordPlay(new AppCache.PlayRecord
                    {
                        TitleKey = titleKey,
                        TitleName = name,
                        PosterUrl = posterUrl,
                        BackdropUrl = backdropUrl,
                        ImdbId = imdbId,
                        TmdbId = tmdbId,
                        Kind = titleKind,
                        Season = season,
                        Episode = episode,
                        ReleaseName = release?.Title,
                        MagnetLink = release?.Link ?? streamUrl,
                        PositionMs = posMs,
                        DurationMs = durMs,
                        PlayedUtc = DateTime.UtcNow,
                    });
                }
                catch { }
            }
        }
    }

    class ChooserScreen : Screen
    {
        readonly TaskCompletionSource<int> result;
        readonly FocusList list;

        public ChooserScreen(App app, string heading, IList<string> items, TaskCompletionSource<int> result) : base(app)
        {
            this.result = result;
            Add(Theme.Label(heading, 48, Theme.Accent, Theme.Margin, 50, Theme.W - 2 * Theme.Margin, 70));
            list = new FocusList(Theme.Margin, 160, Theme.W - 2 * Theme.Margin, Theme.H - 220);
            list.SetItems(System.Linq.Enumerable.Select(items, i => (i, (string)null, (Color)null)));
            list.Activated += i => { result.TrySetResult(i); App.Pop(); };
            Add(list);
        }

        public override bool OnKey(string key) => list.OnKey(key);

        public override void OnClosed() => result.TrySetResult(-1);
    }

    struct SrtCue
    {
        public long StartMs, EndMs;
        public string Text;

        static readonly System.Text.RegularExpressions.Regex TimeRx =
            new System.Text.RegularExpressions.Regex(@"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})");

        public static List<SrtCue> Parse(string srt)
        {
            var list = new List<SrtCue>();
            var blocks = srt.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var block in blocks)
            {
                var lines = block.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var m = TimeRx.Match(lines[i]);
                    if (!m.Success) continue;
                    long start = ParseMs(m, 1);
                    long end = ParseMs(m, 5);
                    var textLines = new List<string>();
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        var l = lines[j].Trim();
                        if (l.Length > 0)
                            textLines.Add(System.Text.RegularExpressions.Regex.Replace(l, "<[^>]+>", ""));
                    }
                    if (textLines.Count > 0)
                        list.Add(new SrtCue { StartMs = start, EndMs = end, Text = string.Join("\n", textLines) });
                    break;
                }
            }
            return list;
        }

        static long ParseMs(System.Text.RegularExpressions.Match m, int offset)
        {
            return long.Parse(m.Groups[offset].Value) * 3600000
                 + long.Parse(m.Groups[offset + 1].Value) * 60000
                 + long.Parse(m.Groups[offset + 2].Value) * 1000
                 + long.Parse(m.Groups[offset + 3].Value);
        }
    }
}
