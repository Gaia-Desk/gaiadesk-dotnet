// The hosted API in the clear (E2e Off, or desks without keys): every route,
// what is sent, results, errors, retries, streams, held waits and files.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GaiaDesk.Tests.Fixtures;
using Xunit;

namespace GaiaDesk.Tests;

public sealed class ApiTests : IAsyncLifetime
{
    private const string D = "123456789";
    private const string Offline = "234567891";
    private MockApi _api = null!;

    public async Task InitializeAsync() => _api = await MockApi.StartAsync(new()
    {
        [D] = new MockDesk(),
        [Offline] = new MockDesk { Online = false },
        ["888888888"] = new MockDesk(),
    });

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private GaiaDeskClient Gd(string key = "ak_test", string? deskToken = "gdagt_test", RetryOptions? retry = null) =>
        new(new GaiaDeskOptions { ApiKey = key, DeskToken = deskToken, BaseUrl = _api.Url, E2e = E2eMode.Off, Retry = retry ?? new RetryOptions { BaseDelay = TimeSpan.FromMilliseconds(5) } });

    private Recorded Last(string pathEnd, string? method = null) => _api.Requests.Last(r => r.Path.EndsWith(pathEnd, StringComparison.Ordinal) && (method is null || r.Method == method));

    [Fact]
    public async Task Credentials_ApiKeyAndDeskToken_PerCallOverride_WakeAndUserAgent()
    {
        using var gd = Gd();
        Assert.Equal(TransportKind.Api, gd.Transport);
        await gd.StatsAsync(D);
        var r = Last("/stats");
        Assert.Equal("Bearer ak_test", r.Header("authorization"));
        Assert.Equal("gdagt_test", r.Header("x-gaiadesk-desk-token"));
        Assert.StartsWith("gaiadesk-dotnet/", r.Header("user-agent"));
        await gd.StatsAsync(D, new CallOptions { DeskToken = "gdagt_other", Wake = 30 });
        r = Last("/stats");
        Assert.Equal("gdagt_other", r.Header("x-gaiadesk-desk-token"));
        Assert.Equal("30", r.Query["wake_s"]);
        await Assert.ThrowsAsync<UsageException>(() => gd.StatsAsync(D, new CallOptions { Wake = 121 }));
    }

    [Fact]
    public async Task Fleet_ListGetReachWake()
    {
        using var gd = Gd();
        var list = await gd.ListDesksAsync();
        Assert.Equal(3, list.Devices.Count);
        Assert.Equal("you@example.com", list.Identity!.Account);
        Assert.Single((await gd.ListDesksAsync(D)).Devices);
        var desk = await gd.GetDeskAsync(Offline);
        Assert.False(desk.Online);
        Assert.Equal("silent", desk.OfflineReason);
        Assert.NotNull(desk.Wake);
        Assert.Null(desk.E2ePub);
        var reach = await gd.GetReachAsync(D, DateTimeOffset.FromUnixTimeSeconds(1790000000), 1);
        Assert.Single(reach.Events);
        Assert.Equal("1790000000", Last("/reach").Query["since"]);
        var woke = await gd.WakeAsync(D, TimeSpan.FromSeconds(30), "wake-1");
        Assert.True(woke.AlreadyOnline);
        var w = Last("/wake");
        Assert.Equal("{\"wait_s\":30}", w.BodyText);
        Assert.Equal("wake-1", w.Header("idempotency-key"));
        _api.Desks["345678912"] = new MockDesk { Online = false };
        var nw = await Assert.ThrowsAsync<UnreachableException>(() => gd.WakeAsync("345678912"));
        Assert.Equal(Reasons.NoWakePath, nw.Reason);
        Assert.Equal(409, nw.Status);
        await Assert.ThrowsAsync<UsageException>(() => gd.WakeAsync(D, TimeSpan.FromSeconds(91)));
    }

