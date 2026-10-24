using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.BEncoding;
using MonoTorrent.Logging;
using MonoTorrent.Streaming;

namespace CraftBox.Core
{
    public class StreamSession
    {
        public TorrentManager Manager;
        public string Dir;            // this session's own piece folder, deleted on close
        public ITorrentManagerFile File;
        public IHttpStream Http;
        public string Uri => Http.FullUri.ToString();
        public string FileName => Path.GetFileName(File.Path);
        public double FileProgress => File.BitField.PercentComplete;   // 0..100
        public long DownloadRate => Manager.Monitor.DownloadRate;      // bytes/s
        public int Peers => Manager.OpenConnections;

        /// Consecutive buffered pieces from the given playback percentage forward, as a % of total file.
        public double BufferAhead(double playPercent)
        {
            var bf = File.BitField;
            if (bf == null || bf.Length == 0) return 0;
            int total = bf.Length;
            int from = Math.Min(total - 1, Math.Max(0, (int)(total * playPercent / 100.0)));
            int ahead = 0;
            for (int i = from; i < total; i++)
            {
                if (bf[i]) ahead++;
                else break;
            }
            return 100.0 * ahead / total;
        }
    }

    /// Streams one torrent file at a time. Pieces land in a temporary folder that is
    /// wiped when the session closes and again on every start (in case the app was killed).
    public class StreamEngine : IDisposable
    {
        public static Action<string> Log;

        static readonly HashSet<string> VideoExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts", ".m2ts", ".webm", ".wmv" };

        readonly ClientEngine engine;
        readonly string root;
        readonly string dataDir;
        readonly HttpClient http;
        // Open/close run one at a time: a slow close must never overlap the next open.
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        StreamSession current;

        // Hardcoded DHT bootstrap IPs — the TV's router DNS-poisons torrent hostnames to 192.168.8.1.
        static readonly (string ip, int port)[] DhtBootstrapNodes =
        {
            ("67.215.246.10", 6881),    // router.bittorrent.com
            ("87.98.162.88", 6881),     // dht.transmissionbt.com (current)
            ("82.221.103.244", 6881),   // router.utorrent.com
            ("185.157.221.247", 25401), // dht.libtorrent.org
            ("185.230.8.64", 6881),     // router.bittorrent.com (alt)
            ("217.182.174.225", 6881),  // dht2.transmissionbt.com
        };

        static readonly string[] ExtraTrackers =
        {
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://explodie.org:6969/announce",
            "udp://exodus.desync.com:6969/announce",
            "udp://tracker.openbittorrent.com:6969/announce",
            "udp://open.demonii.com:1337/announce",
            "udp://tracker.moeking.me:6969/announce",
        };

        public static async Task<StreamEngine> CreateAsync(string tempRoot)
        {
            var cacheDir = Path.Combine(tempRoot, "cache");
            var cacheFile = Path.Combine(cacheDir, "dht_nodes.cache");
            Directory.CreateDirectory(cacheDir);
            // Always rewrite the cache — router DNS poisons torrent hostnames, so use hardcoded IPs.
            {
                var nodes = new List<byte>();
                var rnd = new Random();
                foreach (var (ip, port) in DhtBootstrapNodes)
                {
                    var addr = IPAddress.Parse(ip);
                    var id = new byte[20];
                    rnd.NextBytes(id);
                    nodes.AddRange(id);
                    nodes.AddRange(addr.GetAddressBytes());
                    nodes.Add((byte)(port >> 8));
                    nodes.Add((byte)port);
                }
                if (nodes.Count > 0) File.WriteAllBytes(cacheFile, nodes.ToArray());
            }
            var se = new StreamEngine(tempRoot);
            return se;
        }

        StreamEngine(string tempRoot)
        {
            root = tempRoot;
            dataDir = Path.Combine(root, "data");
            WipeTemp();
            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = Path.Combine(root, "cache"),
                AllowPortForwarding = true,
                AutoSaveLoadDhtCache = true,
                AutoSaveLoadFastResume = false,
                AutoSaveLoadMagnetLinkMetadata = false,
                DhtEndPoint = new IPEndPoint(IPAddress.Any, 0),
                ListenEndPoints = new Dictionary<string, IPEndPoint>
                {
                    ["ipv4"] = new IPEndPoint(IPAddress.Any, 0),
                },
                AllowedEncryption = new List<EncryptionType> { EncryptionType.PlainText, EncryptionType.RC4Full, EncryptionType.RC4Header },
                MaximumHalfOpenConnections = 20,
                MaximumConnections = 60,
                ConnectionTimeout = TimeSpan.FromSeconds(10),
                WebSeedDelay = TimeSpan.FromSeconds(30),
                DiskCacheBytes = 256 * 1024 * 1024,
            }.ToSettings();
            LoggerFactory.Register(name => new DebugLogger(name));
            engine = new ClientEngine(settings);
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        }

