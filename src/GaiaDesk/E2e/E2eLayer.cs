// End-to-end encryption on the hosted API: whether and to which key an
// operation is sealed (the desk's `e2e_pub` from `GET /desks/{id}`, cached;
// pinned keys; the mode; a wake when a desk that must be sealed to lists no
// key), and the one retry each for `e2e_required` and `e2e_decrypt_failed`.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GaiaDesk.E2e;

/// <summary>An operation, sealed: its request envelope, and the seal for its input and events.</summary>
internal sealed record Sealed(SealedRequest Request, CallerSeal Seal);

/// <summary>What the layer needs to call the API for its own lookups: method, path, desk token, JSON body.</summary>
internal delegate Task<JsonElement> ApiJson(string method, string path, string? deskToken, JsonNode? json, CancellationToken ct);

internal sealed class E2eLayer
{
    private static readonly TimeSpan KeyTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NoKeyTtl = TimeSpan.FromSeconds(30);
    private const int DefaultWakeSeconds = 30;
    private static readonly ConcurrentDictionary<string, bool> Warned = new(StringComparer.Ordinal);

    private sealed record KeyInfo(byte[]? Pub, bool Required, string Why);

    private readonly Dictionary<string, byte[]> _pins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime At, KeyInfo Info)> _cache = new(StringComparer.Ordinal);
    private readonly ApiJson _api;
    private readonly string _baseUrl;
    private readonly Action<string> _warn;

    public E2eLayer(E2eMode mode, IDictionary<string, string>? pins, Action<string>? onWarning, ApiJson api, string baseUrl)
    {
        if (!Enum.IsDefined(typeof(E2eMode), mode)) throw Errors.Usage($"e2e is Auto, Require or Off (not {(int)mode})");
        Mode = mode;
        foreach (var kv in pins ?? new Dictionary<string, string>())
        {
            var k = E2eCrypto.DeskKey(kv.Value) ?? throw Errors.Usage($"E2eKeys[\"{kv.Key}\"] is not a 32-byte base64url X25519 key");
            _pins[Check.Desk(kv.Key)] = k;
        }
        _api = api;
        _baseUrl = baseUrl;
        _warn = onWarning ?? (m => Console.Error.WriteLine(m));
    }

    public E2eMode Mode { get; }

    /// <summary>Forget what the lookup said about <paramref name="desk"/> (its key may have rotated).</summary>
    public void Forget(string desk) => _cache.TryRemove(desk, out _);

    private async Task<KeyInfo> Info(string desk, string? deskToken, bool fresh, CancellationToken ct)
    {
        if (!fresh && _cache.TryGetValue(desk, out var hit) && DateTime.UtcNow - hit.At < (hit.Info.Pub is null ? NoKeyTtl : KeyTtl)) return hit.Info;
        JsonElement d;
        try
        {
            d = await _api("GET", $"/desks/{Uri.EscapeDataString(desk)}", deskToken, null, ct).ConfigureAwait(false);
        }
        catch (GaiaDeskException e)
        {
            return new KeyInfo(null, false, $"its key could not be read (GET /desks/{desk}: {e.Message})");
        }
        var pub = E2eCrypto.DeskKey(GaiaDeskJson.Str(d, "e2e_pub"));
        var required = GaiaDeskJson.TryProp(d, "e2e_required", out var r) && r.ValueKind == JsonValueKind.True;
        var offline = GaiaDeskJson.TryProp(d, "online", out var on) && on.ValueKind == JsonValueKind.False;
        var why = pub is not null ? "" : offline
            ? "it is offline, and lists its key only while online"
            : "it lists no end-to-end key (a GaiaDesk from before end-to-end encryption?)";
        var info = new KeyInfo(pub, required, why);
        _cache[desk] = (DateTime.UtcNow, info);
        return info;
    }

    /// <summary>The server's key for <paramref name="desk"/>, refused when a pinned key differs.</summary>
    private byte[]? Checked(string desk, KeyInfo info)
    {
        _pins.TryGetValue(desk, out var pin);
        if (info.Pub is not null && pin is not null && !info.Pub.AsSpan().SequenceEqual(pin))
        {
            Forget(desk);
            throw new E2eException($"the GaiaDesk API lists a different end-to-end key for desk {desk} than the pinned one (E2eKeys); nothing was sent",
                new ErrorDetails { Kind = ErrorKinds.Refused, Reason = Reasons.E2eKeyMismatch, Desk = desk, ExitCode = 254 });
        }
        return info.Pub ?? pin;
    }

    /// <summary>
    /// The key to seal <paramref name="desk"/>'s next operation to, or null to send it in the clear
    /// (Auto, no key: warned once). <paramref name="insist"/>: it must be sealed (the API said
    /// <c>e2e_required</c>). A desk that must be sealed to and lists no key is woken and asked again.
    /// </summary>
    public async Task<byte[]?> Key(string desk, string? deskToken, int? wake, bool insist, CancellationToken ct)
    {
        var info = await Info(desk, deskToken, insist, ct).ConfigureAwait(false);
        if (Checked(desk, info) is { } pub) return pub;
        if (Mode != E2eMode.Require && !info.Required && !insist)
        {
            if (Warned.TryAdd($"{_baseUrl} {desk}", true))
                _warn($"GaiaDesk: operations on desk {desk} are not end-to-end encrypted: {info.Why}. The API relays them in the clear (set E2e = E2eMode.Require to refuse that).");
            return null;
        }
        try
        {
            await _api("POST", $"/desks/{Uri.EscapeDataString(desk)}/wake", deskToken, new JsonObject { ["wait_s"] = Math.Min(90, wake ?? DefaultWakeSeconds) }, ct).ConfigureAwait(false);
        }
        catch (GaiaDeskException) { /* nothing to ring: asked again below */ }
        info = await Info(desk, deskToken, true, ct).ConfigureAwait(false);
        if (Checked(desk, info) is { } woke) return woke;
        throw new E2eException($"desk {desk} must be reached end-to-end encrypted, but {info.Why}; nothing was sent",
            new ErrorDetails { Kind = ErrorKinds.Refused, Reason = Reasons.E2eUnavailable, Desk = desk, ExitCode = 254 });
    }

    /// <summary>
    /// Run one desk operation: <paramref name="attempt"/> sends it (sealed, or in the clear when given null).
    /// A plaintext call the API refuses <c>e2e_required</c> is sealed and sent again; a sealed one the desk
    /// could not open (<c>e2e_decrypt_failed</c>: its key rotated) is sealed to the key asked for again, once.
    /// </summary>
    public async Task<T> Call<T>(string desk, string op, JsonObject request, string? deskToken, int? wake, Func<Sealed?, Task<T>> attempt, CancellationToken ct)
    {
        if (Mode == E2eMode.Off) return await attempt(null).ConfigureAwait(false);
        Sealed Seal(byte[] pub)
        {
            var (r, s) = E2eCrypto.SealRequest(pub, desk, op, request);
            return new Sealed(r, s);
        }
        var key = await Key(desk, deskToken, wake, false, ct).ConfigureAwait(false);
        try
        {
            return await attempt(key is null ? null : Seal(key)).ConfigureAwait(false);
        }
        catch (RefusedException e) when (key is null && e.Reason == Reasons.E2eRequired)
        {
            Forget(desk);
            var k = await Key(desk, deskToken, wake, true, ct).ConfigureAwait(false);
            return await attempt(Seal(k!)).ConfigureAwait(false);
        }
        catch (RefusedException e) when (key is not null && e is not E2eException && e.Reason == Reasons.E2eDecryptFailed)
        {
            Forget(desk);
            var again = await Key(desk, deskToken, wake, false, ct).ConfigureAwait(false);
            if (again is null) throw;
            return await attempt(Seal(again)).ConfigureAwait(false);
        }
    }
}