    [Fact]
    public async Task Audit_FiltersAndPagination()
    {
        using var gd = Gd();
        var some = await gd.ListAuditAsync(new AuditQuery { Desk = D, Action = "api.*", Since = DateTimeOffset.FromUnixTimeMilliseconds(5), Limit = 3 });
        Assert.Equal(3, some.Count);
        var q = Last("/audit").Query;
        Assert.Equal(D, q["desk"]);
        Assert.Equal("api.*", q["action"]);
        Assert.Equal("5", q["since_ms"]);
        var all = new List<AuditEvent>();
        await foreach (var ev in gd.EnumerateAuditAsync(pageSize: 2)) all.Add(ev);
        Assert.Equal(new[] { "aud_6", "aud_5", "aud_4", "aud_3", "aud_2", "aud_1", "aud_0" }, all.Select(a => a.Id));
        var capped = new List<AuditEvent>();
        await foreach (var ev in gd.EnumerateAuditAsync(new AuditQuery { Limit = 4 }, 3)) capped.Add(ev);
        Assert.Equal(4, capped.Count);
        await Assert.ThrowsAsync<UsageException>(() => gd.ListAuditAsync(new AuditQuery { Limit = 501 }));
    }

    [Fact]
    public async Task Webhooks_CreateListDelete()
    {
        using var gd = Gd();
        var w = await gd.CreateWebhookAsync("https://example.com/hook", new[] { WebhookEventTypes.DeskOnline, WebhookEventTypes.JobFinished }, "ops", "k1");
        Assert.StartsWith("whsec_", w.Secret);
        Assert.Equal(new[] { "desk.online", "job.finished" }, w.Events);
        Assert.Equal("k1", Last("/webhooks", "POST").Header("idempotency-key"));
        Assert.Contains((await gd.ListWebhooksAsync()), x => x.Id == w.Id);
        Assert.Equal(w.Id, (await gd.DeleteWebhookAsync(w.Id)).Deleted);
        Assert.Empty(await gd.ListWebhooksAsync());
        var nf = await Assert.ThrowsAsync<UnreachableException>(() => gd.DeleteWebhookAsync(w.Id));
        Assert.Equal(404, nf.Status);
        await Assert.ThrowsAsync<UsageException>(() => gd.CreateWebhookAsync("http://example.com", new[] { "desk.online" }));
        await Assert.ThrowsAsync<UsageException>(() => gd.CreateWebhookAsync("https://example.com", Array.Empty<string>()));
    }

    [Fact]
    public async Task SupportSessions_CreateListGet()
    {
        using var gd = Gd();
        var s = await gd.CreateSupportSessionAsync(new SupportSessionCreate
        {
            Mode = SupportModes.Cobrowse, Customer = new Dictionary<string, object?> { ["name"] = "Ada", ["plan"] = "pro", ["seats"] = 3, ["vip"] = true },
            ExpiresIn = TimeSpan.FromMinutes(30), Origin = "https://app.example.com",
        });
        Assert.StartsWith("gdemb_", s.EmbedToken);
        Assert.Equal("cobrowse", s.Mode);
        var sent = JsonNode.Parse(Last("/support/sessions", "POST").BodyText)!;
        Assert.Equal(1800, sent["expires_in"]!.GetValue<long>());
        Assert.Equal(3, sent["customer"]!["seats"]!.GetValue<int>());
        Assert.Equal("Ada", s.Customer!["name"].GetString());
        Assert.Contains(await gd.ListSupportSessionsAsync(), x => x.Id == s.Id);
        Assert.Equal("open", Last("/support/sessions", "GET").Query["state"]);
        await gd.ListSupportSessionsAsync(includeEnded: true, limit: 10);
        Assert.Equal("all", Last("/support/sessions", "GET").Query["state"]);
        Assert.Equal(s.JoinCode, (await gd.GetSupportSessionAsync(s.Id)).JoinCode);
        await Assert.ThrowsAsync<UnreachableException>(() => gd.GetSupportSessionAsync("ss_ffffffffffffffff"));
        await Assert.ThrowsAsync<UsageException>(() => gd.CreateSupportSessionAsync(new SupportSessionCreate { Mode = "draw" }));
        await Assert.ThrowsAsync<UsageException>(() => gd.CreateSupportSessionAsync(new SupportSessionCreate { ExpiresIn = TimeSpan.FromSeconds(10) }));
    }

