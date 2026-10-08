// The retry rule on a raw socket: a request is sent again only when that
// cannot run anything twice. Every failure mode counted per method, every call
// bounded (a hang fails within 10 s instead of hanging the suite).

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using GaiaDesk.Http;
using GaiaDesk.Tests.Fixtures;
using Xunit;

namespace GaiaDesk.Tests;

public sealed class RetryTests
{
    private const string D = "123456789";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static GaiaDeskClient Gd(string url, int retries = 2, double baseMs = 5, double idle = 1, double response = 30) => new(new GaiaDeskOptions
    {
        ApiKey = "ak_t", DeskToken = "gdagt_t", BaseUrl = url, E2e = E2eMode.Off,
        Retry = new RetryOptions { MaxRetries = retries, BaseDelay = TimeSpan.FromMilliseconds(baseMs) },
        Timeouts = new TimeoutOptions { IdleTimeout = TimeSpan.FromSeconds(idle), ResponseTimeout = TimeSpan.FromSeconds(response) },
    });

    private static async Task<(T Error, TimeSpan Took)> Fails<T>(Func<Task> f) where T : Exception
    {
        var sw = Stopwatch.StartNew();
        var t = Assert.ThrowsAnyAsync<T>(f);
        var done = await Task.WhenAny(t, Task.Delay(Bound));
        Assert.True(done == t, $"no answer within {Bound.TotalSeconds} s: the SDK hung");
        return (await t, sw.Elapsed);
    }

    private static async Task<T> Bounded<T>(Task<T> t) => await t.WaitAsync(Bound);

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static readonly CallOptions Keyed = new() { IdempotencyKey = "key-1" };

    // ─────────────────────────── connection never made ───────────────────────────

    [Fact]
    public async Task ConnectionRefused_ThenTheServerAppears_APostIsSentAndRunsOnce()
    {
        var port = FreePort();
        var appears = Task.Run(async () =>
        {
            await Task.Delay(100);
            return new RawServer(RawMode.Ok, port);
        });
        using var gd = Gd($"http://127.0.0.1:{port}/v1", retries: 4, baseMs: 100);
        var r = await Bounded(gd.ExecAsync(D, "deploy"));
        Assert.Equal(0, r.Exit);
        await using var s = await appears;
        Assert.Equal(1, s.Count("POST"));
        Assert.Equal(1, s.Requests);
    }

