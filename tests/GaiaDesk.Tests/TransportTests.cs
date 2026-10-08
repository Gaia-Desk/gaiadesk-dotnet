// The desk's own API (local: Unix socket / named pipe) and a desk's LAN
// gateway (pinned TLS): the same operations, credentials of their own, and
// the hosted-only routes refused before anything is sent.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GaiaDesk.Tests.Fixtures;
using Xunit;

namespace GaiaDesk.Tests;

public sealed class LocalTransportTests : IAsyncLifetime
{
    private const string D = "123456789";
    private MockApi _api = null!;
    private string _dir = null!;
    private Dictionary<string, string?> _env = null!;

    public async Task InitializeAsync()
    {
        _dir = Directory.CreateTempSubdirectory("gd").FullName;
        _env = new Dictionary<string, string?> { ["GAIADESK_API_DIR"] = _dir, ["GAIADESK_API_PIPE"] = $@"\\.\pipe\gaiadesk-api-test-{Guid.NewGuid():N}" };
        var where = OperatingSystem.IsWindows() ? _env["GAIADESK_API_PIPE"]!.Substring(9) : Path.Combine(_dir, "api.sock");
        _api = await MockApi.StartAsync(new() { [D] = new MockDesk() }, MockMode.Local, socketPath: where);
        await File.WriteAllTextAsync(Path.Combine(_dir, "api-token"), MockApi.LocalAdminToken + "\n");
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        Directory.Delete(_dir, true);
    }

    private GaiaDeskClient Gd(string? deskToken = null) => GaiaDeskClient.Local(new LocalOptions { Environment = _env, DeskToken = deskToken });

    [Fact]
    public async Task Exec_WithTheAdminTokenFromItsFile_AsBearer()
    {
        using var gd = Gd();
        Assert.Equal(TransportKind.Local, gd.Transport);
        Assert.Equal(E2eMode.Off, gd.E2e);
        Assert.Equal(0, (await gd.ExecAsync(D, "hostname")).Exit);
        var r = _api.Requests.Last();
        Assert.Equal($"Bearer {MockApi.LocalAdminToken}", r.Header("authorization"));
        Assert.Null(r.Header("x-gaiadesk-desk-token"));
        Assert.Single((await gd.ListDesksAsync()).Devices);
    }

    [Fact]
    public async Task AnAgentToken_IsSentInstead_PerCallToo()
    {
        using var gd = Gd("gdagt_local");
        await gd.StatsAsync(D);
        Assert.Equal("gdagt_local", _api.Requests.Last().Header("x-gaiadesk-desk-token"));
        Assert.Null(_api.Requests.Last().Header("authorization"));
        using var admin = Gd();
        await admin.StatsAsync(D, new CallOptions { DeskToken = "gdagt_call" });
        Assert.Equal("gdagt_call", _api.Requests.Last().Header("x-gaiadesk-desk-token"));
    }

    [Fact]
    public async Task Streams_Bytes_AndHeldWaits_OverTheSocket()
    {
        using var gd = Gd();
        var s = await gd.ExecStream(D, "make").CollectAsync();
        Assert.Equal("ran: make é\n", s.Stdout);
        Assert.True(s.Exit.Succeeded);
        await gd.UploadBytesAsync(D, "x.bin", new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, await gd.DownloadBytesAsync(D, "x.bin"));
        Assert.Equal("held", (await gd.WaitJobAsync(D, "held")).Job.Name);
    }

    [Fact]
    public async Task NoSocket_IsLocalApiUnavailable_SayingHowToTurnItOn()
    {
        var env = new Dictionary<string, string?>(_env) { ["GAIADESK_API_DIR"] = Path.Combine(_dir, "nowhere"), ["GAIADESK_API_PIPE"] = @"\\.\pipe\gaiadesk-api-nobody-here" };
        using var gd = GaiaDeskClient.Local(new LocalOptions { Environment = env, DeskToken = "gdagt_x", Retry = RetryOptions.None });
        var e = await Assert.ThrowsAsync<UnreachableException>(() => gd.StatsAsync(D));
        Assert.Equal(Reasons.LocalApiUnavailable, e.Reason);
        Assert.Contains("Local API on", e.Message);
        using var notoken = GaiaDeskClient.Local(new LocalOptions { Environment = env });
        var n = _api.Requests.Count;
        Assert.Equal(Reasons.LocalApiUnavailable, (await Assert.ThrowsAsync<UnreachableException>(() => notoken.StatsAsync(D))).Reason);
        Assert.Equal(n, _api.Requests.Count);
    }

