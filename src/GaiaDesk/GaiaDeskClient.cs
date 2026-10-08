// The client: GaiaDesk's hosted /v1 API from an API key, the desk's own API
// (Local), or a desk's LAN gateway (Lan). This file: construction, and the
// hosted API's fleet routes (desks, reach, wake, audit, webhooks, support
// sessions). Desk operations: GaiaDeskClient.DeskOps.cs and .Files.cs.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using GaiaDesk.E2e;
using GaiaDesk.Http;

namespace GaiaDesk;

/// <summary>
/// Drives GaiaDesk desks through the /v1 API: the hosted API (<see cref="GaiaDeskClient(GaiaDeskOptions)"/>),
/// the desk's own API (<see cref="Local"/>) or a desk's LAN gateway (<see cref="Lan"/>). Thread-safe; reuse one.
/// </summary>
public sealed partial class GaiaDeskClient : IDisposable
{
    /// <summary>The hosted API.</summary>
    public const string DefaultApiUrl = "https://api.gaiadesk.net/v1";
    /// <summary>The most one file may be through the API (256 MB; larger files go through gaiadesk-cli).</summary>
    public const long ApiFileLimit = 256L * 1024 * 1024;
    /// <summary>The longest one <c>GET …/jobs/{name}/wait</c> holds, in seconds; a longer wait asks again.</summary>
    public const int ApiWaitMax = 870;

    private readonly HttpCore _core;

    /// <summary>The hosted API, from an API key (and optionally a scoped agent token for desk operations).</summary>
    public GaiaDeskClient(string apiKey, string? deskToken = null)
        : this(new GaiaDeskOptions { ApiKey = apiKey, DeskToken = deskToken }) { }

    /// <summary>The hosted API.</summary>
    public GaiaDeskClient(GaiaDeskOptions options)
    {
        if (options is null) throw Errors.Usage("options are required");
        var key = Check.NonEmpty(options.ApiKey, "ApiKey");
        var deskToken = Check.OptionalToken(options.DeskToken);
        var baseUrl = (options.BaseUrl ?? DefaultApiUrl).TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
            throw Errors.Usage($"BaseUrl must be an http(s) URL: \"{options.BaseUrl}\"");
        var owns = options.HttpClient is null;
        var http = options.HttpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Task<Dictionary<string, string>> Credentials(string? callToken, CancellationToken ct)
        {
            var h = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" };
            if ((callToken ?? deskToken) is { } t) h["X-GaiaDesk-Desk-Token"] = t;
            return Task.FromResult(h);
        }
        _core = new HttpCore(TransportKind.Api, baseUrl, $"the GaiaDesk API ({baseUrl})", http, owns, Credentials, options.Retry, options.UserAgent);
        _core.E2e = new E2eLayer(options.E2e, options.E2eKeys, options.OnWarning,
            (method, path, token, json, ct) => _core.JsonAsync(new ApiRequest(method, path) { DeskToken = token, Json = json }, ct), baseUrl);
    }

    internal GaiaDeskClient(HttpCore core)
    {
        _core = core;
    }

    /// <summary>Which API this client talks to.</summary>
    public TransportKind Transport => _core.Transport;

    /// <summary>The API's base URL (<c>…/v1</c>).</summary>
    public string BaseUrl => _core.BaseUrl;

    /// <summary>The end-to-end encryption mode (hosted API only; <see cref="E2eMode.Off"/> on the local and LAN transports, which never leave the desk or the LAN).</summary>
    public E2eMode E2e => _core.E2e?.Mode ?? E2eMode.Off;

    /// <summary>Disposes the HttpClient the SDK made (never one passed in).</summary>
    public void Dispose() => _core.Dispose();

    private static string DeskPath(string deskId) => $"/desks/{Uri.EscapeDataString(Check.Desk(deskId))}";

    private UsageException NotServed(string what, string? hint = null)
    {
        var name = Transport switch { TransportKind.Local => "local", TransportKind.Lan => "lan", _ => "API" };
        hint ??= Transport == TransportKind.Api ? "use gaiadesk-cli" : "it is served by the hosted API only (a desk's own API serves desk operations)";
        return Errors.Usage($"{what} is not available over the {name} transport; {hint}", what);
    }

