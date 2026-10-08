namespace GaiaDesk;

/// <summary>Which /v1 API a client talks to.</summary>
public enum TransportKind
{
    /// <summary>GaiaDesk's hosted API (<c>https://api.gaiadesk.net/v1</c>), from an API key.</summary>
    Api,
    /// <summary>The desk's own API, on its Unix socket or Windows named pipe (code running on the desk).</summary>
    Local,
    /// <summary>A desk's opt-in LAN gateway over pinned TLS (<c>https://&lt;desk&gt;:7443/v1</c>).</summary>
    Lan,
}

/// <summary>End-to-end encryption of desk operations on the hosted API.</summary>
public enum E2eMode
{
    /// <summary>Seal when the desk lists a key; otherwise plaintext with a one-time warning, unless the desk requires it.</summary>
    Auto,
    /// <summary>Never send a desk operation in the clear: no key (after a wake) is an <see cref="E2eException"/>.</summary>
    Require,
    /// <summary>Never seal.</summary>
    Off,
}

/// <summary>The shell a command runs in (<c>shell</c>).</summary>
public enum Shell
{
    /// <summary>The desk's own: the login shell on macOS/Linux, cmd.exe on Windows.</summary>
    Default,
    /// <summary>No shell: run the program directly (an argument list).</summary>
    None,
    /// <summary>sh</summary>
    Sh,
    /// <summary>bash</summary>
    Bash,
    /// <summary>zsh</summary>
    Zsh,
    /// <summary>cmd.exe</summary>
    Cmd,
    /// <summary>PowerShell 7 (pwsh).</summary>
    Pwsh,
    /// <summary>Sent as <c>pwsh</c>, as gaiadesk-cli reads it.</summary>
    PowerShell,
}

/// <summary>A background job's CPU priority.</summary>
public enum JobPriority
{
    /// <summary>low</summary>
    Low,
    /// <summary>normal</summary>
    Normal,
    /// <summary>high</summary>
    High,
}

/// <summary>When the SDK sends a request again by itself.</summary>
public sealed class RetryOptions
{
    /// <summary>
    /// The most times one request is sent again (default 2; 0 turns retries off). A 429
    /// (<c>rate_limited</c>, <c>desk_busy</c>: nothing ran) is retried for every operation, after
    /// <c>Retry-After</c>; a lost connection or a 502/503/504 only for operations that only read
    /// (GETs). Operations that change something are never sent twice after they may have run.
    /// </summary>
    public int MaxRetries { get; set; } = 2;
    /// <summary>The first back-off when the answer gave no <c>Retry-After</c> (doubled each time, with jitter). Default 0.5 s.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);
    /// <summary>The longest the SDK waits before one retry, <c>Retry-After</c> included (default 30 s); a longer <c>Retry-After</c> is not waited for.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>No retries.</summary>
    public static RetryOptions None => new() { MaxRetries = 0 };
}

/// <summary>
/// How long the SDK waits on the network before giving up, so a server or proxy that stops answering
/// (a dropped connection that is never closed, a half-open socket) is a typed error, never a hang.
/// </summary>
public sealed class TimeoutOptions
{
    /// <summary>
    /// The longest wait for an answer to begin (its status and headers), sending the request included.
    /// Default 16 minutes: above the API's 15-minute limit on a call (a buffered exec answers when its
    /// command ends). <see cref="Timeout.InfiniteTimeSpan"/>: no limit. Exceeded: <see cref="UnreachableException"/>, kind <c>timeout</c>.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMinutes(16);
    /// <summary>
    /// The longest silence while reading an answer's body (a JSON result, a download, an event stream,
    /// a held wait). Default 90 s: the API's streams and held waits send a keep-alive every 15 s.
    /// <see cref="Timeout.InfiniteTimeSpan"/>: no limit. Exceeded: <see cref="ConnectionLostException"/>, kind <c>timeout</c>.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(90);