    [Fact]
    public async Task HostedOnlyOperations_AreUsageErrors_NamingTheLocalTransport()
    {
        using var gd = Gd();
        var n = _api.Requests.Count;
        Assert.Contains("local transport", (await Assert.ThrowsAsync<UsageException>(() => gd.GetDeskAsync(D))).Message);
        await Assert.ThrowsAsync<UsageException>(() => gd.WakeAsync(D));
        await Assert.ThrowsAsync<UsageException>(() => gd.ListAuditAsync());
        await Assert.ThrowsAsync<UsageException>(() => gd.ListWebhooksAsync());
        await Assert.ThrowsAsync<UsageException>(() => gd.CreateSupportSessionAsync());
        await Assert.ThrowsAsync<UsageException>(() => gd.GetReachAsync(D));
        Assert.Equal(n, _api.Requests.Count);
        Assert.Throws<UsageException>(() => GaiaDeskClient.Local(new LocalOptions { SocketPath = " " }));
    }

    [Fact]
    public void Paths_PipeNames_AndPipeUsers()
    {
        Assert.Equal("ada.lovelace_x", LocalApi.PipeUser("Ada.Lovelace x"));
        Assert.Equal("user", LocalApi.PipeUser(""));
        Assert.Equal(64, LocalApi.PipeUser(new string('a', 100)).Length);
        Assert.Equal(@"\\.\pipe\gaiadesk-api-bob", LocalApi.PipeName(new Dictionary<string, string?> { ["USERNAME"] = "Bob" }));
        Assert.Equal(@"\\.\pipe\gaiadesk-api-acct", LocalApi.PipeName(new Dictionary<string, string?>(), "acct"));
        Assert.Equal("custom", LocalApi.PipeName(new Dictionary<string, string?> { ["GAIADESK_API_PIPE"] = "custom" }));
        var none = new Dictionary<string, string?>();
        Assert.Equal("/home/me/.gaiadesk/api.sock", LocalApi.SocketPath(none, "/home/me", false));
        Assert.Equal("/x/api-token", LocalApi.TokenPath(new Dictionary<string, string?> { ["GAIADESK_API_DIR"] = "/x" }, "/home/me", false));
        Assert.Equal("/home/me/.gaiadesk/api-token", LocalApi.TokenPath(new Dictionary<string, string?> { ["GAIADESK_API_DIR"] = "rel" }, "/home/me", false));
        Assert.Equal(@"C:\Users\me\.gaiadesk\api-token", LocalApi.TokenPath(none, @"C:\Users\me", true));
        Assert.Equal(@"D:\gd\api.sock", LocalApi.SocketPath(new Dictionary<string, string?> { ["GAIADESK_API_DIR"] = @"D:\gd" }, @"C:\Users\me", true));
    }
}

public sealed class LanTransportTests : IAsyncLifetime
{
    private const string D = "123456789";
    private MockApi _api = null!;
    private X509Certificate2 _cert = null!;
    private string _fp = null!;

    public async Task InitializeAsync()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=gaiadesk-123456789.local", key, HashAlgorithmName.SHA256);
        using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
#pragma warning disable SYSLIB0057
        _cert = new X509Certificate2(made.Export(X509ContentType.Pkcs12, "pw"), "pw", X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
        _fp = LanGateway.FingerprintOf(_cert);
        _api = await MockApi.StartAsync(new() { [D] = new MockDesk() }, MockMode.Lan, _cert);
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        _cert.Dispose();
    }

    private GaiaDeskClient Gd(string? fp = null, string? token = "gdagt_lan") =>
        GaiaDeskClient.Lan(new LanOptions { BaseUrl = _api.Url, Fingerprint = fp ?? _fp.Replace(":", "").ToUpperInvariant(), DeskToken = token, Retry = RetryOptions.None });