    private void HostedOnly(string what)
    {
        if (Transport != TransportKind.Api) throw NotServed(what);
    }

    private async Task<T> Json<T>(ApiRequest r, CancellationToken ct) =>
        GaiaDeskJson.To<T>(await _core.JsonAsync(r, ct).ConfigureAwait(false), r.Operation);

    private static ProtocolException NoList(string what, string operation, JsonElement json) =>
        new($"the GaiaDesk API answered {operation} with no {what} list", new ErrorDetails { Operation = operation, Json = json, ExitCode = 255 });

    private static List<T> ListOf<T>(JsonElement json, string key, string operation)
    {
        if (!GaiaDeskJson.TryProp(json, key, out var v) || v.ValueKind != JsonValueKind.Array) throw NoList(key, operation, json);
        return GaiaDeskJson.To<List<T>>(v, operation);
    }

    // ───────────────────────────── desks ─────────────────────────────

    /// <summary><c>GET /desks</c>: the desks on the account and its team, online first (the local API: this desk; a LAN gateway: it and the desks it reaches). <paramref name="deskId"/> filters it to one.</summary>
    public async Task<DeskList> ListDesksAsync(string? deskId = null, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var r = new ApiRequest("GET", "/desks").With(options);
        r.Wake = null;
        var json = await _core.JsonAsync(r, cancellationToken).ConfigureAwait(false);
        if (!GaiaDeskJson.TryProp(json, "devices", out var d) || d.ValueKind != JsonValueKind.Array)
            throw new ProtocolException("the GaiaDesk API listed no devices", new ErrorDetails { Operation = r.Operation, Json = json, ExitCode = 255 });
        var list = GaiaDeskJson.To<DeskList>(json, r.Operation);
        if (deskId is null) return list;
        var id = Check.Desk(deskId);
        list.Devices = list.Devices.Where(x => x.DeskId == id).ToList();
        return list;
    }

    /// <summary><c>GET /desks/{id}</c>: one desk, with its reachability, wake hints and end-to-end key. Hosted API only.</summary>
    public Task<DeskDetail> GetDeskAsync(string deskId, CancellationToken cancellationToken = default)
    {
        HostedOnly("GetDeskAsync");
        return Json<DeskDetail>(new ApiRequest("GET", DeskPath(deskId)), cancellationToken);
    }

    /// <summary><c>GET /desks/{id}/reach</c>: its online/offline history, newest first (default the last 7 days; the log keeps 30). Hosted API only.</summary>
    public Task<ReachLog> GetReachAsync(string deskId, DateTimeOffset? since = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        HostedOnly("GetReachAsync");
        if (limit is < 1 or > 1000) throw Errors.Usage("limit is 1 to 1000");
        var r = new ApiRequest("GET", $"{DeskPath(deskId)}/reach");
        if (since is { } s) r.Query["since"] = s.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (limit is { } l) r.Query["limit"] = l.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Json<ReachLog>(r, cancellationToken);
    }

    /// <summary>
    /// <c>POST /desks/{id}/wake</c>: ring the desk's doorbell and ask its LAN siblings to Wake-on-LAN it;
    /// with <paramref name="wait"/> (at most 90 s), wait for it to come online. Nothing to ring is an
    /// <see cref="UnreachableException"/> (<c>no_wake_path</c>). Hosted API only.
    /// </summary>
    public Task<WakeResult> WakeAsync(string deskId, TimeSpan? wait = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        HostedOnly("WakeAsync");
        var r = new ApiRequest("POST", $"{DeskPath(deskId)}/wake") { IdempotencyKey = idempotencyKey, Json = new JsonObject() };
        if (wait is { } w)
        {
            var s = Check.Seconds(w, "wait");
            if (s > 90) throw Errors.Usage("wait is at most 90 seconds");
            r.Json = new JsonObject { ["wait_s"] = s };
        }
        return Json<WakeResult>(r, cancellationToken);
    }