    internal void Validate()
    {
        foreach (var (t, name) in new[] { (ResponseTimeout, "ResponseTimeout"), (IdleTimeout, "IdleTimeout") })
            if (t != Timeout.InfiniteTimeSpan && t <= TimeSpan.Zero) throw Errors.Usage($"Timeouts.{name} must be positive (or Timeout.InfiniteTimeSpan)");
    }
}

/// <summary>Options for the hosted API (<see cref="GaiaDeskClient(GaiaDeskOptions)"/>).</summary>
public sealed class GaiaDeskOptions
{
    /// <summary>An API key (<c>ak_…</c>), or a signed-in person's session or OAuth token. Required.</summary>
    public string? ApiKey { get; set; }
    /// <summary>A scoped agent token (<c>gdagt_…</c>) sent as <c>X-GaiaDesk-Desk-Token</c>, which the desk verifies. Per call: <see cref="CallOptions.DeskToken"/>.</summary>
    public string? DeskToken { get; set; }
    /// <summary>The API's base URL (default <see cref="GaiaDeskClient.DefaultApiUrl"/>).</summary>
    public string? BaseUrl { get; set; }
    /// <summary>
    /// The HttpClient to send with (from <c>IHttpClientFactory</c>, say). The SDK never disposes it. Its
    /// <see cref="HttpClient.Timeout"/> applies to every request, so set it to <see cref="Timeout.InfiniteTimeSpan"/>
    /// (or above 15 minutes) for long waits and streams; the SDK's own client has no timeout.
    /// </summary>
    public HttpClient? HttpClient { get; set; }
    /// <summary>End-to-end encryption of desk operations (default <see cref="E2eMode.Auto"/>).</summary>
    public E2eMode E2e { get; set; } = E2eMode.Auto;
    /// <summary>Pinned desk keys, desk id to <c>e2e_pub</c> (base64url): a different key from the server is refused.</summary>
    public IDictionary<string, string>? E2eKeys { get; set; }
    /// <summary>Where the SDK's warnings go (default: standard error).</summary>
    public Action<string>? OnWarning { get; set; }
    /// <summary>Retries (default: <see cref="RetryOptions"/>'s defaults).</summary>
    public RetryOptions? Retry { get; set; }
    /// <summary>Network timeouts (default: <see cref="TimeoutOptions"/>'s defaults).</summary>
    public TimeoutOptions? Timeouts { get; set; }
    /// <summary>The <c>User-Agent</c> product added before the SDK's own (<c>myapp/1.2</c>).</summary>
    public string? UserAgent { get; set; }
}

/// <summary>Options for the desk's own API (<see cref="GaiaDeskClient.Local(LocalOptions)"/>).</summary>
public sealed class LocalOptions
{
    /// <summary>The socket path or pipe name (default: <c>$GAIADESK_API_DIR/api.sock</c>, else <c>~/.gaiadesk/api.sock</c>; Windows <c>$GAIADESK_API_PIPE</c>, else <c>\\.\pipe\gaiadesk-api-&lt;user&gt;</c>).</summary>
    public string? SocketPath { get; set; }
    /// <summary>The desk's local admin token (<c>gdlocal_…</c>; default: read from its file on each request).</summary>
    public string? Token { get; set; }
    /// <summary>An agent token (<c>gdagt_…</c>), sent instead of the admin token.</summary>
    public string? DeskToken { get; set; }
    /// <summary>Where defaults are read from (default: this process's environment).</summary>
    public IDictionary<string, string?>? Environment { get; set; }
    /// <summary>Retries (default: <see cref="RetryOptions"/>'s defaults).</summary>
    public RetryOptions? Retry { get; set; }
    /// <summary>Network timeouts (default: <see cref="TimeoutOptions"/>'s defaults).</summary>
    public TimeoutOptions? Timeouts { get; set; }
}

