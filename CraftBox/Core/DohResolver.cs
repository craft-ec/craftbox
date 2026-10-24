using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CraftBox.Core
{
    /// DNS-over-HTTPS resolver + HTTP fetch with correct TLS SNI for Cloudflare-proxied domains.
    /// The TV's router poisons DNS for torrent/media sites, so this resolves via dns.google
    /// and connects to the real IP with SocketsHttpHandler.SslOptions.TargetHost for correct SNI.
    static class DohResolver
    {
        static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        static readonly object _lock = new object();

        public static async Task<string> ResolveAsync(string host, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(host, out var cached))
                    return cached;
            }
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                var url = $"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=A";
                var json = await client.GetStringAsync(url).ConfigureAwait(false);
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("Answer", out var answers))
                    {
                        foreach (var a in answers.EnumerateArray())
                        {
                            if (a.TryGetProperty("type", out var t) && t.GetInt32() == 1 &&
                                a.TryGetProperty("data", out var d))
                            {
                                var ip = d.GetString();
                                lock (_lock) { _cache[host] = ip; }
                                return ip;
                            }
                        }
                    }
                }
            }
            return null;
        }

        /// Fetch a URL via DOH-resolved IP with correct TLS SNI. Follows redirects through DoH.
        public static async Task<string> FetchAsync(string url, CancellationToken ct)
        {
            var data = await FetchCoreAsync(url, ct).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(data);
        }

        /// Fetch binary content (images, .torrent files, etc.) via DOH-resolved IP.
        public static async Task<byte[]> FetchBytesAsync(string url, CancellationToken ct)
        {
            return await FetchCoreAsync(url, ct).ConfigureAwait(false);
        }

        static async Task<byte[]> FetchCoreAsync(string url, CancellationToken ct)
        {
            var uri = new Uri(url);
            for (int hop = 0; hop < 5; hop++)
            {
                var ip = await ResolveAsync(uri.Host, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(ip))
                    throw new Exception($"DOH resolution failed for {uri.Host}");
                var builder = new UriBuilder(uri) { Host = ip };
                var req = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                req.Headers.Host = uri.Host;
                using (var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = uri.Host,
                        RemoteCertificateValidationCallback = (_, __, ___, ____) => true,
                    },
                    ConnectTimeout = TimeSpan.FromSeconds(10),
                })
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) })
                using (var res = await client.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var code = (int)res.StatusCode;
                    if (code >= 300 && code < 400 && res.Headers.Location != null)
                    {
                        uri = res.Headers.Location.IsAbsoluteUri
                            ? res.Headers.Location
                            : new Uri(uri, res.Headers.Location);
                        continue;
                    }
                    if (!res.IsSuccessStatusCode)
                        throw new Exception($"HTTP {code} from {uri.Host}");
                    return await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            throw new Exception($"Too many redirects fetching {url}");
        }
    }
}
