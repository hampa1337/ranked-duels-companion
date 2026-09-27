using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RankedDuelsCompanion
{
    class NotLoggedInException : Exception { }

    class ApiException : Exception
    {
        public string Code { get; }
        public ApiException(string code) : base(code) { Code = code; }
    }

    // Talks to the same Supabase Edge Functions the website uses.
    static class Api
    {
        public const string Website = "https://ranked-duels.rankedduels.workers.dev";
        const string FunctionsUrl = "https://yodmkzrzovmxftrwjhmw.supabase.co/functions/v1";
        const string SessionHeader = "x-rd-session";

        static readonly HttpClient http = CreateClient();
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("RankedDuelsCompanion/" + Install.Version);
            return c;
        }

        static async Task<Dictionary<string, object>> Call(string name, string session, object body = null)
        {
            var req = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, $"{FunctionsUrl}/{name}");
            req.Headers.Add(SessionHeader, session ?? "");
            if (body != null) req.Content = new StringContent(json.Serialize(body), Encoding.UTF8, "application/json");

            using (var res = await http.SendAsync(req))
            {
                var text = await res.Content.ReadAsStringAsync();
                if (res.StatusCode == HttpStatusCode.Unauthorized) throw new NotLoggedInException();
                Dictionary<string, object> data = null;
                try { data = json.Deserialize<Dictionary<string, object>>(text); } catch { }
                if (!res.IsSuccessStatusCode)
                    throw new ApiException(data != null && data.TryGetValue("error", out var e) ? e as string : $"HTTP {(int)res.StatusCode}");
                return data ?? new Dictionary<string, object>();
            }
        }

        public static Task<Dictionary<string, object>> Me(string session) => Call("me", session);

        public static Task<Dictionary<string, object>> Upload(string session, string savedVariables) =>
            Call("upload", session, new Dictionary<string, object> { ["savedVariables"] = savedVariables });

        // Newest released version, published next to the download on the website.
        public static async Task<string> LatestVersion()
        {
            try
            {
                var v = (await http.GetStringAsync(Website + "/download/version.txt")).Trim();
                return System.Version.TryParse(v, out _) ? v : null;
            }
            catch { return null; }
        }

        public static int Int(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToInt32(v) : 0;

        public static IList List(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) ? v as IList ?? new ArrayList() : new ArrayList();

        // ------------------------------------------------------------------
        // Battle.net login, desktop-app style: listen on 127.0.0.1, open the
        // normal login page in the browser, and let the server send the
        // browser back to us with the session token.
        // ------------------------------------------------------------------
        public static async Task<string> LoginAsync(CancellationToken ct)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string state = RandomToken();
                string redirect = $"http://127.0.0.1:{port}/callback?state={state}";
                Process.Start(new ProcessStartInfo($"{FunctionsUrl}/bnet-login?redirect={Uri.EscapeDataString(redirect)}")
                {
                    UseShellExecute = true,
                });

                using (ct.Register(() => listener.Stop()))
                {
                    for (;;)
                    {
                        TcpClient client;
                        try
                        {
                            client = await listener.AcceptTcpClientAsync();
                        }
                        catch (Exception) when (ct.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(ct);
                        }

                        using (client)
                        {
                            var stream = client.GetStream();
                            var target = await ReadRequestTarget(stream);
                            if (target == null || !target.StartsWith("/callback?"))
                            {
                                await Respond(stream, 404, "Not found", "");
                                continue;
                            }
                            var q = ParseQuery(target.Substring("/callback?".Length));
                            q.TryGetValue("session", out var session);
                            if (!q.TryGetValue("state", out var gotState) || gotState != state || string.IsNullOrEmpty(session))
                            {
                                await Respond(stream, 400, "Login didn't work", "Please try again from the Ranked Duels Companion window.");
                                continue;
                            }
                            await Respond(stream, 200, "You're connected!",
                                "Ranked Duels Companion is logged in. You can close this tab.");
                            return session;
                        }
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        static async Task<string> ReadRequestTarget(NetworkStream stream)
        {
            // Only the first line matters: "GET /callback?... HTTP/1.1".
            var buf = new byte[8192];
            int len = 0;
            var readTask = Task.Run(async () =>
            {
                while (len < buf.Length)
                {
                    int n = await stream.ReadAsync(buf, len, buf.Length - len);
                    if (n == 0) break;
                    len += n;
                    if (Encoding.ASCII.GetString(buf, 0, len).Contains("\r\n\r\n")) break;
                }
            });
            if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask) return null;
            var firstLine = Encoding.ASCII.GetString(buf, 0, len).Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            var parts = firstLine.Split(' ');
            return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
        }

        static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>();
            foreach (var pair in query.Split('&'))
            {
                var i = pair.IndexOf('=');
                if (i > 0) d[Uri.UnescapeDataString(pair.Substring(0, i))] = Uri.UnescapeDataString(pair.Substring(i + 1).Replace('+', ' '));
            }
            return d;
        }

        static async Task Respond(NetworkStream stream, int status, string title, string text)
        {
            string html = $@"<!doctype html><html><head><meta charset=""utf-8""><title>{WebUtility.HtmlEncode(title)} · Ranked Duels</title>
<style>body{{margin:0;min-height:100vh;display:grid;place-items:center;background:#09090b;color:#ededf0;font:16px system-ui,sans-serif}}
main{{background:#111114;border:1px solid #232329;border-radius:14px;padding:32px 40px;max-width:420px;text-align:center}}
h1{{margin:0 0 8px;font-size:22px;text-transform:uppercase;letter-spacing:.02em}}p{{margin:0 0 20px;color:#8a8a95}}
a{{color:#e5484d;font-weight:600;text-decoration:none}}</style></head>
<body><main><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(text)}</p><a href=""{Website}"">Go to the leaderboard</a></main></body></html>";
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, 0, head.Length);
            await stream.WriteAsync(body, 0, body.Length);
        }

        static string RandomToken()
        {
            var bytes = new byte[18];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
    }
}
