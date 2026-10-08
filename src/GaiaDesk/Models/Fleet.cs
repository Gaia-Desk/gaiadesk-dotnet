// Fleet, reach, wake, audit, webhooks and support sessions: the hosted API's
// own objects (api/openapi.yaml components/schemas Api*).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace GaiaDesk;

/// <summary>When the desk was last reached.</summary>
public sealed class ReachSuccess : GaiaDeskObject
{
    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("at")] public long At { get; set; }
    /// <summary>The road taken.</summary>
    [JsonPropertyName("route")] public string Route { get; set; } = "";
    /// <summary>Connect time.</summary>
    [JsonPropertyName("connect_ms")] public long? ConnectMs { get; set; }
    /// <summary>Round trip.</summary>
    [JsonPropertyName("rtt_ms")] public long? RttMs { get; set; }
}

/// <summary>When reaching the desk last failed.</summary>
public sealed class ReachFailure : GaiaDeskObject
{
    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("at")] public long At { get; set; }
    /// <summary>The failure's kind.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    /// <summary>For a person.</summary>
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

/// <summary>Who the API sees: <c>{source, account}</c>.</summary>
public sealed class Identity : GaiaDeskObject
{
    /// <summary>api_key, session, …</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    /// <summary>The account.</summary>
    [JsonPropertyName("account")] public string? Account { get; set; }
}

/// <summary>A desk, as <c>GET /desks</c> lists it.</summary>
public class Desk : GaiaDeskObject
{
    /// <summary>Its nine-digit id.</summary>
    [JsonPropertyName("desk_id")] public string DeskId { get; set; } = "";
    /// <summary>Its name.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>It holds its signaling connection now.</summary>
    [JsonPropertyName("online")] public bool? Online { get; set; }
    /// <summary>Seconds since it was last heard.</summary>
    [JsonPropertyName("signal_idle_secs")] public long? SignalIdleSecs { get; set; }
    /// <summary>Unix seconds it was last seen.</summary>
    [JsonPropertyName("last_seen")] public long? LastSeen { get; set; }
    /// <summary>macos, windows, linux.</summary>
    [JsonPropertyName("os")] public string? Os { get; set; }
    /// <summary>Its GaiaDesk version.</summary>
    [JsonPropertyName("app_version")] public string? AppVersion { get; set; }
    /// <summary>Unattended access is on.</summary>
    [JsonPropertyName("anytime")] public bool? Anytime { get; set; }
    /// <summary><c>you</c>, or a team mate's email.</summary>
    [JsonPropertyName("owner")] public string? Owner { get; set; }
    /// <summary>account, team, server, …</summary>
    [JsonPropertyName("sources")] public List<string> Sources { get; set; } = new();
    /// <summary>Reachable now (a probe's answer).</summary>
    [JsonPropertyName("reachable")] public bool? Reachable { get; set; }
    /// <summary>The last time it was reached.</summary>
    [JsonPropertyName("last_ok")] public ReachSuccess? LastOk { get; set; }
    /// <summary>The last time reaching it failed.</summary>
    [JsonPropertyName("last_failure")] public ReachFailure? LastFailure { get; set; }
    /// <summary>Unix seconds it went offline.</summary>
    [JsonPropertyName("offline_since")] public long? OfflineSince { get; set; }
    /// <summary><c>closed</c>, <c>silent</c>, <c>error</c>, <c>updating</c>, <c>server-restart</c>, <c>id-changed</c>, <c>unknown</c>.</summary>
    [JsonPropertyName("offline_reason")] public string? OfflineReason { get; set; }
    /// <summary>The reason, for a person.</summary>
    [JsonPropertyName("offline_reason_text")] public string? OfflineReasonText { get; set; }
    /// <summary><c>id-changed</c>: the id it answers to now.</summary>
    [JsonPropertyName("offline_detail")] public string? OfflineDetail { get; set; }
    /// <summary>What it takes now: <c>desk_op</c>, <c>desk_op_e2e</c>.</summary>
    [JsonPropertyName("features")] public List<string>? Features { get; set; }
    /// <summary>Its end-to-end X25519 public key (base64url) while online and able to open sealed operations.</summary>
    [JsonPropertyName("e2e_pub")] public string? E2ePub { get; set; }
    /// <summary>Its owner requires end-to-end encryption for API commands.</summary>
    [JsonPropertyName("e2e_required")] public bool? E2eRequired { get; set; }
}

