// The `lan` transport: a desk's opt-in LAN gateway, `https://<desk>:7443/v1`,
// serving the same /v1 desk operations as the hosted API. Its certificate is
// self-signed, so the chain and the host name cannot be checked: the SHA-256
// of the certificate is pinned instead (the fingerprint the desk shows in
// Settings), and checked during the TLS handshake, before any byte of the
// request is written. The gateway takes agent tokens only.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using GaiaDesk.Http;

namespace GaiaDesk;

/// <summary>Helpers for a desk's LAN gateway.</summary>
public static class LanGateway
{
    private static readonly Regex Prefix = new(@"^sha-?256[:=\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A SHA-256 certificate fingerprint as the desk shows it: 32 lowercase hex pairs joined by <c>:</c>. Takes it with or
    /// without colons (or spaces), any case, an optional <c>sha256:</c> prefix; anything else is a <see cref="UsageException"/>.
    /// </summary>
    public static string NormalizeFingerprint(string fingerprint)
    {
        if (fingerprint is null) throw Errors.Usage("fingerprint must be a string (the SHA-256 the desk shows, ab:cd:…)");
        var hex = Prefix.Replace(fingerprint.Trim(), "").Replace(":", "").Replace(" ", "").Replace("\t", "").ToLowerInvariant();
        if (hex.Length != 64 || hex.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Errors.Usage($"fingerprint must be the certificate's SHA-256: 32 hex pairs (ab:cd:…), not \"{fingerprint}\"");
        return string.Join(":", Enumerable.Range(0, 32).Select(i => hex.Substring(i * 2, 2)));
    }

    /// <summary>The fingerprint of a certificate, as <see cref="NormalizeFingerprint"/> writes it.</summary>
    public static string FingerprintOf(X509Certificate certificate)
    {
        using var sha = SHA256.Create();
        return NormalizeFingerprint(E2e.E2eCrypto.ToHex(sha.ComputeHash(certificate.GetRawCertData())));
    }
}

public sealed partial class GaiaDeskClient
{
    /// <summary>
    /// A desk's LAN gateway over pinned TLS: the certificate's SHA-256 must be <see cref="LanOptions.Fingerprint"/>, else
    /// a <see cref="FingerprintMismatchException"/> and nothing is sent. Agent tokens only. Desk operations and
    /// <c>ListDesksAsync</c> (this desk, and the paired desks it reaches on its LAN); never sealed.
    /// </summary>
    public static GaiaDeskClient Lan(LanOptions options)
    {
        if (options is null) throw Errors.Usage("options are required");
        var baseUrl = options.BaseUrl?.TrimEnd('/');
        if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host.Length == 0)
            throw Errors.Usage($"the lan transport needs an https:// BaseUrl (https://<desk>:7443/v1), not \"{options.BaseUrl}\"");
        if (options.Fingerprint is null) throw Errors.Usage("the lan transport needs the gateway certificate's Fingerprint (Settings → GaiaDesk API → LAN gateway)");
        var pinned = LanGateway.NormalizeFingerprint(options.Fingerprint);
        var deskToken = Check.OptionalToken(options.DeskToken);
        var origin = u.Authority;
        var seen = new ConditionalWeakTable<HttpRequestMessage, string>();

        var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (req, cert, _, _) =>
            {
                var actual = cert is null ? "" : LanGateway.FingerprintOf(cert);
                if (actual == pinned) return true;
                seen.Remove(req);
                seen.Add(req, actual);
                return false;
            },
        };

        GaiaDeskException? ConnectError(HttpRequestMessage msg, HttpRequestException e)
        {
            if (seen.TryGetValue(msg, out var actual))
                return new FingerprintMismatchException(
                    $"the desk at {origin} did not prove the pinned identity: its certificate's SHA-256 is {(actual.Length == 0 ? "(none)" : actual)}, not {pinned}. " +
                    "Do not proceed: this may not be your desk. Check the fingerprint in its Settings → GaiaDesk API.",
                    pinned, actual, new ErrorDetails { Kind = ErrorKinds.Unreachable, Reason = Reasons.FingerprintMismatch, ExitCode = 255 });
            var why = e.InnerException?.Message ?? e.Message;
            return new UnreachableException($"the desk's LAN gateway ({origin}) could not be reached: {why}",
                new ErrorDetails { Kind = ErrorKinds.Network, Reason = "network", ExitCode = 255 }, e);
        }

        Task<Dictionary<string, string>> Credentials(string? callToken, CancellationToken ct)
        {
            var t = callToken ?? deskToken;
            if (t is null) throw Errors.Usage("the lan transport needs an agent token (DeskToken, gdagt_…): a desk's LAN gateway does not take its admin token");
            return Task.FromResult(new Dictionary<string, string> { ["X-GaiaDesk-Desk-Token"] = t });
        }

        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new GaiaDeskClient(new HttpCore(TransportKind.Lan, baseUrl, $"the desk's LAN gateway ({origin})", http, true, Credentials, options.Retry, null, options.Timeouts, ConnectError));
    }
}