    // ───────────────────────────── audit ─────────────────────────────

    /// <summary><c>GET /audit</c>: audit events about the caller and their own desks, newest first. Hosted API only.</summary>
    public async Task<IReadOnlyList<AuditEvent>> ListAuditAsync(AuditQuery? query = null, CancellationToken cancellationToken = default)
    {
        HostedOnly("ListAuditAsync");
        var r = AuditRequest(query ?? new AuditQuery());
        return ListOf<AuditEvent>(await _core.JsonAsync(r, cancellationToken).ConfigureAwait(false), "events", r.Operation);
    }

    /// <summary>
    /// Every audit event matching <paramref name="query"/>, newest first, fetched a page at a time
    /// (<paramref name="pageSize"/>, at most 500) by moving <c>until_ms</c> back past the oldest seen.
    /// <see cref="AuditQuery.Limit"/>, when set, caps the total.
    /// </summary>
    public async IAsyncEnumerable<AuditEvent> EnumerateAuditAsync(AuditQuery? query = null, int pageSize = 500, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        HostedOnly("EnumerateAuditAsync");
        if (pageSize is < 1 or > 500) throw Errors.Usage("pageSize is 1 to 500");
        var q = query ?? new AuditQuery();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var until = q.Until;
        var total = 0;
        for (;;)
        {
            var page = new AuditQuery { Desk = q.Desk, Actor = q.Actor, Action = q.Action, Token = q.Token, Since = q.Since, Until = until, Limit = pageSize };
            var r = AuditRequest(page);
            var events = ListOf<AuditEvent>(await _core.JsonAsync(r, cancellationToken).ConfigureAwait(false), "events", r.Operation);
            var fresh = 0;
            foreach (var e in events)
            {
                if (!seen.Add(e.Id)) continue;
                fresh++;
                yield return e;
                if (q.Limit is { } cap && ++total >= cap) yield break;
            }
            if (events.Count < pageSize || events.Count == 0) yield break;
            var oldest = events.Min(e => e.OccurredAtMs);
            // Inclusive or not, the oldest millisecond is asked again and its seen events skipped; no progress moves past it.
            var next = DateTimeOffset.FromUnixTimeMilliseconds(fresh == 0 ? oldest - 1 : oldest);
            if (until is { } prev && next >= prev && fresh > 0) next = DateTimeOffset.FromUnixTimeMilliseconds(oldest - 1);
            until = next;
        }
    }

    private static ApiRequest AuditRequest(AuditQuery q)
    {
        var r = new ApiRequest("GET", "/audit");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (q.Desk is not null) r.Query["desk"] = Check.Desk(q.Desk);
        if (q.Actor is not null) r.Query["actor"] = q.Actor;
        if (q.Action is not null) r.Query["action"] = q.Action;
        if (q.Token is not null) r.Query["token"] = q.Token;
        if (q.Since is { } s) r.Query["since_ms"] = s.ToUnixTimeMilliseconds().ToString(inv);
        if (q.Until is { } u) r.Query["until_ms"] = u.ToUnixTimeMilliseconds().ToString(inv);
        if (q.Limit is { } l)
        {
            if (l is < 1 or > 500) throw Errors.Usage("limit is 1 to 500");
            r.Query["limit"] = l.ToString(inv);
        }
        return r;
    }

    // ───────────────────────────── webhooks ─────────────────────────────

    /// <summary><c>GET /webhooks</c>: the account's webhook subscriptions (never their secrets). Hosted API only.</summary>
    public async Task<IReadOnlyList<Webhook>> ListWebhooksAsync(CancellationToken cancellationToken = default)
    {
        HostedOnly("ListWebhooksAsync");
        var r = new ApiRequest("GET", "/webhooks");
        return ListOf<Webhook>(await _core.JsonAsync(r, cancellationToken).ConfigureAwait(false), "webhooks", r.Operation);
    }

