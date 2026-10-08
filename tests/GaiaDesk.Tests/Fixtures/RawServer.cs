// A raw TCP "HTTP server" with no framework in between, for the ways a real
// server or proxy fails: accept a request and close the socket before any
// response byte (FIN or RST, with or without reading the body), answer the
// headers and part of the body and then go silent with the socket open, or
// never answer at all; answer an error status (keep-alive), or answer one
// request on a connection and drop the next one sent on it. It proves what
// the SDK does on the wire itself, not what a test harness happens to do.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GaiaDesk.Tests.Fixtures;

public enum RawMode
{
    /// <summary>Read the request's headers, then close (FIN) before any response byte, leaving the body unread.</summary>
    CloseBeforeResponse,
    /// <summary>Read the headers, then reset the connection (RST) before any response byte.</summary>
    ResetBeforeResponse,
    /// <summary>Read the whole request (headers and Content-Length body), then close before any response byte.</summary>
    CloseAfterBody,
    /// <summary>Send 200 headers and one chunk of a chunked body, then nothing, with the socket left open.</summary>
    StallMidBody,
    /// <summary>Send 200 headers with a Content-Length larger than what follows, then nothing, the socket open.</summary>
    StallMidJson,
    /// <summary>Send 200 text/event-stream headers and one stdout event, then nothing, the socket open.</summary>
    StallMidEvents,
    /// <summary>Read the request and never answer.</summary>
    Silent,
    /// <summary>Answer <see cref="RawServer.StatusCode"/> with a JSON error envelope (<see cref="RawServer.StatusReason"/>, optional <c>Retry-After</c>), keep-alive.</summary>
    Status,
    /// <summary>Answer 200 with a small JSON body (a stats or exec result), keep-alive.</summary>
    Ok,
    /// <summary>Answer the first request on a connection 200 (keep-alive); close the connection on the next one without answering.</summary>
    KeepAliveThenClose,
}

