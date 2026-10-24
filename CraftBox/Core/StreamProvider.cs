using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CraftBox.Core
{
    public class StreamResult
    {
        public string Url;
        public string Format; // "hls", "mp4"
        public string Server;
        public string Quality; // "1080p", "720p", "480p", "auto"
        public Dictionary<string, string> Headers;
        public List<StreamTrack> Tracks;
    }

    public class StreamTrack
    {
        public string Label;
        public string Url;
        public string Language;
    }

    public static class StreamProvider
    {
        const string VidLinkOrigin = "https://vidlink.pro";
        const string VidSrcOrigin = "https://vidsrc.sh";
        const string VidSrcPlayerOrigin = "https://stellarconductornexus.com";
        const string VidSrcDataOrigin = "https://data.vidsrc.sh";
        const string VidFastOrigin = "https://vidfast.vc";
        static string VidFastPlayerDigest = "";
        const string UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";

        static byte[] VidLinkKey = new byte[0];

        public static void Configure(string vidLinkKey, string vidFastDigest)
        {
            if (!string.IsNullOrEmpty(vidLinkKey)) VidLinkKey = HexDecode(vidLinkKey);
            if (!string.IsNullOrEmpty(vidFastDigest)) VidFastPlayerDigest = vidFastDigest;
        }

        public static async Task<List<StreamResult>> ResolveAsync(int tmdbId, bool isTv, int season = 0, int episode = 0, CancellationToken ct = default)
        {
            var tasks = new[]
            {
                WithTimeout(() => ResolveVidLinkAsync(tmdbId, isTv, season, episode, ct), 15, ct),
                WithTimeout(() => ResolveVidSrcAsync(tmdbId, isTv, season, episode, ct), 18, ct),
                WithTimeout(() => ResolveVidFastAsync(tmdbId, isTv, season, episode, ct), 18, ct),
            };

            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }

            var results = new List<StreamResult>();
            foreach (var task in tasks)
                if (task.IsCompleted && !task.IsFaulted && task.Result != null)
                    results.AddRange(task.Result);
            return results;
        }

        static async Task<List<StreamResult>> WithTimeout(Func<Task<List<StreamResult>>> provider, int timeoutSec, CancellationToken ct)
        {
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                try { return await provider().ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new List<StreamResult>(); }
                catch { return new List<StreamResult>(); }
            }
        }

        // ===================== VidLink =====================

        static async Task<List<StreamResult>> ResolveVidLinkAsync(int tmdbId, bool isTv, int season, int episode, CancellationToken ct)
        {
            var embedPath = isTv
                ? $"/tv/{tmdbId}/{season}/{episode}"
                : $"/movie/{tmdbId}";

            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");

            var embedResp = await http.GetAsync(VidLinkOrigin + embedPath, ct).ConfigureAwait(false);
            if (!embedResp.IsSuccessStatusCode) return null;

            var token = GenerateVidLinkToken(tmdbId);

            var apiPath = isTv
                ? $"/api/b/tv/{token}/{season}/{episode}?multiLang=1"
                : $"/api/b/movie/{token}?multiLang=1";

            var req = new HttpRequestMessage(HttpMethod.Get, VidLinkOrigin + apiPath);
            req.Headers.TryAddWithoutValidation("Origin", VidLinkOrigin);
            req.Headers.TryAddWithoutValidation("Referer", VidLinkOrigin + embedPath);
            req.Headers.TryAddWithoutValidation("X-Playback-Environment", "hls");

            var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseVidLinkResponse(json);
        }

        static string GenerateVidLinkToken(int tmdbId)
        {
            var idBytes = Encoding.UTF8.GetBytes(tmdbId.ToString());
            var expiry = new byte[8];
            var ts = DateTimeOffset.UtcNow.AddSeconds(120).ToUnixTimeSeconds();
            expiry[0] = (byte)(ts >> 56);
            expiry[1] = (byte)(ts >> 48);
            expiry[2] = (byte)(ts >> 40);
            expiry[3] = (byte)(ts >> 32);
            expiry[4] = (byte)(ts >> 24);
            expiry[5] = (byte)(ts >> 16);
            expiry[6] = (byte)(ts >> 8);
            expiry[7] = (byte)ts;

            var message = new byte[idBytes.Length + 8];
            Buffer.BlockCopy(idBytes, 0, message, 0, idBytes.Length);
            Buffer.BlockCopy(expiry, 0, message, idBytes.Length, 8);

            var nonce = new byte[24];
            var sealed_ = NaCl.SecretboxSeal(message, nonce, VidLinkKey);

            var full = new byte[24 + sealed_.Length];
            Buffer.BlockCopy(nonce, 0, full, 0, 24);
            Buffer.BlockCopy(sealed_, 0, full, 24, sealed_.Length);

            return Base64UrlEncode(full);
        }

        static List<StreamResult> ParseVidLinkResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "null") return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("stream", out var stream) || stream.ValueKind == JsonValueKind.Null)
                return null;

            var results = new List<StreamResult>();

            var tracks = new List<StreamTrack>();
            if (stream.TryGetProperty("captions", out var captions) && captions.ValueKind == JsonValueKind.Array)
            {
                foreach (var cap in captions.EnumerateArray())
                {
                    var url = Str(cap, "url");
                    if (string.IsNullOrEmpty(url)) url = Str(cap, "file");
                    var label = Str(cap, "label");
                    if (!string.IsNullOrEmpty(url))
                        tracks.Add(new StreamTrack { Url = url, Label = label, Language = Str(cap, "language") });
                }
            }

            if (stream.TryGetProperty("qualities", out var qualities) && qualities.ValueKind == JsonValueKind.Object)
            {
                foreach (var q in qualities.EnumerateObject())
                {
                    var url = Str(q.Value, "url");
                    if (string.IsNullOrEmpty(url)) url = Str(q.Value, "playlist");
                    var type = Str(q.Value, "type");
                    if (!string.IsNullOrEmpty(url))
                    {
                        results.Add(new StreamResult
                        {
                            Url = url,
                            Format = string.IsNullOrEmpty(type) ? "hls" : type.ToLowerInvariant(),
                            Server = "quality:" + q.Name,
                            Quality = q.Name + "p",
                            Headers = ParseHeaders(q.Value, "headers"),
                            Tracks = tracks,
                        });
                    }
                }
            }

            var playlist = Str(stream, "playlist");
            var deliveryType = Str(stream, "deliveryType");
            if (!string.IsNullOrEmpty(playlist))
            {
                var headers = ParseHeaders(stream, "playlistHeaders");
                results.Add(new StreamResult
                {
                    Url = playlist,
                    Format = string.IsNullOrEmpty(deliveryType) ? "hls" : deliveryType.ToLowerInvariant(),
                    Server = "primary",
                    Quality = "auto",
                    Headers = headers,
                    Tracks = tracks,
                });
            }

            if (stream.TryGetProperty("alternates", out var alts) && alts.ValueKind == JsonValueKind.Object)
            {
                foreach (var a in alts.EnumerateObject())
                {
                    var url = Str(a.Value, "url");
                    if (string.IsNullOrEmpty(url)) url = Str(a.Value, "playlist");
                    var type = Str(a.Value, "type");
                    if (!string.IsNullOrEmpty(url))
                    {
                        results.Add(new StreamResult
                        {
                            Url = url,
                            Format = string.IsNullOrEmpty(type) ? "hls" : type.ToLowerInvariant(),
                            Server = "alt:" + a.Name,
                            Headers = ParseHeaders(a.Value, "headers"),
                            Tracks = tracks,
                        });
                    }
                }
            }

            return results.Count > 0 ? results : null;
        }

        // ===================== VidSrc =====================

        static async Task<List<StreamResult>> ResolveVidSrcAsync(int tmdbId, bool isTv, int season, int episode, CancellationToken ct)
        {
            var embedPath = isTv ? $"/tv/{tmdbId}/{season}/{episode}" : $"/movie/{tmdbId}";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(16) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");

            var embedHtml = await HttpGetStr(http, VidSrcOrigin + "/embed" + embedPath, VidSrcOrigin + "/", ct).ConfigureAwait(false);
            if (embedHtml == null) return null;

            var gateRel = ParseVidSrcGate(embedHtml);
            if (gateRel == null) return null;

            var gateJson = await HttpGetStr(http, VidSrcOrigin + gateRel, VidSrcOrigin + "/embed" + embedPath, ct).ConfigureAwait(false);
            if (gateJson == null) return null;

            string gateSrc;
            using (var doc = JsonDocument.Parse(gateJson))
            {
                gateSrc = doc.RootElement.TryGetProperty("src", out var s) ? s.GetString() : null;
            }
            if (string.IsNullOrEmpty(gateSrc) || !gateSrc.StartsWith(VidSrcPlayerOrigin)) return null;

            var landingHtml = await HttpGetStr(http, gateSrc, VidSrcOrigin + "/", ct).ConfigureAwait(false);
            if (landingHtml == null) return null;

            var playerUrl = ParseWindowField(landingHtml, "CFG", "playerUrl");
            if (string.IsNullOrEmpty(playerUrl)) return null;
            if (playerUrl.StartsWith("/")) playerUrl = VidSrcPlayerOrigin + playerUrl;
            if (!playerUrl.StartsWith(VidSrcPlayerOrigin)) return null;

            var playerHtml = await HttpGetStr(http, playerUrl, VidSrcPlayerOrigin + "/", ct).ConfigureAwait(false);
            if (playerHtml == null) return null;

            var configJson = ParseWindowJson(playerHtml, "CONFIG");
            if (configJson == null) return null;

            string apiField, streamBase, apiToken;
            using (var doc = JsonDocument.Parse(configJson))
            {
                var r = doc.RootElement;
                apiField = Str(r, "api");
                streamBase = Str(r, "streamBase");
                apiToken = Str(r, "apiToken");
            }

            string apiUrl;
            if (isTv)
            {
                if (string.IsNullOrEmpty(streamBase)) return null;
                apiUrl = streamBase + "&season=" + season + "&episode=" + episode + "&stream_urls=";
            }
            else
            {
                if (string.IsNullOrEmpty(apiField)) return null;
                apiUrl = apiField;
            }
            if (!string.IsNullOrEmpty(apiToken))
                apiUrl += "&api_token=" + Uri.EscapeDataString(apiToken);
            if (!apiUrl.StartsWith(VidSrcDataOrigin)) return null;

            var sourcesJson = await HttpGetStr(http, apiUrl, VidSrcPlayerOrigin + "/", ct).ConfigureAwait(false);
            if (sourcesJson == null) return null;

            return await ParseVidSrcSources(sourcesJson, http, ct).ConfigureAwait(false);
        }

        static string ParseVidSrcGate(string html)
        {
            foreach (Match m in Regex.Matches(html, @"(?is)<iframe\b[^>]*>"))
            {
                var tag = m.Value;
                string id = null, dataApi = null;
                foreach (Match a in Regex.Matches(tag, @"(?i)\b(id|data-api)\s*=\s*[""']([^""']*)[""']"))
                {
                    var name = a.Groups[1].Value.ToLowerInvariant();
                    var val = WebUtility.HtmlDecode(a.Groups[2].Value);
                    if (name == "id") id = val;
                    else if (name == "data-api") dataApi = val;
                }
                if (id == "player_iframe" && dataApi != null && dataApi.StartsWith("/vs_src.php"))
                    return dataApi;
            }
            return null;
        }

        static string ParseWindowField(string html, string varName, string field)
        {
            var json = ParseWindowJson(html, varName);
            if (json == null) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty(field, out var v) ? v.GetString() : null;
            }
            catch { return null; }
        }

        static string ParseWindowJson(string html, string varName)
        {
            var rx = new Regex(@"(?s)window\." + Regex.Escape(varName) + @"\s*=\s*(\{[^<]*?\})\s*;");
            var m = rx.Match(html);
            return m.Success ? m.Groups[1].Value : null;
        }

        static async Task<List<StreamResult>> ParseVidSrcSources(string json, HttpClient http, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status_code", out var sc) || sc.GetString() != "200") return null;
            if (!root.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty("stream_urls", out var streamEl)) return null;

            var streams = new List<string>();
            if (streamEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in streamEl.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.String) streams.Add(s.GetString());
            }
            else if (streamEl.ValueKind == JsonValueKind.String)
            {
                var encoded = streamEl.GetString();
                if (string.IsNullOrEmpty(encoded)) return null;

                byte[] wasmKey = null;
                if (root.TryGetProperty("vs", out var vsEl))
                    wasmKey = await FetchVidSrcWasmKey(vsEl, http, ct).ConfigureAwait(false);
                if (wasmKey == null) return null;

                var cipher = Convert.FromBase64String(encoded);
                if (cipher.Length <= 12) return null;

                var nonce = new byte[12];
                Buffer.BlockCopy(cipher, 0, nonce, 0, 12);
                var body = new byte[cipher.Length - 12];
                Buffer.BlockCopy(cipher, 12, body, 0, body.Length);

                var plain = IetfChaCha20.Decrypt(wasmKey, nonce, body);
                foreach (var line in Encoding.UTF8.GetString(plain).Split('\n'))
                    if (!string.IsNullOrEmpty(line)) streams.Add(line);
            }
            else return null;

            if (streams.Count == 0) return null;

            var tokens = await FetchVidSrcHostTokens(streams, http, ct).ConfigureAwait(false);

            var results = new List<StreamResult>();
            var seen = new HashSet<string>();
            foreach (var rawUrl in streams)
            {
                if (!seen.Add(rawUrl)) continue;
                if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") continue;

                var origin = uri.GetLeftPart(UriPartial.Authority);
                tokens.TryGetValue(origin, out var tok);

                var finalUrl = rawUrl;
                if (rawUrl.Contains("__TOKEN__"))
                {
                    if (string.IsNullOrEmpty(tok)) continue;
                    finalUrl = rawUrl.Replace("__TOKEN__", Uri.EscapeDataString(tok));
                }
                else if (!string.IsNullOrEmpty(tok))
                {
                    finalUrl += (rawUrl.Contains("?") ? "&" : "?") + "token=" + Uri.EscapeDataString(tok);
                }

                var fmt = "hls";
                if (finalUrl.Contains(".mp4")) fmt = "mp4";

                results.Add(new StreamResult
                {
                    Url = finalUrl,
                    Format = fmt,
                    Server = "vidsrc:" + uri.Host,
                    Quality = "auto",
                    Headers = new Dictionary<string, string> { { "Referer", VidSrcPlayerOrigin + "/" } },
                });
            }

            return results.Count > 0 ? results : null;
        }

        static async Task<byte[]> FetchVidSrcWasmKey(JsonElement vsEl, HttpClient http, CancellationToken ct)
        {
            try
            {
                byte[] module = null;
                if (vsEl.TryGetProperty("wasm_url", out var wasmUrlEl) && wasmUrlEl.ValueKind == JsonValueKind.String)
                {
                    var wasmUrl = wasmUrlEl.GetString();
                    if (!string.IsNullOrEmpty(wasmUrl) && wasmUrl.StartsWith(VidSrcDataOrigin))
                    {
                        var resp = await http.GetAsync(wasmUrl, ct).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                            module = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    }
                }
                if (module == null && vsEl.TryGetProperty("wasm", out var wasmB64) && wasmB64.ValueKind == JsonValueKind.String)
                    module = Convert.FromBase64String(wasmB64.GetString());
                if (module == null) return null;
                return WasmKeyExtractor.ExtractKey(module);
            }
            catch { return null; }
        }

        static async Task<Dictionary<string, string>> FetchVidSrcHostTokens(List<string> streams, HttpClient http, CancellationToken ct)
        {
            var origins = new HashSet<string>();
            foreach (var url in streams)
                if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https")
                    origins.Add(u.GetLeftPart(UriPartial.Authority));

            var tokens = new Dictionary<string, string>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(3000);

            var tasks = new List<Task>();
            foreach (var origin in origins)
            {
                var o = origin;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var rq = new HttpRequestMessage(HttpMethod.Get, o + "/generate.php");
                        rq.Headers.TryAddWithoutValidation("Referer", VidSrcPlayerOrigin + "/");
                        var resp = await http.SendAsync(rq, cts.Token).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            var body = (await resp.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
                            string parsed = null;
                            if (body.StartsWith("{"))
                            {
                                try
                                {
                                    using var d = JsonDocument.Parse(body);
                                    foreach (var n in new[] { "token", "data", "string", "result" })
                                        if (d.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                                        { parsed = v.GetString(); break; }
                                }
                                catch { }
                            }
                            else if (body.StartsWith("\""))
                            {
                                try { parsed = JsonSerializer.Deserialize<string>(body); } catch { }
                            }
                            else if (body.Length > 0 && body.Length <= 4096)
                                parsed = body;

                            if (!string.IsNullOrEmpty(parsed))
                                lock (tokens) tokens[o] = parsed;
                        }
                    }
                    catch { }
                }));
            }
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
            return tokens;
        }

        // ===================== VidFast =====================

        static readonly Regex VfFlightRx = new Regex(@"self\.__next_f\.push\(\[1,(""(?:[^""\\]|\\.)*"")\]\)", RegexOptions.Compiled);
        static readonly Regex VfScriptRx = new Regex(@"src=""(/_next/static/chunks/365-[a-zA-Z0-9-]+\.js)""", RegexOptions.Compiled);
        static readonly Regex VfRootRx = new Regex(@"""(/zirlaku/u/[A-Za-z0-9_-]{32,512})""", RegexOptions.Compiled);
        static readonly Regex VfCsrfRx = new Regex(@"""X-Csrf-Token"":""([A-Za-z0-9_-]{16,128})""", RegexOptions.Compiled);

        static async Task<List<StreamResult>> ResolveVidFastAsync(int tmdbId, bool isTv, int season, int episode, CancellationToken ct)
        {
            var embedPath = isTv ? $"/tv/{tmdbId}/{season}/{episode}" : $"/movie/{tmdbId}";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(16) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");

            var embedHtml = await HttpGetStr(http, VidFastOrigin + embedPath, VidFastOrigin + "/", ct).ConfigureAwait(false);
            if (embedHtml == null) return null;

            // Parse bootstrap from Flight data
            var flight = new StringBuilder();
            foreach (Match m in VfFlightRx.Matches(embedHtml))
            {
                try { var chunk = JsonSerializer.Deserialize<string>(m.Groups[1].Value); if (chunk != null) flight.Append(chunk); }
                catch { return null; }
            }

            string bToken = null;
            foreach (var line in flight.ToString().Split('\n'))
            {
                var idx = line.IndexOf(':');
                if (idx < 0) continue;
                var payload = line.Substring(idx + 1);
                if (!payload.StartsWith("[\"$\",\"$L")) continue;
                try
                {
                    using var d = JsonDocument.Parse(payload);
                    var arr = d.RootElement;
                    if (arr.GetArrayLength() != 4) continue;
                    var props = arr[3];
                    if (!props.TryGetProperty("en", out var enEl)) continue;
                    if (bToken != null) return null;
                    bToken = enEl.GetString();
                    if (string.IsNullOrEmpty(bToken) || bToken.Length < 16 || bToken.Length > 4096) return null;
                    if (Str(props, "host") != "vidfast.vc") return null;
                    if (Str(props, "id") != tmdbId.ToString()) return null;
                    if (isTv && (Str(props, "season") != season.ToString() || Str(props, "episode") != episode.ToString())) return null;
                }
                catch { }
            }
            if (bToken == null) return null;

            var scriptMatch = VfScriptRx.Match(embedHtml);
            if (!scriptMatch.Success) return null;
            var playerScriptUrl = VidFastOrigin + scriptMatch.Groups[1].Value;

            var scriptBody = await HttpGetBytes(http, playerScriptUrl, VidFastOrigin + "/", ct).ConfigureAwait(false);
            if (scriptBody == null) return null;

            byte[] scriptHash;
            using (var sha = SHA256.Create()) scriptHash = sha.ComputeHash(scriptBody);
            if (BitConverter.ToString(scriptHash).Replace("-", "").ToLowerInvariant() != VidFastPlayerDigest)
                return null;

            var scriptText = Encoding.UTF8.GetString(scriptBody);
            var rootMatch = VfRootRx.Match(scriptText);
            var csrfMatch = VfCsrfRx.Match(scriptText);
            if (!rootMatch.Success || !csrfMatch.Success) return null;
            var root = rootMatch.Groups[1].Value;
            var csrf = csrfMatch.Groups[1].Value;

            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);

            var packed = VidFastCrypto.PackRequest(bToken, salt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (packed == null) return null;

            var challengeBody = await VfPost(http, VidFastOrigin + root + "/JgQXcWPNH7s/" + packed, csrf, ct).ConfigureAwait(false);
            if (challengeBody == null) return null;

            var challengeToken = VidFastCrypto.ChallengeToken(challengeBody);
            if (challengeToken == null) return null;

            var serverListBody = await VfPost(http, VidFastOrigin + root + "/dBWKuO8aW6U/" + challengeToken, csrf, ct).ConfigureAwait(false);
            if (serverListBody == null) return null;

            var serverListPlain = VidFastCrypto.DecryptEnvelope(Encoding.UTF8.GetString(serverListBody));
            if (serverListPlain == null) return null;

            JsonElement serverArr;
            try
            {
                using var d = JsonDocument.Parse(serverListPlain);
                if (d.RootElement.ValueKind != JsonValueKind.Array || d.RootElement.GetArrayLength() == 0) return null;
                serverArr = d.RootElement.Clone();
            }
            catch { return null; }

            var results = new List<StreamResult>();
            foreach (var srv in serverArr.EnumerateArray())
            {
                var sName = Str(srv, "name");
                var sData = Str(srv, "data");
                if (string.IsNullOrEmpty(sName) || string.IsNullOrEmpty(sData)) continue;
                try
                {
                    var unlockBody = await VfPost(http, VidFastOrigin + root + "/M6SyMXJBpeg/" + sData, csrf, ct).ConfigureAwait(false);
                    if (unlockBody == null) continue;

                    var srcPlain = VidFastCrypto.DecryptEnvelope(Encoding.UTF8.GetString(unlockBody));
                    if (srcPlain == null) continue;

                    using var sd = JsonDocument.Parse(srcPlain);
                    var sUrl = Str(sd.RootElement, "url");
                    if (string.IsNullOrEmpty(sUrl) || !Uri.TryCreate(sUrl, UriKind.Absolute, out var sUri)) continue;

                    var fmt = "hls";
                    var path = sUri.AbsolutePath.ToLowerInvariant();
                    if (path.EndsWith(".mp4")) fmt = "mp4";
                    else if (path.EndsWith(".mpd")) fmt = "dash";
                    if (sd.RootElement.TryGetProperty("mp4", out var mp4v) && mp4v.ValueKind == JsonValueKind.True)
                        fmt = "mp4";

                    var hdrs = new Dictionary<string, string>();
                    bool noRef = sd.RootElement.TryGetProperty("noReferrer", out var nr) && nr.ValueKind == JsonValueKind.True;
                    if (!noRef) hdrs["Referer"] = VidFastOrigin + "/";

                    results.Add(new StreamResult
                    {
                        Url = sUrl,
                        Format = fmt,
                        Server = "vidfast:" + sName,
                        Quality = "auto",
                        Headers = hdrs.Count > 0 ? hdrs : null,
                    });
                }
                catch { }
            }

            return results.Count > 0 ? results : null;
        }

        static async Task<byte[]> VfPost(HttpClient http, string url, string csrf, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Referer", VidFastOrigin + "/");
            if (!string.IsNullOrEmpty(csrf))
                req.Headers.TryAddWithoutValidation("X-Csrf-Token", csrf);
            var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        // ===================== Shared Helpers =====================

        static async Task<string> HttpGetStr(HttpClient http, string url, string referer, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", referer);
            var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        static async Task<byte[]> HttpGetBytes(HttpClient http, string url, string referer, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", referer);
            var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        static Dictionary<string, string> ParseHeaders(JsonElement el, string prop)
        {
            var dict = new Dictionary<string, string>();
            if (el.TryGetProperty(prop, out var headers) && headers.ValueKind == JsonValueKind.Object)
                foreach (var h in headers.EnumerateObject())
                    if (h.Value.ValueKind == JsonValueKind.String)
                        dict[h.Name] = h.Value.GetString();
            return dict.Count > 0 ? dict : null;
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        static byte[] HexDecode(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }

    // ===================== WASM Key Extraction =====================

    static class WasmKeyExtractor
    {
        public static byte[] ExtractKey(byte[] module)
        {
            if (module == null || module.Length > 128 * 1024 || module.Length < 8) return null;
            if (module[0] != 0 || module[1] != 97 || module[2] != 115 || module[3] != 109 ||
                module[4] != 1 || module[5] != 0 || module[6] != 0 || module[7] != 0) return null;

            byte[] dataPayload = null;
            int pos = 8;
            while (pos < module.Length)
            {
                if (pos >= module.Length) return null;
                byte id = module[pos++];
                uint size = ReadULEB128(module, ref pos);
                if (size == uint.MaxValue || pos + (int)size > module.Length) return null;
                if (id == 11)
                {
                    dataPayload = new byte[size];
                    Buffer.BlockCopy(module, pos, dataPayload, 0, (int)size);
                }
                pos += (int)size;
            }
            if (dataPayload == null) return null;

            var memory = new byte[4096];
            var occupied = new bool[4096];
            int dp = 0;
            uint segCount = ReadULEB128(dataPayload, ref dp);
            if (segCount == uint.MaxValue || segCount > 32) return null;

            for (uint i = 0; i < segCount; i++)
            {
                uint mode = ReadULEB128(dataPayload, ref dp);
                if (mode != 0) return null;
                if (dp >= dataPayload.Length || dataPayload[dp] != 0x41) return null;
                dp++;
                uint offset = ReadSignedPositive(dataPayload, ref dp);
                if (offset == uint.MaxValue) return null;
                if (dp >= dataPayload.Length || dataPayload[dp] != 0x0b) return null;
                dp++;
                uint segSize = ReadULEB128(dataPayload, ref dp);
                if (segSize == uint.MaxValue || offset + segSize > 4096 || dp + (int)segSize > dataPayload.Length) return null;

                Buffer.BlockCopy(dataPayload, dp, memory, (int)offset, (int)segSize);
                for (uint j = offset; j < offset + segSize; j++)
                    occupied[j] = true;
                dp += (int)segSize;
            }

            for (int i = 0; i < 32; i++)
                if (!occupied[i]) return null;

            var candidates = new List<uint>();
            for (uint off = 512; off <= 3968; off += 128)
            {
                bool valid = true;
                for (int i = 0; i < 32 && valid; i++)
                    if (!occupied[off + i]) valid = false;
                if (valid) candidates.Add(off);
            }
            if (candidates.Count == 0) return null;

            var key = new byte[32];
            for (int i = 0; i < 32; i++)
                key[i] = (byte)(memory[i] ^ memory[candidates[0] + i]);
            return key;
        }

        static uint ReadULEB128(byte[] data, ref int pos)
        {
            uint value = 0;
            for (int i = 0; i < 5; i++)
            {
                if (pos >= data.Length) return uint.MaxValue;
                byte b = data[pos++];
                if (i == 4 && b > 15) return uint.MaxValue;
                value |= (uint)(b & 127) << (7 * i);
                if ((b & 128) == 0) return value;
            }
            return uint.MaxValue;
        }

        static uint ReadSignedPositive(byte[] data, ref int pos)
        {
            uint value = 0;
            for (int i = 0; i < 5; i++)
            {
                if (pos >= data.Length) return uint.MaxValue;
                byte b = data[pos++];
                if (i == 4 && b > 7) return uint.MaxValue;
                value |= (uint)(b & 127) << (7 * i);
                if ((b & 128) == 0)
                {
                    if ((b & 64) != 0) return uint.MaxValue;
                    return value;
                }
            }
            return uint.MaxValue;
        }
    }

    // ===================== ChaCha20 IETF =====================

    static class IetfChaCha20
    {
        public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext)
        {
            var plain = new byte[ciphertext.Length];
            var block = new byte[64];
            uint counter = 0;
            for (int i = 0; i < ciphertext.Length; i += 64)
            {
                GenBlock(key, counter, nonce, block);
                counter++;
                int len = Math.Min(64, ciphertext.Length - i);
                for (int j = 0; j < len; j++)
                    plain[i + j] = (byte)(ciphertext[i + j] ^ block[j]);
            }
            return plain;
        }

        static void GenBlock(byte[] key, uint counter, byte[] nonce, byte[] output)
        {
            var s = new uint[16];
            s[0] = 0x61707865; s[1] = 0x3320646e; s[2] = 0x79622d32; s[3] = 0x6b206574;
            for (int i = 0; i < 8; i++) s[4 + i] = LE32(key, i * 4);
            s[12] = counter;
            s[13] = LE32(nonce, 0); s[14] = LE32(nonce, 4); s[15] = LE32(nonce, 8);

            var w = (uint[])s.Clone();
            for (int i = 0; i < 10; i++)
            {
                QR(w, 0, 4, 8, 12); QR(w, 1, 5, 9, 13); QR(w, 2, 6, 10, 14); QR(w, 3, 7, 11, 15);
                QR(w, 0, 5, 10, 15); QR(w, 1, 6, 11, 12); QR(w, 2, 7, 8, 13); QR(w, 3, 4, 9, 14);
            }
            for (int i = 0; i < 16; i++) ToLE32(w[i] + s[i], output, i * 4);
        }

        static void QR(uint[] s, int a, int b, int c, int d)
        {
            s[a] += s[b]; s[d] ^= s[a]; s[d] = RotL(s[d], 16);
            s[c] += s[d]; s[b] ^= s[c]; s[b] = RotL(s[b], 12);
            s[a] += s[b]; s[d] ^= s[a]; s[d] = RotL(s[d], 8);
            s[c] += s[d]; s[b] ^= s[c]; s[b] = RotL(s[b], 7);
        }

        static uint RotL(uint v, int n) => (v << n) | (v >> (32 - n));
        static uint LE32(byte[] b, int o) => (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);
        static void ToLE32(uint v, byte[] b, int o) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    }

    // ===================== VidFast Crypto =====================

    static class VidFastCrypto
    {
        static readonly byte[] ReqKey = Hex("d9e8463c1896176b48c12fb2a45216013074cb6003894c2aacf320f633cdfea8");
        static readonly byte[] ReqIV = Hex("48096b61504315115fc8ff0edbe47a92");
        static readonly byte[] ReqSeed = Hex("ffb6b223470f60");

        public static string PackRequest(string token, byte[] salt, long timestamp)
        {
            if (salt.Length != 16 || string.IsNullOrEmpty(token)) return null;

            var tsBytes = BitConverter.GetBytes(timestamp);
            if (!BitConverter.IsLittleEndian) Array.Reverse(tsBytes);

            var plain = Concat(salt, tsBytes, Encoding.UTF8.GetBytes(token));
            int pad = 16 - plain.Length % 16;
            var padded = new byte[plain.Length + pad];
            Buffer.BlockCopy(plain, 0, padded, 0, plain.Length);
            for (int i = plain.Length; i < padded.Length; i++) padded[i] = (byte)pad;

            byte[] payload;
            using (var aes = Aes.Create())
            {
                aes.Key = ReqKey; aes.IV = ReqIV; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
                using var enc = aes.CreateEncryptor();
                payload = enc.TransformFinalBlock(padded, 0, padded.Length);
            }

            var mask = Sha256(Concat(ReqSeed, salt));
            for (int i = 0; i < payload.Length; i++)
            {
                if (i > 0 && i % 32 == 0) mask = Sha256(mask);
                payload[i] ^= mask[i % 32];
            }

            var rotMask = Sha256(Concat(ReqKey, salt));
            for (int i = 0; i < payload.Length; i++)
            {
                byte m = rotMask[i % 32];
                payload[i] = (byte)(RotL8(payload[i], m & 7) + (m ^ 165));
            }

            var sbox = Permutation(256, Concat(salt, ReqSeed, ReqIV));
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)sbox[payload[i]];

            int numBlocks = payload.Length / 16;
            var blockOrder = Permutation(numBlocks, Concat(ReqSeed, salt));
            var shuffled = new byte[payload.Length];
            var orderBytes = new byte[numBlocks * 4];
            for (int i = 0; i < numBlocks; i++)
            {
                Buffer.BlockCopy(payload, blockOrder[i] * 16, shuffled, i * 16, 16);
                var ob = BitConverter.GetBytes((uint)blockOrder[i]);
                if (!BitConverter.IsLittleEndian) Array.Reverse(ob);
                Buffer.BlockCopy(ob, 0, orderBytes, i * 4, 4);
            }

            var byteOrder = Permutation(payload.Length, Concat(ReqKey, salt, new[] { (byte)payload.Length }));
            for (int i = 0; i < payload.Length; i++) payload[i] = shuffled[byteOrder[i]];

            var cksum = Sha256(Concat(orderBytes, payload));
            var header = new byte[19];
            header[0] = 1;
            Buffer.BlockCopy(salt, 0, header, 1, 16);
            var bc = BitConverter.GetBytes((ushort)numBlocks);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bc);
            Buffer.BlockCopy(bc, 0, header, 17, 2);

            return CustomB64(Concat(header, orderBytes, payload, Sub(cksum, 0, 8)));
        }

        public static string ChallengeToken(byte[] response)
        {
            if (response == null || response.Length == 0 || response.Length > 4096) return null;
            foreach (byte b in response) if (b > 127 || b < 32) return null;

            var reversed = (byte[])response.Clone();
            Array.Reverse(reversed);
            var payload = Encoding.ASCII.GetBytes(BitConverter.ToString(reversed).Replace("-", "").ToLowerInvariant());

            var stages = new (string key, string prefix, byte[] ops, string rc4)[]
            {
                ("JZltXDoh7Eags8L7e0hyBaX6HTZ4ErE1rydu4MTAhx0=", "2PcZ/vEJN24=", new byte[]{0,1,2,3,4,1,4,3,4,0}, "wCrbGEDVWBGRVggKP8pODbU55I28p/AnwOqtkKsAt8Q="),
                ("svlpt4PNgksvqHbiResvgC9SO7RZzgmm+TK1uXVNkAw=", "p3uks0I=", new byte[]{3,2,0,8,1,8,3,5,2,6}, "4yIb/l2zh9Qz62DrakKcfjlcgmldjWIIfBadCsL/blo="),
                ("T1CiTh7ajlEieB/+USBMBpwFLQqBy7r+u0v+LEDGDrQ=", "iXAPz1fd9GQ=", new byte[]{5,2,4,6,2,7,4,5,6,8}, "xj1hV8IXKy38jEOtXojtsDXi3+Sr2D2ve2GhbcHKvLI="),
                ("oO0GK4QVPEEc7k2uGOPwQ9+uO+g0cWi2tupQfOZH5m4=", "f7prYx4j", new byte[]{2,3,7,3,6,6,0,7,6,9}, "1OqtIu7/lgRxDn1zaGDc8PtVF0cIJZNOsiNse4qxKqQ="),
                ("oa2189LasXo4boT0vDgZDE02/YMqD1qtaXZQzM7MStw=", "xDGTyVE=", new byte[]{10,6,9,4,10,4,0,9,6,2}, "yOwHRwrYyRB1fwQFiSRjzVFZpWatYUr8xBzZP0zdpO4="),
            };

            foreach (var (keyB64, prefixB64, ops, rc4B64) in stages)
            {
                var stageKey = Convert.FromBase64String(keyB64);
                var prefix = Convert.FromBase64String(prefixB64);
                var transformed = new List<byte>(payload.Length + prefix.Length);
                for (int i = 0; i < payload.Length; i++)
                {
                    if (i < prefix.Length) transformed.Add(prefix[i]);
                    transformed.Add(TransformByte((byte)(payload[i] ^ stageKey[i % 32]), ops[i % 10]));
                }
                payload = Rc4(Convert.FromBase64String(rc4B64), transformed.ToArray());
            }

            return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] DecryptEnvelope(string encoded)
        {
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 512 * 1024) return null;
            byte[] packet;
            try { packet = Convert.FromBase64String(encoded); } catch { return null; }
            if (packet.Length < 52) return null;

            var baseKey = ResponseKey();
            var salt = Sub(packet, 0, 16);
            var nonce = Sub(packet, 16, 12);
            var rest = Sub(packet, 28, packet.Length - 28);
            if (rest.Length < 16) return null;

            var derivedKey = Sha256(Concat(baseKey, salt));
            var ct = Sub(rest, 0, rest.Length - 16);
            var tag = Sub(rest, rest.Length - 16, 16);

            try
            {
                var plain = new byte[ct.Length];
                using var gcm = new AesGcm(derivedKey);
                gcm.Decrypt(nonce, ct, tag, plain);
                if (plain.Length < 8) return null;
                return Sub(plain, 8, plain.Length - 8);
            }
            catch { return null; }
        }

        static byte[] ResponseKey()
        {
            var seed = Hex("38f6913e38cf38e7c90ea6f1fa31bf7501108effc57f6cd778a3b9dd68e3f51c");
            var parts = new List<byte>(seed);
            foreach (ulong v in new ulong[] { 9633601958, 3000935290 })
            {
                var b = BitConverter.GetBytes(v);
                if (!BitConverter.IsLittleEndian) Array.Reverse(b);
                parts.AddRange(b);
            }
            return Sha256(parts.ToArray());
        }

        static byte TransformByte(byte value, byte op)
        {
            switch (op)
            {
                case 0: return (byte)(value - 226);
                case 1: return (byte)(value - 76);
                case 2: return RotL8(value, 2);
                case 3: return (byte)(value - 200);
                case 4: return (byte)(value ^ 59);
                case 5: return (byte)(value - 218);
                case 6: return RotL8(value, 1);
                case 7: return RotL8(value, 6);
                case 8: return (byte)(value + 230);
                case 9: return RotL8(value, 7);
                case 10: return (byte)(value - 216);
                default: return value;
            }
        }

        static byte RotL8(byte v, int n) => (byte)((v << (n & 7)) | (v >> (8 - (n & 7))));

        static byte[] Rc4(byte[] key, byte[] input)
        {
            var s = new byte[256];
            for (int i = 0; i < 256; i++) s[i] = (byte)i;
            byte j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (byte)(j + s[i] + key[i % key.Length]);
                var t = s[i]; s[i] = s[j]; s[j] = t;
            }
            var result = new byte[input.Length];
            byte a = 0, b = 0;
            for (int i = 0; i < input.Length; i++)
            {
                a++;
                b = (byte)(b + s[a]);
                var t = s[a]; s[a] = s[b]; s[b] = t;
                result[i] = (byte)(input[i] ^ s[(byte)(s[a] + s[b])]);
            }
            return result;
        }

        static int[] Permutation(int n, byte[] seed)
        {
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            var next = SplitPRNG(seed);
            for (int i = n - 1; i > 0; i--)
            {
                int j = (int)(next() % (uint)(i + 1));
                var t = order[i]; order[i] = order[j]; order[j] = t;
            }
            return order;
        }

        static Func<uint> SplitPRNG(byte[] seed)
        {
            var h = Sha256(seed);
            uint state = LE32(h, 0) ^ LE32(h, 4) ^ LE32(h, 8) ^ LE32(h, 12);
            if (state == 0) state = 0x9e3779b9;
            return () => { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; };
        }

        static string CustomB64(byte[] data)
        {
            const string std = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            const string custom = "W_Anx1lGLyJEfTOU3Xeqdh58ki9SHjPm4-Yts0rvCbBg6cDRVFzQNoZI2wMapKu7";
            var b64 = Convert.ToBase64String(data).TrimEnd('=');
            var sb = new StringBuilder(b64.Length);
            foreach (var ch in b64) { int idx = std.IndexOf(ch); sb.Append(idx >= 0 ? custom[idx] : ch); }
            return sb.ToString();
        }

        static byte[] Sha256(byte[] data) { using var sha = SHA256.Create(); return sha.ComputeHash(data); }
        static uint LE32(byte[] b, int o) => (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);

        static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        static byte[] Concat(params byte[][] parts)
        {
            int total = 0; foreach (var p in parts) total += p.Length;
            var result = new byte[total]; int pos = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, result, pos, p.Length); pos += p.Length; }
            return result;
        }

        static byte[] Sub(byte[] data, int offset, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(data, offset, result, 0, length);
            return result;
        }
    }

    // ===================== NaCl (XSalsa20-Poly1305) =====================

    static class NaCl
    {
        public static byte[] SecretboxSeal(byte[] message, byte[] nonce, byte[] key)
        {
            var padded = new byte[32 + message.Length];
            Buffer.BlockCopy(message, 0, padded, 32, message.Length);

            var cipher = XSalsa20(padded, nonce, key);

            var polyKey = new byte[32];
            Buffer.BlockCopy(cipher, 0, polyKey, 0, 32);

            var ciphertext = new byte[cipher.Length - 32];
            Buffer.BlockCopy(cipher, 32, ciphertext, 0, ciphertext.Length);

            var mac = Poly1305(polyKey, ciphertext);

            var result = new byte[16 + ciphertext.Length];
            Buffer.BlockCopy(mac, 0, result, 0, 16);
            Buffer.BlockCopy(ciphertext, 0, result, 16, ciphertext.Length);
            return result;
        }

        static byte[] XSalsa20(byte[] data, byte[] nonce, byte[] key)
        {
            var subNonce = new byte[16];
            Buffer.BlockCopy(nonce, 0, subNonce, 0, 16);
            var subKey = HSalsa20(key, subNonce);
            var streamNonce = new byte[8];
            Buffer.BlockCopy(nonce, 16, streamNonce, 0, 8);
            return Salsa20Encrypt(data, subKey, streamNonce);
        }

        static byte[] HSalsa20(byte[] key, byte[] nonce)
        {
            var state = new uint[16];
            state[0] = 0x61707865;
            state[1] = LE32(key, 0);  state[2] = LE32(key, 4);
            state[3] = LE32(key, 8);  state[4] = LE32(key, 12);
            state[5] = 0x3320646e;
            state[6] = LE32(nonce, 0);  state[7] = LE32(nonce, 4);
            state[8] = LE32(nonce, 8);  state[9] = LE32(nonce, 12);
            state[10] = 0x79622d32;
            state[11] = LE32(key, 16);  state[12] = LE32(key, 20);
            state[13] = LE32(key, 24);  state[14] = LE32(key, 28);
            state[15] = 0x6b206574;

            Salsa20Core(state);

            var result = new byte[32];
            ToLE32(state[0], result, 0);   ToLE32(state[5], result, 4);
            ToLE32(state[10], result, 8);  ToLE32(state[15], result, 12);
            ToLE32(state[6], result, 16);  ToLE32(state[7], result, 20);
            ToLE32(state[8], result, 24);  ToLE32(state[9], result, 28);
            return result;
        }

        static byte[] Salsa20Encrypt(byte[] data, byte[] key, byte[] nonce)
        {
            var output = new byte[data.Length];
            var block = new byte[64];

            for (long blockIdx = 0; blockIdx < data.Length; blockIdx += 64)
            {
                var state = new uint[16];
                state[0] = 0x61707865;
                state[1] = LE32(key, 0);  state[2] = LE32(key, 4);
                state[3] = LE32(key, 8);  state[4] = LE32(key, 12);
                state[5] = 0x3320646e;
                state[6] = LE32(nonce, 0);  state[7] = LE32(nonce, 4);
                state[8] = (uint)(blockIdx / 64);
                state[9] = (uint)((blockIdx / 64) >> 32);
                state[10] = 0x79622d32;
                state[11] = LE32(key, 16);  state[12] = LE32(key, 20);
                state[13] = LE32(key, 24);  state[14] = LE32(key, 28);
                state[15] = 0x6b206574;

                var working = (uint[])state.Clone();
                Salsa20Core(working);
                for (int i = 0; i < 16; i++) working[i] += state[i];
                for (int i = 0; i < 16; i++) ToLE32(working[i], block, i * 4);

                int len = (int)Math.Min(64, data.Length - blockIdx);
                for (int i = 0; i < len; i++)
                    output[blockIdx + i] = (byte)(data[blockIdx + i] ^ block[i]);
            }
            return output;
        }

        static void Salsa20Core(uint[] state)
        {
            for (int i = 0; i < 10; i++)
            {
                QR(state, 0, 4, 8, 12); QR(state, 5, 9, 13, 1);
                QR(state, 10, 14, 2, 6); QR(state, 15, 3, 7, 11);
                QR(state, 0, 1, 2, 3); QR(state, 5, 6, 7, 4);
                QR(state, 10, 11, 8, 9); QR(state, 15, 12, 13, 14);
            }
        }

        static void QR(uint[] s, int a, int b, int c, int d)
        {
            s[b] ^= RotL(s[a] + s[d], 7);
            s[c] ^= RotL(s[b] + s[a], 9);
            s[d] ^= RotL(s[c] + s[b], 13);
            s[a] ^= RotL(s[d] + s[c], 18);
        }

        static uint RotL(uint v, int n) => (v << n) | (v >> (32 - n));

        static byte[] Poly1305(byte[] key, byte[] message)
        {
            var rBytes = new byte[17];
            Buffer.BlockCopy(key, 0, rBytes, 0, 16);
            rBytes[3] &= 15; rBytes[7] &= 15; rBytes[11] &= 15; rBytes[15] &= 15;
            rBytes[4] &= 252; rBytes[8] &= 252; rBytes[12] &= 252;

            var sBytes = new byte[17];
            Buffer.BlockCopy(key, 16, sBytes, 0, 16);

            var r = new BigInteger(rBytes);
            var s = new BigInteger(sBytes);
            var p = (BigInteger.One << 130) - 5;

            var acc = BigInteger.Zero;
            for (int i = 0; i < message.Length; i += 16)
            {
                int blockLen = Math.Min(16, message.Length - i);
                var blockBytes = new byte[blockLen + 2];
                Buffer.BlockCopy(message, i, blockBytes, 0, blockLen);
                blockBytes[blockLen] = 1;
                var n = new BigInteger(blockBytes);
                acc = ((acc + n) * r) % p;
            }

            acc += s;
            var result = acc.ToByteArray();
            var mac = new byte[16];
            int copyLen = Math.Min(16, result.Length);
            Buffer.BlockCopy(result, 0, mac, 0, copyLen);
            return mac;
        }

        static uint LE32(byte[] b, int offset) =>
            (uint)b[offset] | ((uint)b[offset + 1] << 8) | ((uint)b[offset + 2] << 16) | ((uint)b[offset + 3] << 24);

        static void ToLE32(uint v, byte[] b, int offset)
        {
            b[offset] = (byte)v;
            b[offset + 1] = (byte)(v >> 8);
            b[offset + 2] = (byte)(v >> 16);
            b[offset + 3] = (byte)(v >> 24);
        }
    }
}