    /// <summary><c>POST /webhooks</c>: subscribe an https endpoint to events (<see cref="WebhookEventTypes"/>). Keep <see cref="WebhookCreated.Secret"/>: it is shown once. Hosted API only.</summary>
    public Task<WebhookCreated> CreateWebhookAsync(string url, IEnumerable<string> events, string? description = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        HostedOnly("CreateWebhookAsync");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") throw Errors.Usage($"a webhook URL is an https:// URL: \"{url}\"");
        var list = (events ?? throw Errors.Usage("events are required")).ToList();
        if (list.Count == 0) throw Errors.Usage("at least one event is required");
        var body = new JsonObject { ["url"] = url, ["events"] = new JsonArray(list.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()) };
        if (description is not null) body["description"] = description;
        return Json<WebhookCreated>(new ApiRequest("POST", "/webhooks") { Json = body, IdempotencyKey = idempotencyKey }, cancellationToken);
    }

    /// <summary><c>DELETE /webhooks/{id}</c>: unsubscribe (deliveries still queued are dropped). Hosted API only.</summary>
    public Task<WebhookDeleted> DeleteWebhookAsync(string webhookId, CancellationToken cancellationToken = default)
    {
        HostedOnly("DeleteWebhookAsync");
        var id = Check.NonEmpty(webhookId, "webhookId");
        return Json<WebhookDeleted>(new ApiRequest("DELETE", $"/webhooks/{Uri.EscapeDataString(id)}"), cancellationToken);
    }

    // ───────────────────────────── support sessions ─────────────────────────────

    /// <summary>
    /// <c>POST /support/sessions</c>: a support session for the embed SDKs. Hand <see cref="SupportSessionCreated.EmbedToken"/>
    /// (shown once) to the customer's page or app; agents join with <see cref="SupportSession.JoinCode"/>. Hosted API only.
    /// </summary>
    public Task<SupportSessionCreated> CreateSupportSessionAsync(SupportSessionCreate? session = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        HostedOnly("CreateSupportSessionAsync");
        var s = session ?? new SupportSessionCreate();
        var body = new JsonObject();
        if (s.Mode is not null)
        {
            if (s.Mode is not (SupportModes.View or SupportModes.Cobrowse)) throw Errors.Usage("mode is view or cobrowse");
            body["mode"] = s.Mode;
        }
        if (s.Customer is not null) body["customer"] = JsonNode.Parse(JsonSerializer.Serialize(s.Customer, GaiaDeskJson.Options));
        if (s.ExpiresIn is { } e)
        {
            var secs = Check.Seconds(e, "ExpiresIn");
            if (secs is < 60 or > 86400) throw Errors.Usage("ExpiresIn is 60 seconds to 24 hours");
            body["expires_in"] = secs;
        }
        if (s.Origin is not null) body["origin"] = s.Origin;
        return Json<SupportSessionCreated>(new ApiRequest("POST", "/support/sessions") { Json = body, IdempotencyKey = idempotencyKey }, cancellationToken);
    }

    /// <summary><c>GET /support/sessions</c>: the account's and team's sessions, newest first; open ones unless <paramref name="includeEnded"/>. Hosted API only.</summary>
    public async Task<IReadOnlyList<SupportSession>> ListSupportSessionsAsync(bool includeEnded = false, int? limit = null, CancellationToken cancellationToken = default)
    {
        HostedOnly("ListSupportSessionsAsync");
        if (limit is < 1 or > 200) throw Errors.Usage("limit is 1 to 200");
        var r = new ApiRequest("GET", "/support/sessions");
        r.Query["state"] = includeEnded ? "all" : "open";
        if (limit is { } l) r.Query["limit"] = l.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ListOf<SupportSession>(await _core.JsonAsync(r, cancellationToken).ConfigureAwait(false), "sessions", r.Operation);
    }

    /// <summary><c>GET /support/sessions/{id}</c>: a session's state. Hosted API only.</summary>
    public Task<SupportSession> GetSupportSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        HostedOnly("GetSupportSessionAsync");
        var id = Check.NonEmpty(sessionId, "sessionId");
        return Json<SupportSession>(new ApiRequest("GET", $"/support/sessions/{Uri.EscapeDataString(id)}"), cancellationToken);
    }
}