internal sealed class RawServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private readonly List<TcpClient> _held = new();
    private int _connections;
    private int _requests;

    public RawServer(RawMode mode, int port = 0)
    {
        Mode = mode;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
        _accept = Task.Run(AcceptLoop);
    }

    public RawMode Mode { get; set; }
    /// <summary><see cref="RawMode.Status"/>: the status, its envelope's kind and reason, and a <c>Retry-After</c> (seconds) or null.</summary>
    public int StatusCode { get; set; } = 503;
    public string StatusKind { get; set; } = "unreachable";
    public string StatusReason { get; set; } = "upstream";
    public int? RetryAfter { get; set; }
    /// <summary>The body a 200 answers with: a stats and an exec result at once.</summary>
    public const string OkBody = "{\"desk\":\"123456789\",\"hostname\":\"raw\",\"cpu_percent\":5,\"exit\":0,\"remote_code\":0,\"stdout\":\"ok\",\"stderr\":\"\",\"notes\":[]}";
    public string Url { get; }
    public int Connections => Volatile.Read(ref _connections);
    public int Requests => Volatile.Read(ref _requests);
    private readonly Dictionary<string, int> _byMethod = new();
    /// <summary>Requests received with this method.</summary>
    public int Count(string method) { lock (_byMethod) return _byMethod.TryGetValue(method, out var n) ? n : 0; }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => Serve(c));
        }
    }

    private static async Task<(string Head, byte[] Body)> ReadHead(NetworkStream s, byte[] leftover, CancellationToken ct)
    {
        var buf = new List<byte>(leftover);
        if (Find(buf) is { } early) return early;
        var one = new byte[4096];
        while (true)
        {
            var n = await s.ReadAsync(one, ct);
            if (n == 0) return ("", Array.Empty<byte>());
            buf.AddRange(one.AsSpan(0, n).ToArray());
            if (Find(buf) is { } found) return found;
        }
    }

    private static (string, byte[])? Find(List<byte> buf)
    {
        var all = buf.ToArray();
        var at = Encoding.ASCII.GetString(all).IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return at >= 0 ? (Encoding.ASCII.GetString(all, 0, at), all.AsSpan(at + 4).ToArray()) : null;
    }

    private async Task Serve(TcpClient c)
    {
        try
        {
            var s = c.GetStream();
            var leftover = Array.Empty<byte>();
            for (var onThisConnection = 0; ; onThisConnection++)
            {
                var (head, rest) = await ReadHead(s, leftover, _stop.Token);
                if (head.Length == 0) { c.Dispose(); return; }
                Interlocked.Increment(ref _requests);
                var method = head.Split(' ')[0];
                lock (_byMethod) _byMethod[method] = (_byMethod.TryGetValue(method, out var m) ? m : 0) + 1;
                switch (Mode)
                {
                    case RawMode.CloseBeforeResponse:
                        c.Dispose();
                        return;
                    case RawMode.ResetBeforeResponse:
                        c.Client.LingerState = new LingerOption(true, 0);
                        c.Dispose();
                        return;
                    case RawMode.CloseAfterBody:
                        await ReadBody(s, head, rest);
                        c.Dispose();
                        return;
                    case RawMode.StallMidBody:
                        await Write(s, "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n");
                        break;
                    case RawMode.StallMidJson:
                        await Write(s, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{\"desk\":");
                        break;
                    case RawMode.StallMidEvents:
                        await Write(s, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n");
                        var ev = "event: stdout\ndata: {\"event\":\"stdout\",\"data\":\"hi\"}\n\n";
                        await Write(s, $"{Encoding.UTF8.GetByteCount(ev):x}\r\n{ev}\r\n");
                        break;
                    case RawMode.Silent:
                        break;
                    case RawMode.Status:
                    {
                        leftover = await ReadBody(s, head, rest);
                        var env = $"{{\"error\":{{\"kind\":\"{StatusKind}\",\"message\":\"raw {StatusCode}\",\"reason\":\"{StatusReason}\",\"request_id\":\"req_raw\"}}}}";
                        var ra = RetryAfter is { } sec ? $"Retry-After: {sec}\r\n" : "";
                        await Write(s, $"HTTP/1.1 {StatusCode} Raw\r\nContent-Type: application/json\r\n{ra}Content-Length: {Encoding.UTF8.GetByteCount(env)}\r\n\r\n{env}");
                        continue;
                    }
                    case RawMode.Ok:
                    case RawMode.KeepAliveThenClose when onThisConnection == 0:
                        leftover = await ReadBody(s, head, rest);
                        await Write(s, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(OkBody)}\r\n\r\n{OkBody}");
                        continue;
                    case RawMode.KeepAliveThenClose:
                        c.Dispose(); // the next request on a reused connection: closed before any answer
                        return;
                }
                lock (_held) _held.Add(c); // held open, silent, until the server stops
                return;
            }
        }
        catch (Exception) { c.Dispose(); }
    }

    /// <summary>Read a request's Content-Length body; what follows it (the next request) is returned.</summary>
    private async Task<byte[]> ReadBody(NetworkStream s, string head, byte[] rest)
    {
        var len = 0L;
        foreach (var line in head.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) len = long.Parse(line.Substring(15).Trim());
        var got = new List<byte>(rest);
        var b = new byte[65536];
        while (got.Count < len) { var n = await s.ReadAsync(b, _stop.Token); if (n == 0) break; got.AddRange(b.AsSpan(0, n).ToArray()); }
        return got.Count > len ? got.Skip((int)len).ToArray() : Array.Empty<byte>();
    }

    private static async Task Write(NetworkStream s, string text)
    {
        await s.WriteAsync(Encoding.ASCII.GetBytes(text));
        await s.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        lock (_held) foreach (var c in _held) c.Dispose();
        try { await _accept; } catch (Exception) { }
    }
}