/// <summary>How a desk could be woken now.</summary>
public sealed class WakeHints : GaiaDeskObject
{
    /// <summary>Doorbell sockets it holds (a sleeping Mac's).</summary>
    [JsonPropertyName("doorbell_sockets")] public int DoorbellSockets { get; set; }
    /// <summary>It can be woken on its LAN by an awake sibling.</summary>
    [JsonPropertyName("lan_wake")] public bool LanWake { get; set; }
}

/// <summary><c>GET /desks/{id}</c>: one desk, with its wake hints.</summary>
public sealed class DeskDetail : Desk
{
    /// <summary>How it could be woken now.</summary>
    [JsonPropertyName("wake")] public WakeHints? Wake { get; set; }
}

/// <summary><c>GET /desks</c>: the desks, online first.</summary>
public sealed class DeskList : GaiaDeskObject
{
    /// <summary>The desks.</summary>
    [JsonPropertyName("devices")] public List<Desk> Devices { get; set; } = new();
    /// <summary>Where the list came from.</summary>
    [JsonPropertyName("sources")] public List<string> Sources { get; set; } = new();
    /// <summary>Notes about the list.</summary>
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();
    /// <summary>Who asked.</summary>
    [JsonPropertyName("identity")] public Identity? Identity { get; set; }
}

/// <summary>One online/offline transition.</summary>
public sealed class ReachEvent : GaiaDeskObject
{
    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("at")] public long At { get; set; }
    /// <summary>It came online (true) or went offline.</summary>
    [JsonPropertyName("online")] public bool Online { get; set; }
    /// <summary>registered, silent, closed, …</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    /// <summary>For a person.</summary>
    [JsonPropertyName("reason_text")] public string ReasonText { get; set; } = "";
    /// <summary>More detail.</summary>
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    /// <summary>Its GaiaDesk version then.</summary>
    [JsonPropertyName("version")] public string? Version { get; set; }
}

/// <summary><c>GET /desks/{id}/reach</c>: the transitions, newest first.</summary>
public sealed class ReachLog : GaiaDeskObject
{
    /// <summary>The desk.</summary>
    [JsonPropertyName("desk_id")] public string DeskId { get; set; } = "";
    /// <summary>Unix seconds the log starts.</summary>
    [JsonPropertyName("since")] public long Since { get; set; }
    /// <summary>The transitions.</summary>
    [JsonPropertyName("events")] public List<ReachEvent> Events { get; set; } = new();
}

/// <summary>What a wake rang.</summary>
public sealed class WakeRang : GaiaDeskObject
{
    /// <summary>Doorbell sockets rung.</summary>
    [JsonPropertyName("doorbell")] public int Doorbell { get; set; }
    /// <summary>Awake LAN siblings asked to Wake-on-LAN it.</summary>
    [JsonPropertyName("lan_helpers")] public int LanHelpers { get; set; }
}

/// <summary><c>POST /desks/{id}/wake</c>.</summary>
public sealed class WakeResult : GaiaDeskObject
{
    /// <summary>The desk.</summary>
    [JsonPropertyName("desk_id")] public string DeskId { get; set; } = "";
    /// <summary>Online now.</summary>
    [JsonPropertyName("online")] public bool Online { get; set; }
    /// <summary>It came online after this ring, within the wait.</summary>
    [JsonPropertyName("woke")] public bool Woke { get; set; }
    /// <summary>It was online already (not rung).</summary>
    [JsonPropertyName("already_online")] public bool AlreadyOnline { get; set; }
    /// <summary>What was rung.</summary>
    [JsonPropertyName("rang")] public WakeRang? Rang { get; set; }
    /// <summary>How long it waited.</summary>
    [JsonPropertyName("waited_ms")] public long WaitedMs { get; set; }
}

/// <summary>Who or what an audit event is about.</summary>
public sealed class AuditParty : GaiaDeskObject
{
    /// <summary>Its type.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }
    /// <summary>Its id.</summary>
    [JsonPropertyName("id")] public string? Id { get; set; }
    /// <summary>Its name.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
}

