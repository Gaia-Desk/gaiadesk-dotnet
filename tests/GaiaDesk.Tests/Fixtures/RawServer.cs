// A raw TCP "HTTP server" with no framework in between, for the ways a real
// server or proxy fails: accept a request and close the socket before any
// response byte (FIN or RST, with or without reading the body), answer the
// headers and part of the body and then go silent with the socket open, or
// never answer at all. It proves what the SDK does on the wire itself, not
// what a test harness happens to do.

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
}

internal sealed class RawServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private readonly List<TcpClient> _held = new();
    private int _connections;
    private int _requests;

    public RawServer(RawMode mode)
    {
        Mode = mode;
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
        _accept = Task.Run(AcceptLoop);
    }

    public RawMode Mode { get; set; }
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

    private static async Task<(string Head, byte[] Body)> ReadHead(NetworkStream s, CancellationToken ct)
    {
        var buf = new List<byte>();
        var one = new byte[4096];
        while (true)
        {
            var n = await s.ReadAsync(one, ct);
            if (n == 0) return ("", Array.Empty<byte>());
            buf.AddRange(one.AsSpan(0, n).ToArray());
            var all = buf.ToArray();
            var at = Encoding.ASCII.GetString(all).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (at >= 0) return (Encoding.ASCII.GetString(all, 0, at), all.AsSpan(at + 4).ToArray());
        }
    }

    private async Task Serve(TcpClient c)
    {
        try
        {
            var s = c.GetStream();
            var (head, rest) = await ReadHead(s, _stop.Token);
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
                {
                    var len = 0L;
                    foreach (var line in head.Split("\r\n"))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) len = long.Parse(line.Substring(15).Trim());
                    var got = (long)rest.Length;
                    var b = new byte[65536];
                    while (got < len) { var n = await s.ReadAsync(b, _stop.Token); if (n == 0) break; got += n; }
                    c.Dispose();
                    return;
                }
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
            }
            lock (_held) _held.Add(c); // held open, silent, until the server stops
        }
        catch (Exception) { c.Dispose(); }
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
