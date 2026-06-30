using Basis.Network.Core;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Server
{
    public sealed class BasisNetworkHealthCheck : IDisposable
    {
        private static readonly byte[] Empty = Array.Empty<byte>();

        private readonly HttpListener httpListener = new HttpListener();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        private readonly string host;
        private readonly ushort port;
        private readonly string pathNormalized;

        private readonly DateTimeOffset startTimeUtc;

        private Task listenTask;

        public BasisNetworkHealthCheck(Configuration config)
        {
            host = config.HealthCheckHost;
            port = config.HealthCheckPort;

            // path を正規化する。先頭 slash を保証し、root 以外の末尾 slash を除去する。
            pathNormalized = NormalizePath(config.HealthPath);

            // prefix は slash で終わる必要がある。IPv6 address literal には bracket 表記が必要。
            httpListener.Prefixes.Add($"http://{FormatHost(host)}:{port}/");
            httpListener.Start();

            startTimeUtc = DateTimeOffset.UtcNow;

            listenTask = ListenLoopAsync(cts.Token);

            BNL.Log($"HTTP health check started at 'http://{FormatHost(host)}:{port}{pathNormalized}'");
        }

        private static string NormalizePath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "/";

            p = p.Trim();
            if (!p.StartsWith("/")) p = "/" + p;

            // "/" でない場合は末尾 slash を取り除く。
            if (p.Length > 1 && p.EndsWith("/")) p = p.Substring(0, p.Length - 1);

            return p;
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context = null;

                try
                {
                    context = await httpListener.GetContextAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return; // listener が閉じられた。
                }
                catch (HttpListenerException)
                {
                    return; // listener が停止した、または error。
                }
                catch (Exception e)
                {
                    BNL.LogWarning("HTTP health check loop error: " + e);
                    continue;
                }

                _ = Task.Run(() => HandleRequest(context), token);
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                var req = context.Request;
                var res = context.Response;

                // 基本的な hardening / semantics。
                res.Headers["Cache-Control"] = "no-store, max-age=0";
                res.Headers["X-Content-Type-Options"] = "nosniff";

                if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    res.StatusCode = 405;
                    res.Close(Empty, false);
                    return;
                }

                var reqPath = NormalizePath(req.Url.AbsolutePath);
                if (!string.Equals(reqPath, pathNormalized, StringComparison.Ordinal))
                {
                    res.StatusCode = 404;
                    res.Close(Empty, false);
                    return;
                }

                // readiness を決める。例: "listening" は process 生存、"ready" は server 存在を意味する。
                bool ready = NetworkServer.Server != null; // 実際の readiness check に置き換え可能。
                res.StatusCode = ready ? 200 : 503;

                var nowUtc = DateTimeOffset.UtcNow;

                // numeric field は quote せず number として JSON を組み立てる。
                // JSON escaping の心配を完全になくしたい場合は、version を制御済みの単純値に保つ。
                string json;

                if (NetworkServer.Configuration.EnableStatistics && NetworkServer.Server != null)
                {
                    int visitors = NetworkServer.Server.ConnectedPeersCount;
                    long sent = NetworkServer.Server.Statistics.BytesSent;
                    long recv = NetworkServer.Server.Statistics.BytesReceived;
                    int capacity = NetworkServer.Configuration.PeerLimit;

                    json =
                        "{" +
                        "\"listening\":true," +
                        $"\"ready\":{(ready ? "true" : "false")}," +
                        $"\"visitors\":{visitors}," +
                        $"\"capacity\":{capacity}," +
                        $"\"sent\":{sent}," +
                        $"\"recv\":{recv}," +
                        $"\"currentTime\":\"{nowUtc:O}\"," +
                        $"\"startTime\":\"{startTimeUtc:O}\"," +
                        $"\"version\":\"{BasisNetworkVersion.ServerVersion}\"" +
                        "}";
                }
                else
                {
                    json =
                        "{" +
                        "\"listening\":true," +
                        $"\"ready\":{(ready ? "true" : "false")}," +
                        $"\"currentTime\":\"{nowUtc:O}\"," +
                        $"\"startTime\":\"{startTimeUtc:O}\"," +
                        $"\"version\":\"{BasisNetworkVersion.ServerVersion}\"" +
                        "}";
                }

                byte[] payload = Encoding.UTF8.GetBytes(json);

                res.ContentType = "application/json; charset=utf-8";
                res.ContentEncoding = Encoding.UTF8;
                res.ContentLength64 = payload.Length;

                res.OutputStream.Write(payload, 0, payload.Length);
                res.OutputStream.Close();
            }
            catch
            {
                try { context?.Response?.Abort(); } catch { /* 無視 */ }
            }
        }

        // HttpListener URL prefix では、IPv6 address literal に bracket 表記が必要。
        private static string FormatHost(string host) =>
            IPAddress.TryParse(host, out IPAddress addr) && addr.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{host}]"
                : host;

        public void Stop() => Dispose();

        public void Dispose()
        {
            if (cts.IsCancellationRequested) return;

            cts.Cancel();

            try { httpListener.Stop(); } catch { }
            try { httpListener.Close(); } catch { }

            try { listenTask?.Wait(250); } catch { }

            cts.Dispose();
        }
    }
}