    [Fact]
    public async Task ThePinnedFingerprint_GoesThrough_WithTheAgentTokenAndNoAuthorization()
    {
        using var gd = Gd();
        Assert.Equal(TransportKind.Lan, gd.Transport);
        Assert.Equal(0, (await gd.ExecAsync(D, "uptime")).Exit);
        Assert.Equal("gdagt_lan", _api.Requests.Last().Header("x-gaiadesk-desk-token"));
        Assert.Null(_api.Requests.Last().Header("authorization"));
        Assert.Single((await gd.ListDesksAsync()).Devices);
    }

    [Fact]
    public async Task StreamsAndFiles_OverThePinnedConnection()
    {
        using var gd = Gd();
        Assert.Equal("line1\nline2 é\n", (await gd.FollowJobLogs(D, "build").CollectAsync()).Stdout);
        await gd.UploadBytesAsync(D, "lan.bin", new byte[] { 9, 9 });
        Assert.Equal(new byte[] { 9, 9 }, await gd.DownloadBytesAsync(D, "lan.bin"));
    }

    [Fact]
    public async Task AWrongFingerprint_IsFingerprintMismatch_AndTheGatewayNeverGotARequest()
    {
        var n = _api.Requests.Count;
        var wrong = string.Join(":", Enumerable.Repeat("ab", 32));
        using var gd = Gd(wrong);
        var e = await Assert.ThrowsAsync<FingerprintMismatchException>(() => gd.StatsAsync(D));
        Assert.Equal(Reasons.FingerprintMismatch, e.Reason);
        Assert.Equal(wrong, e.Expected);
        Assert.Equal(_fp, e.Actual);
        Assert.Contains("Do not proceed", e.Message);
        Assert.Equal(n, _api.Requests.Count);
    }

    [Fact]
    public async Task NoAgentToken_IsAUsageError_AndNothingIsSent()
    {
        var n = _api.Requests.Count;
        using var gd = Gd(token: null);
        await Assert.ThrowsAsync<UsageException>(() => gd.StatsAsync(D));
        Assert.Equal(n, _api.Requests.Count);
        await gd.StatsAsync(D, new CallOptions { DeskToken = "gdagt_call" });
        Assert.Equal("gdagt_call", _api.Requests.Last().Header("x-gaiadesk-desk-token"));
    }

    [Fact]
    public async Task OptionsAreChecked_AGatewayThatIsNotThereIsNetwork_HostedOnlyIsUsage()
    {
        Assert.Throws<UsageException>(() => GaiaDeskClient.Lan(new LanOptions { BaseUrl = "http://x:7443/v1", Fingerprint = _fp }));
        Assert.Throws<UsageException>(() => GaiaDeskClient.Lan(new LanOptions { BaseUrl = "https://x:7443/v1" }));
        Assert.Throws<UsageException>(() => GaiaDeskClient.Lan(new LanOptions { BaseUrl = "https://x:7443/v1", Fingerprint = "abc" }));
        using var nowhere = GaiaDeskClient.Lan(new LanOptions { BaseUrl = "https://127.0.0.1:1/v1", Fingerprint = _fp, DeskToken = "t", Retry = RetryOptions.None });
        var e = await Assert.ThrowsAsync<UnreachableException>(() => nowhere.StatsAsync(D));
        Assert.Equal((ErrorKinds.Network, "network"), (e.Kind, e.Reason!));
        using var gd = Gd();
        Assert.Contains("lan transport", (await Assert.ThrowsAsync<UsageException>(() => gd.WakeAsync(D))).Message);
    }

    [Fact]
    public void NormalizeFingerprint_WithOrWithoutColons_AnyCase_ThePrefix()
    {
        var hex = new string('A', 64);
        var want = string.Join(":", Enumerable.Repeat("aa", 32));
        Assert.Equal(want, LanGateway.NormalizeFingerprint(hex));
        Assert.Equal(want, LanGateway.NormalizeFingerprint("SHA256:" + want.ToUpperInvariant()));
        Assert.Equal(want, LanGateway.NormalizeFingerprint(want.Replace(":", " ")));
        Assert.Throws<UsageException>(() => LanGateway.NormalizeFingerprint("zz"));
        Assert.Throws<UsageException>(() => LanGateway.NormalizeFingerprint(new string('a', 62)));
    }
}
