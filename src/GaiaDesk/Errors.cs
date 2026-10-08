// Typed errors, from the API's one error envelope:
// {"error": {"kind", "message", "reason"?, "desk"?, "request_id"}} — the
// object gaiadesk-cli --json prints, so every client branches the same way.
//
// The exception CLASS follows the envelope's kind (six: usage, refused,
// unreachable, connection_lost, failed, protocol); Kind is the finest known:
// the envelope's reason when it is one of the SDK's kinds (so `offline` stays
// `offline`), else its kind.

using System.Text.Json;

namespace GaiaDesk;

/// <summary>The kinds an error can have (<see cref="GaiaDeskException.Kind"/>). The first six are the API's own.</summary>
public static class ErrorKinds
{
    /// <summary>Bad arguments: fix the request.</summary>
    public const string Usage = "usage";
    /// <summary>The API or the desk said no (credentials, scopes, rate limits, the desk's settings).</summary>
    public const string Refused = "refused";
    /// <summary>The desk (or the API) could not be reached.</summary>
    public const string Unreachable = "unreachable";
    /// <summary>The desk went away mid-operation.</summary>
    public const string ConnectionLost = "connection_lost";
    /// <summary>Allowed, ran, and did not succeed.</summary>
    public const string Failed = "failed";
    /// <summary>An answer this SDK cannot read, or a desk too old for the request.</summary>
    public const string Protocol = "protocol";
    /// <summary>No connection to the API at all (reason <c>network</c>).</summary>
    public const string Network = "network";
    /// <summary>The desk is offline.</summary>
    public const string Offline = "offline";
    /// <summary>No such desk on the account or team.</summary>
    public const string UnknownDesk = "unknown_desk";
    /// <summary>Not online.</summary>
    public const string NotOnline = "not_online";
    /// <summary>Not signed in.</summary>
    public const string NotSignedIn = "not_signed_in";
    /// <summary>A request ran out of time.</summary>
    public const string Timeout = "timeout";
    /// <summary>Something on this machine (a local file that could not be read or written).</summary>
    public const string Local = "local";

    internal static readonly HashSet<string> Sdk = new(StringComparer.Ordinal)
    {
        Usage, Offline, UnknownDesk, NotOnline, Refused, Network, NotSignedIn, Timeout,
        ConnectionLost, Local, Failed, "interrupted", Protocol, Unreachable,
    };

    /// <summary>The SDK kind for an envelope's kind and reason: the reason when it is an SDK kind, else the kind.</summary>
    public static string For(string kind, string? reason) =>
        reason is not null && Sdk.Contains(reason) ? reason : kind;
}

/// <summary>The finer causes (<see cref="GaiaDeskException.Reason"/>) callers most often branch on. Others pass through as sent.</summary>
public static class Reasons
{
    /// <summary>Over the API key's rate limit (429, with <see cref="GaiaDeskException.RetryAfter"/>).</summary>
    public const string RateLimited = "rate_limited";
    /// <summary>The desk already runs 16 API operations (429).</summary>
    public const string DeskBusy = "desk_busy";
    /// <summary>No credential, or one that does not verify (401).</summary>
    public const string Unauthenticated = "unauthenticated";
    /// <summary>The API key lacks the scope (403).</summary>
    public const string MissingScope = "missing_scope";
    /// <summary>The desk's owner turned off "Allow commands from the GaiaDesk API" (403).</summary>
    public const string DeskOptedOut = "desk_opted_out";
    /// <summary>Token administration with an agent token (403).</summary>
    public const string AgentCannotAdmin = "agent_cannot_admin";
    /// <summary>The desk runs a GaiaDesk from before desk operations (409 protocol).</summary>
    public const string DeskTooOld = "desk_too_old";
    /// <summary>A sleeping desk with nothing to ring (409).</summary>
    public const string NoWakePath = "no_wake_path";
    /// <summary>The same Idempotency-Key on a different request (400).</summary>
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    /// <summary>The first request with this Idempotency-Key is still running: retry shortly.</summary>
    public const string IdempotencyKeyInFlight = "idempotency_key_in_flight";
    /// <summary>A file larger than the API moves (256 MB).</summary>
    public const string TooLarge = "too_large";
    /// <summary>A route this API does not serve (the local API and LAN gateway serve desk operations only).</summary>
    public const string NoSuchRoute = "no_such_route";

