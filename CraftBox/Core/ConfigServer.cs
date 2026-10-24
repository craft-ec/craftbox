using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CraftBox.Core
{
    /// Settings page served from the TV, so URLs and API keys can be typed on a phone
    /// instead of the remote: open http://<tv-ip>:8099/ on the same network.
    public class ConfigServer : IDisposable
    {
        public const int MaxIndexers = 4;
        readonly HttpListener listener = new HttpListener();
        readonly Func<Settings> get;
        readonly Action<Settings> save;
        public int Port { get; }
        public Func<string> GetEngineStatus { get; set; }
        public Func<string, CancellationToken, Task<string>> TestPlayFn { get; set; }

        public ConfigServer(int port, Func<Settings> get, Action<Settings> save, HttpClient http = null)
        {
            Port = port;
            this.get = get;
            this.save = save;
            listener.Prefixes.Add($"http://*:{port}/");
        }

        public void Start()
        {
            listener.Start();
            _ = Task.Run(Loop);
        }

        /// Addresses a phone on the LAN can use to reach this page.
        public IEnumerable<string> Urls()
        {
            IEnumerable<IPAddress> addrs;
            try
            {
                addrs = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            }
            catch (Exception) { addrs = Enumerable.Empty<IPAddress>(); }
            return addrs.Select(a => $"http://{a}:{Port}/").ToList();
        }

        async Task Loop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                try { await Handle(ctx).ConfigureAwait(false); }
                catch (Exception) { try { ctx.Response.StatusCode = 500; } catch (Exception) { } }
                finally { try { ctx.Response.Close(); } catch (Exception) { } }
            }
        }

        async Task Handle(HttpListenerContext ctx)
        {
            if (ctx.Request.Url.AbsolutePath == "/debug")
            {
                await HandleDebug(ctx).ConfigureAwait(false);
                return;
            }
            if (ctx.Request.Url.AbsolutePath == "/test-play")
            {
                await HandleTestPlay(ctx).ConfigureAwait(false);
                return;
            }
            if (ctx.Request.Url.AbsolutePath == "/log")
            {
                await HandleLog(ctx).ConfigureAwait(false);
                return;
            }
            var saved = false;
            string prowlarrStatus = null;
            if (ctx.Request.HttpMethod == "POST")
            {
                string body;
                using (var r = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await r.ReadToEndAsync().ConfigureAwait(false);
                var s = FromForm(ParseForm(body), get());
                if (!string.IsNullOrEmpty(s.ProwlarrUrl))
                {
                    try
                    {
                        var indexers = await Prowlarr.DiscoverAsync(s.ProwlarrUrl, s.ProwlarrApiKey).ConfigureAwait(false);
                        s.Indexers = indexers;
                        prowlarrStatus = $"Connected — {indexers.Count} indexer{(indexers.Count == 1 ? "" : "s")} found";
                    }
                    catch (Exception e)
                    {
                        prowlarrStatus = "Error: " + e.Message;
                    }
                }
                save(s);
                saved = true;
            }
            else if (!string.IsNullOrEmpty(get().ProwlarrUrl) && get().Indexers.Count > 0)
            {
                prowlarrStatus = $"{get().Indexers.Count} indexer{(get().Indexers.Count == 1 ? "" : "s")} configured";
            }
            var html = Encoding.UTF8.GetBytes(Page(get(), saved, prowlarrStatus));
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = html.Length;
            await ctx.Response.OutputStream.WriteAsync(html, 0, html.Length).ConfigureAwait(false);
        }

        internal static Dictionary<string, string> ParseForm(string body)
        {
            var d = new Dictionary<string, string>();
            foreach (var pair in body.Split('&'))
            {
                if (pair.Length == 0) continue;
                var i = pair.IndexOf('=');
                var k = WebUtility.UrlDecode(i < 0 ? pair : pair.Substring(0, i));
                var v = i < 0 ? "" : WebUtility.UrlDecode(pair.Substring(i + 1));
                d[k] = v.Trim();
            }
            return d;
        }

        internal static Settings FromForm(Dictionary<string, string> f, Settings current)
        {
            string F(string k) => f.TryGetValue(k, out var v) ? v : "";
            var s = new Settings
            {
                ProwlarrUrl = F("prowlarr_url"),
                ProwlarrApiKey = F("prowlarr_key"),
                EztvUrl = F("eztv_url"),
                TmdbKey = F("tmdb"),
                BufferLimitMb = current.BufferLimitMb,
            };
            if (int.TryParse(F("buffer"), out var mb) && mb >= 200) s.BufferLimitMb = mb;
            if (string.IsNullOrEmpty(s.ProwlarrUrl))
            {
                for (int i = 0; i < MaxIndexers; i++)
                {
                    var url = F($"url{i}");
                    if (url.Length == 0) continue;
                    var name = F($"name{i}");
                    s.Indexers.Add(new IndexerConfig { Name = name.Length > 0 ? name : $"Indexer {i + 1}", Url = url, ApiKey = F($"key{i}") });
                }
            }
            return s;
        }

        static string E(string s) => WebUtility.HtmlEncode(s ?? "");

        static string Page(Settings s, bool saved, string prowlarrStatus = null)
        {
            var sb = new StringBuilder();
            sb.Append(@"<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'>
<title>TV Torrent settings</title><style>
body{font:16px system-ui,sans-serif;margin:0;padding:16px;background:#101418;color:#e8edf2;max-width:640px}
h1{font-size:22px;color:#6fd3ff}fieldset{border:1px solid #2a333c;border-radius:8px;margin:0 0 14px;padding:10px 12px}
label{display:block;font-size:13px;color:#8a96a3;margin-top:8px}input{width:100%;box-sizing:border-box;padding:10px;font-size:16px;
border-radius:6px;border:1px solid #2a333c;background:#1b2128;color:#fff}button{padding:12px 24px;font-size:16px;border:0;border-radius:8px;
background:#6fd3ff;color:#001}.ok{background:#1d3b2a;padding:10px;border-radius:6px}.err{background:#3b1d1d;padding:10px;border-radius:6px}
.status{font-size:14px;margin-top:6px}p{color:#8a96a3;font-size:14px}
details{margin-top:14px}summary{cursor:pointer;color:#8a96a3;font-size:14px}</style></head><body>
<h1>TV Torrent settings</h1>");
            if (saved) sb.Append("<p class=ok>Saved. The TV reloads its catalog now.</p>");
            sb.Append(@"<form method=post>");
            var hasProwlarr = !string.IsNullOrEmpty(s.ProwlarrUrl);
            sb.Append($@"<fieldset><legend>Prowlarr</legend>
<p style='margin:0 0 4px;font-size:14px'>Enter your Prowlarr server URL. Indexers are discovered automatically.</p>
<label>Prowlarr URL</label><input name=prowlarr_url value=""{E(s.ProwlarrUrl)}"" placeholder=""http://192.168.1.x:9696"" inputmode=url autocapitalize=off>
<label>API key (if authentication is enabled)</label><input name=prowlarr_key value=""{E(s.ProwlarrApiKey)}"" autocapitalize=off>");
            if (prowlarrStatus != null)
            {
                var cls = prowlarrStatus.StartsWith("Error") ? "err" : "ok";
                sb.Append($@"<p class='status {cls}'>{E(prowlarrStatus)}</p>");
            }
            if (hasProwlarr && s.Indexers.Count > 0)
            {
                sb.Append("<p class=status>Indexers: ");
                sb.Append(E(string.Join(", ", s.Indexers.ConvertAll(ix => ix.Name))));
                sb.Append("</p>");
            }
            sb.Append("</fieldset>");
            sb.Append($@"<fieldset><legend>EZTV</legend>
<p style='margin:0 0 4px;font-size:14px'>Enter the EZTV API URL to browse TV shows directly.</p>
<label>EZTV API URL</label><input name=eztv_url value=""{E(s.EztvUrl)}"" placeholder=""https://eztvx.to/api/get-torrents"" inputmode=url autocapitalize=off>
</fieldset>");
            sb.Append(@"<details" + (hasProwlarr ? "" : " open") + @"><summary>Manual indexers (without Prowlarr)</summary>");
            sb.Append(@"<p>Or add Torznab feeds directly, e.g. Jackett: <i>http://server:9117/api/v2.0/indexers/all/results/torznab/api</i>.</p>");
            var manualIndexers = hasProwlarr ? new List<IndexerConfig>() : s.Indexers;
            for (int i = 0; i < MaxIndexers; i++)
            {
                var ix = i < manualIndexers.Count ? manualIndexers[i] : new IndexerConfig();
                sb.Append($@"<fieldset><legend>Indexer {i + 1}</legend>
<label>Name</label><input name=name{i} value=""{E(ix.Name)}"">
<label>Torznab URL</label><input name=url{i} value=""{E(ix.Url)}"" inputmode=url autocapitalize=off>
<label>API key</label><input name=key{i} value=""{E(ix.ApiKey)}"" autocapitalize=off></fieldset>");
            }
            sb.Append("</details>");
            sb.Append($@"<fieldset><legend>Posters (optional)</legend><label>TMDB API key or read token</label>
<input name=tmdb value=""{E(s.TmdbKey)}"" autocapitalize=off></fieldset>
<fieldset><legend>Playback</legend><label>Temporary buffer limit on the TV (MB)</label>
<input name=buffer value=""{s.BufferLimitMb}"" inputmode=numeric></fieldset>
<button>Save</button></form></body></html>");
            return sb.ToString();
        }

        async Task HandleDebug(HttpListenerContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== TV Torrent Debug ===\n");
            var s = get();
            sb.AppendLine($"EztvUrl: {s.EztvUrl}");
            sb.AppendLine($"YtsUrl: {s.YtsUrl}");
            sb.AppendLine($"TmdbKey: {(string.IsNullOrEmpty(s.TmdbKey) ? "(empty)" : s.TmdbKey.Substring(0, Math.Min(8, s.TmdbKey.Length)) + "...")}");
            sb.AppendLine();

            var dirs = Tizen.Applications.Application.Current.DirectoryInfo;
            sb.AppendLine($"DataDir: {dirs.Data}");
            sb.AppendLine($"CacheDir: {dirs.Cache}");
            sb.AppendLine();

            // Test file write — this is the critical diagnostic
            var testPath = System.IO.Path.Combine(dirs.Data, "write_test.txt");
            try
            {
                System.IO.File.WriteAllText(testPath, "write test " + DateTime.UtcNow.ToString("o"));
                sb.AppendLine($"FILE WRITE TEST: OK (wrote {testPath})");
                try { System.IO.File.Delete(testPath); } catch { }
            }
            catch (Exception e)
            {
                sb.AppendLine($"FILE WRITE TEST: FAILED");
                sb.AppendLine($"  Exception: {e.GetType().FullName}: {e.Message}");
                if (e.InnerException != null)
                    sb.AppendLine($"  Inner: {e.InnerException.GetType().FullName}: {e.InnerException.Message}");
            }
            sb.AppendLine();

            // Test cache dir write
            var cacheTestPath = System.IO.Path.Combine(dirs.Cache, "write_test.txt");
            try
            {
                System.IO.Directory.CreateDirectory(dirs.Cache);
                System.IO.File.WriteAllText(cacheTestPath, "cache write test");
                sb.AppendLine($"CACHE WRITE TEST: OK (wrote {cacheTestPath})");
                try { System.IO.File.Delete(cacheTestPath); } catch { }
            }
            catch (Exception e)
            {
                sb.AppendLine($"CACHE WRITE TEST: FAILED");
                sb.AppendLine($"  Exception: {e.GetType().FullName}: {e.Message}");
                if (e.InnerException != null)
                    sb.AppendLine($"  Inner: {e.InnerException.GetType().FullName}: {e.InnerException.Message}");
            }
            sb.AppendLine();

            // Test creating a subdirectory in cache (use a test subdir, not the engine's)
            var subDir = System.IO.Path.Combine(dirs.Cache, "_writetest");
            try
            {
                System.IO.Directory.CreateDirectory(subDir);
                var subFile = System.IO.Path.Combine(subDir, "test.bin");
                System.IO.File.WriteAllBytes(subFile, new byte[] { 1, 2, 3 });
                sb.AppendLine($"SUBDIR CREATE+WRITE TEST: OK");
                try { System.IO.Directory.Delete(subDir, true); } catch { }
            }
            catch (Exception e)
            {
                sb.AppendLine($"SUBDIR CREATE+WRITE TEST: FAILED: {e.GetType().Name}: {e.Message}");
            }
            sb.AppendLine();

            // List data dir contents
            try
            {
                var files = System.IO.Directory.GetFileSystemEntries(dirs.Data);
                sb.AppendLine($"Data dir contents ({files.Length} entries):");
                foreach (var f in files)
                    sb.AppendLine($"  {System.IO.Path.GetFileName(f)}");
            }
            catch (Exception e) { sb.AppendLine($"Data dir listing: {e.Message}"); }
            sb.AppendLine();

            // List cache dir contents
            try
            {
                var files = System.IO.Directory.GetFileSystemEntries(dirs.Cache);
                sb.AppendLine($"Cache dir contents ({files.Length} entries):");
                foreach (var f in files)
                    sb.AppendLine($"  {System.IO.Path.GetFileName(f)}");
            }
            catch (Exception e) { sb.AppendLine($"Cache dir listing: {e.Message}"); }
            sb.AppendLine();

            // Test DOH resolve
            foreach (var host in new[] { "eztvx.to", "yts.rs", "dns.google" })
            {
                try
                {
                    var ip = await DohResolver.ResolveAsync(host).ConfigureAwait(false);
                    sb.AppendLine($"DOH {host} -> {ip ?? "null"}");
                }
                catch (Exception e) { sb.AppendLine($"DOH {host} -> ERROR: {e.GetType().Name}: {e.Message}"); }
            }
            sb.AppendLine();

            // Test DOH fetch
            if (!string.IsNullOrEmpty(s.EztvUrl))
            {
                try
                {
                    var body = await DohResolver.FetchAsync(s.EztvUrl + "?limit=1", default).ConfigureAwait(false);
                    sb.AppendLine($"EZTV fetch: OK ({body.Length} bytes)");
                }
                catch (Exception e) { sb.AppendLine($"EZTV fetch: ERROR: {e.GetType().Name}: {e.Message}"); }
            }

            if (!string.IsNullOrEmpty(s.YtsUrl))
            {
                try
                {
                    var body = await DohResolver.FetchAsync(s.YtsUrl + "?limit=1", default).ConfigureAwait(false);
                    sb.AppendLine($"YTS fetch: OK ({body.Length} bytes)");
                }
                catch (Exception e) { sb.AppendLine($"YTS fetch: ERROR: {e.GetType().Name}: {e.Message}"); }
            }
            sb.AppendLine();

            // Engine status
            try
            {
                var es = GetEngineStatus?.Invoke();
                if (!string.IsNullOrEmpty(es))
                    sb.AppendLine(es);
            }
            catch (Exception e) { sb.AppendLine($"Engine status error: {e.Message}"); }
            sb.AppendLine();

            // Show log file if exists
            try
            {
                var logPath = System.IO.Path.Combine(dirs.Data, "debug.log");
                if (System.IO.File.Exists(logPath))
                {
                    sb.AppendLine("=== debug.log ===");
                    sb.AppendLine(System.IO.File.ReadAllText(logPath));
                }
                else
                {
                    sb.AppendLine("No debug.log found.");
                }
            }
            catch (Exception e) { sb.AppendLine($"Log read error: {e.Message}"); }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        async Task HandleTestPlay(HttpListenerContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Test Play ===\n");

            if (TestPlayFn == null)
            {
                sb.AppendLine("ERROR: Engine not ready (TestPlayFn not set)");
                await WriteText(ctx, sb.ToString()).ConfigureAwait(false);
                return;
            }

            // Check for a .torrent file in data dir first (pushed for testing), then magnet from query, then YTS
            var qs = ctx.Request.QueryString;
            var magnet = qs["magnet"];
            if (!string.IsNullOrEmpty(magnet) && !magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                magnet = null;
            var torrentPath = System.IO.Path.Combine(
                Tizen.Applications.Application.Current.DirectoryInfo.Data, "test.torrent");
            if (System.IO.File.Exists(torrentPath))
            {
                sb.AppendLine($"Found test.torrent ({new System.IO.FileInfo(torrentPath).Length} bytes), using file...");
                magnet = "file:" + torrentPath;
            }
            else if (string.IsNullOrEmpty(magnet))
            {
                sb.AppendLine("Fetching a well-seeded movie from YTS...");
                try
                {
                    var s = get();
                    var ytsUrl = !string.IsNullOrEmpty(s.YtsUrl) ? s.YtsUrl : "https://yts.rs/api/v2/list_movies.json";
                    var json = await DohResolver.FetchAsync(ytsUrl + "?sort_by=date_added&limit=5", default).ConfigureAwait(false);
                    var releases = YtsClient.Parse(json);
                    var best = releases.Count > 0 ? releases.OrderByDescending(r => r.Seeders).First() : null;
                    if (best == null) { sb.AppendLine("No movies with torrents found from YTS"); await WriteText(ctx, sb.ToString()).ConfigureAwait(false); return; }
                    magnet = best.Link;
                    sb.AppendLine($"Using: {best.Title} ({best.Seeders} seeds)");
                    sb.AppendLine($"Magnet: {magnet.Substring(0, Math.Min(120, magnet.Length))}...\n");
                }
                catch (Exception e)
                {
                    sb.AppendLine($"YTS fetch failed: {e.GetType().Name}: {e.Message}");
                    await WriteText(ctx, sb.ToString()).ConfigureAwait(false);
                    return;
                }
            }

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300)))
            {
                try
                {
                    var result = await TestPlayFn(magnet, cts.Token).ConfigureAwait(false);
                    sb.Append(result);
                }
                catch (Exception e)
                {
                    sb.AppendLine($"Test error: {e.GetType().Name}: {e.Message}");
                }
            }

            await WriteText(ctx, sb.ToString()).ConfigureAwait(false);
        }

        async Task HandleLog(HttpListenerContext ctx)
        {
            var dirs = Tizen.Applications.Application.Current.DirectoryInfo;
            var logPath = System.IO.Path.Combine(dirs.Data, "debug.log");
            string text;
            if (System.IO.File.Exists(logPath))
            {
                text = System.IO.File.ReadAllText(logPath);
                if (ctx.Request.QueryString["clear"] == "1")
                    System.IO.File.WriteAllText(logPath, "");
            }
            else
            {
                text = "(no debug.log)";
            }
            await WriteText(ctx, text).ConfigureAwait(false);
        }

        static async Task WriteText(HttpListenerContext ctx, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        public void Dispose()
        {
            try { listener.Stop(); listener.Close(); } catch (Exception) { }
        }
    }
}
