// The one retry rule every GaiaDesk SDK follows: a request is sent again only
// when that cannot run anything twice. A connection that was never made, any
// method; a connection lost after sending or a 502/503/504, GETs only; a 429 or
// a 409 idempotency_key_in_flight (refused before anything ran), any method.
// Never a timeout, never once an answer has begun.

using System.Net.Sockets;
using System.Security.Authentication;

namespace GaiaDesk.Http;

internal static class RetryPolicy
{
    private static readonly HashSet<string> PermanentUnavailable = new(StringComparer.Ordinal) { "api_disabled", "desk_ops_disabled", "local_api_off" };

    /// <summary>Whether <paramref name="e"/>, the failure of one attempt at <paramref name="method"/>, may be sent again.</summary>
    public static bool Retryable(GaiaDeskException e, string method)
    {
        // The SDK's own timeouts (no HTTP status): the request may be running; never sent again.
        if (e.Status is null && (e.Kind == ErrorKinds.Timeout || e.Reason == "timeout")) return false;
        if (e.NeverConnected) return true;
        if (e.Status == 429 || e.Reason is Reasons.RateLimited or Reasons.DeskBusy) return true;
        if (e.Status == 409 && e.Reason == Reasons.IdempotencyKeyInFlight) return true;
        if (method != "GET") return false;
        return (e is UnreachableException && e.Status is null && e.Reason == "network")
            || e.Status is 502 or 504
            || (e.Status == 503 && (e.Reason is null || !PermanentUnavailable.Contains(e.Reason)));
    }

    /// <summary>
    /// The wait before retry <paramref name="attempt"/> (0 for the first), or null when it is not to be waited
    /// for (a <c>Retry-After</c> longer than <see cref="RetryOptions.MaxRetryWait"/>: the error is thrown at once).
    /// <paramref name="jitter"/> is a uniform random number in [0.5, 1.0].
    /// </summary>
    public static TimeSpan? Delay(RetryOptions o, GaiaDeskException e, int attempt, double jitter)
    {
        var honoursRetryAfter = e.Status is 429 or 503 || e.Reason is Reasons.RateLimited or Reasons.DeskBusy;
        if (honoursRetryAfter && e.RetryAfter is { } ra)
            return ra > o.MaxRetryWait ? null : ra < TimeSpan.Zero ? TimeSpan.Zero : ra;
        return Backoff(o, attempt, jitter);
    }

    private static readonly Random Random = new();

    /// <summary>A uniform random number in [0.5, 1.0]: what a back-off is multiplied by.</summary>
    public static double Jitter()
    {
        lock (Random) return 0.5 + Random.NextDouble() * 0.5;
    }

    /// <summary><c>min(MaxDelay, BaseDelay * 2^attempt) * jitter</c>.</summary>
    public static TimeSpan Backoff(RetryOptions o, int attempt, double jitter)
    {
        var ms = Math.Min(o.MaxDelay.TotalMilliseconds, o.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(ms * jitter);
    }

    // HttpRequestError (System.Net.Http, .NET 8+): the values this policy reads.
    private const int NameResolutionError = 1, ConnectionError = 2, SecureConnectionError = 3;

    /// <summary>
    /// Whether the HTTP stack failed before any byte of the request was written: name resolution, the TCP (or
    /// socket/pipe) connect, or the TLS handshake breaking off. Not a connect that timed out, and not a TLS
    /// certificate check that failed (both final).
    /// </summary>
    public static bool NeverConnected(HttpRequestException e) => NeverConnected(e, RequestError(e));

    internal static bool NeverConnected(HttpRequestException e, int? requestError)
    {
        if (Chain(e).Any(x => x is TimeoutException || x is SocketException { SocketErrorCode: SocketError.TimedOut })) return false;
        if (requestError is { } code)
            return code switch
            {
                NameResolutionError or ConnectionError => !Chain(e).Any(x => x is AuthenticationException),
                // The handshake broke off on the wire (an IO/socket failure), not a certificate refused.
                SecureConnectionError => Chain(e).Skip(1).Any(x => x is IOException or SocketException),
                _ => false,
            };
        // Runtimes before .NET 8 (the netstandard2.1 build on .NET Core 3.1–7): SocketsHttpHandler wraps the
        // connect step's SocketException (or a ConnectCallback's own exception) directly, while a connection
        // lost after sending arrives as an IOException. A TLS handshake that broke off cannot be told apart
        // from a lost connection there, so it is not retried.
        return e.InnerException is SocketException || e.InnerException is GaiaDeskException;
    }

    private static int? RequestError(HttpRequestException e)
    {
#if NET8_0_OR_GREATER
        return (int)e.HttpRequestError;
#else
        // The netstandard2.1 build running on .NET 8 or later still has it.
        var v = typeof(HttpRequestException).GetProperty("HttpRequestError")?.GetValue(e);
        return v is null ? null : Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
#endif
    }

    private static IEnumerable<Exception> Chain(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException) yield return x;
    }
}
