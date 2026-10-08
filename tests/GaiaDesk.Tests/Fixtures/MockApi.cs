// A mock of GaiaDesk's /v1 API AND the desks behind it, on Kestrel in this
// process: the hosted API (fleet, reach, wake, audit, webhooks, support
// sessions, desk operations), or a desk's own API (local: a Unix socket or
// named pipe; lan: HTTPS with a given certificate). Each desk may hold an
// X25519 key (listed as `e2e_pub` while online) and opens sealed requests
// with it; the "API" answers as the real one does: plaintext JSON / SSE /
// bytes for a plaintext call, `{"e2e": {"events"}}`, the error envelope with a
// placeholder message and `e2e.events`, `sealed` SSE events and NDJSON
// downloads for a sealed one. Every request is recorded raw, so a test can
// prove what the API saw. Desk operations: MockApi.DeskOps.cs.

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GaiaDesk.E2e;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GaiaDesk.Tests.Fixtures;

internal enum MockMode { Hosted, Local, Lan }

internal sealed class MockDesk
{
    /// <summary>Its X25519 secret; none: a desk from before end-to-end encryption.</summary>
    public byte[]? Secret { get; set; }
    /// <summary>Keys it still opens with after a rotation (the API lists only Secret's).</summary>
    public List<byte[]> Previous { get; } = new();
    public bool Online { get; set; } = true;
    /// <summary>"Require end-to-end encryption for API commands".</summary>
    public bool Required { get; set; }
    /// <summary>Offline until woken (POST …/wake).</summary>
    public bool Wakeable { get; set; }
    /// <summary>Lookups that list no key before it shows (a stale view).</summary>
    public int HideKeyLookups { get; set; }
    /// <summary>Exec admin: null runs it as administrator; else the refusal reason.</summary>
    public string? AdminRefusal { get; set; } = Reasons.AdminNotEnabled;
}

