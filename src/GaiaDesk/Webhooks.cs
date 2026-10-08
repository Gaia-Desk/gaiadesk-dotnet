// Webhook deliveries: verifying their signature and reading them.
//
//   GaiaDesk-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256 of "<t>.<raw body>" keyed with the secret>
//
// Recompute over the raw bytes, compare in constant time, reject a `t` more
// than five minutes from now, and de-duplicate by the event id.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GaiaDesk;

/// <summary>Verifies (and, for tests, makes) a webhook delivery's <c>GaiaDesk-Signature</c>.</summary>
public static class WebhookSignature
{
    /// <summary>The header that carries the signature.</summary>
    public const string HeaderName = "GaiaDesk-Signature";
    /// <summary>The header that carries the event id (stable across retries).</summary>
    public const string EventIdHeader = "GaiaDesk-Event-Id";
    /// <summary>The header that carries the event type.</summary>
    public const string EventTypeHeader = "GaiaDesk-Event-Type";
    /// <summary>How far a delivery's <c>t</c> may be from now (five minutes).</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True when <paramref name="signatureHeader"/> is a valid signature of <paramref name="rawBody"/> (the exact bytes
    /// received) under <paramref name="secret"/> (<c>whsec_…</c>), made within <paramref name="tolerance"/> of
    /// <paramref name="now"/>.
    /// </summary>
    public static bool Verify(string secret, string? signatureHeader, byte[] rawBody, DateTimeOffset? now = null, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader) || rawBody is null) return false;
        long? t = null;
        string? v1 = null;
        foreach (var part in signatureHeader!.Split(','))
        {
            var i = part.IndexOf('=');
            if (i <= 0) continue;
            var k = part.Substring(0, i).Trim();
            var v = part.Substring(i + 1).Trim();
            if (k == "t" && long.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var tv)) t = tv;
            else if (k == "v1") v1 = v;
        }
        if (t is null || v1 is null || v1.Length != 64) return false;
        var nowS = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        if (Math.Abs(nowS - t.Value) > (long)(tolerance ?? DefaultTolerance).TotalSeconds) return false;
        byte[] got;
        try { got = E2e.E2eCrypto.Hex(v1); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(Mac(secret, t.Value, rawBody), got);
    }

    /// <summary>The same, for a body received as a string (it is verified as its UTF-8 bytes).</summary>
    public static bool Verify(string secret, string? signatureHeader, string rawBody, DateTimeOffset? now = null, TimeSpan? tolerance = null) =>
        Verify(secret, signatureHeader, Encoding.UTF8.GetBytes(rawBody ?? ""), now, tolerance);

    /// <summary>The <c>GaiaDesk-Signature</c> value for a body at a time: what the API sends (for tests of your endpoint).</summary>
    public static string Sign(string secret, byte[] rawBody, DateTimeOffset timestamp)
    {
        var t = timestamp.ToUnixTimeSeconds();
        return $"t={t},v1={E2e.E2eCrypto.ToHex(Mac(secret, t, rawBody))}";
    }

    private static byte[] Mac(string secret, long t, byte[] body)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var prefix = Encoding.UTF8.GetBytes($"{t.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        h.TransformBlock(prefix, 0, prefix.Length, null, 0);
        h.TransformFinalBlock(body, 0, body.Length);
        return h.Hash!;
    }
}

/// <summary>The desk a delivery is about.</summary>
public sealed class WebhookDesk : GaiaDeskObject
{
    /// <summary>Its id.</summary>
    [JsonPropertyName("desk_id")] public string DeskId { get; set; } = "";
    /// <summary>Its owner.</summary>
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    /// <summary>The reach log's reason (registered, silent, closed, …).</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    /// <summary>The reason, for a person.</summary>
    [JsonPropertyName("reason_text")] public string? ReasonText { get; set; }
    /// <summary>Its GaiaDesk version.</summary>
    [JsonPropertyName("version")] public string? Version { get; set; }
    /// <summary><c>desk.woke</c>: how long after the ring it came online.</summary>
    [JsonPropertyName("woke_after_ms")] public long? WokeAfterMs { get; set; }
}

/// <summary>One webhook delivery (<c>desk.online</c>, <c>desk.offline</c>, <c>desk.woke</c>, <c>job.finished</c>, <c>support.session.*</c>).</summary>
public sealed class WebhookEvent : GaiaDeskObject
{
    /// <summary><c>evt_…</c>: stable across retries; de-duplicate by it.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>The event type (<see cref="WebhookEventTypes"/>).</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("created")] public long Created { get; set; }
    /// <summary>The event's data, as sent.</summary>
    [JsonPropertyName("data")] public JsonElement Data { get; set; }

    /// <summary>The desk it is about (desk.* and job.finished).</summary>
    [JsonIgnore]
    public WebhookDesk? Desk => Prop<WebhookDesk>("desk");

    /// <summary><c>job.finished</c>: the job as it ended.</summary>
    [JsonIgnore]
    public Job? Job => Prop<Job>("job");

    /// <summary><c>support.session.*</c>: the session (never its join code or embed token).</summary>
    [JsonIgnore]
    public SupportSession? SupportSession => Prop<SupportSession>("support_session");

    private T? Prop<T>(string name) where T : class =>
        Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v.Deserialize<T>(GaiaDeskJson.Options) : null;

    /// <summary>A delivery's body. Verify it first (<see cref="WebhookSignature.Verify(string, string?, byte[], DateTimeOffset?, TimeSpan?)"/>).</summary>
    public static WebhookEvent Parse(byte[] rawBody) => Parse(Encoding.UTF8.GetString(rawBody));

    /// <summary>A delivery's body, as text.</summary>
    public static WebhookEvent Parse(string rawBody)
    {
        try
        {
            return JsonSerializer.Deserialize<WebhookEvent>(rawBody, GaiaDeskJson.Options) ?? throw new JsonException("null");
        }
        catch (JsonException e)
        {
            throw new ProtocolException($"not a GaiaDesk webhook delivery: {e.Message}", new ErrorDetails { ExitCode = 255 });
        }
    }
}