/// <summary>Options for a desk's LAN gateway (<see cref="GaiaDeskClient.Lan(LanOptions)"/>).</summary>
public sealed class LanOptions
{
    /// <summary>The gateway's base URL, <c>https://&lt;host&gt;:7443/v1</c>. Required.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>The gateway certificate's SHA-256 fingerprint, as the desk's Settings shows it. Required.</summary>
    public string? Fingerprint { get; set; }
    /// <summary>The agent token (<c>gdagt_…</c>); required here or on each call.</summary>
    public string? DeskToken { get; set; }
    /// <summary>Retries (default: <see cref="RetryOptions"/>'s defaults).</summary>
    public RetryOptions? Retry { get; set; }
    /// <summary>Network timeouts (default: <see cref="TimeoutOptions"/>'s defaults).</summary>
    public TimeoutOptions? Timeouts { get; set; }
}

/// <summary>Per call.</summary>
public class CallOptions
{
    /// <summary>The scoped agent token for this call only, instead of the client's.</summary>
    public string? DeskToken { get; set; }
    /// <summary>A desk operation on the hosted API: if the desk is asleep, ring it and wait up to this many seconds (0-120; <c>wake_s</c>).</summary>
    public int? Wake { get; set; }
    /// <summary>A POST's <c>Idempotency-Key</c>: a retry with the same key and request within 24 hours gets the first answer again.</summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>Options for <c>ExecAsync</c> and <c>ExecStream</c>.</summary>
public sealed class ExecOptions : CallOptions
{
    /// <summary>The shell (default: the desk's).</summary>
    public Shell? Shell { get; set; }
    /// <summary>Environment variables for the command (sent in the request, never logged).</summary>
    public IReadOnlyDictionary<string, string>? Env { get; set; }
    /// <summary>The directory it starts in on the desk.</summary>
    public string? Cwd { get; set; }
    /// <summary>Text for its stdin, then end of input (absent: stdin is closed).</summary>
    public string? Stdin { get; set; }
    /// <summary>Stop it after this long (<see cref="TimeSpan.Zero"/>: no limit; absent: 30 minutes, held under the API's 15).</summary>
    public TimeSpan? Timeout { get; set; }
    /// <summary><c>ExecAsync</c>: a non-zero exit (or a timeout) is a <see cref="CommandException"/>.</summary>
    public bool Check { get; set; }
    /// <summary>
    /// Run it as administrator (root on macOS/Linux, SYSTEM on Windows). Needs a desk token with the
    /// <c>admin</c> scope and the desk owner's Admin access; a refusal is <c>exit</c> 254 with reason
    /// <c>admin_scope_missing</c>, <c>admin_not_enabled</c>, <c>admin_denied</c> or <c>admin_unavailable</c>.
    /// </summary>
    public bool Admin { get; set; }
}

/// <summary>Options for <c>RunJobAsync</c>.</summary>
public sealed class JobOptions : CallOptions
{
    /// <summary>CPU priority.</summary>
    public JobPriority? Priority { get; set; }
    /// <summary>Share of the whole machine, 1-100.</summary>
    public int? CpuPercent { get; set; }
    /// <summary>Memory cap: megabytes, or <c>"512M"</c> / <c>"2G"</c>.</summary>
    public string? Memory { get; set; }
    /// <summary>true: keep the desk awake while it runs; false: don't; null: the desk's default.</summary>
    public bool? KeepAwake { get; set; }
    /// <summary>The directory it starts in.</summary>
    public string? Cwd { get; set; }
    /// <summary>The shell (sh, bash, zsh, cmd, pwsh; not Default or None).</summary>
    public Shell? Shell { get; set; }
    /// <summary>Environment variables (never logged by the desk).</summary>
    public IReadOnlyDictionary<string, string>? Env { get; set; }
}

/// <summary>Options for <c>CreateTokenAsync</c>.</summary>
public sealed class TokenCreateOptions : CallOptions
{
    /// <summary>One token per desk, under one name. Required.</summary>
    public IReadOnlyList<string> Desks { get; set; } = Array.Empty<string>();
    /// <summary>Its name. Required over the API.</summary>
    public string? Name { get; set; }
    /// <summary>How long it lives (default 7 days).</summary>
    public TimeSpan? Expires { get; set; }
    /// <summary>What it may do (default exec, cp, jobs). <c>admin</c> is never implied: name it (<see cref="TokenScopes.Admin"/>).</summary>
    public IReadOnlyList<string>? Scopes { get; set; }
    /// <summary>Confine its work to this folder on the desk.</summary>
    public string? Cwd { get; set; }
    /// <summary>Run its work as the desk's low-privilege agent user.</summary>
    public bool LowPriv { get; set; }
}

/// <summary>Agent token scopes (<see cref="TokenCreateOptions.Scopes"/>).</summary>
public static class TokenScopes
{
    /// <summary>Run commands.</summary>
    public const string Exec = "exec";
    /// <summary>Interactive shells.</summary>
    public const string Shell = "shell";
    /// <summary>Copy files.</summary>
    public const string Cp = "cp";
    /// <summary>Background jobs.</summary>
    public const string Jobs = "jobs";
    /// <summary>Screen sessions.</summary>
    public const string Screen = "screen";
    /// <summary>Port forwarding.</summary>
    public const string Forward = "forward";
    /// <summary>ASK to run as administrator (exec <c>Admin = true</c>); the desk owner's Admin access still decides. Never implied.</summary>
    public const string Admin = "admin";
}

/// <summary>Filters for <c>ListAuditAsync</c> (<c>GET /audit</c>).</summary>
public sealed class AuditQuery
{
    /// <summary>Only events about this desk.</summary>
    public string? Desk { get; set; }
    /// <summary>Only events by this actor id (an email, a token id).</summary>
    public string? Actor { get; set; }
    /// <summary>Only this action, or every action under it when it ends in <c>.*</c> (<c>api.*</c>).</summary>
    public string? Action { get; set; }
    /// <summary>Only events by this agent token id or API key id.</summary>
    public string? Token { get; set; }
    /// <summary>From this time.</summary>
    public DateTimeOffset? Since { get; set; }
    /// <summary>Up to this time.</summary>
    public DateTimeOffset? Until { get; set; }
    /// <summary>At most this many (1-500; default 100).</summary>
    public int? Limit { get; set; }
}

/// <summary>Support session modes.</summary>
public static class SupportModes
{
    /// <summary>The agent sees the shared tab or app.</summary>
    public const string View = "view";
    /// <summary>The agent may also point, highlight, click, scroll and type inside the page.</summary>
    public const string Cobrowse = "cobrowse";
}

/// <summary>A new support session (<c>POST /support/sessions</c>).</summary>
public sealed class SupportSessionCreate
{
    /// <summary><see cref="SupportModes.View"/> (default) or <see cref="SupportModes.Cobrowse"/>.</summary>
    public string? Mode { get; set; }
    /// <summary>Who the customer is: at most 16 fields of short strings, numbers or booleans.</summary>
    public IReadOnlyDictionary<string, object?>? Customer { get; set; }
    /// <summary>How long until the session ends (60 s to 24 h; default 1 h).</summary>
    public TimeSpan? ExpiresIn { get; set; }
    /// <summary>The page origin the web embed must run on (leave out for a desktop app's native embed).</summary>
    public string? Origin { get; set; }
}

/// <summary>Webhook event types.</summary>
public static class WebhookEventTypes
{
    /// <summary>A desk came online.</summary>
    public const string DeskOnline = "desk.online";
    /// <summary>A desk went offline (with the reason).</summary>
    public const string DeskOffline = "desk.offline";
    /// <summary>A desk rung through the API came online within three minutes.</summary>
    public const string DeskWoke = "desk.woke";
    /// <summary>A background job started through the API ended.</summary>
    public const string JobFinished = "job.finished";
    /// <summary>A support agent joined a customer's shared tab.</summary>
    public const string SupportSessionJoined = "support.session.joined";
    /// <summary>A support session ended.</summary>
    public const string SupportSessionEnded = "support.session.ended";
}