    [Fact]
    public async Task Exec_SendsAnExecSpec_AndReadsTheResult()
    {
        using var gd = Gd();
        var r = await gd.ExecAsync(D, "uname -a", new ExecOptions
        {
            Shell = Shell.PowerShell, Env = new Dictionary<string, string> { ["A"] = "1" }, Cwd = "src", Stdin = "hi", Timeout = TimeSpan.FromSeconds(1.5), IdempotencyKey = "x1",
        });
        Assert.Equal(0, r.Exit);
        Assert.Equal("ran: uname -a é\nenv: A=1\nstdin: hi\ncwd: src\n", r.Stdout);
        Assert.Equal("warn\n", r.Stderr);
        var sent = Last("/exec");
        Assert.Equal("{\"command\":\"uname -a\",\"shell\":\"pwsh\",\"env\":{\"A\":\"1\"},\"cwd\":\"src\",\"timeout_secs\":2,\"stdin\":\"hi\"}", sent.BodyText);
        Assert.Equal("x1", sent.Header("idempotency-key"));
        Assert.StartsWith("application/json", sent.Header("content-type"));
        await gd.ExecAsync(D, new[] { "ls", "-la", "my dir" }, new ExecOptions { Shell = Shell.None });
        Assert.Equal("{\"argv\":[\"ls\",\"-la\",\"my dir\"],\"shell\":\"none\"}", Last("/exec").BodyText);
        var c = await Assert.ThrowsAsync<CommandException>(() => gd.ExecAsync(D, "exit3", new ExecOptions { Check = true }));
        Assert.Equal(3, c.ExitCode);
        Assert.Equal(3, c.Result.Exit);
        Assert.Equal(3, (await gd.ExecAsync(D, "exit3")).Exit);
    }

    [Fact]
    public async Task Exec_Admin_SendsTheField_AndRefusalsAreTyped()
    {
        using var gd = Gd();
        var e = await Assert.ThrowsAsync<RefusedException>(() => gd.ExecAsync(D, "whoami", new ExecOptions { Admin = true }));
        Assert.Equal(Reasons.AdminNotEnabled, e.Reason);
        Assert.Equal(254, e.ExitCode);
        Assert.Equal(D, e.Desk);
        Assert.Equal("{\"command\":\"whoami\",\"admin\":true}", Last("/exec").BodyText);
        foreach (var why in new[] { Reasons.AdminScopeMissing, Reasons.AdminDenied, Reasons.AdminUnavailable })
        {
            _api.Desks[D].AdminRefusal = why;
            Assert.Equal(why, (await Assert.ThrowsAsync<RefusedException>(() => gd.ExecAsync(D, "whoami", new ExecOptions { Admin = true }))).Reason);
        }
        _api.Desks[D].AdminRefusal = null;
        Assert.Equal("root\n", (await gd.ExecAsync(D, "whoami", new ExecOptions { Admin = true })).Stdout);
        await gd.ExecAsync(D, "whoami");
        Assert.DoesNotContain("admin", Last("/exec").BodyText);
    }

    [Fact]
    public async Task Exec_ValidationFailsBeforeAnythingIsSent()
    {
        using var gd = Gd();
        var n = _api.Requests.Count;
        await Assert.ThrowsAsync<UsageException>(() => gd.ExecAsync(D, "  "));
        await Assert.ThrowsAsync<UsageException>(() => gd.ExecAsync(D, Array.Empty<string>()));
        await Assert.ThrowsAsync<UsageException>(() => gd.ExecAsync("bad id", "x"));
        await Assert.ThrowsAsync<UsageException>(() => gd.ExecAsync("-d", "x"));
        await Assert.ThrowsAsync<UsageException>(() => gd.ExecAsync(D, "x", new ExecOptions { Env = new Dictionary<string, string> { ["A=B"] = "1" } }));
        await Assert.ThrowsAsync<UsageException>(() => gd.ExecAsync(D, "x", new ExecOptions { Env = new Dictionary<string, string> { ["A"] = "a\0b" } }));
        await Assert.ThrowsAsync<UsageException>(() => gd.ExecAsync(D, "x", new ExecOptions { Cwd = " " }));
        await Assert.ThrowsAsync<UsageException>(() => gd.ExecAsync(D, "x", new ExecOptions { Timeout = TimeSpan.FromSeconds(-1) }));
        await Assert.ThrowsAsync<UsageException>(() => gd.ExecAsync(D, "x", new ExecOptions { IdempotencyKey = "bad\nkey" }));
        Assert.Throws<UsageException>(() => gd.ExecStream(D, "x", new ExecOptions { Check = true }));
        Assert.Equal(n, _api.Requests.Count);
    }