        public long FreeBytes
        {
            get
            {
                try
                {
                    Directory.CreateDirectory(root);
                    var fullPath = Path.GetFullPath(root);
                    long best = 0;
                    int bestLen = 0;
                    foreach (var d in DriveInfo.GetDrives())
                    {
                        if (!d.IsReady) continue;
                        var mp = d.RootDirectory.FullName;
                        if (fullPath.StartsWith(mp) && mp.Length > bestLen)
                        {
                            bestLen = mp.Length;
                            best = d.AvailableFreeSpace;
                        }
                    }
                    return best > 0 ? best : long.MaxValue;
                }
                catch (Exception) { return long.MaxValue; }
            }
        }

        /// Opens a release and returns a local http:// URL the TV player can read with range requests.
        /// season/episode pick the right file out of a season pack; pass -1 for movies.
        /// chooseFile is asked to pick from the video file names when a season pack is opened without an episode.
        public async Task<StreamSession> OpenAsync(string link, int season, int episode,
            IProgress<string> status, Func<IList<string>, Task<int>> chooseFile, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (current != null) await CloseLockedAsync(current).ConfigureAwait(false);
                current = await OpenLockedAsync(link, season, episode, status, chooseFile, ct).ConfigureAwait(false);
                return current;
            }
            finally { gate.Release(); }
        }

        async Task<StreamSession> OpenLockedAsync(string link, int season, int episode,
            IProgress<string> status, Func<IList<string>, Task<int>> chooseFile, CancellationToken ct)
        {
            Log?.Invoke($"OpenAsync: link={link.Substring(0, Math.Min(80, link.Length))}...");
            status?.Report("Resolving torrent…");
            var source = await ResolveAsync(link, ct).ConfigureAwait(false);
            Log?.Invoke($"Resolved: torrent={source.torrent != null}, magnet={source.magnet != null}");
            if (source.magnet != null)
            {
                Log?.Invoke($"Magnet hash={source.magnet.InfoHashes.V1?.ToHex()}, trackers={source.magnet.AnnounceUrls?.Count ?? 0}");
                var torrentFile = await TryDownloadTorrentFileAsync(source.magnet, ct).ConfigureAwait(false);
                if (torrentFile != null)
                {
                    Log?.Invoke($"Downloaded .torrent file: {torrentFile.Name}, files={torrentFile.Files.Count}, size={torrentFile.Size/(1024*1024)}MB");
                    source = (torrentFile, null);
                }
                else
                {
                    source.magnet = InjectTrackers(source.magnet);
                    Log?.Invoke($"After inject: {source.magnet.AnnounceUrls?.Count ?? 0} trackers");
                    source.magnet = await ResolveTrackerDnsAsync(source.magnet).ConfigureAwait(false);
                    Log?.Invoke($"After DOH resolve: {source.magnet.AnnounceUrls?.Count ?? 0} trackers");
                }
            }
            var dir = Path.Combine(dataDir, Guid.NewGuid().ToString("N"));

            var torrentSettings = new TorrentSettingsBuilder { MaximumConnections = 40, AllowInitialSeeding = false }.ToSettings();
            Log?.Invoke("Adding torrent to engine...");
            var manager = source.torrent != null
                ? await engine.AddStreamingAsync(source.torrent, dir, torrentSettings).ConfigureAwait(false)
                : await engine.AddStreamingAsync(source.magnet, dir, torrentSettings).ConfigureAwait(false);
            AttachTelemetry(manager);
            try
            {
                Log?.Invoke("Starting torrent...");
                await manager.StartAsync().ConfigureAwait(false);
                Log?.Invoke($"Started: state={manager.State}, metadata={manager.HasMetadata}");
                // Add DoH-resolved trackers FIRST — they're the primary peer source (embedded trackers stripped, router DNS-poisons them)
                await AddResolvedTrackersAsync(manager).ConfigureAwait(false);
                if (!manager.HasMetadata)
                {
                    using (var metaCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        metaCts.CancelAfter(TimeSpan.FromSeconds(90));
                        var metaTask = manager.WaitForMetadataAsync(metaCts.Token);
                        int tick = 0;
                        while (!metaTask.IsCompleted)
                        {
                            var peers = manager.OpenConnections;
                            status?.Report($"Finding peers… {peers} connected");
                            if (++tick % 3 == 0)
                                Log?.Invoke($"Waiting metadata: peers={peers}, state={manager.State}, t={tick*2}s");
                            await Task.WhenAny(metaTask, Task.Delay(2000, ct)).ConfigureAwait(false);
                        }
                        await metaTask.ConfigureAwait(false);
                        Log?.Invoke($"Metadata received! files={manager.Files.Count}");
                    }
                }

                ITorrentManagerFile file;
                var videos = manager.Files.Where(f => IsVideo(f.Path)).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
                if (season >= 0 && episode < 0 && videos.Count > 1 && chooseFile != null)
                {
                    var i = await chooseFile(videos.Select(f => Path.GetFileName(f.Path)).ToList()).ConfigureAwait(false);
                    if (i < 0) throw new OperationCanceledException();
                    file = videos[i];
                }
                else
                {
                    file = PickFile(manager.Files, season, episode)
                        ?? throw new Exception("No playable video file in this torrent.");
                }
                const long minFreeForStreaming = 200L << 20; // 200 MB buffer headroom
                if (FreeBytes < minFreeForStreaming)
                    throw new Exception($"Not enough free space for streaming buffer: {Gb(FreeBytes)} free, need at least {Gb(minFreeForStreaming)}.");

                foreach (var f in manager.Files)
                    await manager.SetFilePriorityAsync(f, f == file ? Priority.Normal : Priority.DoNotDownload).ConfigureAwait(false);
                Log?.Invoke($"File priorities set");

                status?.Report("Buffering…");
                Log?.Invoke($"Creating stream for {Path.GetFileName(file.Path)} ({file.Length/(1024*1024)}MB)");
                var stream = await manager.StreamProvider.CreateHttpStreamAsync(file, false, ct).ConfigureAwait(false);
                Log?.Invoke($"Stream ready: {stream.FullUri}");

                status?.Report("Connecting to peers…");
                var bufferStart = DateTime.UtcNow;
                var bufferDeadline = bufferStart.AddSeconds(120);
                while (DateTime.UtcNow < bufferDeadline && !ct.IsCancellationRequested)
                {
                    if (manager.Monitor.DataBytesReceived > 0) break;
                    var peers = manager.OpenConnections;
                    var dlRate = manager.Monitor.DownloadRate;
                    var elapsed = (int)(DateTime.UtcNow - bufferStart).TotalSeconds;
                    if (dlRate > 0)
                        status?.Report($"Loading… {dlRate / 1024} KB/s from {peers} peers");
                    else if (peers > 0)
                        status?.Report($"Connected to {peers} peers, waiting for data… ({elapsed}s)");
                    else
                        status?.Report($"Finding peers… ({elapsed}s)");
                    if (elapsed % 5 == 0 && elapsed > 0)
                        Log?.Invoke($"BUFFER t={elapsed}s open={peers} dl={dlRate/1024}KB/s progress={file.BitField.PercentComplete:F1}% totalRx={manager.Monitor.DataBytesReceived}B");
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                Log?.Invoke($"Initial buffer: {file.BitField.PercentComplete:F1}%, peers={manager.OpenConnections}, dl={manager.Monitor.DownloadRate/1024}KB/s, totalRx={manager.Monitor.DataBytesReceived}B");
                if (manager.Monitor.DataBytesReceived <= 0)
                    throw new Exception($"No data received after 120s. Peers connected: {manager.OpenConnections}. Total bytes received: {manager.Monitor.DataBytesReceived}. Check debug.log for details.");

                return new StreamSession { Manager = manager, Dir = dir, File = file, Http = stream };
            }
            catch
            {
                await RemoveAsync(manager, dir).ConfigureAwait(false);
                throw;
            }
        }

        /// Stops one session and deletes its pieces. Safe to call twice or after a newer session opened.
        public async Task CloseAsync(StreamSession s)
        {
            if (s == null) return;
            await gate.WaitAsync().ConfigureAwait(false);
            try { await CloseLockedAsync(s).ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        async Task CloseLockedAsync(StreamSession s)
        {
            if (current == s) current = null;
            try { s.Http.Dispose(); } catch (Exception) { }
            await RemoveAsync(s.Manager, s.Dir).ConfigureAwait(false);
        }

        async Task RemoveAsync(TorrentManager m, string dir)
        {
            try
            {
                if (m.State != TorrentState.Stopped) await m.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
                await engine.RemoveAsync(m, RemoveMode.CacheDataAndDownloadedData).ConfigureAwait(false);
            }
            catch (Exception) { }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
        }

        void WipeTemp()
        {
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true); } catch (Exception) { }
        }

        static ITorrentManagerFile PickFile(IList<ITorrentManagerFile> files, int season, int episode)
        {
            var i = PickIndex(files.Select(f => (f.Path, f.Length)).ToList(), season, episode);
            return i < 0 ? null : files[i];
        }

        static bool IsVideo(string path) =>
            VideoExt.Contains(Path.GetExtension(path)) && !Regex.IsMatch(path, @"\bsample\b", RegexOptions.IgnoreCase);

        /// Index of the file to play: the matching episode in a pack, else the largest video. -1 if none.
        internal static int PickIndex(IList<(string path, long length)> files, int season, int episode)
        {
            var videos = Enumerable.Range(0, files.Count)
                .Where(i => IsVideo(files[i].path))
                .OrderByDescending(i => files[i].length).ToList();
            if (season >= 0 && episode >= 0)
            {
                var rx = new Regex($@"(?:S0*{season}[\s._-]?E0*{episode}(?!\d))|(?:\b0*{season}x0*{episode}(?!\d))", RegexOptions.IgnoreCase);
                foreach (var i in videos)
                    if (rx.IsMatch(Path.GetFileName(files[i].path))) return i;
            }
            return videos.Count > 0 ? videos[0] : -1;
        }

        static MagnetLink InjectTrackers(MagnetLink original)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (original.AnnounceUrls != null)
                foreach (var u in original.AnnounceUrls)
                    existing.Add(u);
            var combined = new List<string>(existing);
            foreach (var t in ExtraTrackers)
                if (existing.Add(t))
                    combined.Add(t);
            var uri = $"magnet:?xt=urn:btih:{original.InfoHashes.V1?.ToHex() ?? original.InfoHashes.V2?.ToHex()}";
            if (!string.IsNullOrEmpty(original.Name))
                uri += "&dn=" + Uri.EscapeDataString(original.Name);
            foreach (var t in combined)
                uri += "&tr=" + Uri.EscapeDataString(t);
            return MagnetLink.Parse(uri);
        }

        static async Task<MagnetLink> ResolveTrackerDnsAsync(MagnetLink magnet)
        {
            if (magnet.AnnounceUrls == null || magnet.AnnounceUrls.Count == 0) return magnet;
            var resolved = new List<string>();
            foreach (var tracker in magnet.AnnounceUrls)
            {
                resolved.Add(await ResolveTrackerUrlAsync(tracker).ConfigureAwait(false));
            }
            var uri = $"magnet:?xt=urn:btih:{magnet.InfoHashes.V1?.ToHex() ?? magnet.InfoHashes.V2?.ToHex()}";
            if (!string.IsNullOrEmpty(magnet.Name))
                uri += "&dn=" + Uri.EscapeDataString(magnet.Name);
            foreach (var t in resolved)
                uri += "&tr=" + Uri.EscapeDataString(t);
            return MagnetLink.Parse(uri);
        }

        static async Task<string> ResolveTrackerUrlAsync(string trackerUrl)
        {
            try
            {
                if (!System.Uri.TryCreate(trackerUrl, UriKind.Absolute, out var u)) return trackerUrl;
                if (IPAddress.TryParse(u.Host, out _)) return trackerUrl;
                // Only replace hostname→IP for UDP trackers.
                // HTTP trackers behind CDNs need the real hostname in Host/SNI header,
                // so replacing with IP would break them. UDP has no Host header.
                if (!u.Scheme.Equals("udp", StringComparison.OrdinalIgnoreCase))
                {
                    Log?.Invoke($"Tracker skip (HTTP): {trackerUrl}");
                    return trackerUrl;
                }
                var ip = await DohResolver.ResolveAsync(u.Host).ConfigureAwait(false);
                if (string.IsNullOrEmpty(ip)) return trackerUrl;
                Log?.Invoke($"Tracker resolved: {u.Host} -> {ip}");
                var builder = new UriBuilder(u) { Host = ip };
                return builder.Uri.ToString().TrimEnd('/');
            }
            catch { return trackerUrl; }
        }

        async Task AddManualPeersAsync(TorrentManager manager, Action<string> log)
        {
            try
            {
                var peersFile = Path.Combine(
                    Tizen.Applications.Application.Current.DirectoryInfo.Data, "local_peers.txt");
                if (!File.Exists(peersFile)) return;
                var lines = File.ReadAllLines(peersFile);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                    try
                    {
                        var peerUri = new Uri($"ipv4://{trimmed}/");
                        await manager.AddPeerAsync(new PeerInfo(peerUri, new BEncodedString(new byte[20]), true)).ConfigureAwait(false);
                        log?.Invoke($"Added manual peer: {trimmed}");
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"Failed to add peer {trimmed}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Manual peer load error: {ex.Message}");
            }
        }

        static byte[] StripTrackerBytes(byte[] raw)
        {
            try
            {
                var dict = (BEncodedDictionary)BEncodedValue.Decode(raw);
                dict.Remove("announce");
                dict.Remove("announce-list");
                dict.Remove("url-list");
                dict.Remove("httpseeds");
                return dict.Encode();
            }
            catch { return raw; }
        }

        static Torrent LoadTorrentWithoutTrackers(string path)
        {
            var raw = File.ReadAllBytes(path);
            var stripped = StripTrackerBytes(raw);
            if (Torrent.TryLoad(stripped, out var t))
                return t;
            return Torrent.Load(raw);
        }

        async Task AddResolvedTrackersAsync(TorrentManager manager)
        {
            var resolved = await Task.WhenAll(ExtraTrackers.Select(t => ResolveTrackerUrlAsync(t))).ConfigureAwait(false);
            foreach (var url in resolved)
            {
                try
                {
                    await manager.TrackerManager.AddTrackerAsync(new Uri(url)).ConfigureAwait(false);
                    Log?.Invoke($"Added resolved tracker: {url}");
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Failed to add tracker {url}: {ex.Message}");
                }
            }
        }

        async Task<Torrent> TryDownloadTorrentFileAsync(MagnetLink magnet, CancellationToken ct)
        {
            var hash = magnet.InfoHashes.V1?.ToHex();
            if (string.IsNullOrEmpty(hash)) return null;
            var urls = new[]
            {
                $"https://itorrents.org/torrent/{hash}.torrent",
            };
            foreach (var url in urls)
            {
                try
                {
                    Log?.Invoke($"Trying torrent file: {url}");
                    var data = await DohResolver.FetchBytesAsync(url, ct).ConfigureAwait(false);
                    if (data != null && data.Length > 50)
                    {
                        var stripped = StripTrackerBytes(data);
                        if (Torrent.TryLoad(stripped, out var t))
                        {
                            Log?.Invoke($"Torrent file OK (trackers stripped): {t.Name}, {t.Files.Count} files");
                            return t;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log?.Invoke($"Torrent file fetch failed: {e.Message}");
                }
            }
            return null;
        }

        async Task<(Torrent torrent, MagnetLink magnet)> ResolveAsync(string link, CancellationToken ct)
        {
            link = link.Trim();
            for (int hop = 0; hop < 6; hop++)
            {
                if (link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                    return (null, MagnetLink.Parse(link));
                if (!System.Uri.TryCreate(link, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                    throw new Exception("Not a magnet link or http(s) torrent URL.");

                using (var res = await http.GetAsync(uri, ct).ConfigureAwait(false))
                {
                    var code = (int)res.StatusCode;
                    if (code >= 300 && code < 400 && res.Headers.Location != null)
                    {
                        // Prowlarr/Jackett download links often redirect to a magnet.
                        var loc = res.Headers.Location;
                        link = loc.IsAbsoluteUri ? loc.OriginalString : new Uri(uri, loc).ToString();
                        continue;
                    }
                    if (!res.IsSuccessStatusCode) throw new Exception($"Indexer returned HTTP {code} for the torrent.");
                    var bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    var clean = StripTrackerBytes(bytes);
                    if (Torrent.TryLoad(clean, out var t)) return (t, null);
                    var text = System.Text.Encoding.UTF8.GetString(bytes);
                    var m = Regex.Match(text, @"magnet:\?[^""'<>\s]+");
                    if (m.Success) { link = WebUtility.HtmlDecode(m.Value); continue; }
                    throw new Exception("The link did not return a torrent.");
                }
            }
            throw new Exception("Too many redirects resolving the torrent.");
        }

        public string StatusReport()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Engine: initialized, root={root}");
                sb.AppendLine($"  Torrents: {engine.Torrents.Count}");
                sb.AppendLine($"  Disk: {Gb(FreeBytes)} free");
                var cacheFile = Path.Combine(root, "cache", "dht_nodes.cache");
                sb.AppendLine($"  DHT cache: {(File.Exists(cacheFile) ? new FileInfo(cacheFile).Length + " bytes" : "missing")}");
                sb.AppendLine($"  DHT endpoint: {engine.Settings.DhtEndPoint}");
                foreach (var m in engine.Torrents)
                {
                    sb.AppendLine($"  [{m.InfoHashes.V1?.ToHex()?.Substring(0,8)}] state={m.State}, " +
                        $"peers={m.OpenConnections}, metadata={m.HasMetadata}, " +
                        $"dl={m.Monitor.DownloadRate/1024}KB/s, progress={m.PartialProgress:F1}%");
                }
                if (current != null)
                {
                    sb.AppendLine($"  Active stream: {current.FileName}");
                    sb.AppendLine($"    Progress: {current.FileProgress:F1}%");
                    sb.AppendLine($"    Peers: {current.Peers}");
                    sb.AppendLine($"    Download: {current.DownloadRate / 1024} KB/s");
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                return $"Engine status error: {e.Message}";
            }
        }

        public async Task<string> TestAsync(string magnetUri, CancellationToken ct)
        {
            var report = new System.Text.StringBuilder();
            void R(string msg)
            {
                var line = $"[{DateTime.UtcNow:HH:mm:ss}] {msg}";
                report.AppendLine(line);
                Log?.Invoke("TEST: " + msg);
            }
            TorrentManager manager = null;
            string dir = null;
            try
            {
                dir = Path.Combine(dataDir, "test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                var settings = new TorrentSettingsBuilder { MaximumConnections = 40 }.ToSettings();

                var torrentPath = System.IO.Path.Combine(
                    Tizen.Applications.Application.Current.DirectoryInfo.Data, "test.torrent");
                bool fromFile = File.Exists(torrentPath);
                if (fromFile)
                {
                    R($"Loading .torrent file (trackers stripped): {torrentPath}");
                    var torrent = LoadTorrentWithoutTrackers(torrentPath);
                    R($"Torrent: {torrent.Name}, files={torrent.Files.Count}, size={torrent.Size/(1024*1024)}MB, trackers={torrent.AnnounceUrls?.Count ?? 0}");
                    R("Adding torrent from file (no embedded trackers)...");
                    manager = await engine.AddStreamingAsync(torrent, dir, settings).ConfigureAwait(false);
                }
                else
                {
                    R($"Parsing magnet: {magnetUri.Substring(0, Math.Min(100, magnetUri.Length))}...");
                    var magnet = MagnetLink.Parse(magnetUri);
                    R($"Hash: {magnet.InfoHashes.V1?.ToHex()}, Name: {magnet.Name}, Trackers: {magnet.AnnounceUrls?.Count ?? 0}");

                    magnet = InjectTrackers(magnet);
                    R($"Injected trackers, total: {magnet.AnnounceUrls?.Count ?? 0}");
                    magnet = await ResolveTrackerDnsAsync(magnet).ConfigureAwait(false);
                    R($"After DOH resolve: {magnet.AnnounceUrls?.Count ?? 0} trackers");
                    if (magnet.AnnounceUrls != null)
                        foreach (var t in magnet.AnnounceUrls)
                            R($"  tr: {t}");

                    R("Adding torrent from magnet...");
                    manager = await engine.AddStreamingAsync(magnet, dir, settings).ConfigureAwait(false);
                }
                AttachTelemetry(manager);

                R("Starting...");
                await manager.StartAsync().ConfigureAwait(false);
                R($"State: {manager.State}, HasMetadata: {manager.HasMetadata}");

                // Add local LAN peers FIRST so they connect before WAN peers flood the queue
                await AddManualPeersAsync(manager, R).ConfigureAwait(false);
                // Add trackers in background after manual peers have time to connect (3s delay)
                _ = Task.Run(async () => { R("Adding DoH-resolved trackers (background)..."); await AddResolvedTrackersAsync(manager).ConfigureAwait(false); R("DoH-resolved trackers done."); });

                R($"DHT endpoint: {engine.Settings.DhtEndPoint}");
                if (engine.Settings.ListenEndPoints != null)
                    foreach (var kvp in engine.Settings.ListenEndPoints)
                        R($"Listen: {kvp.Key} = {kvp.Value}");
                else
                    R("Listen: NONE (no ListenEndPoints configured)");
                R($"PortForwarding: {engine.Settings.AllowPortForwarding}");

                R($"Engine settings: enc=[{string.Join(",", engine.Settings.AllowedEncryption)}]");
                R($"Engine settings: maxHalf={engine.Settings.MaximumHalfOpenConnections} maxConn={engine.Settings.MaximumConnections}");

                for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
                {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    var tcpInfo = "";
                    var peerConns = new List<string>();
                    try
                    {
                        if (System.IO.File.Exists("/proc/net/tcp"))
                        {
                            var lines = System.IO.File.ReadAllLines("/proc/net/tcp");
                            int syn = 0, est = 0, tw = 0, cw = 0, other = 0;
                            foreach (var line in lines.Skip(1))
                            {
                                var parts = line.Split(new char[0], StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length < 4) continue;
                                switch (parts[3])
                                {
                                    case "01": est++; break;
                                    case "02": syn++; break;
                                    case "06": tw++; break;
                                    case "07": cw++; break;
                                    default: other++; break;
                                }
                                if (parts[3] == "01" && peerConns.Count < 8)
                                {
                                    var rem = parts[2];
                                    var hp = rem.Split(':');
                                    if (hp.Length == 2)
                                    {
                                        var hexIp = hp[0];
                                        var hexPort = hp[1];
                                        if (hexIp != "0100007F" && hexIp.Length == 8)
                                        {
                                            var ipNum = Convert.ToUInt32(hexIp, 16);
                                            var ip = $"{ipNum & 0xFF}.{(ipNum >> 8) & 0xFF}.{(ipNum >> 16) & 0xFF}.{(ipNum >> 24) & 0xFF}";
                                            var port = Convert.ToInt32(hexPort, 16);
                                            peerConns.Add($"{ip}:{port}");
                                        }
                                    }
                                }
                            }
                            tcpInfo = $" tcp[EST={est} SYN={syn} TW={tw} CW={cw}]";
                        }
                    }
                    catch { }

                    var dl = manager.Monitor.DownloadRate;
                    var ul = manager.Monitor.UploadRate;
                    var videoFile = manager.Files.Where(f => IsVideo(f.Path)).OrderByDescending(f => f.Length).FirstOrDefault();
                    var fileProg = videoFile != null ? $" file={videoFile.BitField.PercentComplete:F1}%" : "";
                    var totalDown = manager.Monitor.DataBytesReceived;
                    if (i % 2 == 0 || dl > 0)
                        R($"  t={i*2+2}s: peers={manager.OpenConnections} dl={dl/1024}KB/s progress={manager.PartialProgress:F1}%{fileProg} bytes={totalDown/(1024*1024)}MB{tcpInfo}");
                    if (peerConns.Count > 0 && (i % 15 == 0))
                        R($"  EST peers: {string.Join(" ", peerConns)}");

                    if (manager.HasMetadata && i >= 2)
                    {
                        if (i == 2)
                        {
                            R($"METADATA OK! Files={manager.Files.Count}");
                            foreach (var f in manager.Files)
                                R($"  {f.Path} ({f.Length/(1024*1024)}MB)");
                            var video = manager.Files.Where(f => IsVideo(f.Path)).OrderByDescending(f => f.Length).FirstOrDefault();
                            if (video != null)
                            {
                                R($"Video: {video.Path} ({Gb(video.Length)})");
                                _ = Task.Run(async () =>
                                {
                                    foreach (var f in manager.Files)
                                        await manager.SetFilePriorityAsync(f, f == video ? Priority.Normal : Priority.DoNotDownload).ConfigureAwait(false);
                                    R($"File priorities set (background)");
                                });
                                R($"Creating stream (no prebuffer)...");
                                try
                                {
                                    var stream = await manager.StreamProvider.CreateHttpStreamAsync(video, false, ct).ConfigureAwait(false);
                                    R($"HTTP stream: {stream.FullUri}");
                                    stream.Dispose();
                                }
                                catch (Exception ex) { R($"Stream create err: {ex.Message}"); }
                            }
                        }
                        if (dl > 0 && manager.PartialProgress > 5.0) { R("DATA FLOWING — enough for streaming"); break; }
                    }
                }
                R($"FINAL: state={manager.State} peers={manager.OpenConnections} meta={manager.HasMetadata} dl={manager.Monitor.DownloadRate/1024}KB/s progress={manager.PartialProgress:F1}%");

                // Raw TCP probe: try connecting to a few peer IPs directly from the TV
                R("--- RAW TCP PROBE ---");
                try
                {
                    if (System.IO.File.Exists("/proc/net/tcp"))
                    {
                        var probeTargets = new List<(string ip, int port)>();
                        var lines = System.IO.File.ReadAllLines("/proc/net/tcp");
                        foreach (var line in lines.Skip(1))
                        {
                            if (probeTargets.Count >= 3) break;
                            var parts = line.Split(new char[0], StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length < 4 || parts[3] != "02") continue; // SYN_SENT
                            var hp = parts[2].Split(':');
                            if (hp.Length != 2 || hp[0].Length != 8 || hp[0] == "0100007F") continue;
                            var ipNum = Convert.ToUInt32(hp[0], 16);
                            var ip = $"{ipNum & 0xFF}.{(ipNum >> 8) & 0xFF}.{(ipNum >> 16) & 0xFF}.{(ipNum >> 24) & 0xFF}";
                            var port = Convert.ToInt32(hp[1], 16);
                            probeTargets.Add((ip, port));
                        }
                        foreach (var (ip, port) in probeTargets)
                        {
                            try
                            {
                                using (var sock = new System.Net.Sockets.TcpClient())
                                {
                                    var connectTask = sock.ConnectAsync(ip, port);
                                    if (await Task.WhenAny(connectTask, Task.Delay(3000)).ConfigureAwait(false) == connectTask && !connectTask.IsFaulted)
                                        R($"  TCP {ip}:{port} -> CONNECTED (raw)");
                                    else
                                        R($"  TCP {ip}:{port} -> TIMEOUT/FAIL (raw)");
                                }
                            }
                            catch (Exception ex) { R($"  TCP {ip}:{port} -> {ex.GetType().Name}: {ex.Message}"); }
                        }
                        if (probeTargets.Count == 0)
                            R("  No SYN_SENT peers to probe");
                    }
                }
                catch (Exception ex) { R($"  Probe error: {ex.Message}"); }
            }
            catch (OperationCanceledException) { R("Cancelled"); }
            catch (Exception e) { R($"ERROR: {e.GetType().Name}: {e.Message}"); }
            finally
            {
                if (manager != null && dir != null)
                {
                    R("Cleaning up...");
                    await RemoveAsync(manager, dir).ConfigureAwait(false);
                }
            }
            return report.ToString();
        }

        static void AttachTelemetry(TorrentManager manager)
        {
            int connFails = 0, peerPlus = 0, peerMinus = 0;
            manager.PeerConnected += (o, e) =>
            {
                try
                {
                    peerPlus++;
                    var p = e.Peer;
                    Log?.Invoke($"PEER+ #{peerPlus} uri={p.Uri} enc={p.EncryptionType} client={p.ClientApp.Client} seeder={p.IsSeeder} choking={p.IsChoking} amInterested={p.AmInterested}");
                }
                catch (Exception ex) { Log?.Invoke($"PEER+ err: {ex.Message}"); }
            };
            manager.PeerDisconnected += (o, e) =>
            {
                try
                {
                    peerMinus++;
                    var p = e.Peer;
                    Log?.Invoke($"PEER- #{peerMinus} uri={p.Uri} enc={p.EncryptionType} client={p.ClientApp.Client} seeder={p.IsSeeder}");
                }
                catch (Exception ex) { Log?.Invoke($"PEER- err: {ex.Message}"); }
            };
            manager.ConnectionAttemptFailed += (o, e) =>
            {
                try
                {
                    connFails++;
                    if (connFails <= 50)
                        Log?.Invoke($"CONN_FAIL #{connFails} uri={e.Peer.ConnectionUri} reason={e.Reason}");
                    else if (connFails % 20 == 0)
                        Log?.Invoke($"CONN_FAIL #{connFails} reason={e.Reason} (sampling)");
                }
                catch (Exception ex) { Log?.Invoke($"CONN_FAIL err: {ex.Message}"); }
            };
            manager.PeersFound += (o, e) =>
            {
                try
                {
                    Log?.Invoke($"PEERS_FOUND new={e.NewPeers} existing={e.ExistingPeers} type={e.GetType().Name}");
                }
                catch (Exception ex) { Log?.Invoke($"PEERS_FOUND err: {ex.Message}"); }
            };
            manager.TorrentStateChanged += (o, e) =>
            {
                Log?.Invoke($"STATE {e.OldState} -> {e.NewState}");
            };
        }

        static string Gb(long b) => (b / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB";

        public void Dispose()
        {
            try { CloseAsync(current).Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            engine.Dispose();
            http.Dispose();
            WipeTemp();
        }
    }

    class DebugLogger : ILogger
    {
        readonly string name;
        public DebugLogger(string name) { this.name = name; }
        public void Info(string message) { StreamEngine.Log?.Invoke($"[MT:{name}] {message}"); }
        public void Debug(string message) { }
        public void Error(string message) { StreamEngine.Log?.Invoke($"[MT:{name}] ERROR: {message}"); }
    }
}