    /// <summary>The desk requires end-to-end encryption and the call was plaintext (409).</summary>
    public const string E2eRequired = "e2e_required";
    /// <summary>The SDK would not send the operation in the clear and there is no key for the desk (<see cref="E2eException"/>).</summary>
    public const string E2eUnavailable = "e2e_unavailable";
    /// <summary>The server listed a different key than the pinned one (<see cref="E2eException"/>).</summary>
    public const string E2eKeyMismatch = "e2e_key_mismatch";
    /// <summary>A sealed message did not open: altered, reordered, or sealed for another desk, operation or key.</summary>
    public const string E2eDecryptFailed = "e2e_decrypt_failed";
    /// <summary>A sealed message is malformed.</summary>
    public const string E2eMalformed = "e2e_malformed";
    /// <summary>A plaintext answer to a sealed call (a hostile or broken server).</summary>
    public const string E2eUnsealedAnswer = "e2e_unsealed_answer";
    /// <summary>The desk cannot open sealed operations (409 protocol).</summary>
    public const string E2eUnsupported = "e2e_unsupported";

    /// <summary>Exec <c>Admin = true</c>: the token has no <c>admin</c> scope (or it is a person's call).</summary>
    public const string AdminScopeMissing = "admin_scope_missing";
    /// <summary>Exec <c>Admin = true</c>: Admin access is off on the desk.</summary>
    public const string AdminNotEnabled = "admin_not_enabled";
    /// <summary>Exec <c>Admin = true</c>: the person at the desk said no, nobody answered, or nobody was there.</summary>
    public const string AdminDenied = "admin_denied";
    /// <summary>Exec <c>Admin = true</c>: no privileged process on the desk, or a desk too old for the field.</summary>
    public const string AdminUnavailable = "admin_unavailable";
    /// <summary>Windows Smart App Control / WDAC refused the program (administrator does not get past it).</summary>
    public const string BlockedByOsPolicy = "blocked_by_os_policy";

    /// <summary>The local API is not being served here (no socket or pipe, or no admin token).</summary>
    public const string LocalApiUnavailable = "local_api_unavailable";
    /// <summary>The LAN gateway's certificate did not match the pinned fingerprint.</summary>
    public const string FingerprintMismatch = "fingerprint_mismatch";
}

/// <summary>
/// Every failure the SDK reports. The class follows the error's kind
/// (<see cref="UsageException"/>, <see cref="RefusedException"/>, <see cref="UnreachableException"/>,
/// <see cref="ConnectionLostException"/>, <see cref="OperationFailedException"/>, <see cref="ProtocolException"/>);
/// <see cref="Kind"/> and <see cref="Reason"/> say more. Cancelling a call's
/// <see cref="CancellationToken"/> throws <see cref="OperationCanceledException"/> instead.
/// </summary>
public class GaiaDeskException : Exception
{
    /// <summary>Creates the error.</summary>
    public GaiaDeskException(string message, ErrorDetails? details = null, Exception? inner = null)
        : base(message, inner)
    {
        var d = details ?? new ErrorDetails();
        Kind = d.Kind ?? "error";
        Reason = d.Reason;
        Desk = d.Desk;
        ExitCode = d.ExitCode;
        Status = d.Status;
        RequestId = d.RequestId;
        RetryAfter = d.RetryAfter;
        Operation = d.Operation;
        Json = d.Json;
    }

    /// <summary>The finest kind known: the reason when it is one of <see cref="ErrorKinds"/>, else the error's kind.</summary>
    public string Kind { get; }
    /// <summary>The finer cause (<c>offline</c>, <c>missing_scope</c>, <c>e2e_required</c>, <c>admin_denied</c>, …), or null.</summary>
    public string? Reason { get; }
    /// <summary>The desk the error concerned, when it was said.</summary>
    public string? Desk { get; }
    /// <summary>What gaiadesk-cli would exit with for this failure (1 failed, 254 refused, 255 the rest; a command's own code for exec).</summary>
    public int? ExitCode { get; }
    /// <summary>The HTTP status of the failed request (a held answer: the status it would have had), or null.</summary>
    public int? Status { get; }
    /// <summary>The request's id (<c>req_…</c>): quote it to support.</summary>
    public string? RequestId { get; }
    /// <summary>How long to wait before retrying (a 429's <c>Retry-After</c>), or null.</summary>
    public TimeSpan? RetryAfter { get; }
    /// <summary>The operation that failed, as <c>METHOD /path</c>.</summary>
    public string? Operation { get; internal set; }
    /// <summary>The answer's JSON (the error envelope), when there was one.</summary>
    public JsonElement? Json { get; internal set; }
}