    [Fact]
    public async Task ExecStream_ChunksThenExit_AndAStartRefusalIsItsError()
    {
        using var gd = Gd();
        await using var s = gd.ExecStream(D, "make", new ExecOptions { Env = new Dictionary<string, string> { ["K"] = "v" } });
        var chunks = new List<OutputChunk>();
        await foreach (var c in s) chunks.Add(c);
        var exit = await s.WaitAsync();
        Assert.Equal("ran: make é\nenv: K=v\n", string.Concat(chunks.Where(c => c.Source == OutputSource.Stdout).Select(c => c.Text)));
        Assert.Equal("warn\n", string.Concat(chunks.Where(c => c.Source == OutputSource.Stderr).Select(c => c.Text)));
        Assert.True(exit.Succeeded);
        Assert.Equal(0, exit.Result!.Exit);
        Assert.Equal("1", Last("/exec").Query["stream"]);
        Assert.Equal("text/event-stream", Last("/exec").Header("accept"));
        var refused = await gd.ExecStream(D, "refuse").CollectAsync();
        Assert.Equal(254, refused.Exit.ExitCode);
        Assert.Equal(ErrorKinds.Refused, refused.Exit.Error!.Kind);
        Assert.Equal("token_refused", refused.Exit.Error.Reason);
        Assert.Throws<RefusedException>(() => refused.Exit.ThrowIfError());
        var lost = await gd.ExecStream(D, "lose").CollectAsync();
        Assert.Equal(ErrorKinds.ConnectionLost, lost.Exit.Error!.Kind);
        Assert.Equal(255, lost.Exit.ExitCode);
    }