    [Fact]
    public async Task ConnectionRefused_WithRetriesOff_FailsAtOnce()
    {
        var port = FreePort();
        // One refused connect is instant on Linux and macOS but ~2 s on Windows (it retries the SYN):
        // "at once" is one connect, with no back-off (a retry would wait at least 2.5 s here) after it.
        var sw = Stopwatch.StartNew();
        using (var probe = new TcpClient())
            await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Bound));
        var oneConnect = sw.Elapsed;
        using var gd = Gd($"http://127.0.0.1:{port}/v1", retries: 0, baseMs: 5000);
        var (e, took) = await Fails<UnreachableException>(() => gd.ExecAsync(D, "deploy"));
        Assert.Equal((ErrorKinds.Network, "network"), (e.Kind, e.Reason!));
        Assert.True(took < oneConnect + TimeSpan.FromSeconds(2), $"took {took}; one connect took {oneConnect}");
    }

    // ─────────────────────── lost after sending: GETs only ───────────────────────

    [Theory]
    [InlineData(RawMode.CloseBeforeResponse)]
    [InlineData(RawMode.ResetBeforeResponse)]
    public async Task DroppedBeforeAnyResponseByte_EveryChangeIsSentOnce_AKeyDoesNotUnlockARetry(RawMode mode)
    {
        await using var s = new RawServer(mode);
        using var gd = Gd(s.Url);
        await Fails<UnreachableException>(() => gd.ExecAsync(D, "deploy", new ExecOptions { IdempotencyKey = "key-1" }));
        Assert.Equal(1, s.Count("POST"));
        await Fails<UnreachableException>(() => gd.RunJobAsync(D, "nightly", "make", new JobOptions { IdempotencyKey = "key-1" }));
        Assert.Equal(2, s.Count("POST"));
        await Fails<UnreachableException>(() => gd.UploadBytesAsync(D, "/tmp/x", new byte[1024], Keyed));
        Assert.Equal(1, s.Count("PUT"));
        // Bodiless DELETEs: the HTTP stack would re-send these by itself if the SDK let it.
        var (e, _) = await Fails<UnreachableException>(() => gd.KillJobAsync(D, "nightly"));
        Assert.Equal(ErrorKinds.Network, e.Kind);
        Assert.Equal(1, s.Count("DELETE"));
        await Fails<UnreachableException>(() => gd.RevokeTokenAsync(D, "gdagt_old"));
        Assert.Equal(2, s.Count("DELETE"));
        Assert.Equal(0, s.Count("GET"));
    }

    // ───────────────────────────── statuses ─────────────────────────────

    [Theory]
    [InlineData(502, "connection_lost", "desk_disconnected")]
    [InlineData(502, "protocol", "protocol")]
    [InlineData(503, "unreachable", "upstream")]
    [InlineData(504, "unreachable", "upstream")]
    public async Task GatewayStatuses_AGetIsTriedThreeTimes_APostOnce(int status, string kind, string reason)
    {
        await using var s = new RawServer(RawMode.Status) { StatusCode = status, StatusKind = kind, StatusReason = reason };
        using var gd = Gd(s.Url);
        var (e, _) = await Fails<GaiaDeskException>(() => gd.StatsAsync(D));
        Assert.Equal(status, e.Status);
        Assert.Equal(3, s.Count("GET"));
        await Fails<GaiaDeskException>(() => gd.ExecAsync(D, "deploy"));
        Assert.Equal(1, s.Count("POST"));
        await Fails<GaiaDeskException>(() => gd.KillJobAsync(D, "nightly"));
        Assert.Equal(1, s.Count("DELETE"));
        using var once = Gd(s.Url, retries: 0);
        await Fails<GaiaDeskException>(() => once.StatsAsync(D));
        Assert.Equal(4, s.Count("GET"));
    }

    [Theory]
    [InlineData("api_disabled")]
    [InlineData("desk_ops_disabled")]
    [InlineData("local_api_off")]
    public async Task A503ThatIsPermanent_IsNotRetried(string reason)
    {
        await using var s = new RawServer(RawMode.Status) { StatusCode = 503, StatusReason = reason };
        using var gd = Gd(s.Url);
        await Fails<GaiaDeskException>(() => gd.StatsAsync(D));
        Assert.Equal(1, s.Count("GET"));
    }

    [Fact]
    public async Task A503RetryAfter_IsWaitedFor()
    {
        await using var s = new RawServer(RawMode.Status) { StatusCode = 503, RetryAfter = 1 };
        using var gd = Gd(s.Url, retries: 1);
        var (_, took) = await Fails<UnreachableException>(() => gd.StatsAsync(D));
        Assert.Equal(2, s.Count("GET"));
        Assert.True(took >= TimeSpan.FromMilliseconds(950), $"took {took}");
    }

    [Fact]
    public async Task A429_AnyMethodIsRetried_AfterRetryAfter()
    {
        await using var s = new RawServer(RawMode.Status) { StatusCode = 429, StatusKind = "refused", StatusReason = "rate_limited", RetryAfter = 0 };
        using var gd = Gd(s.Url);
        var (e, _) = await Fails<RefusedException>(() => gd.ExecAsync(D, "deploy"));
        Assert.Equal((429, Reasons.RateLimited), (e.Status!.Value, e.Reason!));
        Assert.Equal(3, s.Count("POST"));
        await Fails<RefusedException>(() => gd.UploadBytesAsync(D, "/tmp/x", new byte[1024]));
        Assert.Equal(3, s.Count("PUT"));
        s.StatusReason = "desk_busy";
        await Fails<RefusedException>(() => gd.KillJobAsync(D, "nightly"));
        Assert.Equal(3, s.Count("DELETE"));
        using var once = Gd(s.Url, retries: 0);
        await Fails<RefusedException>(() => once.ExecAsync(D, "deploy"));
        Assert.Equal(4, s.Count("POST"));
    }

    [Fact]
    public async Task A429RetryAfterLongerThanMaxRetryWait_FailsAtOnce_CarryingIt()
    {
        await using var s = new RawServer(RawMode.Status) { StatusCode = 429, StatusKind = "refused", StatusReason = "rate_limited", RetryAfter = 120 };
        using var gd = Gd(s.Url);
        var (e, took) = await Fails<RefusedException>(() => gd.StatsAsync(D));
        Assert.Equal(TimeSpan.FromSeconds(120), e.RetryAfter);
        Assert.Equal(1, s.Count("GET"));
        Assert.True(took < TimeSpan.FromSeconds(1), $"took {took}");
    }

    [Fact]
    public async Task A409IdempotencyKeyInFlight_AnyMethodIsRetried()
    {
        await using var s = new RawServer(RawMode.Status) { StatusCode = 409, StatusKind = "refused", StatusReason = Reasons.IdempotencyKeyInFlight };
        using var gd = Gd(s.Url);
        await Fails<GaiaDeskException>(() => gd.ExecAsync(D, "deploy", new ExecOptions { IdempotencyKey = "key-1" }));
        Assert.Equal(3, s.Count("POST"));
        await Fails<GaiaDeskException>(() => gd.UploadBytesAsync(D, "/tmp/x", new byte[16], Keyed));
        Assert.Equal(3, s.Count("PUT"));
        await Fails<GaiaDeskException>(() => gd.KillJobAsync(D, "nightly", Keyed));
        Assert.Equal(3, s.Count("DELETE"));
        s.StatusReason = "conflict"; // any other 409 is final
        await Fails<GaiaDeskException>(() => gd.ExecAsync(D, "deploy"));
        Assert.Equal(4, s.Count("POST"));
    }

    // ───────────────────────────── timeouts ─────────────────────────────

    [Theory]
    [InlineData(RawMode.Silent)]
    [InlineData(RawMode.StallMidJson)]
    public async Task Timeouts_AreNeverRetried(RawMode mode)
    {
        await using var s = new RawServer(mode);
        using var gd = Gd(s.Url, idle: 1, response: 1);
        var (e, _) = await Fails<GaiaDeskException>(() => gd.StatsAsync(D));
        Assert.Equal(ErrorKinds.Timeout, e.Kind);
        Assert.Equal(1, s.Count("GET"));
    }

    // ─────────────────── the HTTP stack's own re-sends ───────────────────

    [Fact]
    public async Task AReusedConnectionClosedBeforeAnswering_OnlyAGetIsSentAgain()
    {
        await using var s = new RawServer(RawMode.KeepAliveThenClose);
        using var gd = Gd(s.Url);
        // Each change rides a pooled connection a GET just used; the server closes it on receipt.
        async Task<int> Sends(string method, Func<Task> change)
        {
            Assert.Equal(5, (await Bounded(gd.StatsAsync(D))).CpuPercent); // the connection, answered and kept
            var before = s.Count(method);
            var (e, _) = await Fails<UnreachableException>(change);
            Assert.Equal((ErrorKinds.Network, "network"), (e.Kind, e.Reason!));
            return s.Count(method) - before;
        }
        Assert.Equal(1, await Sends("DELETE", () => gd.KillJobAsync(D, "nightly")));
        Assert.Equal(1, await Sends("DELETE", () => gd.RevokeTokenAsync(D, "gdagt_old")));
        Assert.Equal(1, await Sends("POST", () => gd.ExecAsync(D, "deploy")));
        Assert.Equal(1, await Sends("PUT", () => gd.UploadBytesAsync(D, "/tmp/x", new byte[1024])));
        // A GET on such a connection is sent again (by the stack or the SDK) and succeeds.
        await Bounded(gd.StatsAsync(D));
        Assert.Equal(5, (await Bounded(gd.StatsAsync(D))).CpuPercent);
    }

    // ───────────────────────────── the policy itself ─────────────────────────────

    [Fact]
    public void Defaults_Backoff_Jitter_AndTheRetryAfterCap()
    {
        var o = new RetryOptions();
        Assert.Equal((2, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(60)), (o.MaxRetries, o.BaseDelay, o.MaxDelay, o.MaxRetryWait));
        Assert.Equal(TimeSpan.FromMilliseconds(250), RetryPolicy.Backoff(o, 0, 1.0));
        Assert.Equal(TimeSpan.FromMilliseconds(125), RetryPolicy.Backoff(o, 0, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(500), RetryPolicy.Backoff(o, 1, 1.0));
        Assert.Equal(TimeSpan.FromSeconds(8), RetryPolicy.Backoff(o, 5, 1.0));  // 250 ms * 32 = 8 s
        Assert.Equal(TimeSpan.FromSeconds(8), RetryPolicy.Backoff(o, 20, 1.0)); // capped
        Assert.Equal(TimeSpan.FromSeconds(4), RetryPolicy.Backoff(o, 20, 0.5));
        for (var i = 0; i < 2000; i++) Assert.InRange(RetryPolicy.Jitter(), 0.5, 1.0);

        GaiaDeskException Answer(int status, int? retryAfterS) => new RefusedException("x", new ErrorDetails
        {
            Status = status, Reason = status == 429 ? Reasons.RateLimited : "upstream", RetryAfter = retryAfterS is { } r ? TimeSpan.FromSeconds(r) : null,
        });
        Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.Delay(o, Answer(429, 60), 0, 1.0));
        Assert.Null(RetryPolicy.Delay(o, Answer(429, 61), 0, 1.0));
        Assert.Equal(TimeSpan.FromSeconds(3), RetryPolicy.Delay(o, Answer(503, 3), 0, 1.0));
        Assert.Equal(TimeSpan.Zero, RetryPolicy.Delay(o, Answer(429, -5), 0, 1.0));
        Assert.Equal(TimeSpan.FromMilliseconds(500), RetryPolicy.Delay(o, Answer(502, 30), 1, 1.0)); // only 429/503 honour Retry-After
        Assert.Equal(TimeSpan.FromMilliseconds(250), RetryPolicy.Delay(o, Answer(503, null), 0, 1.0));
    }

    [Fact]
    public void RetryOptions_AreChecked()
    {
        foreach (var bad in new[]
        {
            new RetryOptions { MaxRetries = -1 }, new RetryOptions { BaseDelay = TimeSpan.FromMilliseconds(-1) },
            new RetryOptions { MaxDelay = TimeSpan.FromSeconds(-1) }, new RetryOptions { MaxRetryWait = TimeSpan.FromSeconds(-1) },
        })
            Assert.Throws<UsageException>(() => new GaiaDeskClient(new GaiaDeskOptions { ApiKey = "ak", Retry = bad }));
        using var off = new GaiaDeskClient(new GaiaDeskOptions { ApiKey = "ak", Retry = new RetryOptions { MaxRetries = 0, BaseDelay = TimeSpan.Zero, MaxRetryWait = TimeSpan.Zero } });
    }

    [Fact]
    public void ConnectFailures_AreToldFromLostConnections()
    {
        static HttpRequestException Http(HttpRequestError error, Exception inner) => new(error, "x", inner);
        Assert.True(RetryPolicy.NeverConnected(Http(HttpRequestError.ConnectionError, new SocketException((int)SocketError.ConnectionRefused))));
        Assert.True(RetryPolicy.NeverConnected(Http(HttpRequestError.NameResolutionError, new SocketException((int)SocketError.HostNotFound))));
        Assert.True(RetryPolicy.NeverConnected(Http(HttpRequestError.SecureConnectionError, new IOException("handshake: unexpected EOF"))));
        Assert.True(RetryPolicy.NeverConnected(Http(HttpRequestError.ConnectionError, new UnreachableException("no socket", new ErrorDetails { Reason = Reasons.LocalApiUnavailable }))));
        // Final: a connect that timed out, a certificate refused, anything after the request was written.
        Assert.False(RetryPolicy.NeverConnected(Http(HttpRequestError.ConnectionError, new SocketException((int)SocketError.TimedOut))));
        Assert.False(RetryPolicy.NeverConnected(Http(HttpRequestError.SecureConnectionError, new AuthenticationException("the remote certificate is invalid"))));
        Assert.False(RetryPolicy.NeverConnected(Http(HttpRequestError.ResponseEnded, new IOException("premature EOF"))));
        Assert.False(RetryPolicy.NeverConnected(Http(HttpRequestError.Unknown, new IOException("reset", new SocketException((int)SocketError.ConnectionReset)))));
        // Runtimes before .NET 8 (no HttpRequestError): the connect step's SocketException is the direct inner one.
        Assert.True(RetryPolicy.NeverConnected(new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)), null));
        Assert.False(RetryPolicy.NeverConnected(new HttpRequestException("x", new IOException("reset", new SocketException((int)SocketError.ConnectionReset))), null));
        Assert.False(RetryPolicy.NeverConnected(new HttpRequestException("x", new SocketException((int)SocketError.TimedOut)), null));
    }
}