/// <summary>The details an error is built from.</summary>
public sealed class ErrorDetails
{
    /// <summary>See <see cref="GaiaDeskException.Kind"/>.</summary>
    public string? Kind { get; set; }
    /// <summary>See <see cref="GaiaDeskException.Reason"/>.</summary>
    public string? Reason { get; set; }
    /// <summary>See <see cref="GaiaDeskException.Desk"/>.</summary>
    public string? Desk { get; set; }
    /// <summary>See <see cref="GaiaDeskException.ExitCode"/>.</summary>
    public int? ExitCode { get; set; }
    /// <summary>See <see cref="GaiaDeskException.Status"/>.</summary>
    public int? Status { get; set; }
    /// <summary>See <see cref="GaiaDeskException.RequestId"/>.</summary>
    public string? RequestId { get; set; }
    /// <summary>See <see cref="GaiaDeskException.RetryAfter"/>.</summary>
    public TimeSpan? RetryAfter { get; set; }
    /// <summary>See <see cref="GaiaDeskException.Operation"/>.</summary>
    public string? Operation { get; set; }
    /// <summary>See <see cref="GaiaDeskException.Json"/>.</summary>
    public JsonElement? Json { get; set; }
}

/// <summary>Bad arguments (kind <c>usage</c>), caught by the SDK before anything is sent, or by the API (400).</summary>
public class UsageException : GaiaDeskException
{
    /// <summary>Creates the error.</summary>
    public UsageException(string message, ErrorDetails? details = null, Exception? inner = null)
        : base(message, WithKind(details, ErrorKinds.Usage), inner) { }

    internal static ErrorDetails WithKind(ErrorDetails? d, string kind)
    {
        d ??= new ErrorDetails();
        d.Kind ??= kind;
        return d;
    }
}

/// <summary>The API or the desk said no (exit 254): credentials, a missing scope, rate limits, a desk setting, an admin refusal.</summary>
public class RefusedException : GaiaDeskException
{
    /// <summary>Creates the error.</summary>
    public RefusedException(string message, ErrorDetails? details = null, Exception? inner = null)
        : base(message, UsageException.WithKind(details, ErrorKinds.Refused), inner) { }
}

/// <summary>The desk or the API could not be reached: offline, unknown desk, no network, a timeout.</summary>
public class UnreachableException : GaiaDeskException
{
    /// <summary>Creates the error.</summary>
    public UnreachableException(string message, ErrorDetails? details = null, Exception? inner = null)
        : base(message, UsageException.WithKind(details, ErrorKinds.Unreachable), inner) { }
}

/// <summary>The connection to the desk went away mid-operation (kind <c>connection_lost</c>).</summary>
public class ConnectionLostException : GaiaDeskException
{
    /// <summary>Creates the error.</summary>
    public ConnectionLostException(string message, ErrorDetails? details = null, Exception? inner = null)
        : base(message, UsageException.WithKind(details, ErrorKinds.ConnectionLost), inner) { }
}

/// <summary>A desk operation ran and did not succeed (exit 1): no such job, a file that failed to copy, …</summary>
public class OperationFailedException : GaiaDeskException
{
    /// <summary>Creates the error.</summary>
    public OperationFailedException(string message, ErrorDetails? details = null, Exception? inner = null)
        : base(message, UsageException.WithKind(details, ErrorKinds.Failed), inner) { }
}

/// <summary>An answer that is not what the API documents, a sealed answer that does not open, or a desk too old for the request.</summary>
public class ProtocolException : GaiaDeskException
{
    /// <summary>Creates the error.</summary>
    public ProtocolException(string message, ErrorDetails? details = null, Exception? inner = null)
        : base(message, UsageException.WithKind(details, ErrorKinds.Protocol), inner) { }
}

/// <summary>
/// End-to-end encryption: the SDK would not send the operation in the clear
/// (<see cref="Reasons.E2eUnavailable"/>: <see cref="E2eMode.Require"/>, or a desk that requires it,
/// and no key for the desk) or the server handed out a key other than the pinned one
/// (<see cref="Reasons.E2eKeyMismatch"/>). Nothing was sent to the desk.
/// </summary>
public class E2eException : RefusedException
{
    /// <summary>Creates the error.</summary>
    public E2eException(string message, ErrorDetails? details = null) : base(message, details) { }
}