internal sealed record Recorded(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

/// <summary>A canned answer for the next matching request(s).</summary>
internal sealed class Injection
{
    public Func<Recorded, bool> Match { get; init; } = _ => true;
    public int Status { get; init; } = 503;
    public string Body { get; init; } = "";
    public string ContentType { get; init; } = "application/json";
    public Dictionary<string, string> Headers { get; init; } = new();
    public int Times { get; set; } = 1;
}

internal sealed partial class MockApi : IAsyncDisposable
{
    public const string LocalAdminToken = "gdlocal_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static int _rid;
    private readonly WebApplication _app;
    private readonly List<Recorded> _requests = new();
    private readonly List<Injection> _injections = new();

    public Dictionary<string, MockDesk> Desks { get; }
    public MockMode Mode { get; }
    public string Url { get; private set; } = "";
    public string? SocketPath { get; private set; }
    public List<string> Sealed { get; } = new();
    public List<string> Plain { get; } = new();
    public List<string> Wakes { get; } = new();
    public Dictionary<string, byte[]> Files { get; } = new();
    /// <summary>Misbehave as a hostile server: flip a bit of each sealed event, or answer a sealed call in the clear.</summary>
    public string? Tamper { get; set; }
    /// <summary>Streams the server saw the caller hang up on.</summary>
    public int Aborted;

    public int Connections;
    public List<JsonObject> Webhooks { get; } = new();
    public List<JsonObject> SupportSessions { get; } = new();

    private MockApi(WebApplication app, Dictionary<string, MockDesk> desks, MockMode mode)
    {
        _app = app;
        Desks = desks;
        Mode = mode;
    }

    public IReadOnlyList<Recorded> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public void Inject(Injection i)
    {
        lock (_injections) _injections.Add(i);
    }

    public static async Task<MockApi> StartAsync(Dictionary<string, MockDesk> desks, MockMode mode = MockMode.Hosted, X509Certificate2? cert = null, string? socketPath = null)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = null;
            if (mode == MockMode.Local)
            {
                if (OperatingSystem.IsWindows()) k.ListenNamedPipe(socketPath!);
                else k.ListenUnixSocket(socketPath!);
            }
            else if (mode == MockMode.Lan) k.Listen(IPAddress.Loopback, 0, lo => lo.UseHttps(cert!));
            else k.Listen(IPAddress.Loopback, 0);
        });
        var app = b.Build();
        var m = new MockApi(app, desks, mode);
        app.Use(async (ctx, next) =>
        {
            Interlocked.Increment(ref m.Connections);
            await next();
        });
        app.Run(async ctx =>
        {
            try { await m.Handle(ctx); }
            catch (OperationCanceledException) { Interlocked.Increment(ref m.Aborted); }
            catch (IOException) { Interlocked.Increment(ref m.Aborted); }
        });
        await app.StartAsync();
        if (mode == MockMode.Local) m.SocketPath = socketPath;
        else
        {
            var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var port = new Uri(addr.Replace("[::]", "127.0.0.1")).Port;
            m.Url = mode == MockMode.Lan ? $"https://127.0.0.1:{port}/v1" : $"http://127.0.0.1:{port}/v1";
        }
        return m;
    }

    public async ValueTask DisposeAsync()
    {
        // Streams the caller hung up on may still be draining: give them a moment, then close.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _app.StopAsync(cts.Token);
        await _app.DisposeAsync();
    }

    public static string RequestId() => $"req_{Interlocked.Increment(ref _rid):x24}";

    // ───────────────────────────── writing ─────────────────────────────

    private static async Task Send(HttpContext ctx, int status, JsonNode? v, Dictionary<string, string>? headers = null)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers["X-Request-Id"] = RequestId();
        foreach (var kv in headers ?? new()) ctx.Response.Headers[kv.Key] = kv.Value;
        await ctx.Response.WriteAsync(v?.ToJsonString() ?? "null");
    }

    public static JsonObject Envelope(string kind, string message, string? reason, string? desk = null)
    {
        var error = new JsonObject { ["kind"] = kind, ["message"] = message, ["reason"] = reason ?? kind, ["request_id"] = RequestId() };
        if (desk is not null) error["desk"] = desk;
        return new JsonObject { ["error"] = error };
    }

    private static Task Fail(HttpContext ctx, int status, string kind, string message, string? reason, string? desk = null, Dictionary<string, string>? headers = null) =>
        Send(ctx, status, Envelope(kind, message, reason, desk), headers);

    private static async Task<byte[]> ReadAll(Stream s)
    {
        using var m = new MemoryStream();
        await s.CopyToAsync(m);
        return m.ToArray();
    }

    // ───────────────────────────── routing ─────────────────────────────

    private async Task Handle(HttpContext ctx)
    {
        var req = ctx.Request;
        var body = await ReadAll(req.Body);
        var rec = new Recorded(req.Method, req.Path.Value ?? "/",
            req.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
            req.Headers.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value.ToString()), body);
        lock (_requests) _requests.Add(rec);
        Injection? inj = null;
        lock (_injections)
        {
            inj = _injections.FirstOrDefault(i => i.Times > 0 && i.Match(rec));
            if (inj is not null) inj.Times--;
        }
        if (inj is not null)
        {
            ctx.Response.StatusCode = inj.Status;
            ctx.Response.ContentType = inj.ContentType;
            foreach (var kv in inj.Headers) ctx.Response.Headers[kv.Key] = kv.Value;
            await ctx.Response.WriteAsync(inj.Body);
            return;
        }
        if (!rec.Path.StartsWith("/v1/", StringComparison.Ordinal)) { await Fail(ctx, 404, "unreachable", "no such route", "no_such_route"); return; }
        var rest = rec.Path.Substring(3);
        var auth = rec.Header("authorization");
        var deskToken = rec.Header("x-gaiadesk-desk-token");
        switch (Mode)
        {
            case MockMode.Hosted when auth is null || auth == "Bearer ak_bad":
                await Fail(ctx, 401, "refused", "Sign in, or send an API key as `Authorization: Bearer ak_…`.", "unauthenticated");
                return;
            case MockMode.Local when deskToken is null && auth != $"Bearer {LocalAdminToken}":
                await Fail(ctx, 401, "refused", "The local API takes the desk's admin token or an agent token.", "unauthenticated");
                return;
            case MockMode.Lan when deskToken is null:
                await Fail(ctx, 401, "refused", "A desk's LAN gateway takes agent tokens only.", auth?.StartsWith("Bearer gdlocal_", StringComparison.Ordinal) == true ? "admin_token_local_only" : "unauthenticated");
                return;
        }
        if (Mode == MockMode.Hosted && auth == "Bearer ak_noscope") { await Fail(ctx, 403, "refused", "This key lacks the `desks:write` scope.", "missing_scope"); return; }
        if (rest == "/desks" && rec.Method == "GET") { await ListDesks(ctx); return; }
        var hostedOnly = rest.StartsWith("/audit", StringComparison.Ordinal) || rest.StartsWith("/webhooks", StringComparison.Ordinal) || rest.StartsWith("/support", StringComparison.Ordinal);
        if (!hostedOnly)
        {
            var dm = System.Text.RegularExpressions.Regex.Match(rest, "^/desks/[^/]+(/reach|/wake)?$");
            hostedOnly = dm.Success && (dm.Groups[1].Success || rec.Method == "GET");
        }
        if (Mode != MockMode.Hosted && hostedOnly) { await Fail(ctx, 404, "unreachable", "This route is served by the hosted API only.", "no_such_route"); return; }
        if (rest.StartsWith("/audit", StringComparison.Ordinal)) { await Audit(ctx, rec); return; }
        if (rest.StartsWith("/webhooks", StringComparison.Ordinal)) { await WebhookRoute(ctx, rec, rest); return; }
        if (rest.StartsWith("/support/sessions", StringComparison.Ordinal)) { await SupportRoute(ctx, rec, rest); return; }
        var route = System.Text.RegularExpressions.Regex.Match(rest, "^/desks/([^/]+)(/.*)?$");
        if (!route.Success) { await Fail(ctx, 400, "usage", $"no route {rec.Method} {rec.Path}", "no_route"); return; }
        var id = Uri.UnescapeDataString(route.Groups[1].Value);
        var sub = route.Groups[2].Success ? route.Groups[2].Value : "";
        if (id == "999999990") { ctx.Response.StatusCode = 500; ctx.Response.ContentType = "text/html"; await ctx.Response.WriteAsync("<html>oops</html>"); return; }
        if (id == "999999991") { await Fail(ctx, 429, "refused", "Over this key's rate limit.", "rate_limited", null, new() { ["Retry-After"] = "7" }); return; }
        if (!Desks.TryGetValue(id, out var d)) { await Fail(ctx, 404, "unreachable", "No desk with this id on your account or team.", "unknown_desk", id); return; }
        if (sub == "" && rec.Method == "GET") { await GetDesk(ctx, id, d); return; }
        if (sub == "/reach" && rec.Method == "GET") { await Reach(ctx, rec, id); return; }
        if (sub == "/wake" && rec.Method == "POST") { await Wake(ctx, rec, id, d); return; }
        var isTokens = sub.StartsWith("/tokens", StringComparison.Ordinal);
        if (Mode == MockMode.Hosted && isTokens && deskToken is not null) { await Fail(ctx, 403, "refused", "Token administration is the desk owner's.", "agent_cannot_admin", id); return; }
        if (Mode == MockMode.Hosted && !isTokens && auth!.StartsWith("Bearer ak_", StringComparison.Ordinal) && deskToken is null)
        { await Fail(ctx, 403, "refused", "From an API key, desk operations need a scoped agent token (X-GaiaDesk-Desk-Token).", "desk_token_required", id); return; }
        await DeskOp(ctx, rec, id, sub, d, body);
    }

    private async Task ListDesks(HttpContext ctx)
    {
        var devices = new JsonArray();
        foreach (var kv in Desks.OrderByDescending(kv => kv.Value.Online))
            devices.Add(new JsonObject { ["desk_id"] = kv.Key, ["name"] = $"desk-{kv.Key}", ["online"] = kv.Value.Online, ["os"] = "macos", ["owner"] = "you", ["sources"] = new JsonArray("account") });
        await Send(ctx, 200, new JsonObject
        {
            ["devices"] = devices, ["sources"] = new JsonArray("server"), ["notes"] = new JsonArray(),
            ["identity"] = new JsonObject { ["account"] = "you@example.com", ["source"] = "api_key" },
        });
    }

    private async Task GetDesk(HttpContext ctx, string id, MockDesk d)
    {
        var stale = d.HideKeyLookups > 0 && d.HideKeyLookups-- > 0;
        var o = new JsonObject
        {
            ["desk_id"] = id, ["online"] = d.Online, ["owner"] = "you", ["sources"] = new JsonArray("account"),
            ["features"] = d.Secret is null ? new JsonArray("desk_op") : new JsonArray("desk_op", "desk_op_e2e"),
            ["e2e_required"] = d.Required && !stale,
            ["wake"] = new JsonObject { ["doorbell_sockets"] = d.Wakeable ? 1 : 0, ["lan_wake"] = false },
        };
        if (d.Online && d.Secret is not null && !stale) o["e2e_pub"] = E2eCrypto.B64Url(E2eCrypto.X25519Public(d.Secret));
        else o["e2e_pub"] = null;
        if (!d.Online) { o["offline_since"] = 1791290000; o["offline_reason"] = "silent"; o["offline_reason_text"] = "nothing heard from the host"; }
        await Send(ctx, 200, o);
    }

    private static async Task Reach(HttpContext ctx, Recorded rec, string id)
    {
        var since = rec.Query.TryGetValue("since", out var s) ? long.Parse(s) : 1790700000;
        var events = new JsonArray
        {
            new JsonObject { ["at"] = 1791290000, ["online"] = false, ["reason"] = "silent", ["reason_text"] = "nothing heard from the host" },
            new JsonObject { ["at"] = 1791200000, ["online"] = true, ["reason"] = "registered", ["reason_text"] = "connected", ["version"] = "0.10.325" },
        };
        if (rec.Query.TryGetValue("limit", out var l)) while (events.Count > int.Parse(l)) events.RemoveAt(events.Count - 1);
        await Send(ctx, 200, new JsonObject { ["desk_id"] = id, ["since"] = since, ["events"] = events });
    }

    private async Task Wake(HttpContext ctx, Recorded rec, string id, MockDesk d)
    {
        lock (Wakes) Wakes.Add(id);
        if (d.Online) { await Send(ctx, 200, WakeResult(id, true, false, true, 0)); return; }
        if (!d.Wakeable) { await Fail(ctx, 409, "unreachable", "Nothing can wake this desk now.", "no_wake_path", id); return; }
        d.Online = true;
        await Send(ctx, 200, WakeResult(id, true, true, false, 1), new() { ["Idempotent-Replayed"] = rec.Header("idempotency-key") == "replayed" ? "true" : "false" });
    }

    private static JsonObject WakeResult(string id, bool online, bool woke, bool already, int doorbell) => new()
    {
        ["desk_id"] = id, ["online"] = online, ["woke"] = woke, ["already_online"] = already,
        ["rang"] = new JsonObject { ["doorbell"] = doorbell, ["lan_helpers"] = 0 }, ["waited_ms"] = woke ? 6200 : 0,
    };

    /// <summary>Seven events, newest first, one millisecond apart from 1791000000006 down; filtered by until_ms (inclusive) and limit.</summary>
    private static async Task Audit(HttpContext ctx, Recorded rec)
    {
        var until = rec.Query.TryGetValue("until_ms", out var u) ? long.Parse(u) : long.MaxValue;
        var limit = rec.Query.TryGetValue("limit", out var l) ? int.Parse(l) : 100;
        var events = new JsonArray();
        for (var i = 6; i >= 0; i--)
        {
            var at = 1791000000000 + i;
            if (at > until || events.Count >= limit) continue;
            events.Add(new JsonObject
            {
                ["id"] = $"aud_{i}", ["action"] = "api.execOnDesk", ["stream"] = "api", ["occurred_at_ms"] = at,
                ["actor"] = new JsonObject { ["type"] = "api_key", ["id"] = "ak_1" },
                ["target"] = new JsonObject { ["type"] = "desk", ["id"] = "123456789", ["name"] = null },
                ["metadata"] = new JsonObject { ["status"] = 200 },
            });
        }
        await Send(ctx, 200, new JsonObject { ["events"] = events });
    }

    private async Task WebhookRoute(HttpContext ctx, Recorded rec, string rest)
    {
        if (rest == "/webhooks" && rec.Method == "GET")
        {
            JsonArray list;
            lock (Webhooks) list = new JsonArray(Webhooks.Select(w => (JsonNode?)w.DeepClone()).ToArray());
            await Send(ctx, 200, new JsonObject { ["webhooks"] = list });
            return;
        }
        if (rest == "/webhooks" && rec.Method == "POST")
        {
            var b = JsonNode.Parse(rec.BodyText)!.AsObject();
            var url = b["url"]?.GetValue<string>() ?? "";
            if (!url.StartsWith("https://", StringComparison.Ordinal)) { await Fail(ctx, 400, "usage", "An endpoint must be https://.", "bad_url"); return; }
            var w = new JsonObject
            {
                ["id"] = $"wh_{Webhooks.Count + 1:x16}", ["url"] = url, ["events"] = b["events"]!.DeepClone(),
                ["description"] = b["description"]?.GetValue<string>() ?? "", ["created_at"] = 1791300000,
            };
            lock (Webhooks) Webhooks.Add(w);
            var created = (JsonObject)w.DeepClone();
            created["secret"] = "whsec_" + new string('a', 64);
            await Send(ctx, 201, created);
            return;
        }
        var id = rest.Substring("/webhooks/".Length);
        if (rec.Method == "DELETE")
        {
            int removed;
            lock (Webhooks) removed = Webhooks.RemoveAll(w => w["id"]!.GetValue<string>() == id);
            if (removed == 0) { await Fail(ctx, 404, "unreachable", "No such webhook.", "unknown_webhook"); return; }
            await Send(ctx, 200, new JsonObject { ["deleted"] = id });
            return;
        }
        await Fail(ctx, 400, "usage", "no route", "no_route");
    }

    private async Task SupportRoute(HttpContext ctx, Recorded rec, string rest)
    {
        if (rest == "/support/sessions" && rec.Method == "POST")
        {
            var b = JsonNode.Parse(rec.BodyText)!.AsObject();
            var s = new JsonObject
            {
                ["id"] = $"ss_{SupportSessions.Count + 1:x16}", ["state"] = "waiting", ["mode"] = b["mode"]?.GetValue<string>() ?? "view",
                ["customer"] = b["customer"]?.DeepClone() ?? new JsonObject(), ["customer_present"] = false, ["customer_verified"] = true,
                ["join_code"] = "123456789", ["join_url"] = "https://gaiadesk.net/app/support.html#session=ss_1",
                ["desk_id"] = null, ["origin"] = b["origin"]?.GetValue<string>(), ["owner"] = "you@example.com",
                ["created_at"] = 1791300000, ["expires_at"] = 1791300000 + (b["expires_in"]?.GetValue<long>() ?? 3600),
                ["joined_at"] = null, ["joined_by"] = null, ["ended_at"] = null, ["end_reason"] = null,
            };
            lock (SupportSessions) SupportSessions.Add(s);
            var created = (JsonObject)s.DeepClone();
            created["embed_token"] = "gdemb_" + new string('b', 64);
            await Send(ctx, 201, created);
            return;
        }
        if (rest == "/support/sessions" && rec.Method == "GET")
        {
            var all = rec.Query.TryGetValue("state", out var st) && st == "all";
            JsonArray list;
            lock (SupportSessions) list = new JsonArray(SupportSessions.Where(s => all || s["state"]!.GetValue<string>() is "waiting" or "joined").Select(s => (JsonNode?)s.DeepClone()).ToArray());
            await Send(ctx, 200, new JsonObject { ["sessions"] = list });
            return;
        }
        var id = rest.Substring("/support/sessions/".Length);
        JsonObject? found;
        lock (SupportSessions) found = SupportSessions.FirstOrDefault(s => s["id"]!.GetValue<string>() == id);
        if (found is null) { await Fail(ctx, 404, "unreachable", "No such support session.", "unknown_support_session"); return; }
        await Send(ctx, 200, found.DeepClone());
    }
}
