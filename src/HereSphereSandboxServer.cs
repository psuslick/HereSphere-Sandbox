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
            StartCatalog();
            ServicePointManager.DefaultConnectionLimit = 64;

            Uri listenUri = new Uri(_publicBaseUrl);
            IPAddress listenAddress;
            if (!IPAddress.TryParse(listenUri.Host, out listenAddress))
                throw new InvalidOperationException("The Sandbox listener requires a numeric guest IP address.");
            TcpListener listener = new TcpListener(listenAddress, port);
            listener.Start(64);
            Console.WriteLine("HereSphere Sandbox service listening on " + listenAddress.ToString() + ":" + port.ToString(CultureInfo.InvariantCulture));

            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                client.NoDelay = true;
                Task.Run(() => HandleClient(client, false));
            }
        }

        private static void HandleClient(TcpClient client, bool isAdmin)
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
                    Dispatch(req, stream, isAdmin);
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

        private static void Dispatch(RequestData req, NetworkStream stream, bool isAdmin)
        {
            Uri targetUri;
            if (!Uri.TryCreate("http://localhost" + req.Target, UriKind.Absolute, out targetUri))
            {
                WriteJson(stream, 400, Error("Invalid request target."));
                return;
            }
            string path = targetUri.AbsolutePath;
            if (isAdmin) { HandleAdmin(req, stream, path); return; }

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
                    "<h1>HereSphere Sandbox Server v0.5</h1><p><b>Status:</b> running inside Windows Sandbox.</p>" +
                    "<p>Set HereSphere Link Server to <code>" + WebUtility.HtmlEncode(_publicBaseUrl) + "</code>.</p>" +
                    "<p>This alpha build supports Link Server streaming through <code>/stream</code>. Direct media URLs, yt-dlp extraction, static HTML media discovery, byte ranges, and HLS playlist rewriting are supported.</p>" +
                    "<p>The HereSphere library is at <code>/heresphere</code>. Open the management UI <b>inside Windows Sandbox</b> at <code>http://127.0.0.1:5001</code>.</p></body></html>";
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

            if (path == "/heresphere" || path.StartsWith("/heresphere/", StringComparison.Ordinal))
            {
                HandleHereSphere(req, stream, path);
                return;
            }
            if (path.StartsWith("/thumb/", StringComparison.Ordinal) && req.Method == "GET")
            {
                HandleThumb(path.Substring(7), stream);
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
            sb.Append("Server: HereSphereSandbox/0.5\r\n");
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
        // v0.5 catalog: all changes to these collections are disposable Sandbox-session state.
        private sealed class CatalogItem
        {
            public string Id, Url, Title, Group, Source, ThumbnailUrl, Description, State;
            public long DurationMs;
        }
        private static readonly ConcurrentDictionary<string, CatalogItem> Catalog = new ConcurrentDictionary<string, CatalogItem>();
        private static readonly ConcurrentDictionary<string, byte[]> ImageCache = new ConcurrentDictionary<string, byte[]>();
        private static readonly ConcurrentDictionary<string, string> ImageTypes = new ConcurrentDictionary<string, string>();
        private static readonly object SourcesLock = new object();
        private static List<string> IndividualSources = new List<string>();
        private static List<string> WebsiteSources = new List<string>();
        private static int ScanBusy;
        private static int MaxItemsPerSite = 48;
        private static int MaxPagesPerSite = 3;
        private static string ScanStatus = "Waiting";
        private static int ScanGeneration;
        private static readonly string SourceRoot = @"C:\SandboxBootstrap\HereSphere\Sources";
        private static readonly string UiPath = @"C:\App\ui\index.html";

        private static void StartCatalog()
        {
            lock (SourcesLock)
            {
                IndividualSources = ReadSourceFile(Path.Combine(SourceRoot, "videos.txt"));
                WebsiteSources = ReadSourceFile(Path.Combine(SourceRoot, "websites.txt"));
            }
            Task.Run(() => CatalogScan());
            Task.Run(() => RunAdminListener());
            Console.WriteLine("Management UI: http://127.0.0.1:5001 (Sandbox browser only)");
        }
        private static List<string> ReadSourceFile(string path)
        {
            if (!File.Exists(path)) return new List<string>();
            return NormalizeUrls(File.ReadAllText(path));
        }
        private static List<string> NormalizeUrls(string input)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in (input ?? "").Split(new[] {'\r','\n'}, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.Length > 3000) continue;
                Uri uri; string reason;
                if (TryValidatePublicHttpUri(line, out uri, out reason) && seen.Add(uri.ToString())) result.Add(uri.ToString());
                if (result.Count >= 250) break;
            }
            return result;
        }
        private static void RunAdminListener()
        {
            try
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 5001);
                listener.Start(16);
                while (true)
                {
                    TcpClient c = listener.AcceptTcpClient();
                    c.NoDelay = true;
                    Task.Run(() => HandleClient(c, true));
                }
            }
            catch (Exception ex) { Console.WriteLine("Management UI failed: " + ex.GetType().Name); }
        }
        private static bool HandleAdmin(RequestData req, NetworkStream stream, string path)
        {
            string host;
            if (!req.Headers.TryGetValue("Host", out host) ||
                !(String.Equals(host, "127.0.0.1:5001", StringComparison.OrdinalIgnoreCase) ||
                  String.Equals(host, "localhost:5001", StringComparison.OrdinalIgnoreCase)))
            { WriteJson(stream, 403, Error("Management interface is guest-loopback only.")); return true; }
            string origin;
            if (req.Headers.TryGetValue("Origin", out origin) &&
                !(String.Equals(origin, "http://127.0.0.1:5001", StringComparison.OrdinalIgnoreCase) ||
                  String.Equals(origin, "http://localhost:5001", StringComparison.OrdinalIgnoreCase)))
            { WriteJson(stream, 403, Error("Cross-origin management requests are blocked.")); return true; }
            if (path == "/" && req.Method == "GET")
            {
                if (!File.Exists(UiPath)) { WriteJson(stream, 500, Error("UI file missing in cloned repository.")); return true; }
                byte[] html = File.ReadAllBytes(UiPath);
                WriteBytes(stream, 200, "text/html; charset=utf-8", html,
                    new Dictionary<string,string>{{"Cache-Control","no-store"},{"Content-Security-Policy","default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"},{"X-Content-Type-Options","nosniff"}});
                return true;
            }
            if (path == "/api/catalog" && req.Method == "GET")
            {
                List<object> entries = Catalog.Values.OrderBy(x => x.Group).ThenBy(x => x.Source).ThenBy(x => x.Title).Select(x => (object)CatalogPayload(x)).ToList();
                List<string> videos, websites;
                lock (SourcesLock) { videos = new List<string>(IndividualSources); websites = new List<string>(WebsiteSources); }
                WriteJson(stream, 200, new Dictionary<string,object> {
                    {"success",true},{"items",entries},{"videos",videos},{"websites",websites},
                    {"scanning",Interlocked.CompareExchange(ref ScanBusy, 0, 0) != 0},
                    {"status",ScanStatus},{"maxItemsPerSite",MaxItemsPerSite},{"maxPagesPerSite",MaxPagesPerSite}
                });
                return true;
            }
            if (path == "/api/sources" && req.Method == "POST")
            {
                Dictionary<string,object> data = DecodeObject(req.Body);
                lock (SourcesLock)
                {
                    IndividualSources = NormalizeUrls(GetString(data,"videos"));
                    WebsiteSources = NormalizeUrls(GetString(data,"websites"));
                }
                WriteJson(stream, 200, new Dictionary<string,object>{{"success",true},{"message","Session sources updated; use Rescan to apply."}});
                return true;
            }
            if (path == "/api/settings" && req.Method == "POST")
            {
                Dictionary<string,object> data = DecodeObject(req.Body);
                MaxItemsPerSite = ReadClampedInt(data,"maxItemsPerSite",MaxItemsPerSite,5,120);
                MaxPagesPerSite = ReadClampedInt(data,"maxPagesPerSite",MaxPagesPerSite,1,5);
                WriteJson(stream,200,new Dictionary<string,object>{{"success",true}});
                return true;
            }
            if (path == "/api/scan" && req.Method == "POST")
            {
                if (Interlocked.CompareExchange(ref ScanBusy,1,0) == 0)
                {
                    Task.Run(() => CatalogScan(true));
                    WriteJson(stream,202,new Dictionary<string,object>{{"success",true},{"message","Scan started."}});
                }
                else WriteJson(stream,200,new Dictionary<string,object>{{"success",false},{"message","Scan already running."}});
                return true;
            }
            if (path.StartsWith("/thumb/",StringComparison.Ordinal) && req.Method == "GET")
            {
                HandleThumb(path.Substring(7),stream);
                return true;
            }
            WriteJson(stream,404,Error("Management route not found."));
            return true;
        }
        private static Dictionary<string,object> DecodeObject(byte[] bytes)
        {
            try { return Json.DeserializeObject(Utf8.GetString(bytes ?? new byte[0])) as Dictionary<string,object> ?? new Dictionary<string,object>(); }
            catch { return new Dictionary<string,object>(); }
        }
        private static int ReadClampedInt(Dictionary<string,object> obj, string key, int fallback, int min, int max)
        {
            object value; int result;
            return obj.TryGetValue(key,out value) && Int32.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),out result)
                ? Math.Max(min,Math.Min(max,result)) : fallback;
        }
        private static Dictionary<string,object> CatalogPayload(CatalogItem item)
        {
            return new Dictionary<string,object> {
                {"id",item.Id},{"url",item.Url},{"title",item.Title},{"group",item.Group},{"source",item.Source},
                {"description",item.Description ?? ""},{"durationMs",item.DurationMs},
                {"thumbnail", "/thumb/"+item.Id},{"hasThumbnail",!String.IsNullOrWhiteSpace(item.ThumbnailUrl)},
                {"state",item.State ?? "pending"}
            };
        }
        private static void CatalogScan(bool alreadyClaimed = false)
        {
            if (!alreadyClaimed && Interlocked.CompareExchange(ref ScanBusy,1,0) != 0) return;
            int generation = Interlocked.Increment(ref ScanGeneration);
            try
            {
                ScanStatus = "Reading source lists";
                Catalog.Clear(); ImageCache.Clear(); ImageTypes.Clear();
                List<string> videos, websites;
                lock(SourcesLock) { videos = new List<string>(IndividualSources); websites = new List<string>(WebsiteSources); }
                foreach (string url in videos)
                {
                    CatalogItem item = AddCatalogItem(url,"individual","Individual Videos",null,null);
                    if (item != null) { ScanStatus = "Indexing individual videos"; ProbeMetadata(item); }
                }
                foreach (string website in websites)
                {
                    ScanStatus = "Discovering videos on " + SafeSourceName(website);
                    try { DiscoverWebsite(website); }
                    catch(Exception ex) { Console.WriteLine("Discovery failed ("+SafeSourceName(website)+"): "+ex.GetType().Name); }
                }
                ScanStatus = "Complete: " + Catalog.Count.ToString(CultureInfo.InvariantCulture) + " entries";
            }
            catch (Exception ex) { ScanStatus = "Scan error: " + ex.GetType().Name; }
            finally { Interlocked.Exchange(ref ScanBusy,0); }
        }
        private static string SafeSourceName(string url)
        {
            Uri uri; return Uri.TryCreate(url,UriKind.Absolute,out uri) ? uri.Host : "source";
        }
        private static CatalogItem AddCatalogItem(string url, string group, string source, string title, string thumb)
        {
            Uri uri; string reason;
            if (!TryValidatePublicHttpUri(url,out uri,out reason)) return null;
            string normalized = uri.AbsoluteUri;
            string id = StableId(group+"|"+normalized);
            CatalogItem item = new CatalogItem { Id=id, Url=uri.ToString(), Group=group, Source=source,
                Title=String.IsNullOrWhiteSpace(title)?ReadableName(uri):title,ThumbnailUrl=thumb, State="queued" };
            return Catalog.GetOrAdd(id,item);
        }
        private static string StableId(string input)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(input))).Replace("-", "").Substring(0,24).ToLowerInvariant();
        }
        private static string ReadableName(Uri uri)
        {
            string name = uri.Segments.Length>0 ? Uri.UnescapeDataString(uri.Segments[uri.Segments.Length-1]).Trim('/') : "";
            return String.IsNullOrWhiteSpace(name)?uri.Host:name.Replace('-',' ').Replace('_',' ');
        }
        private static Dictionary<string,object> ProbeYtDlp(string url, bool flat, int count, int timeoutMs)
        {
            if (String.IsNullOrEmpty(_ytDlpPath) || !File.Exists(_ytDlpPath)) return null;
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _ytDlpPath; psi.CreateNoWindow=true; psi.UseShellExecute=false;
            psi.RedirectStandardOutput=true; psi.RedirectStandardError=true;
            psi.StandardOutputEncoding=Encoding.UTF8; psi.StandardErrorEncoding=Encoding.UTF8;
            psi.Arguments = "--no-config --no-plugin-dirs --no-remote-components --no-cache-dir --skip-download --dump-single-json --no-warnings " +
                (flat ? "--flat-playlist --playlist-end " + count.ToString(CultureInfo.InvariantCulture) + " " : "--no-playlist ") +
                "-- " + QuoteArg(url);
            using (Process p = new Process())
            {
                p.StartInfo=psi; p.Start();
                Task<string> stdout=p.StandardOutput.ReadToEndAsync(); Task<string> stderr=p.StandardError.ReadToEndAsync();
                if(!p.WaitForExit(timeoutMs)) {try{p.Kill();}catch{} return null;}
                if (!Task.WaitAll(new Task[]{stdout,stderr},5000) || p.ExitCode!=0 || stdout.Result.Length>12*1024*1024) return null;
                try {return Json.DeserializeObject(stdout.Result) as Dictionary<string,object>;} catch {return null;}
            }
        }
        private static long DurationMs(Dictionary<string,object> info)
        {
            object value; double result;
            return info.TryGetValue("duration",out value) && value!=null && Double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture), NumberStyles.Any,CultureInfo.InvariantCulture,out result)
                 ? (long)Math.Min(Int32.MaxValue,Math.Max(0,result*1000)) : 0;
        }
        private static void ProbeMetadata(CatalogItem item)
        {
            try
            {
                Uri uri; string reason;
                if(!TryValidatePublicHttpUri(item.Url,out uri,out reason)) {item.State="blocked";return;}
                if(LooksLikeDirectMedia(uri)) { item.State="ready"; return; }
                Dictionary<string,object> info=ProbeYtDlp(item.Url,false,1,20000);
                if(info != null)
                {
                    string title=GetString(info,"title"), image=GetString(info,"thumbnail");
                    if(!String.IsNullOrWhiteSpace(title)) item.Title=title;
                    item.DurationMs=DurationMs(info);
                    if (SafeThumbnailUrl(image)) item.ThumbnailUrl=image;
                    item.Description=GetString(info,"description");
                    if (item.Description!=null && item.Description.Length>500) item.Description=item.Description.Substring(0,500);
                    item.State="ready"; return;
                }
                string html=FetchSmallText(uri,1024*1024);
                // oEmbed discovery is common on video platforms; the URL itself is
                // fetched only through the same public-address/redirect safety checks.
                Match oe=Regex.Match(html,"<link[^>]+type\\s*=\\s*['\\\"]application/json\\+oembed['\\\"][^>]+href\\s*=\\s*['\\\"]([^'\\\"]+)",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
                if (!oe.Success)
                    oe=Regex.Match(html,"<link[^>]+href\\s*=\\s*['\\\"]([^'\\\"]+)['\\\"][^>]+type\\s*=\\s*['\\\"]application/json\\+oembed",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
                if(oe.Success)
                {
                    Uri oeUri;string why;
                    if(Uri.TryCreate(uri,WebUtility.HtmlDecode(oe.Groups[1].Value),out oeUri) && TryValidatePublicHttpUri(oeUri.ToString(),out oeUri,out why))
                    {
                        try
                        {
                            Dictionary<string,object> oeInfo=Json.DeserializeObject(FetchSmallText(oeUri,256*1024)) as Dictionary<string,object>;
                            if(oeInfo!=null)
                            {
                                string oeTitle=GetString(oeInfo,"title");
                                string oeThumb=GetString(oeInfo,"thumbnail_url");
                                if(!String.IsNullOrWhiteSpace(oeTitle)) item.Title=oeTitle;
                                if(SafeThumbnailUrl(oeThumb)) item.ThumbnailUrl=oeThumb;
                            }
                        } catch(Exception) { }
                    }
                }
                string titleMeta=ExtractMeta(html,"og:title|twitter:title");
                string imageMeta=ExtractMeta(html,"og:image(?::url|:secure_url)?|twitter:image");
                if (!String.IsNullOrEmpty(titleMeta) && (item.Title==ReadableName(uri))) item.Title=WebUtility.HtmlDecode(titleMeta);
                Uri absolute;
                if (String.IsNullOrEmpty(item.ThumbnailUrl) && Uri.TryCreate(uri,WebUtility.HtmlDecode(imageMeta ?? ""),out absolute) && SafeThumbnailUrl(absolute.ToString())) item.ThumbnailUrl=absolute.ToString();
                item.State="listed";
            }
            catch(Exception ex) {item.State="unverified";}
        }
        private static string ExtractMeta(string html,string names)
        {
            // Handles either common attribute order. Failure is expected for many public sites.
            Match match=Regex.Match(html,"<meta[^>]+(?:property|name)\\s*=\\s*['\\\"](?:"+names+")[\\'\\\"][^>]+content\\s*=\\s*['\\\"]([^'\\\"]+)",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
            if(match.Success) return match.Groups[1].Value;
            match=Regex.Match(html,"<meta[^>]+content\\s*=\\s*['\\\"]([^'\\\"]+)['\\\"][^>]+(?:property|name)\\s*=\\s*['\\\"](?:"+names+")",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
            return match.Success? match.Groups[1].Value:null;
        }
        private static bool SafeThumbnailUrl(string url)
        {
            Uri uri;string reason;
            return !String.IsNullOrEmpty(url) && TryValidatePublicHttpUri(url,out uri,out reason);
        }
        private static void DiscoverWebsite(string website)
        {
            int limit=MaxItemsPerSite;
            string source=SafeSourceName(website);
            HashSet<string> seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<CatalogItem> added=new List<CatalogItem>();
            // First ask any yt-dlp extractor for collection entries. Unfamiliar sites still get generic discovery.
            Dictionary<string,object> flat=ProbeYtDlp(website,true,limit,30000);
            object raw;
            if(flat!=null && flat.TryGetValue("entries",out raw))
            {
                IEnumerable entries=raw as IEnumerable;
                if(entries!=null)
                foreach(object obj in entries)
                {
                    Dictionary<string,object> ent=obj as Dictionary<string,object>;
                    if(ent==null || added.Count>=limit) break;
                    string link=GetString(ent,"webpage_url") ?? GetString(ent,"original_url") ?? GetString(ent,"url");
                    Uri resolved;
                    if(!Uri.TryCreate(link,UriKind.Absolute,out resolved))
                    {
                        // Some extractors return IDs, not URLs. Prefer their own webpage_url; never guess a provider URL.
                        continue;
                    }
                    if(!seen.Add(resolved.ToString())) continue;
                    string thumb=GetString(ent,"thumbnail");
                    CatalogItem item=AddCatalogItem(resolved.ToString(),"discovered",source,GetString(ent,"title"),SafeThumbnailUrl(thumb)?thumb:null);
                    if(item!=null) {added.Add(item);item.State="listed";}
                }
            }
            // Generic discovery always runs, including for previously unknown websites and embeds.
            Queue<Uri> pages=new Queue<Uri>();
            Uri root=new Uri(website);
            pages.Enqueue(root);
            HashSet<string> crawled=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while(pages.Count>0 && crawled.Count<MaxPagesPerSite && added.Count<limit)
            {
                Uri page=pages.Dequeue();
                if(!crawled.Add(page.ToString())) continue;
                string html;
                try {html=FetchSmallText(page,2*1024*1024);} catch {continue;}
                Regex anchor=new Regex("<a\\b([^>]*)>([\\s\\S]*?)</a>",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(2));
                foreach(Match match in anchor.Matches(html))
                {
                    if(added.Count>=limit) break;
                    Match href=Regex.Match(match.Groups[1].Value,"\\bhref\\s*=\\s*['\\\"]([^'\\\"]+)",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
                    if(!href.Success) continue;
                    Uri link; string reason;
                    if(!Uri.TryCreate(page,WebUtility.HtmlDecode(href.Groups[1].Value),out link) ||
                       !TryValidatePublicHttpUri(link.ToString(),out link,out reason)) continue;
                    if(!seen.Add(link.ToString()) || link.Equals(page)) continue;
                    // An arbitrary anchor may be navigation; preserve unfamiliar candidate sources without provider allowlists.
                    string inner=match.Groups[2].Value;
                    string label=Regex.Replace(inner,"<[^>]+>"," ").Trim();
                    bool imageAnchor=inner.IndexOf("<img",StringComparison.OrdinalIgnoreCase)>=0;
                    bool hinted=Regex.IsMatch(link.AbsolutePath,"video|watch|play|clip|film|vr|movie|embed|360|180|\\.(?:mp4|webm|m3u8)",RegexOptions.IgnoreCase);
                    if(!imageAnchor && !hinted) {if(link.Host==page.Host && pages.Count<MaxPagesPerSite*2) pages.Enqueue(link); continue;}
                    string thumb=null;
                    Match image=Regex.Match(inner,"<img[^>]+(?:src|data-src)\\s*=\\s*['\\\"]([^'\\\"]+)",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
                    Uri imageUri;
                    if(image.Success && Uri.TryCreate(page,WebUtility.HtmlDecode(image.Groups[1].Value),out imageUri) && SafeThumbnailUrl(imageUri.ToString())) thumb=imageUri.ToString();
                    CatalogItem item=AddCatalogItem(link.ToString(),"discovered",source,label.Length>2 && label.Length<150 ? WebUtility.HtmlDecode(label):null,thumb);
                    if(item!=null) {added.Add(item); item.State="listed";}
                }
                if(page==root && pages.Count==0) break;
            }
            // Metadata probes are bounded; they never start media playback or download media files.
            foreach(CatalogItem item in added.Take(20))
            {
                if (String.IsNullOrEmpty(item.ThumbnailUrl)) ProbeMetadata(item);
                else item.State="ready";
            }
        }
        private static string GuessStereo(string title)
        {
            string lower=(title??"").ToLowerInvariant();
            if(Regex.IsMatch(lower,@"(^|[\s_\-])(sbs|side.by.side|3d.sbs)([\s_\-]|$)")) return "sbs";
            if(Regex.IsMatch(lower,@"(^|[\s_\-])(tb|top.bottom|over.under)([\s_\-]|$)")) return "tb";
            return "";
        }
        private static string GuessProjection(string title)
        {
            string lower=(title??"").ToLowerInvariant();
            if(Regex.IsMatch(lower,@"(^|[^0-9])360([^0-9]|$)") || lower.Contains("vr360")) return "equirectangular360";
            if(Regex.IsMatch(lower,@"(^|[^0-9])180([^0-9]|$)") || lower.Contains("vr180")) return "equirectangular";
            return "";
        }
        private static int GuessFov(string title)
        {
            string p=GuessProjection(title);
            return p=="equirectangular360"?360:p=="equirectangular"?180:0;
        }
        private static void HandleHereSphere(RequestData req,NetworkStream stream,string path)
        {
            Dictionary<string,string> header=new Dictionary<string,string>{{"HereSphere-JSON-Version","1"},{"Cache-Control","no-store"}};
            if(path=="/heresphere" || path=="/heresphere/")
            {
                List<object> individual=Catalog.Values.Where(x=>x.Group=="individual").OrderBy(x=>x.Title).Select(x=>(object)(_publicBaseUrl+"/heresphere/item/"+x.Id)).ToList();
                List<object> discovered=Catalog.Values.Where(x=>x.Group=="discovered").OrderBy(x=>x.Source).ThenBy(x=>x.Title).Select(x=>(object)(_publicBaseUrl+"/heresphere/item/"+x.Id)).ToList();
                object body=new Dictionary<string,object>{{"access",1},{"library",new object[]{
                    new Dictionary<string,object>{{"name","Individual Videos"},{"list",individual}},
                    new Dictionary<string,object>{{"name","Discovered Videos"},{"list",discovered}}
                }}};
                WriteBytes(stream,200,"application/json; charset=utf-8",Utf8.GetBytes(Json.Serialize(body)),header);
                return;
            }
            if(path.StartsWith("/heresphere/item/",StringComparison.Ordinal))
            {
                string id=path.Substring("/heresphere/item/".Length);
                CatalogItem item;
                if(!Catalog.TryGetValue(id,out item)) {WriteJson(stream,404,Error("Unknown item."));return;}
                bool needMedia=true;
                if(req.Method=="POST")
                {
                    Dictionary<string,object> data=DecodeObject(req.Body);
                    object flag;
                    if(data.TryGetValue("needsMediaSource",out flag) && flag is bool) needMedia=(bool)flag;
                }
                List<object> sources=new List<object>();
                if(needMedia)
                {
                    try
                    {
                        Uri safe;string reason;
                        if(TryValidatePublicHttpUri(item.Url,out safe,out reason))
                        {
                            ResolveResult resolved=Resolve(safe);
                            sources.Add(new Dictionary<string,object>{{"url",resolved.VideoUrl},{"resolution",""},{"height",0},{"width",0},{"size",0L}});
                            // Separately extracted audio tracks aren't part of HereSphere's media-source schema.
                        }
                    }
                    catch(Exception ex) {Console.WriteLine("HereSphere media resolve failed: "+ex.GetType().Name);}
                }
                object body=new Dictionary<string,object>{
                    {"access",1},{"title",item.Title},{"description",item.Description??""},
                    {"thumbnailImage",_publicBaseUrl+"/thumb/"+item.Id},
                    {"dateReleased",""},{"dateAdded",DateTime.UtcNow.ToString("yyyy-MM-dd")},
                    {"duration",item.DurationMs},{"projection",GuessProjection(item.Title)},{"stereo",GuessStereo(item.Title)},
                    {"fov",GuessFov(item.Title)},{"lens",""},
                    {"isFavorite",false},{"writeFavorite",false},{"writeRating",false},{"writeTags",false},{"writeHSP",false},
                    {"tags",new object[]{new Dictionary<string,object>{{"name", item.Group=="individual" ? "Individual Videos" : "Source: "+item.Source}}}},
                    {"media",new object[]{new Dictionary<string,object>{{"name","Video"},{"sources",sources}}}}
                };
                WriteBytes(stream,200,"application/json; charset=utf-8",Utf8.GetBytes(Json.Serialize(body)),header);
                return;
            }
            WriteJson(stream,404,Error("Unknown HereSphere item."));
        }
        private static void HandleThumb(string id,NetworkStream stream)
        {
            CatalogItem item;
            if(id.Length!=24 || !Catalog.TryGetValue(id,out item)) {WriteJson(stream,404,Error("Unknown thumbnail."));return;}
            byte[] cached;
            string type;
            if(ImageCache.TryGetValue(id,out cached) && ImageTypes.TryGetValue(id,out type))
            {WriteBytes(stream,200,type,cached,new Dictionary<string,string>{{"Cache-Control","private, max-age=1800"}});return;}
            try
            {
                if(!SafeThumbnailUrl(item.ThumbnailUrl)) throw new InvalidDataException("No public thumbnail URL.");
                MediaRecord rec=new MediaRecord {Url=item.ThumbnailUrl,Headers=new Dictionary<string,string>{{"User-Agent","Mozilla/5.0"}},Label="thumbnail"};
                using(HttpResponseMessage response=SendSafeRequest(rec,"GET",null,3))
                {
                    string ct=response.Content.Headers.ContentType==null?"":response.Content.Headers.ContentType.MediaType;
                    if(ct!="image/jpeg" && ct!="image/png" && ct!="image/webp" && ct!="image/gif") throw new InvalidDataException("Non-image content.");
                    using(Stream input=response.Content.ReadAsStreamAsync().Result)
                    using(MemoryStream output=new MemoryStream())
                    {
                        byte[] buffer=new byte[16384];int len;
                        while((len=input.Read(buffer,0,buffer.Length))>0)
                        {if(output.Length+len>3*1024*1024)throw new InvalidDataException("Thumbnail too large.");output.Write(buffer,0,len);}
                        cached=output.ToArray();
                    }
                    if (ImageCache.Count < 160) { ImageCache[id]=cached; ImageTypes[id]=ct; }
                    WriteBytes(stream,200,ct,cached,new Dictionary<string,string>{{"Cache-Control","private, max-age=1800"}});
                    return;
                }
            }
            catch(Exception) {}
            byte[] fallback=File.ReadAllBytes(@"C:\App\ui\placeholder.png");
            WriteBytes(stream,200,"image/png",fallback,new Dictionary<string,string>{{"Cache-Control","no-store"}});
        }
    }
}