/// <summary>The LAN gateway's certificate did not match the pinned fingerprint: it may not be your desk. Do not proceed.</summary>
public sealed class FingerprintMismatchException : UnreachableException
{
    /// <summary>Creates the error.</summary>
    public FingerprintMismatchException(string message, string expected, string actual, ErrorDetails? details = null)
        : base(message, details)
    {
        Expected = expected;
        Actual = actual;
    }

    /// <summary>The pinned fingerprint (<c>ab:cd:…</c>).</summary>
    public string Expected { get; }
    /// <summary>The fingerprint the server presented (<c>ab:cd:…</c>, or empty).</summary>
    public string Actual { get; }
}

/// <summary><c>ExecAsync</c> with <c>Check = true</c>: the command exited non-zero (or timed out).</summary>
public sealed class CommandException : GaiaDeskException
{
    /// <summary>Creates the error.</summary>
    public CommandException(string message, ExecResult result, ErrorDetails? details = null)
        : base(message, UsageException.WithKind(details, ErrorKinds.Failed))
    {
        Result = result;
    }

    /// <summary>The command's result (its output, exit and duration).</summary>
    public ExecResult Result { get; }
}

/// <summary>The API's error object: <c>{"kind", "message", "reason"?, "desk"?}</c>.</summary>
public sealed class ErrorEnvelope
{
    /// <summary>One of the six: usage, refused, unreachable, connection_lost, failed, protocol.</summary>
    public string Kind { get; set; } = "";
    /// <summary>What went wrong, for a person.</summary>
    public string Message { get; set; } = "";
    /// <summary>The finer cause, when there is one.</summary>
    public string? Reason { get; set; }
    /// <summary>The desk it concerned, when there was one.</summary>
    public string? Desk { get; set; }
    /// <summary>The request's id, when the API gave one.</summary>
    public string? RequestId { get; set; }
    /// <summary>A held answer's failure: the HTTP status it would have had.</summary>
    public int? Status { get; set; }

    /// <summary>
    /// The envelope in <paramref name="json"/> (<c>{"error": {"kind", …}}</c>), or null when it is not one
    /// (including exec's own <c>"error": null</c> on success).
    /// </summary>
    public static ErrorEnvelope? From(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("error", out var e)) return null;
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String) return null;
        var env = new ErrorEnvelope { Kind = k.GetString()!, Message = Str(e, "message") ?? "" };
        var reason = Str(e, "reason");
        if (!string.IsNullOrEmpty(reason)) env.Reason = reason;
        var desk = Str(e, "desk");
        if (!string.IsNullOrEmpty(desk)) env.Desk = desk;
        env.RequestId = Str(e, "request_id");
        if (e.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var st)) env.Status = st;
        return env;
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

internal static class Errors
{
    /// <summary>gaiadesk-cli's exit code for a desk operation that failed with this kind.</summary>
    public static int DeskOpExit(string kind) => kind switch
    {
        ErrorKinds.Refused => 254,
        ErrorKinds.Failed => 1,
        "interrupted" => 130,
        _ => 255,
    };

    /// <summary>The exception class for an error's kind (one of the six); <c>details.Kind</c> defaults to <paramref name="kind"/>.</summary>
    public static GaiaDeskException ForKind(string kind, string message, ErrorDetails details)
    {
        details.Kind ??= kind;
        return kind switch
        {
            ErrorKinds.Usage => new UsageException(message, details),
            ErrorKinds.Refused => new RefusedException(message, details),
            ErrorKinds.ConnectionLost => new ConnectionLostException(message, details),
            ErrorKinds.Failed => new OperationFailedException(message, details),
            ErrorKinds.Protocol => new ProtocolException(message, details),
            ErrorKinds.Unreachable => new UnreachableException(message, details),
            _ => new GaiaDeskException(message, details),
        };
    }

    /// <summary>The typed error for an envelope.</summary>
    public static GaiaDeskException FromEnvelope(ErrorEnvelope env, string fallback, ErrorDetails details)
    {
        details.Kind = ErrorKinds.For(env.Kind, env.Reason);
        details.Reason ??= env.Reason;
        details.Desk ??= env.Desk;
        details.RequestId ??= env.RequestId;
        details.Status ??= env.Status;
        return ForKind(env.Kind, string.IsNullOrEmpty(env.Message) ? fallback : env.Message, details);
    }

    public static UsageException Usage(string message, string? operation = null) =>
        new(message, new ErrorDetails { Kind = ErrorKinds.Usage, Operation = operation });
}
