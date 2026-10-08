// End-to-end encryption on the hosted API, against a mock API that is also the
// desk: every operation sealed gives exactly what it gives in the clear, the
// API never sees the command, env, stdin, path or file bytes, and the modes,
// pins and retries behave.

using System.Text;
using System.Text.Json;
using GaiaDesk.E2e;
using GaiaDesk.Tests.Fixtures;
using Xunit;

namespace GaiaDesk.Tests;

public sealed class E2eApiTests : IAsyncLifetime
{
    private const string Canary = "canary-7f3a9";
    private static readonly byte[] Key = E2eCrypto.Hex("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");
    private static readonly byte[] Key2 = E2eCrypto.Hex("2122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40");
    private static readonly string Pub = E2eCrypto.B64Url(E2eCrypto.X25519Public(Key));
    private static readonly string Pub2 = E2eCrypto.B64Url(E2eCrypto.X25519Public(Key2));

    private const string SealedDesk = "111111111";
    private const string Old = "222222222";     // no end-to-end key
    private const string Must = "333333333";    // requires it
    private const string Asleep = "444444444";  // requires it, offline until woken
    private const string Gone = "555555555";    // offline, no wake path
    private const string Stale = "666666666";   // requires it; the first lookup lists no key
    private const string Rotated = "777777777";

    private MockApi _api = null!;
    private readonly List<string> _warnings = new();

    public async Task InitializeAsync() => _api = await MockApi.StartAsync(new()
    {
        [SealedDesk] = new MockDesk { Secret = Key },
        [Old] = new MockDesk(),
        [Must] = new MockDesk { Secret = Key, Required = true },
        [Asleep] = new MockDesk { Secret = Key, Required = true, Online = false, Wakeable = true },
        [Gone] = new MockDesk { Secret = Key, Online = false },
        [Stale] = new MockDesk { Secret = Key, Required = true, HideKeyLookups = 1 },
        [Rotated] = new MockDesk { Secret = Key },
    });

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private GaiaDeskClient Gd(E2eMode mode = E2eMode.Auto, Dictionary<string, string>? pins = null, string key = "ak_test", string? deskToken = "gdagt_test") => new(new GaiaDeskOptions
    {
        ApiKey = key, DeskToken = deskToken, BaseUrl = _api.Url, E2e = mode, E2eKeys = pins,
        OnWarning = m => { lock (_warnings) _warnings.Add(m); }, Retry = new RetryOptions { BaseDelay = TimeSpan.FromMilliseconds(5) },
    });

    private static string J<T>(T v) => JsonSerializer.Serialize(v, GaiaDeskJson.Options);

    /// <summary>Run <paramref name="f"/>, and assert the API saw nothing of the canary in any request it made.</summary>
    private async Task<T> Blind<T>(Func<Task<T>> f)
    {
        var before = _api.Requests.Count;
        var r = await f();
        var seen = _api.Requests.Skip(before).ToList();
        Assert.NotEmpty(seen);
        foreach (var q in seen)
        {
            var raw = string.Join("|", q.Path, string.Join(",", q.Query), string.Join(",", q.Headers), q.BodyText);
            Assert.False(raw.Contains(Canary), $"the API saw the canary in {q.Method} {q.Path}");
        }
        return r;
    }

    /// <summary>The same call sealed (the API saw none of it) and in the clear: equal answers.</summary>
    private async Task<T> Same<T>(Func<GaiaDeskClient, Task<T>> f, string? key = null, string? token = "gdagt_test")
    {
        using var sealedGd = Gd(key: key ?? "ak_test", deskToken: token);
        using var plainGd = Gd(E2eMode.Off, key: key ?? "ak_test", deskToken: token);
        var before = _api.Sealed.Count;
        var s = await Blind(() => f(sealedGd));
        Assert.True(_api.Sealed.Count > before, "it went sealed");
        var p = await f(plainGd);
        Assert.Equal(J(p), J(s));
        return s;
    }

    private static string Shape(GaiaDeskException e) => $"{e.GetType().Name}|{e.Kind}|{e.Reason}|{e.Status}|{e.Message}|{e.Desk}|{e.ExitCode}";

    private async Task<GaiaDeskException> SameError(Func<GaiaDeskClient, Task> f)
    {
        using var sealedGd = Gd();
        using var plainGd = Gd(E2eMode.Off);
        var s = await Blind(async () => await Assert.ThrowsAnyAsync<GaiaDeskException>(() => f(sealedGd)));
        var p = await Assert.ThrowsAnyAsync<GaiaDeskException>(() => f(plainGd));
        Assert.Equal(Shape(p), Shape(s));
        return s;
    }

