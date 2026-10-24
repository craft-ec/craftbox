using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
using CraftBox.Core;
using Timer = Tizen.NUI.Timer;
using Path = System.IO.Path;

namespace CraftBox.UI
{
    class App : NUIApplication
    {
        public Settings Settings { get; private set; }
        public TorznabClient Torznab { get; private set; }
        public EztvClient Eztv { get; private set; }
        public EztvCache EztvCache { get; private set; }
        public YtsClient Yts { get; private set; }
        public YtsCache YtsCache { get; private set; }
        public Tmdb Tmdb { get; private set; }
        public PirateBayClient PirateBay { get; private set; }
        public TorrentioClient Torrentio { get; private set; }
        public NyaaClient Nyaa { get; private set; }
        public AppCache Cache { get; private set; }
        public StreamEngine Engine { get; private set; }
        public ConfigServer Config { get; private set; }
        public AccountManager Accounts { get; private set; }
        public SyncClient Sync { get; private set; }
        public HttpClient Http { get; } = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        public event Action SettingsChanged;

        readonly List<Screen> stack = new List<Screen>();
        SynchronizationContext ui;
        string settingsPath;
        TextLabel toast;
        Timer toastTimer;

        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    var dirs = Tizen.Applications.Application.Current?.DirectoryInfo;
                    if (dirs != null)
                    {
                        var path = Path.Combine(dirs.Data, "debug.log");
                        File.AppendAllText(path, $"[{DateTime.UtcNow:HH:mm:ss}] FATAL UNHANDLED: {e.ExceptionObject}\n");
                    }
                }
                catch { }
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                e.SetObserved();
                try
                {
                    var dirs = Tizen.Applications.Application.Current?.DirectoryInfo;
                    if (dirs != null)
                    {
                        var path = Path.Combine(dirs.Data, "debug.log");
                        File.AppendAllText(path, $"[{DateTime.UtcNow:HH:mm:ss}] UNOBSERVED_TASK: {e.Exception}\n");
                    }
                }
                catch { }
            };
            new App().Run(args);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            ui = SynchronizationContext.Current;
            try
            {
                var dirs = Tizen.Applications.Application.Current.DirectoryInfo;
                settingsPath = Path.Combine(dirs.Data, "settings.json");
                Settings = Settings.Load(settingsPath);
                Torznab = new TorznabClient(Http);
                Eztv = new EztvClient(Http);
                EztvCache = new EztvCache(Eztv, dirs.Data);
                Yts = new YtsClient(Http);
                YtsCache = new YtsCache(Yts, dirs.Data);
                Tmdb = new Tmdb(Http, Settings.TmdbKey, dirs.Data);
                StreamProvider.Configure(Settings.VidLinkKey, Settings.VidFastDigest);
                PirateBay = new PirateBayClient(Http);
                Torrentio = new TorrentioClient();
                Nyaa = new NyaaClient();
                Cache = new AppCache(dirs.Data);
                if (Cache.GetText("_catalog_v") != "3")
                {
                    Cache.ClearAllCatalogs();
                    Cache.PutText("_catalog_v", "3");
                }
                Accounts = new AccountManager(dirs.Data);
                Sync = new SyncClient(Http, Settings.SyncUrl, Settings.SyncToken);
                Sync.Log = msg => Log(msg);

                var win = Window.Instance;
                win.BackgroundColor = Theme.Bg;
                win.KeyEvent += OnKeyEvent;

                StreamEngine.Log = msg => Log(msg);
                Config = new ConfigServer(8099, () => Settings, s => Post(() => ApplySettings(s)), Http);
                Config.GetEngineStatus = () => Engine?.StatusReport() ?? "Engine: not initialized";
                Config.TestPlayFn = async (magnet, ct) =>
                {
                    if (Engine == null) return "Engine not initialized";
                    return await Engine.TestAsync(magnet, ct).ConfigureAwait(false);
                };
                try { Config.Start(); }
                catch (Exception e) { Tizen.Log.Error("CraftBox", "config server: " + e.Message); }

                if (!string.IsNullOrEmpty(Settings.ProwlarrUrl))
                    _ = RefreshProwlarrAsync();

                if (Tmdb.Enabled)
                {
                    var t = new Timer(100);
                    t.Tick += (s, e) => { _ = Tmdb.PreWarmCatalogAsync(Cache); return false; };
                    t.Start();
                }

                Push(new AccountScreen(this));
                _ = InitEngineAsync(Path.Combine(dirs.Cache, "torrent"));
            }
            catch (Exception e)
            {
                Log("FATAL OnCreate: " + e);
            }
        }

        protected override void OnLowMemory(Tizen.Applications.LowMemoryEventArgs e)
        {
            base.OnLowMemory(e);
            Log($"LOW_MEMORY status={e.LowMemoryStatus}");
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        async Task InitEngineAsync(string root)
        {
            try { Engine = await StreamEngine.CreateAsync(root); }
            catch (Exception e)
            {
                Log("Engine init failed: " + e);
                Toast("Torrent engine failed to start: " + e.Message);
            }
        }

        internal void Log(string msg)
        {
            try
            {
                var path = Path.Combine(Tizen.Applications.Application.Current.DirectoryInfo.Data, "debug.log");
                File.AppendAllText(path, $"[{DateTime.UtcNow:HH:mm:ss}] {msg}\n");
                if (++logLines % 500 == 0)
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        var lines = File.ReadAllLines(path);
                        File.WriteAllLines(path, lines.Skip(lines.Length / 2));
                    }
                }
            }
            catch { }
        }
        int logLines;

        public void ApplySettings(Settings s)
        {
            Settings = s;
            Settings.Save(settingsPath);
            Tmdb = new Tmdb(Http, s.TmdbKey, Path.GetDirectoryName(settingsPath));
            SettingsChanged?.Invoke();
        }

        async Task RefreshProwlarrAsync()
        {
            try
            {
                var indexers = await Prowlarr.DiscoverAsync(Settings.ProwlarrUrl, Settings.ProwlarrApiKey).ConfigureAwait(false);
                Post(() =>
                {
                    Settings.Indexers = indexers;
                    Settings.Save(settingsPath);
                    SettingsChanged?.Invoke();
                });
            }
            catch (Exception e) { Tizen.Log.Error("CraftBox", "prowlarr: " + e.Message); }
        }

        public void SelectAccount(Account account)
        {
            Accounts.Active = account;
            Cache.SetAccountId(account.Id);
            Tmdb.AgeLimit = account.Age > 0 ? account.Age : 0;
            Log($"ACCOUNT selected: {account.Name} (id={account.Id}, age={account.Age}, ageLimit={Tmdb.AgeLimit})");
            _ = Task.Run(async () =>
            {
                try { await Sync.SyncAccountAsync(Cache, account.Id).ConfigureAwait(false); }
                catch (Exception e) { Log($"SYNC error: {e.Message}"); }
            });
            Push(new HomeScreen(this));
        }

        public void SwitchAccount()
        {
            while (stack.Count > 1)
            {
                var top = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                Window.Instance.Remove(top);
                top.OnClosed();
                top.Dispose();
            }
            var picker = stack[0];
            picker.Show();
            picker.OnShown();
        }

        /// Run on the UI thread (for callbacks from the player, HTTP server, engine).
        public void Post(Action a) => ui.Post(_ => a(), null);

        public void Push(Screen s)
        {
            if (stack.Count > 0) stack[stack.Count - 1].Hide();
            stack.Add(s);
            Window.Instance.Add(s);
            s.OnShown();
            RaiseToast();
        }

        public void Pop()
        {
            if (stack.Count <= 1) { Exit(); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var top = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            Window.Instance.Remove(top);
            var t0 = sw.ElapsedMilliseconds;
            top.OnClosed();
            var t1 = sw.ElapsedMilliseconds;
            var under = stack[stack.Count - 1];
            under.Show();
            var t2 = sw.ElapsedMilliseconds;
            under.OnShown();
            var t3 = sw.ElapsedMilliseconds;
            RaiseToast();
            Log($"POP remove={t0}ms OnClosed={t1-t0}ms Show={t2-t1}ms OnShown={t3-t2}ms total={t3}ms");
            var t = new Timer(50);
            t.Tick += (s, e) => { var d = sw.ElapsedMilliseconds; top.Dispose(); Log($"POP dispose={sw.ElapsedMilliseconds-d}ms"); return false; };
            t.Start();
        }

        public void Toast(string text, int ms = 4000)
        {
            if (toast == null)
            {
                toast = Theme.Label("", 28, Theme.Text, 160, Theme.H - 140, Theme.W - 320, 80, true);
                toast.BackgroundColor = new Color(0.17f, 0.21f, 0.25f, 0.95f);
                toast.HorizontalAlignment = HorizontalAlignment.Center;
                Window.Instance.Add(toast);
            }
            toast.Text = text;
            toast.Show();
            RaiseToast();
            toastTimer?.Stop();
            toastTimer = new Timer((uint)ms);
            toastTimer.Tick += (s, e) => { toast.Hide(); return false; };
            toastTimer.Start();
        }

        void RaiseToast() => toast?.RaiseToTop();

        void OnKeyEvent(object sender, Window.KeyEventArgs e)
        {
            if (e.Key.State != Key.StateType.Down || stack.Count == 0) return;
            var key = e.Key.KeyPressedName;
            var top = stack[stack.Count - 1];
            if (top.OnKey(key)) return;
            if (key == "XF86Back" || key == "Escape" || key == "BackSpace") Pop();
        }

        protected override void OnTerminate()
        {
            foreach (var s in stack) s.OnClosed();
            Cache?.FlushIndex();
            if (Accounts?.Active != null)
            {
                try { Sync?.SyncAccountAsync(Cache, Accounts.Active.Id).GetAwaiter().GetResult(); }
                catch { }
            }
            Config?.Dispose();
            Engine?.Dispose();
            base.OnTerminate();
        }
    }
}