/// <summary>One audit event (<c>GET /audit</c>).</summary>
public sealed class AuditEvent : GaiaDeskObject
{
    /// <summary>Its id.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary><c>desk.session.start</c>, <c>agent.exec</c>, <c>api.wakeDesk</c>, …</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    /// <summary>session, agent, enterprise, api, support.</summary>
    [JsonPropertyName("stream")] public string Stream { get; set; } = "";
    /// <summary>When (Unix ms).</summary>
    [JsonPropertyName("occurred_at_ms")] public long OccurredAtMs { get; set; }
    /// <summary>Who did it.</summary>
    [JsonPropertyName("actor")] public AuditParty? Actor { get; set; }
    /// <summary>What it was done to.</summary>
    [JsonPropertyName("target")] public AuditParty? Target { get; set; }
    /// <summary>The event's details (never a body).</summary>
    [JsonPropertyName("metadata")] public JsonElement Metadata { get; set; }
}

internal sealed class AuditList : GaiaDeskObject
{
    [JsonPropertyName("events")] public List<AuditEvent>? Events { get; set; }
}

/// <summary>A webhook subscription (never its secret).</summary>
public class Webhook : GaiaDeskObject
{
    /// <summary><c>wh_…</c></summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>The https endpoint.</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>The events it hears (<see cref="WebhookEventTypes"/>).</summary>
    [JsonPropertyName("events")] public List<string> Events { get; set; } = new();
    /// <summary>Its description.</summary>
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
}

/// <summary><c>POST /webhooks</c>: the subscription and its signing secret (shown once).</summary>
public sealed class WebhookCreated : Webhook
{
    /// <summary>The signing secret (<c>whsec_…</c>). Shown once; keep it to verify deliveries.</summary>
    [JsonPropertyName("secret")] public string Secret { get; set; } = "";
}

internal sealed class WebhookList : GaiaDeskObject
{
    [JsonPropertyName("webhooks")] public List<Webhook>? Webhooks { get; set; }
}

/// <summary><c>DELETE /webhooks/{id}</c>.</summary>
public sealed class WebhookDeleted : GaiaDeskObject
{
    /// <summary>The deleted subscription's id.</summary>
    [JsonPropertyName("deleted")] public string Deleted { get; set; } = "";
}

/// <summary>A support session for the embed SDKs (<c>/support/sessions</c>).</summary>
public class SupportSession : GaiaDeskObject
{
    /// <summary><c>ss_…</c></summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>waiting, joined, ended, expired.</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "";
    /// <summary>view or cobrowse.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    /// <summary>Who the customer is, as the company (or a publishable key's page) said.</summary>
    [JsonPropertyName("customer")] public Dictionary<string, JsonElement>? Customer { get; set; }
    /// <summary>The customer's tab holds its connection now.</summary>
    [JsonPropertyName("customer_present")] public bool CustomerPresent { get; set; }
    /// <summary>False when a publishable key (the page itself) created it.</summary>
    [JsonPropertyName("customer_verified")] public bool CustomerVerified { get; set; }
    /// <summary>The nine digits an agent joins with.</summary>
    [JsonPropertyName("join_code")] public string JoinCode { get; set; } = "";
    /// <summary>The console page that joins it.</summary>
    [JsonPropertyName("join_url")] public string JoinUrl { get; set; } = "";
    /// <summary>While the customer is present, the id the console dials.</summary>
    [JsonPropertyName("desk_id")] public string? DeskId { get; set; }
    /// <summary>The page origin the embed must run on, if pinned.</summary>
    [JsonPropertyName("origin")] public string? Origin { get; set; }
    /// <summary>The account that created it.</summary>
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    /// <summary>Unix seconds it ends.</summary>
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; set; }
    /// <summary>Unix seconds an agent last joined.</summary>
    [JsonPropertyName("joined_at")] public long? JoinedAt { get; set; }
    /// <summary>The last agent who joined.</summary>
    [JsonPropertyName("joined_by")] public string? JoinedBy { get; set; }
    /// <summary>Unix seconds it ended.</summary>
    [JsonPropertyName("ended_at")] public long? EndedAt { get; set; }
    /// <summary>stopped, disconnected, expired.</summary>
    [JsonPropertyName("end_reason")] public string? EndReason { get; set; }
}

/// <summary><c>POST /support/sessions</c>: the session and the page's embed token (shown once).</summary>
public sealed class SupportSessionCreated : SupportSession
{
    /// <summary>The page's one credential for this session (<c>gdemb_…</c>). Shown once.</summary>
    [JsonPropertyName("embed_token")] public string EmbedToken { get; set; } = "";
}

internal sealed class SupportSessionList : GaiaDeskObject
{
    [JsonPropertyName("sessions")] public List<SupportSession>? Sessions { get; set; }
}
