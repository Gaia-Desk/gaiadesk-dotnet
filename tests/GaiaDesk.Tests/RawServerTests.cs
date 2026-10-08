// A server or proxy that drops or stalls a connection, on a raw socket (no
// Kestrel): the SDK fails with a clear transport error within its configured
// timeouts, retries only where the policy allows, and never hangs.

using System.Diagnostics;
using GaiaDesk.Tests.Fixtures;
using Xunit;

namespace GaiaDesk.Tests;

public sealed class RawServerTests
{
    private const string D = "123456789";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10); // a hang shows as this, not as a stuck run

    private static GaiaDeskClient Gd(RawServer s, int retries = 2, double idle = 1, double response = 30) => new(new GaiaDeskOptions
    {
        ApiKey = "ak_t", DeskToken = "gdagt_t", BaseUrl = s.Url, E2e = E2eMode.Off,
        Retry = new RetryOptions { MaxRetries = retries, BaseDelay = TimeSpan.FromMilliseconds(5) },
        Timeouts = new TimeoutOptions { IdleTimeout = TimeSpan.FromSeconds(idle), ResponseTimeout = TimeSpan.FromSeconds(response) },
    });

    private static async Task<(T Error, TimeSpan Took)> Fails<T>(Func<Task> f) where T : Exception
    {
        var sw = Stopwatch.StartNew();
        var t = Assert.ThrowsAsync<T>(f);
        var done = await Task.WhenAny(t, Task.Delay(Bound));
        Assert.True(done == t, $"no answer within {Bound.TotalSeconds} s: the SDK hung");
        return (await t, sw.Elapsed);
    }

    [Theory]
    [InlineData(RawMode.CloseBeforeResponse)]
    [InlineData(RawMode.ResetBeforeResponse)]
    public async Task DroppedBeforeAnyResponseByte_AReadIsRetried_ThenUnreachableNetwork(RawMode mode)
    {
        await using var s = new RawServer(mode);
        using var gd = Gd(s);
        var (e, _) = await Fails<UnreachableException>(() => gd.DownloadBytesAsync(D, "/tmp/x"));
        Assert.Equal((ErrorKinds.Network, "network"), (e.Kind, e.Reason!));
        Assert.Equal("GET /desks/123456789/files", e.Operation);
        // The first try and the SDK's two retries: a GET is safe to send again. (.NET's HTTP stack may also
        // re-send a GET — a request without content — when the connection it used was closed before any
        // answer, up to 3 times per attempt; every other request carries content, so it never re-sends
        // those: the next test and RetryTests pin it.)
        Assert.InRange(s.Count("GET"), 3, 12);
        var before = s.Count("GET");
        await Fails<UnreachableException>(() => gd.StatsAsync(D));
        Assert.InRange(s.Count("GET") - before, 3, 12);
        using var once = Gd(s, retries: 0);
        before = s.Count("GET");
        await Fails<UnreachableException>(() => once.StatsAsync(D));
        Assert.InRange(s.Count("GET") - before, 1, 4);
    }

    [Theory]
    [InlineData(RawMode.CloseBeforeResponse)]
    [InlineData(RawMode.ResetBeforeResponse)]
    [InlineData(RawMode.CloseAfterBody)]
    public async Task DroppedBeforeAnyResponseByte_ALargeUploadOrAnExecIsNeverSentTwice(RawMode mode)
    {
        await using var s = new RawServer(mode);
        using var gd = Gd(s);
        var big = new byte[4 * 1024 * 1024];
        var (e, _) = await Fails<UnreachableException>(() => gd.UploadBytesAsync(D, "/tmp/big", big));
        Assert.Equal(ErrorKinds.Network, e.Kind);
        Assert.Equal(1, s.Count("PUT"));
        await Fails<UnreachableException>(() => gd.UploadAsync(D, "/tmp/streamed", new MemoryStream(big)));
        Assert.Equal(2, s.Count("PUT"));
        await Fails<UnreachableException>(() => gd.ExecAsync(D, "deploy"));
        Assert.Equal(1, s.Count("POST"));
        var st = await gd.ExecStream(D, "deploy").WaitAsync().WaitAsync(Bound);
        Assert.Equal(ErrorKinds.Unreachable, st.Error!.Kind);
        Assert.Equal(2, s.Count("POST"));
        await Fails<UnreachableException>(() => gd.RunJobAsync(D, "nightly", "make"));
        Assert.Equal(3, s.Count("POST"));
        Assert.Equal(0, s.Count("GET"));
    }

    [Fact]
    public async Task StalledMidDownload_ConnectionLostTimeout_WithinTheIdleTimeout_NoPartialFile()
    {
        await using var s = new RawServer(RawMode.StallMidBody);
        using var gd = Gd(s, idle: 1);
        await using (var body = await gd.OpenReadAsync(D, "/tmp/x").WaitAsync(Bound))
        {
            var buf = new byte[16];
            Assert.Equal(5, await body.ReadAsync(buf).AsTask().WaitAsync(Bound));
            var (e, took) = await Fails<ConnectionLostException>(() => body.ReadAsync(buf).AsTask());
            Assert.Equal((ErrorKinds.Timeout, "timeout"), (e.Kind, e.Reason!));
            Assert.Contains("IdleTimeout", e.Message);
            Assert.True(took < TimeSpan.FromSeconds(5), $"took {took}");
        }
        var file = Path.Combine(Path.GetTempPath(), $"gd-stall-{Guid.NewGuid():N}");
        await Fails<ConnectionLostException>(() => gd.DownloadFileAsync(D, "/tmp/x", file));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task StalledMidJson_ConnectionLostTimeout()
    {
        await using var s = new RawServer(RawMode.StallMidJson);
        using var gd = Gd(s, retries: 0);
        var (e, took) = await Fails<ConnectionLostException>(() => gd.StatsAsync(D));
        Assert.Equal(ErrorKinds.Timeout, e.Kind);
        Assert.True(took < TimeSpan.FromSeconds(5), $"took {took}");
    }

    [Fact]
    public async Task StalledMidStream_TheStreamEndsWithATimeoutError()
    {
        await using var s = new RawServer(RawMode.StallMidEvents);
        using var gd = Gd(s);
        var r = await gd.ExecStream(D, "tail -f log").CollectAsync().WaitAsync(Bound);
        Assert.Equal("hi", r.Stdout);
        Assert.Equal((ErrorKinds.ConnectionLost, "timeout"), (r.Exit.Error!.Kind, r.Exit.Error.Reason!));
        Assert.Equal(255, r.Exit.ExitCode);
        var logs = await gd.FollowJobLogs(D, "build").WaitAsync().WaitAsync(Bound);
        Assert.Equal(ErrorKinds.ConnectionLost, logs.Error!.Kind);
    }

    [Fact]
    public async Task ASilentServer_UnreachableTimeout_WithinTheResponseTimeout_NotRetried()
    {
        await using var s = new RawServer(RawMode.Silent);
        using var gd = Gd(s, response: 1);
        var (e, took) = await Fails<UnreachableException>(() => gd.StatsAsync(D));
        Assert.Equal((ErrorKinds.Timeout, "timeout"), (e.Kind, e.Reason!));
        Assert.Contains("ResponseTimeout", e.Message);
        Assert.True(took < TimeSpan.FromSeconds(5), $"took {took}");
        await Fails<UnreachableException>(() => gd.UploadBytesAsync(D, "/tmp/big", new byte[4 * 1024 * 1024]));
        Assert.Equal(1, s.Count("GET"));
        Assert.Equal(1, s.Count("PUT"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var patient = Gd(s, response: 600);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => patient.StatsAsync(D, null, cts.Token)).WaitAsync(Bound);
    }

    [Fact]
    public async Task Stress_300DroppedRequests_NeverHang()
    {
        await using var s = new RawServer(RawMode.CloseBeforeResponse);
        using var gd = Gd(s, retries: 1);
        var up = new byte[512 * 1024];
        var modes = new[] { RawMode.CloseBeforeResponse, RawMode.ResetBeforeResponse, RawMode.CloseAfterBody };
        for (var i = 0; i < 300; i++)
        {
            s.Mode = modes[i % 3];
            Task op = (i % 2) == 0 ? gd.DownloadBytesAsync(D, "/tmp/x") : gd.UploadBytesAsync(D, "/tmp/up", up);
            var done = await Task.WhenAny(op, Task.Delay(Bound));
            Assert.True(done == op, $"iteration {i} ({s.Mode}) hung");
            var e = await Assert.ThrowsAsync<UnreachableException>(() => op);
            Assert.Equal(ErrorKinds.Network, e.Kind);
        }
        Assert.Equal(150, s.Count("PUT"));               // every upload sent exactly once
        Assert.InRange(s.Count("GET"), 300, 1200);        // every read tried twice (plus the stack's bodyless re-sends)
    }

    [Fact]
    public void Timeouts_AreChecked()
    {
        Assert.Throws<UsageException>(() => new GaiaDeskClient(new GaiaDeskOptions { ApiKey = "ak", Timeouts = new TimeoutOptions { IdleTimeout = TimeSpan.Zero } }));
        Assert.Throws<UsageException>(() => new GaiaDeskClient(new GaiaDeskOptions { ApiKey = "ak", Timeouts = new TimeoutOptions { ResponseTimeout = TimeSpan.FromSeconds(-2) } }));
        using var none = new GaiaDeskClient(new GaiaDeskOptions { ApiKey = "ak", Timeouts = new TimeoutOptions { IdleTimeout = Timeout.InfiniteTimeSpan, ResponseTimeout = Timeout.InfiniteTimeSpan } });
    }
}
