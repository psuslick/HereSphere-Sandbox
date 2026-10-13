using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HereSphereSandbox
{
    public sealed class MediaRecord
    {
        public string Url;
        public Dictionary<string, string> Headers;
        public string Label;
        public DateTime ExpiresUtc;
    }

    internal sealed class RequestData
    {
        public string Method;
        public string Target;
        public string Version;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
    }

    internal sealed class ResolveResult
    {
        public string VideoUrl;
        public string AudioUrl;
        public string Title;
    }

    public static class Server
    {
        private static readonly JavaScriptSerializer Json = CreateJson();
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly ConcurrentDictionary<string, MediaRecord> Media = new ConcurrentDictionary<string, MediaRecord>();
        private static string _ytDlpPath;
        private static int _port;
        private static string _publicBaseUrl;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);


        private static JavaScriptSerializer CreateJson()
        {
            JavaScriptSerializer j = new JavaScriptSerializer();
            j.MaxJsonLength = Int32.MaxValue;
            j.RecursionLimit = 100;
            return j;
        }

        private static HttpClient CreateHttpClient()
        {
            HttpClientHandler handler = new HttpClientHandler();
            handler.AllowAutoRedirect = false;
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            handler.UseCookies = false;
            // A host-configured proxy can bypass the destination checks below.
            handler.UseProxy = false;
            HttpClient client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromHours(12);
            return client;
        }

        public static void Run(int port, string ytDlpPath, string publicBaseUrl)
        {
            _port = port;
            _ytDlpPath = ytDlpPath;
            _publicBaseUrl = String.IsNullOrWhiteSpace(publicBaseUrl) ? ("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)) : publicBaseUrl.TrimEnd('/');
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 64;

            Uri listenUri = new Uri(_publicBaseUrl);
            IPAddress listenAddress;
            if (!IPAddress.TryParse(listenUri.Host, out listenAddress))
                throw new InvalidOperationException("The Sandbox listener requires a numeric guest IP address.");
            TcpListener listener = new TcpListener(listenAddress, port);
            listener.Start(64);
            Console.WriteLine("HereSphere Sandbox service listening on 0.0.0.0:" + port.ToString(CultureInfo.InvariantCulture));

            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                client.NoDelay = true;
                Task.Run(() => HandleClient(client));
            }
        }

        private static void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    stream.ReadTimeout = 30000;
                    stream.WriteTimeout = 30000;
                    RequestData req = ReadRequest(stream);
                    if (req == null) return;
                    Dispatch(req, stream);
                }
                catch (Exception ex)
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        WriteJson(stream, 500, new Dictionary<string, object> {
                            { "success", false },
                            { "message", "Server request failed." }
                        });
                    }
                    catch { }
                    Console.WriteLine("Request error: " + ex.GetType().Name);
                }
            }
        }

        private static RequestData ReadRequest(NetworkStream stream)
        {
            MemoryStream header = new MemoryStream();
            int state = 0;
            while (header.Length < 65536)
            {
                int b = stream.ReadByte();
                if (b < 0) return null;
                header.WriteByte((byte)b);
                if (state == 0 && b == 13) state = 1;
                else if (state == 1 && b == 10) state = 2;
                else if (state == 2 && b == 13) state = 3;
                else if (state == 3 && b == 10) break;
                else state = (b == 13) ? 1 : 0;
            }
            if (header.Length >= 65536) throw new InvalidDataException("HTTP header too large.");

            string headerText = Encoding.ASCII.GetString(header.ToArray());
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;
            string[] first = lines[0].Split(' ');
            if (first.Length < 3) throw new InvalidDataException("Malformed request line.");

            RequestData req = new RequestData();
            req.Method = first[0].ToUpperInvariant();
            req.Target = first[1];
            req.Version = first[2];

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (String.IsNullOrEmpty(line)) break;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                if (req.Headers.ContainsKey(name)) throw new InvalidDataException("Duplicate HTTP header.");
                req.Headers[name] = line.Substring(colon + 1).Trim();
            }

            if (req.Headers.ContainsKey("Transfer-Encoding"))
                throw new InvalidDataException("Transfer-Encoding is unsupported.");
            if (req.Method == "POST" && !req.Headers.ContainsKey("Content-Length"))
                throw new InvalidDataException("POST requires Content-Length.");

            int contentLength = 0;
            string cl;
            if (req.Headers.TryGetValue("Content-Length", out cl))
            {
                if (!Int32.TryParse(cl, out contentLength) || contentLength < 0 || contentLength > 1048576)
                    throw new InvalidDataException("Invalid request Content-Length.");
            }
            if (contentLength > 0)
            {
                req.Body = new byte[contentLength];
                int offset = 0;
                while (offset < contentLength)
                {
                    int read = stream.Read(req.Body, offset, contentLength - offset);
                    if (read <= 0) throw new EndOfStreamException("Unexpected end of request body.");
                    offset += read;
                }
            }
            return req;
        }

        private static void Dispatch(RequestData req, NetworkStream stream)
        {
            Uri targetUri;
            if (!Uri.TryCreate("http://localhost" + req.Target, UriKind.Absolute, out targetUri))
            {
                WriteJson(stream, 400, Error("Invalid request target."));
                return;
            }
            string path = targetUri.AbsolutePath;

            // The native HereSphere client needs no browser CORS permission. A public
            // web page must not be able to read this guest's local media proxy.
            if (req.Headers.ContainsKey("Origin"))
            {
                WriteJson(stream, 403, Error("Browser cross-origin requests are disabled."));
                return;
            }

            if (path == "/connection_test")
            {
                WriteJson(stream, 200, new Dictionary<string, object> { { "success", true } });
                return;
            }

            if (path == "/health")
            {
                WriteJson(stream, 200, new Dictionary<string, object> {
                    { "success", true }, { "service", "heresphere-sandbox" }, { "mediaRecords", Media.Count }
                });
                return;
            }

            if (path == "/" && req.Method == "POST")
            {
                // Compatibility fallback: some Link Server clients POST directly to the configured base URL.
                HandleStream(req, stream);
                return;
            }

            if (path == "/" && req.Method == "GET")
            {
                string html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>HereSphere Sandbox Server</title></head>" +
                    "<body style=\"font-family:Segoe UI,Arial;max-width:850px;margin:40px auto;line-height:1.5\">" +
                    "<h1>HereSphere Sandbox Server</h1><p><b>Status:</b> running inside Windows Sandbox.</p>" +
                    "<p>Set HereSphere Link Server to <code>" + WebUtility.HtmlEncode(_publicBaseUrl) + "</code>.</p>" +
                    "<p>This alpha build supports Link Server streaming through <code>/stream</code>. Direct media URLs, yt-dlp extraction, static HTML media discovery, byte ranges, and HLS playlist rewriting are supported.</p>" +
                    "<p>Downloading and a persistent library are intentionally disabled in this disposable build.</p></body></html>";
                WriteBytes(stream, 200, "text/html; charset=utf-8", Utf8.GetBytes(html), null);
                return;
            }

            if (path == "/stream" && req.Method == "POST")
            {
                HandleStream(req, stream);
                return;
            }

            if (path == "/download" && req.Method == "POST")
            {
                WriteJson(stream, 200, new Dictionary<string, object> {
                    { "success", false },
                    { "message", "Downloads are disabled in this disposable Sandbox build. Streaming is supported." }
                });
                return;
            }

            if (path.StartsWith("/media/", StringComparison.Ordinal) && (req.Method == "GET" || req.Method == "HEAD"))
            {
                string token = path.Substring("/media/".Length);
                HandleMedia(token, req, stream);
                return;
            }

            if (path == "/heresphere")
            {
                WriteJson(stream, 501, new Dictionary<string, object> {
                    { "success", false },
                    { "message", "Optimized HereSphere library browsing is not implemented in this alpha build; use the Link Server streaming function." }
                });
                return;
            }

            WriteJson(stream, 404, Error("Not found."));
        }

        private static void HandleStream(RequestData req, NetworkStream stream)
        {
            try
            {
                string body = Utf8.GetString(req.Body ?? new byte[0]);
                object rootObj = Json.DeserializeObject(body);
                Dictionary<string, object> root = rootObj as Dictionary<string, object>;
                if (root == null || !root.ContainsKey("url") || root["url"] == null)
                {
                    WriteJson(stream, 200, Error("Request must contain a url field."));
                    return;
                }
                string inputUrl = Convert.ToString(root["url"], CultureInfo.InvariantCulture);
                Uri safeInput;
                string reason;
                if (!TryValidatePublicHttpUri(inputUrl, out safeInput, out reason))
                {
                    WriteJson(stream, 200, Error("URL rejected by network safety policy: " + reason));
                    return;
                }

                ResolveResult result = Resolve(safeInput);
                Dictionary<string, object> response = new Dictionary<string, object>();
                response["success"] = true;
                response["videoUrl"] = result.VideoUrl;
                response["audioUrl"] = result.AudioUrl;
                if (!String.IsNullOrEmpty(result.Title)) response["title"] = result.Title;
                WriteJson(stream, 200, response);
            }
            catch (Exception ex)
            {
                // Upstream exceptions can contain signed URLs or transient headers.
                Console.WriteLine("Resolve failed: " + ex.GetType().Name);
                WriteJson(stream, 200, Error("Could not resolve a playable public media source."));
            }
        }

        private static ResolveResult Resolve(Uri input)
        {
            if (LooksLikeDirectMedia(input))
            {
                string token = RegisterMedia(input.ToString(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "direct");
                return new ResolveResult { VideoUrl = LocalMediaUrl(token), AudioUrl = null, Title = Path.GetFileName(input.AbsolutePath) };
            }

            Exception ytdlpError = null;
            try
            {
                ResolveResult byYtDlp = ResolveWithYtDlp(input.ToString());
                if (byYtDlp != null && !String.IsNullOrEmpty(byYtDlp.VideoUrl)) return byYtDlp;
            }
            catch (Exception ex)
            {
                ytdlpError = ex;
                Console.WriteLine("yt-dlp did not resolve URL: " + ex.GetType().Name);
            }

            try
            {
                ResolveResult generic = ResolveGenericHtml(input);
                if (generic != null && !String.IsNullOrEmpty(generic.VideoUrl)) return generic;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Generic static discovery failed: " + ex.GetType().Name);
            }

            if (ytdlpError != null) throw new InvalidOperationException("No playable public media was discovered.");
            throw new InvalidOperationException("No playable public media was discovered.");
        }

        private static ResolveResult ResolveWithYtDlp(string url)
        {
            if (String.IsNullOrEmpty(_ytDlpPath) || !File.Exists(_ytDlpPath)) throw new FileNotFoundException("yt-dlp.exe is unavailable.");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _ytDlpPath;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.Arguments = "--no-config --no-plugin-dirs --no-remote-components --no-cache-dir --no-playlist --skip-download --dump-single-json --no-warnings --format " +
                QuoteArg("bestvideo*+bestaudio/best") + " -- " + QuoteArg(url);

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.Start();
                Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("yt-dlp timed out after 60 seconds.");
                }
                Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 5000);
                string stdout = stdoutTask.Result;
                string stderr = stderrTask.Result;
                if (p.ExitCode != 0 || String.IsNullOrWhiteSpace(stdout))
                    throw new InvalidOperationException("yt-dlp exit " + p.ExitCode + ".");

                object obj = Json.DeserializeObject(stdout);
                Dictionary<string, object> info = obj as Dictionary<string, object>;
                if (info == null) throw new InvalidDataException("yt-dlp returned an unexpected JSON shape.");
                return BuildFromYtDlpInfo(info);
            }
        }

        private static ResolveResult BuildFromYtDlpInfo(Dictionary<string, object> info)
        {
            string title = GetString(info, "title");
            Dictionary<string, string> topHeaders = ExtractHeaders(info);
            string video = null;
            string audio = null;

            object requestedObj;
            if (info.TryGetValue("requested_formats", out requestedObj))
            {
                IEnumerable requested = requestedObj as IEnumerable;
                if (requested != null && !(requestedObj is string))
                {
                    foreach (object entryObj in requested)
                    {
                        Dictionary<string, object> f = entryObj as Dictionary<string, object>;
                        if (f == null) continue;
                        string mediaUrl = GetString(f, "url");
                        if (String.IsNullOrEmpty(mediaUrl)) continue;
                        Uri checkedUri;
                        string reason;
                        if (!TryValidatePublicHttpUri(mediaUrl, out checkedUri, out reason)) continue;
                        string vcodec = GetString(f, "vcodec");
                        string acodec = GetString(f, "acodec");
                        Dictionary<string, string> headers = ExtractHeaders(f);
                        if (headers.Count == 0) headers = topHeaders;
                        if (vcodec != null && vcodec != "none" && video == null)
                            video = LocalMediaUrl(RegisterMedia(checkedUri.ToString(), headers, "video"));
                        if (acodec != null && acodec != "none" && (vcodec == null || vcodec == "none") && audio == null)
                            audio = LocalMediaUrl(RegisterMedia(checkedUri.ToString(), headers, "audio"));
                    }
                }
            }

            if (video == null)
            {
                string single = GetString(info, "url");
                if (!String.IsNullOrEmpty(single))
                {
                    Uri checkedUri;
                    string reason;
                    if (!TryValidatePublicHttpUri(single, out checkedUri, out reason))
                        throw new InvalidOperationException("yt-dlp returned a blocked media URL.");
                    video = LocalMediaUrl(RegisterMedia(checkedUri.ToString(), topHeaders, "muxed"));
                }
            }

            if (video == null) throw new InvalidOperationException("yt-dlp returned no usable video URL.");
            return new ResolveResult { VideoUrl = video, AudioUrl = audio, Title = title };
        }

        private static ResolveResult ResolveGenericHtml(Uri page)
        {
            string html = FetchSmallText(page, 5 * 1024 * 1024);
            List<string> candidates = new List<string>();

            Regex meta = new Regex("<meta[^>]+(?:property|name)\\s*=\\s*[\\\"'](?:og:video(?::url)?|twitter:player:stream)[\\\"'][^>]+content\\s*=\\s*[\\\"']([^\\\"']+)[\\\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            foreach (Match m in meta.Matches(html)) candidates.Add(m.Groups[1].Value);
            Regex metaReverse = new Regex("<meta[^>]+content\\s*=\\s*[\\\"']([^\\\"']+)[\\\"'][^>]+(?:property|name)\\s*=\\s*[\\\"'](?:og:video(?::url)?|twitter:player:stream)[\\\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            foreach (Match m in metaReverse.Matches(html)) candidates.Add(m.Groups[1].Value);
            Regex mediaTags = new Regex("<(?:video|source)[^>]+src\\s*=\\s*[\\\"']([^\\\"']+)[\\\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            foreach (Match m in mediaTags.Matches(html)) candidates.Add(m.Groups[1].Value);
            Regex rawMedia = new Regex("https?:(?:\\\\/|/){2}[^\\s\\\"'<>]+?\\.(?:mp4|webm|m4v|mov|m3u8)(?:\\?[^\\s\\\"'<>]*)?", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            foreach (Match m in rawMedia.Matches(html)) candidates.Add(m.Value);

            foreach (string raw in candidates)
            {
                string decoded = WebUtility.HtmlDecode(raw).Replace("\\/", "/").Trim();
                Uri candidate;
                if (!Uri.TryCreate(page, decoded, out candidate)) continue;
                Uri checkedUri;
                string reason;
                if (!TryValidatePublicHttpUri(candidate.ToString(), out checkedUri, out reason)) continue;
                string token = RegisterMedia(checkedUri.ToString(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                    { "Referer", page.ToString() },
                    { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36" }
                }, "generic");
                return new ResolveResult { VideoUrl = LocalMediaUrl(token), AudioUrl = null, Title = page.Host };
            }
            return null;
        }

        private static string FetchSmallText(Uri uri, int maxBytes)
        {
            MediaRecord rec = new MediaRecord { Url = uri.ToString(), Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36" },
                { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8" }
            }, Label = "html" };
            using (HttpResponseMessage response = SendSafeRequest(rec, "GET", null, 5))
            {
                Stream input = response.Content.ReadAsStreamAsync().Result;
                using (input)
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buffer = new byte[32768];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (ms.Length + read > maxBytes) throw new InvalidDataException("HTML response exceeded safety size limit.");
                        ms.Write(buffer, 0, read);
                    }
                    Encoding enc = Encoding.UTF8;
                    return enc.GetString(ms.ToArray());
                }
            }
        }

        private static void HandleMedia(string token, RequestData req, NetworkStream clientStream)
        {
            MediaRecord rec;
            if (String.IsNullOrEmpty(token) || !Media.TryGetValue(token, out rec) || rec.ExpiresUtc < DateTime.UtcNow)
            {
                WriteJson(clientStream, 404, Error("Unknown or expired media token."));
                return;
            }

            string range = null;
            req.Headers.TryGetValue("Range", out range);
            using (HttpResponseMessage upstream = SendSafeRequest(rec, req.Method, range, 5))
            {
                string contentType = upstream.Content.Headers.ContentType != null ? upstream.Content.Headers.ContentType.ToString() : "application/octet-stream";
                Uri effectiveUri = upstream.RequestMessage.RequestUri;
                bool isHls = contentType.IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             effectiveUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

                if (isHls && req.Method != "HEAD")
                {
                    string manifest = ReadLimitedText(upstream.Content, 4 * 1024 * 1024);
                    string rewritten = RewriteHls(manifest, effectiveUri, rec.Headers);
                    Dictionary<string, string> extra = new Dictionary<string, string>();
                    extra["Cache-Control"] = "no-store";
                    WriteBytes(clientStream, (int)upstream.StatusCode, contentType, Utf8.GetBytes(rewritten), extra);
                    return;
                }

                Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                CopyResponseHeader(upstream, headers, "Accept-Ranges");
                CopyResponseHeader(upstream, headers, "Content-Range");
                CopyResponseHeader(upstream, headers, "ETag");
                CopyResponseHeader(upstream, headers, "Last-Modified");
                if (upstream.Content.Headers.ContentEncoding.Count == 0 && upstream.Content.Headers.ContentLength.HasValue)
                    headers["Content-Length"] = upstream.Content.Headers.ContentLength.Value.ToString(CultureInfo.InvariantCulture);

                WriteResponseHead(clientStream, (int)upstream.StatusCode, ReasonPhrase((int)upstream.StatusCode), contentType, headers);
                if (req.Method == "HEAD") return;

                using (Stream input = upstream.Content.ReadAsStreamAsync().Result)
                {
                    byte[] buffer = new byte[64 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        clientStream.Write(buffer, 0, read);
                    }
                }
            }
        }

        private static HttpResponseMessage SendSafeRequest(MediaRecord rec, string method, string range, int redirectsLeft)
        {
            Uri uri;
            string reason;
            if (!TryValidatePublicHttpUri(rec.Url, out uri, out reason))
                throw new InvalidOperationException("Blocked media destination: " + reason);

            HttpRequestMessage message = new HttpRequestMessage(new HttpMethod(method), uri);
            foreach (KeyValuePair<string, string> h in rec.Headers)
            {
                if (String.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(h.Key, "Connection", StringComparison.OrdinalIgnoreCase)) continue;
                message.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            if (!String.IsNullOrEmpty(range)) message.Headers.TryAddWithoutValidation("Range", range);

            HttpResponseMessage response = Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead).Result;
            int status = (int)response.StatusCode;
            if (status >= 300 && status <= 399 && response.Headers.Location != null)
            {
                if (redirectsLeft <= 0)
                {
                    response.Dispose(); message.Dispose();
                    throw new InvalidOperationException("Too many media redirects.");
                }
                Uri next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                response.Dispose(); message.Dispose();
                Uri checkedNext;
                if (!TryValidatePublicHttpUri(next.ToString(), out checkedNext, out reason))
                    throw new InvalidOperationException("Redirect blocked by network safety policy: " + reason);
                MediaRecord redirected = new MediaRecord { Url = checkedNext.ToString(), Headers = rec.Headers, Label = rec.Label };
                return SendSafeRequest(redirected, method, range, redirectsLeft - 1);
            }

            return response;
        }

        private static string ReadLimitedText(HttpContent content, int maxBytes)
        {
            using (Stream input = content.ReadAsStreamAsync().Result)
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[32768];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (output.Length + count > maxBytes)
                        throw new InvalidDataException("Playlist exceeds size limit.");
                    output.Write(buffer, 0, count);
                }
                return Utf8.GetString(output.ToArray());
            }
        }

        private static string RewriteHls(string manifest, Uri baseUri, Dictionary<string, string> headers)
        {
            string[] lines = manifest.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (!trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    lines[i] = ProxyChildUri(trimmed, baseUri, headers);
                }
                else if (trimmed.IndexOf("URI=\"", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    lines[i] = Regex.Replace(line, "URI=\"([^\"]+)\"", delegate(Match m) {
                        return "URI=\"" + ProxyChildUri(m.Groups[1].Value, baseUri, headers) + "\"";
                    }, RegexOptions.IgnoreCase);
                }
            }
            return String.Join("\n", lines);
        }

        private static string ProxyChildUri(string raw, Uri baseUri, Dictionary<string, string> headers)
        {
            Uri child;
            if (!Uri.TryCreate(baseUri, WebUtility.HtmlDecode(raw), out child)) return raw;
            Uri checkedUri;
            string reason;
            if (!TryValidatePublicHttpUri(child.ToString(), out checkedUri, out reason))
                throw new InvalidOperationException("HLS child URL blocked: " + reason);
            return LocalMediaUrl(RegisterMedia(checkedUri.ToString(), headers, "hls-child"));
        }

        private static string RegisterMedia(string url, Dictionary<string, string> headers, string label)
        {
            if (Media.Count > 5000)
            {
                foreach (KeyValuePair<string, MediaRecord> item in Media)
                    if (item.Value.ExpiresUtc < DateTime.UtcNow)
                    {
                        MediaRecord removed;
                        Media.TryRemove(item.Key, out removed);
                    }
                if (Media.Count > 10000) throw new InvalidOperationException("Session media limit reached.");
            }
            string token = Guid.NewGuid().ToString("N");
            Dictionary<string, string> safeHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    if (h.Key == null || h.Value == null) continue;
                    if (String.Equals(h.Key, "Cookie", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(h.Key, "User-Agent", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(h.Key, "Referer", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(h.Key, "Origin", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(h.Key, "Accept", StringComparison.OrdinalIgnoreCase) ||
                        h.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                    {
                        safeHeaders[h.Key] = h.Value;
                    }
                }
            }
            Media[token] = new MediaRecord { Url = url, Headers = safeHeaders, Label = label, ExpiresUtc = DateTime.UtcNow.AddHours(6) };
            return token;
        }

        private static string LocalMediaUrl(string token)
        {
            return _publicBaseUrl + "/media/" + token;
        }

        private static Dictionary<string, string> ExtractHeaders(Dictionary<string, object> obj)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            object hObj;
            if (!obj.TryGetValue("http_headers", out hObj) || hObj == null) return result;
            Dictionary<string, object> headers = hObj as Dictionary<string, object>;
            if (headers == null) return result;
            foreach (KeyValuePair<string, object> kv in headers)
                if (kv.Key != null && kv.Value != null) result[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            return result;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object value;
            if (!d.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool LooksLikeDirectMedia(Uri uri)
        {
            string p = uri.AbsolutePath.ToLowerInvariant();
            return p.EndsWith(".mp4") || p.EndsWith(".webm") || p.EndsWith(".m4v") || p.EndsWith(".mov") || p.EndsWith(".m3u8");
        }

        private static bool TryValidatePublicHttpUri(string input, out Uri uri, out string reason)
        {
            uri = null;
            reason = null;
            Uri parsed;
            if (!Uri.TryCreate(input, UriKind.Absolute, out parsed)) { reason = "not an absolute URI"; return false; }
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) { reason = "scheme must be HTTP or HTTPS"; return false; }
            if (!String.IsNullOrEmpty(parsed.UserInfo)) { reason = "embedded credentials are not allowed"; return false; }
            string host = parsed.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
            if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".lan") || host.EndsWith(".internal") || host.EndsWith(".localdomain"))
            { reason = "local hostname"; return false; }

            IPAddress literal;
            if (IPAddress.TryParse(host, out literal))
            {
                if (!IsPublicIp(literal)) { reason = "non-public IP address"; return false; }
            }
            else
            {
                IPAddress[] addresses;
                try { addresses = Dns.GetHostAddresses(host); }
                catch (Exception ex) { reason = "DNS failed: " + ex.Message; return false; }
                if (addresses == null || addresses.Length == 0) { reason = "DNS returned no addresses"; return false; }
                foreach (IPAddress ip in addresses)
                {
                    if (!IsPublicIp(ip)) { reason = "hostname resolves to non-public address " + ip; return false; }
                }
            }
            uri = parsed;
            return true;
        }

        private static bool IsPublicIp(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return false;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
                if (b[0] == 169 && b[1] == 254) return false;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
                if (b[0] == 192 && b[1] == 168) return false;
                if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return false;
                if (b[0] == 198 && (b[1] == 18 || b[1] == 19 || b[1] == 51)) return false;
                if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;
                if (b[0] >= 224) return false;
                return true;
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Loopback)) return false;
                if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return false;
                byte[] b = ip.GetAddressBytes();
                if ((b[0] & 0xFE) == 0xFC) return false; // fc00::/7
                if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // documentation
                return true;
            }
            return false;
        }

        private static string QuoteArg(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string TrimMessage(string s)
        {
            if (String.IsNullOrWhiteSpace(s)) return "no diagnostic text";
            s = s.Trim();
            if (s.Length > 600) s = s.Substring(0, 600) + "...";
            return s.Replace("\r", " ").Replace("\n", " ");
        }

        private static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object> { { "success", false }, { "message", message } };
        }

        private static void WriteJson(NetworkStream stream, int status, object body)
        {
            byte[] bytes = Utf8.GetBytes(Json.Serialize(body));
            WriteBytes(stream, status, "application/json; charset=utf-8", bytes, null);
        }

        private static void WriteEmpty(NetworkStream stream, int status, Dictionary<string, string> headers)
        {
            WriteResponseHead(stream, status, ReasonPhrase(status), null, headers);
        }

        private static void WriteBytes(NetworkStream stream, int status, string contentType, byte[] bytes, Dictionary<string, string> extraHeaders)
        {
            Dictionary<string, string> headers = extraHeaders != null ? new Dictionary<string, string>(extraHeaders, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers["Content-Length"] = bytes.Length.ToString(CultureInfo.InvariantCulture);
            WriteResponseHead(stream, status, ReasonPhrase(status), contentType, headers);
            if (bytes.Length > 0) stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteResponseHead(NetworkStream stream, int status, string reason, string contentType, Dictionary<string, string> headers)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Server: HereSphereSandbox/0.4\r\n");
            if (!String.IsNullOrEmpty(contentType)) sb.Append("Content-Type: ").Append(SanitizeHeader(contentType)).Append("\r\n");
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    if (String.Equals(h.Key, "Connection", StringComparison.OrdinalIgnoreCase) || String.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(SanitizeHeader(h.Key)).Append(": ").Append(SanitizeHeader(h.Value)).Append("\r\n");
                }
            }
            sb.Append("\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
        }

        private static string SanitizeHeader(string s)
        {
            if (s == null) return "";
            return s.Replace("\r", "").Replace("\n", "");
        }

        private static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 204: return "No Content";
                case 206: return "Partial Content";
                case 301: return "Moved Permanently";
                case 302: return "Found";
                case 303: return "See Other";
                case 307: return "Temporary Redirect";
                case 308: return "Permanent Redirect";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 416: return "Range Not Satisfiable";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                case 502: return "Bad Gateway";
                case 503: return "Service Unavailable";
                default: return "HTTP";
            }
        }

        private static void CopyResponseHeader(HttpResponseMessage response, Dictionary<string, string> target, string name)
        {
            IEnumerable<string> values;
            if (response.Headers.TryGetValues(name, out values) || response.Content.Headers.TryGetValues(name, out values))
                target[name] = String.Join(", ", values.ToArray());
        }
    }
}