    [Fact]
    public async Task Exec_SealedInAPostBody_SameResult_KeyLookedUpOnce()
    {
        var r = await Same(g => g.ExecAsync(SealedDesk, $"echo {Canary}", new ExecOptions
        {
            Env = new Dictionary<string, string> { ["SECRET"] = Canary }, Stdin = Canary, Cwd = $"/srv/{Canary}", Timeout = TimeSpan.FromSeconds(30),
        }));
        Assert.Equal($"ran: echo {Canary} é\nenv: SECRET={Canary}\nstdin: {Canary}\ncwd: /srv/{Canary}\n", r.Stdout);
        var sent = _api.Requests.Where(q => q.Path == $"/v1/desks/{SealedDesk}/exec").ToList()[^2];
        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal(new[] { "e2e" }, body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "ciphertext", "nonce", "pub", "v" }, body.GetProperty("e2e").EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal));
        using var gd = Gd();
        await gd.ExecAsync(SealedDesk, "warm");
        var lookups = _api.Requests.Count(q => q.Method == "GET" && q.Path == $"/v1/desks/{SealedDesk}");
        await gd.ExecAsync(SealedDesk, "again");
        Assert.Equal(lookups, _api.Requests.Count(q => q.Method == "GET" && q.Path == $"/v1/desks/{SealedDesk}"));
    }

    [Fact]
    public async Task Exec_Admin_Sealed_SameRefusalAndResult()
    {
        var e = await SameError(g => g.ExecAsync(SealedDesk, $"whoami {Canary}", new ExecOptions { Admin = true }));
        Assert.IsType<RefusedException>(e);
        Assert.Equal(Reasons.AdminNotEnabled, e.Reason);
        _api.Desks[SealedDesk].AdminRefusal = null;
        try { Assert.Equal("root\n", (await Same(g => g.ExecAsync(SealedDesk, "whoami", new ExecOptions { Admin = true }))).Stdout); }
        finally { _api.Desks[SealedDesk].AdminRefusal = Reasons.AdminNotEnabled; }
    }

    [Fact]
    public async Task ExecStream_SealedEventsOpenIntoTheSameChunksAndExit()
    {
        var r = await Same(async g => await g.ExecStream(SealedDesk, $"echo {Canary}", new ExecOptions { Env = new Dictionary<string, string> { ["K"] = Canary } }).CollectAsync());
        Assert.Equal($"ran: echo {Canary} é\nenv: K={Canary}\n", r.Stdout);
        Assert.Equal("warn\n", r.Stderr);
        Assert.Equal(0, r.Exit.ExitCode);
        var lost = await Same(async g => await g.ExecStream(SealedDesk, "lose").CollectAsync());
        Assert.Equal(ErrorKinds.ConnectionLost, lost.Exit.Error!.Kind);
        var refused = await Same(async g => await g.ExecStream(SealedDesk, "refuse").CollectAsync());
        Assert.Equal("token_refused", refused.Exit.Error!.Reason);
    }

    [Fact]
    public async Task Jobs_Logs_Wait_Kill_Stats_SameAnswersSealed()
    {
        await Same(g => g.RunJobAsync(SealedDesk, "build", $"make {Canary}", new JobOptions { Env = new Dictionary<string, string> { ["CI"] = Canary }, Shell = Shell.Bash, Cwd = Canary }));
        await Same(g => g.ListJobsAsync(SealedDesk));
        Assert.Equal("tail 10\n", (await Same(g => g.JobLogsAsync(SealedDesk, "build", 10))).Output);
        var tail = _api.Requests.Where(q => q.Path.EndsWith("/logs", StringComparison.Ordinal)).ToList()[^2];
        Assert.Empty(tail.Query);
        Assert.NotNull(tail.Header("gaiadesk-e2e"));
        var f = await Same(async g => await g.FollowJobLogs(SealedDesk, "build").CollectAsync());
        Assert.Equal("line1\nline2 é\n", f.Stdout);
        var w = await Same(g => g.WaitJobAsync(SealedDesk, "build", TimeSpan.FromSeconds(60)));
        Assert.Equal(3, w.Job.ExitCode);
        Assert.Equal("held", (await Same(g => g.WaitJobAsync(SealedDesk, "held"))).Job.Name);
        await Same(g => g.KillJobAsync(SealedDesk, "build"));
        await Same(g => g.StatsAsync(SealedDesk));
    }

    [Fact]
    public async Task DeskErrors_PlaceholderBecomesTheDesksMessage_SameClassKindReasonStatus()
    {
        var e = await SameError(g => g.ExecAsync(SealedDesk, "refuse"));
        Assert.Equal("RefusedException|refused|token_refused|403", string.Join("|", Shape(e).Split('|').Take(4)));
        Assert.Contains("no exec scope", e.Message);
        var w = await SameError(g => g.WaitJobAsync(SealedDesk, "held-gone"));
        Assert.IsType<OperationFailedException>(w);
        Assert.Contains("no job named \"held-gone\"", w.Message);
        Assert.Equal("no job named \"missing\"", (await SameError(g => g.JobLogsAsync(SealedDesk, "missing"))).Message);
        var s = await Same(async g => await g.FollowJobLogs(SealedDesk, "missing").CollectAsync());
        Assert.Equal("no job named \"missing\"", s.Exit.Error!.Message);
    }

    [Fact]
    public async Task Files_SealedUploadFramesAndDownloadEvents_SameBytes()
    {
        var big = Enumerable.Range(0, 150 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var marked = Encoding.UTF8.GetBytes($"{Canary} file\n");
        var up = await Same(g => g.UploadBytesAsync(SealedDesk, $"docs/{Canary}.txt", marked));
        Assert.Equal(marked.Length, up.Bytes);
        var put = _api.Requests.Where(q => q.Method == "PUT").ToList()[^2];
        Assert.Equal("application/x-ndjson", put.Header("content-type"));
        Assert.Empty(put.Query);
        using var gd = Gd();
        await Blind(() => gd.UploadBytesAsync(SealedDesk, "big.bin", big));
        var last = _api.Requests.Last(q => q.Method == "PUT");
        Assert.Equal(4, last.BodyText.Trim().Split('\n').Length);
        Assert.Equal(last.Body.Length.ToString(), last.Header("content-length"));
        Assert.Equal(big, await Blind(() => gd.DownloadBytesAsync(SealedDesk, "big.bin")));
        Assert.Equal(marked, await Same(g => g.DownloadBytesAsync(SealedDesk, $"docs/{Canary}.txt")));
        await using (var s = await gd.OpenReadAsync(SealedDesk, "big.bin"))
        {
            var one = new byte[1000];
            Assert.Equal(1000, await s.ReadAtLeastAsync(one, 1000));
            Assert.Equal(big.Take(1000), one);
        }
        await SameError(g => g.DownloadBytesAsync(SealedDesk, "missing"));
        var trunc = await Assert.ThrowsAsync<ConnectionLostException>(() => gd.DownloadBytesAsync(SealedDesk, "truncated"));
        Assert.Equal("incomplete", trunc.Reason);
        var dir = Directory.CreateTempSubdirectory("gdsdk").FullName;
        try
        {
            var local = Path.Combine(dir, "seekable.bin");
            await File.WriteAllBytesAsync(local, big);
            await gd.UploadFileAsync(SealedDesk, local, "up/");
            Assert.Equal(big, _api.Files["up/seekable.bin"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Tokens_MintListRevokeSealed()
    {
        var mint = await Same(g => g.CreateTokenAsync(new TokenCreateOptions { Desks = new[] { SealedDesk }, Name = $"bot-{Canary}", Expires = TimeSpan.FromHours(1) }), "session-person", null);
        Assert.Equal($"bot-{Canary}", mint.Tokens[0].Token!.Label);
        await Same(g => g.ListTokensAsync(SealedDesk), "session-person", null);
        await Same(g => g.RevokeTokenAsync(SealedDesk, "tok1"), "session-person", null);
        Assert.Equal(3, _api.Sealed.Count(o => o.StartsWith("token_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Auto_NoKey_InTheClear_WarnedOncePerDesk()
    {
        using var gd = Gd();
        var before = _api.Plain.Count;
        await gd.StatsAsync(Old);
        await gd.StatsAsync(Old);
        Assert.Equal(before + 2, _api.Plain.Count);
        List<string> mine;
        lock (_warnings) mine = _warnings.Where(w => w.Contains(Old)).ToList();
        Assert.Single(mine);
        Assert.Contains("not end-to-end encrypted", mine[0]);
    }

    [Fact]
    public async Task Require_NeverInTheClear_WokenAndAskedAgain_ElseE2eException()
    {
        using var gd = Gd(E2eMode.Require);
        var before = _api.Requests.Count;
        var e = await Assert.ThrowsAsync<E2eException>(() => gd.ExecAsync(Old, $"echo {Canary}"));
        Assert.Equal((Reasons.E2eUnavailable, Old, 254), (e.Reason!, e.Desk!, e.ExitCode!.Value));
        Assert.IsAssignableFrom<RefusedException>(e);
        Assert.Contains("offline", (await Assert.ThrowsAsync<E2eException>(() => gd.StatsAsync(Gone))).Message);
        Assert.All(_api.Requests.Skip(before), q => Assert.True(q.Path.EndsWith("/wake", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(q.Path, @"^/v1/desks/\d+$")));
        Assert.Contains(Old, _api.Wakes);
        Assert.Contains(Gone, _api.Wakes);
        Assert.Equal(0, (await Blind(() => gd.ExecAsync(Asleep, $"echo {Canary}"))).Exit);
        Assert.Contains(Asleep, _api.Wakes);
    }

    [Fact]
    public async Task ARequiringDesk_SealedInAuto_APlaintextRefusalIsSealedAndRetriedOnce()
    {
        using var gd = Gd();
        var before = _api.Sealed.Count;
        await gd.StatsAsync(Must);
        Assert.Equal(before + 1, _api.Sealed.Count);
        var n = _api.Requests.Count(q => q.Path == $"/v1/desks/{Stale}/stats");
        Assert.Equal(5, (await gd.StatsAsync(Stale)).CpuPercent);
        Assert.Equal(n + 2, _api.Requests.Count(q => q.Path == $"/v1/desks/{Stale}/stats"));
        Assert.Equal("stats", _api.Sealed.Last());
        using var off = Gd(E2eMode.Off);
        var r = await Assert.ThrowsAsync<RefusedException>(() => off.StatsAsync(Must));
        Assert.Equal((Reasons.E2eRequired, 409), (r.Reason!, r.Status!.Value));
    }

    [Fact]
    public async Task ARotatedKey_RefusedDecryptFailed_FetchedAgainAndResealedOnce()
    {
        using var gd = Gd();
        await gd.StatsAsync(Rotated);
        _api.Desks[Rotated].Secret = Key2;
        var n = _api.Requests.Count(q => q.Path == $"/v1/desks/{Rotated}/stats");
        Assert.Equal(5, (await gd.StatsAsync(Rotated)).CpuPercent);
        Assert.Equal(n + 2, _api.Requests.Count(q => q.Path == $"/v1/desks/{Rotated}/stats"));
    }

    [Fact]
    public async Task PinnedKeys_ADifferentKeyIsRefusedBeforeAnythingIsSent_ThePinSealsWhileNoKeyIsListed()
    {
        var before = _api.Requests.Count;
        using (var gd = Gd(pins: new() { [SealedDesk] = Pub2 }))
            Assert.Equal(Reasons.E2eKeyMismatch, (await Assert.ThrowsAsync<E2eException>(() => gd.ExecAsync(SealedDesk, $"echo {Canary}"))).Reason);
        Assert.All(_api.Requests.Skip(before), q => Assert.Equal($"/v1/desks/{SealedDesk}", q.Path));
        using (var gd = Gd(pins: new() { [SealedDesk] = Pub })) Assert.Equal(5, (await gd.StatsAsync(SealedDesk)).CpuPercent);
        using (var gd = Gd(E2eMode.Require, new() { [Gone] = Pub })) Assert.Equal(0, (await Blind(() => gd.ExecAsync(Gone, $"echo {Canary}"))).Exit);
    }

    [Fact]
    public async Task AHostileServer_AlteredEventsOrPlaintextAnswers_AreRefused()
    {
        using var gd = Gd();
        await gd.StatsAsync(SealedDesk);
        _api.Tamper = "flip";
        try
        {
            Assert.Equal(Reasons.E2eDecryptFailed, (await Assert.ThrowsAsync<ProtocolException>(() => gd.StatsAsync(SealedDesk))).Reason);
            Assert.Equal(ErrorKinds.Protocol, (await gd.ExecStream(SealedDesk, "x").CollectAsync()).Exit.Error!.Kind);
            var e = await Assert.ThrowsAsync<RefusedException>(() => gd.ExecAsync(SealedDesk, "refuse"));
            Assert.Contains("did not open", e.Message);
            await Assert.ThrowsAsync<ProtocolException>(() => gd.DownloadBytesAsync(SealedDesk, "big.bin"));
            _api.Tamper = "plaintext";
            Assert.Equal(Reasons.E2eUnsealedAnswer, (await Assert.ThrowsAsync<ProtocolException>(() => gd.StatsAsync(SealedDesk))).Reason);
            var p = await gd.ExecStream(SealedDesk, "x").CollectAsync();
            Assert.Equal("", p.Stdout);
            Assert.Equal(ErrorKinds.Protocol, p.Exit.Error!.Kind);
        }
        finally { _api.Tamper = null; }
        Assert.Equal(5, (await gd.StatsAsync(SealedDesk)).CpuPercent);
    }
}