    [Fact]
    public async Task ExecStream_KillAndCancellationCloseTheRequest()
    {
        using var gd = Gd();
        var s = gd.ExecStream(D, "sleep");
        await using (var e = s.GetAsyncEnumerator())
        {
            Assert.True(await e.MoveNextAsync());
            Assert.Equal("ran: sleep é".Substring(0, 9), e.Current.Text.Substring(0, 9));
        }
        s.Kill();
        var exit = await s.WaitAsync();
        Assert.True(exit.Killed);
        Assert.Equal(130, exit.ExitCode);
        using var cts = new CancellationTokenSource();
        var s2 = gd.ExecStream(D, "sleep", null, cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        Assert.True((await s2.WaitAsync()).Killed);
        for (var i = 0; i < 50 && _api.Aborted < 2; i++) await Task.Delay(20);
        Assert.True(_api.Aborted >= 2, "the server saw the caller hang up");
    }

    [Fact]
    public async Task Jobs_RunListLogsFollowKill()
    {
        using var gd = Gd();
        var job = await gd.RunJobAsync(D, "nightly", "./build.sh --release", new JobOptions
        {
            Priority = JobPriority.Low, CpuPercent = 50, Memory = "2G", KeepAwake = true, Cwd = "src", Shell = Shell.Bash, Env = new Dictionary<string, string> { ["CI"] = "1" },
        });
        Assert.Equal("nightly", job.Name);
        Assert.Equal("./build.sh --release", job.Command);
        Assert.Equal("{\"name\":\"nightly\",\"command\":[\"./build.sh --release\"],\"limits\":{\"priority\":\"low\",\"cpu_percent\":50,\"mem_mb\":2048,\"keep_awake\":true},\"cwd\":\"src\",\"shell\":\"bash\",\"env\":{\"CI\":\"1\"}}", Last("/jobs", "POST").BodyText);
        await gd.RunJobAsync(D, "two", new[] { "make", "test" });
        Assert.Contains("\"command\":[\"make\",\"test\"]", Last("/jobs", "POST").BodyText);
        Assert.Equal("build", Assert.Single(await gd.ListJobsAsync(D)).Name);
        var logs = await gd.JobLogsAsync(D, "build", 10);
        Assert.Equal("tail 10\n", logs.Output);
        Assert.Equal("10", Last("/logs").Query["tail"]);
        var f = await gd.FollowJobLogs(D, "build").CollectAsync();
        Assert.Equal("line1\nline2 é\n", f.Stdout);
        Assert.Equal(0, f.Exit.ExitCode);
        Assert.Equal("job build exited (exit 0)", f.Exit.Message);
        Assert.Equal("exited", f.Exit.Job!.State);
        var missing = await gd.FollowJobLogs(D, "missing").CollectAsync();
        Assert.Equal(1, missing.Exit.ExitCode);
        Assert.Equal("no job named \"missing\"", missing.Exit.Error!.Message);
        Assert.Equal("killed", (await gd.KillJobAsync(D, "build")).State);
        await Assert.ThrowsAsync<UsageException>(() => gd.RunJobAsync(D, "-bad", "x"));
        await Assert.ThrowsAsync<UsageException>(() => gd.RunJobAsync(D, "ok", "x", new JobOptions { Shell = Shell.None }));
        await Assert.ThrowsAsync<UsageException>(() => gd.RunJobAsync(D, "ok", "x", new JobOptions { CpuPercent = 0 }));
        await Assert.ThrowsAsync<UsageException>(() => gd.RunJobAsync(D, "ok", "x", new JobOptions { Memory = "lots" }));
    }

    [Fact]
    public async Task WaitJob_HeldAnswers_HeldFailures_AndLongTimeoutsInTurns()
    {
        using var gd = Gd();
        var w = await gd.WaitJobAsync(D, "build", TimeSpan.FromSeconds(60));
        Assert.False(w.TimedOut);
        Assert.Equal(3, w.Job.ExitCode);
        Assert.Equal("60", Last("/wait").Query["timeout"]);
        Assert.Equal("held", (await gd.WaitJobAsync(D, "held")).Job.Name);
        Assert.Equal("870", Last("/wait").Query["timeout"]);
        var gone = await Assert.ThrowsAsync<OperationFailedException>(() => gd.WaitJobAsync(D, "held-gone"));
        Assert.Equal(422, gone.Status);
        Assert.Equal(1, gone.ExitCode);
        Assert.Contains("no job named", gone.Message);
        var n = _api.Requests.Count(r => r.Path.EndsWith("/still/wait", StringComparison.Ordinal));
        var still = await gd.WaitJobAsync(D, "still", TimeSpan.Zero);
        Assert.True(still.TimedOut);
        Assert.Equal("0", Last("/wait").Query["timeout"]);
        Assert.Equal(n + 1, _api.Requests.Count(r => r.Path.EndsWith("/still/wait", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Files_RawBytesUpAndDown_StreamsAndLocalFiles()
    {
        using var gd = Gd();
        var data = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var up = await gd.UploadBytesAsync(D, "/tmp/a.bin", data);
        Assert.Equal(data.Length, up.Bytes);
        var put = Last("/files", "PUT");
        Assert.Equal("application/octet-stream", put.Header("content-type"));
        Assert.Equal("/tmp/a.bin", put.Query["path"]);
        Assert.Equal(data, put.Body);
        Assert.Equal(data, await gd.DownloadBytesAsync(D, "/tmp/a.bin"));
        // A non-seekable stream, and a seekable one from its position.
        await gd.UploadAsync(D, "s.txt", new NonSeekable(Encoding.UTF8.GetBytes("streamed")));
        Assert.Equal("streamed", Encoding.UTF8.GetString(_api.Files["s.txt"]));
        var ms = new MemoryStream(Encoding.UTF8.GetBytes("XXpart"));
        ms.Position = 2;
        await gd.UploadAsync(D, "p.txt", ms);
        Assert.Equal("part", Encoding.UTF8.GetString(_api.Files["p.txt"]));
        await using (var rs = await gd.OpenReadAsync(D, "p.txt"))
            Assert.Equal("part", await new StreamReader(rs).ReadToEndAsync());
        var dir = Directory.CreateTempSubdirectory("gdsdk").FullName;
        try
        {
            var local = Path.Combine(dir, "report.csv");
            await File.WriteAllTextAsync(local, "a,b\n");
            Assert.Equal(4, (await gd.UploadFileAsync(D, local, "docs/")).Bytes);
            Assert.Equal("docs/report.csv", Last("/files", "PUT").Query["path"]);
            var down = await gd.DownloadFileAsync(D, "docs/report.csv", dir + Path.DirectorySeparatorChar);
            Assert.Equal(Path.Combine(dir, "report.csv"), down.Destination);
            Assert.Equal("a,b\n", await File.ReadAllTextAsync(down.Destination));
            var failed = await Assert.ThrowsAsync<OperationFailedException>(() => gd.DownloadFileAsync(D, "missing", Path.Combine(dir, "m")));
            Assert.Equal("not_found", failed.Reason);
            Assert.False(File.Exists(Path.Combine(dir, "m")));
            var broken = Path.Combine(dir, "broken");
            _api.Files["broken"] = new byte[300_000];
            await Assert.ThrowsAnyAsync<GaiaDeskException>(() => gd.DownloadFileAsync(D, "broken", broken));
            Assert.False(File.Exists(broken), "no partial file");
            await Assert.ThrowsAsync<UsageException>(() => gd.UploadFileAsync(D, dir, "x"));
            var nofile = await Assert.ThrowsAsync<GaiaDeskException>(() => gd.UploadFileAsync(D, Path.Combine(dir, "nope"), "x"));
            Assert.Equal(ErrorKinds.Local, nofile.Kind);
        }
        finally { Directory.Delete(dir, true); }
        var fails = await Assert.ThrowsAsync<OperationFailedException>(() => gd.UploadBytesAsync(D, "fails", new byte[] { 1 }));
        Assert.Contains("disk full", fails.Message);
        await Assert.ThrowsAsync<UsageException>(() => gd.UploadBytesAsync(D, "", new byte[] { 1 }));
    }

    [Fact]
    public async Task Tokens_MintPerDesk_ListRevoke_PartialFailureKeepsMinted()
    {
        using var owner = Gd("session-person", null);
        var m = await owner.CreateTokenAsync(new TokenCreateOptions { Desks = new[] { D }, Name = "bot", Expires = TimeSpan.FromHours(1), Scopes = new[] { "exec", TokenScopes.Admin } });
        Assert.Equal("gdagt_minted_secret", Assert.Single(m.Tokens).Secret);
        Assert.Equal("{\"name\":\"bot\",\"expires_secs\":3600,\"scopes\":[\"exec\",\"admin\"]}", Last("/tokens", "POST").BodyText);
        await owner.CreateTokenAsync(new TokenCreateOptions { Desks = new[] { D }, Name = "bot", Cwd = "/srv", LowPriv = true });
        Assert.Equal("{\"name\":\"bot\",\"expires_secs\":604800,\"scopes\":[\"exec\",\"cp\",\"jobs\"],\"cwd\":\"/srv\",\"low_priv\":true}", Last("/tokens", "POST").BodyText);
        var partial = await Assert.ThrowsAsync<RefusedException>(() => owner.CreateTokenAsync(new TokenCreateOptions { Desks = new[] { D, "888888888" }, Name = "bot" }));
        Assert.Equal(Reasons.DeskOptedOut, partial.Reason);
        Assert.Equal("gdagt_minted_secret", partial.Json!.Value.GetProperty("tokens")[0].GetProperty("secret").GetString());
        Assert.Equal("tok1", Assert.Single(await owner.ListTokensAsync(D)).Id);
        Assert.Equal("tok 1", (await owner.RevokeTokenAsync(D, "tok 1")).Revoked);
        Assert.EndsWith("/tokens/tok 1", Last("/tokens/tok 1", "DELETE").Path);
        await Assert.ThrowsAsync<UsageException>(() => owner.CreateTokenAsync(new TokenCreateOptions { Desks = new[] { D } }));
        await Assert.ThrowsAsync<UsageException>(() => owner.CreateTokenAsync(new TokenCreateOptions { Desks = new[] { D }, Name = "x", Scopes = new[] { "admin" }, Cwd = "/x" }));
        var agent = await Assert.ThrowsAsync<RefusedException>(() => Gd().ListTokensAsync(D));
        Assert.Equal(Reasons.AgentCannotAdmin, agent.Reason);
    }

    [Fact]
    public async Task ErrorEnvelopes_TypedWithKindReasonStatusAndRequestId()
    {
        using var gd = Gd();
        var unauth = await Assert.ThrowsAsync<RefusedException>(() => Gd("ak_bad").ListDesksAsync());
        Assert.Equal((401, Reasons.Unauthenticated, 254), (unauth.Status!.Value, unauth.Reason!, unauth.ExitCode!.Value));
        Assert.StartsWith("req_", unauth.RequestId);
        Assert.Equal("GET /desks", unauth.Operation);
        Assert.Equal(Reasons.MissingScope, (await Assert.ThrowsAsync<RefusedException>(() => Gd("ak_noscope").ListDesksAsync())).Reason);
        var unknown = await Assert.ThrowsAsync<UnreachableException>(() => gd.StatsAsync("111111111"));
        Assert.Equal((ErrorKinds.UnknownDesk, 404, "111111111"), (unknown.Kind, unknown.Status!.Value, unknown.Desk!));
        Assert.Equal("desk_token_required", (await Assert.ThrowsAsync<RefusedException>(() => Gd(deskToken: null).StatsAsync(D))).Reason);
        var html = await Assert.ThrowsAsync<ProtocolException>(() => gd.StatsAsync("999999990"));
        Assert.Equal(500, html.Status);
        var limited = await Assert.ThrowsAsync<RefusedException>(() => Gd(retry: RetryOptions.None).StatsAsync("999999991")); // (it would wait Retry-After: 7 s)
        Assert.Equal((429, Reasons.RateLimited), (limited.Status!.Value, limited.Reason!));
        Assert.Equal(TimeSpan.FromSeconds(7), limited.RetryAfter);
        using var nowhere = new GaiaDeskClient(new GaiaDeskOptions { ApiKey = "ak_x", BaseUrl = "http://127.0.0.1:1/v1", Retry = RetryOptions.None });
        var net = await Assert.ThrowsAsync<UnreachableException>(() => nowhere.ListDesksAsync());
        Assert.Equal((ErrorKinds.Network, "network"), (net.Kind, net.Reason!));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gd.StatsAsync(D, null, cts.Token));
    }

    [Fact]
    public async Task Retries_429ForEverything_TransientOnlyForReads()
    {
        using var gd = Gd();
        var stats = (Recorded r) => r.Path.EndsWith("/stats", StringComparison.Ordinal);
        _api.Inject(new Injection { Match = stats, Status = 429, Body = MockApi.Envelope("refused", "slow down", "rate_limited").ToJsonString(), Headers = new() { ["Retry-After"] = "0" } });
        _api.Inject(new Injection { Match = stats, Status = 503, Body = MockApi.Envelope("unreachable", "try later", "upstream").ToJsonString() });
        var before = _api.Requests.Count(stats);
        Assert.Equal(5, (await gd.StatsAsync(D)).CpuPercent);
        Assert.Equal(before + 3, _api.Requests.Count(stats));
        var exec = (Recorded r) => r.Path.EndsWith("/exec", StringComparison.Ordinal);
        _api.Inject(new Injection { Match = exec, Status = 429, Body = MockApi.Envelope("refused", "busy", "desk_busy").ToJsonString(), Headers = new() { ["Retry-After"] = "0" } });
        Assert.Equal(0, (await gd.ExecAsync(D, "x", new ExecOptions { IdempotencyKey = "same" })).Exit);
        var twoExecs = _api.Requests.Where(exec).TakeLast(2).ToList();
        Assert.Equal(twoExecs[0].BodyText, twoExecs[1].BodyText);
        Assert.All(twoExecs, r => Assert.Equal("same", r.Header("idempotency-key")));
        _api.Inject(new Injection { Match = exec, Status = 502, Body = MockApi.Envelope("connection_lost", "gone", "desk_disconnected").ToJsonString() });
        before = _api.Requests.Count(exec);
        await Assert.ThrowsAsync<ConnectionLostException>(() => gd.ExecAsync(D, "x"));
        Assert.Equal(before + 1, _api.Requests.Count(exec));
        _api.Inject(new Injection { Match = stats, Status = 503, Body = MockApi.Envelope("unreachable", "off", "desk_ops_disabled").ToJsonString() });
        before = _api.Requests.Count(stats);
        await Assert.ThrowsAsync<UnreachableException>(() => gd.StatsAsync(D));
        Assert.Equal(before + 1, _api.Requests.Count(stats));
        _api.Inject(new Injection { Match = stats, Status = 429, Body = MockApi.Envelope("refused", "later", "rate_limited").ToJsonString(), Headers = new() { ["Retry-After"] = "120" } });
        var tooLong = await Assert.ThrowsAsync<RefusedException>(() => gd.StatsAsync(D));
        Assert.Equal(TimeSpan.FromSeconds(120), tooLong.RetryAfter);
        _api.Inject(new Injection { Match = stats, Status = 504, Body = MockApi.Envelope("unreachable", "slow", "upstream").ToJsonString() });
        before = _api.Requests.Count(stats);
        await Assert.ThrowsAsync<UnreachableException>(() => Gd(retry: RetryOptions.None).StatsAsync(D));
        Assert.Equal(before + 1, _api.Requests.Count(stats));
    }

    [Fact]
    public async Task InjectedHttpClient_IsUsedAndNotDisposed()
    {
        var http = new HttpClient();
        using (var gd = new GaiaDeskClient(new GaiaDeskOptions { ApiKey = "ak_test", DeskToken = "t", BaseUrl = _api.Url, HttpClient = http, E2e = E2eMode.Off, UserAgent = "myapp/1.2" }))
            await gd.StatsAsync(D);
        Assert.StartsWith("myapp/1.2 gaiadesk-dotnet/", Last("/stats").Header("user-agent"));
        using (var still = await http.GetAsync(_api.Url + "/desks")) Assert.Equal(401, (int)still.StatusCode); // not disposed
        http.Dispose();
    }

    [Fact]
    public void Options_AreChecked()
    {
        Assert.Throws<UsageException>(() => new GaiaDeskClient(""));
        Assert.Throws<UsageException>(() => new GaiaDeskClient("ak_x", " "));
        Assert.Throws<UsageException>(() => new GaiaDeskClient(new GaiaDeskOptions { ApiKey = "ak_x", BaseUrl = "ftp://x" }));
        Assert.Throws<UsageException>(() => new GaiaDeskClient(new GaiaDeskOptions { ApiKey = "ak_x", E2e = (E2eMode)9 }));
        Assert.Throws<UsageException>(() => new GaiaDeskClient(new GaiaDeskOptions { ApiKey = "ak_x", E2eKeys = new Dictionary<string, string> { [D] = "short" } }));
        Assert.Throws<UsageException>(() => new GaiaDeskClient(new GaiaDeskOptions { ApiKey = "ak_x", Retry = new RetryOptions { MaxRetries = -1 } }));
        using var gd = new GaiaDeskClient("ak_x");
        Assert.Equal(GaiaDeskClient.DefaultApiUrl, gd.BaseUrl);
        Assert.Equal(E2eMode.Auto, gd.E2e);
    }

    private sealed class NonSeekable : MemoryStream
    {
        public NonSeekable(byte[] b) : base(b) { }
        public override bool CanSeek => false;
    }
}

