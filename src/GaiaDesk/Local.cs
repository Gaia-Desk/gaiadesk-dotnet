// The `local` transport: code running ON the desk talks to the GaiaDesk app's
// own /v1 API over a Unix socket (macOS, Linux) or a named pipe (Windows).
// Same operations, results and errors as the hosted API; only the connection
// and the credentials differ:
//
//   socket  $GAIADESK_API_DIR/api.sock, else ~/.gaiadesk/api.sock
//   pipe    $GAIADESK_API_PIPE, else \\.\pipe\gaiadesk-api-<user>
//   token   an agent token (DeskToken) as X-GaiaDesk-Desk-Token, else the
//           desk's local admin token (gdlocal_…, $GAIADESK_API_DIR/api-token,
//           else ~/.gaiadesk/api-token) as `Authorization: Bearer`.

using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using GaiaDesk.Http;
#if NET5_0_OR_GREATER
using System.IO.Pipes;
using System.Net.Sockets;
#endif

namespace GaiaDesk;

/// <summary>Where the desk's local API lives.</summary>
public static class LocalApi
{
    /// <summary>What a missing socket or pipe means, as the error says it.</summary>
    public const string Unavailable = "GaiaDesk is not serving its local API here: is the app running, and is Settings → GaiaDesk API → Local API on?";

    /// <summary>The pipe-name form of a user name: lowercased, <c>[a-z0-9._-]</c> kept, the rest <c>_</c>, at most 64 characters, <c>user</c> if empty.</summary>
    public static string PipeUser(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in (name ?? "").ToLowerInvariant())
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' ? c : '_');
        var s = sb.ToString();
        if (s.Length > 64) s = s.Substring(0, 64);
        return s.Length == 0 ? "user" : s;
    }

    /// <summary>The local API's Windows pipe: <c>$GAIADESK_API_PIPE</c>, else <c>\\.\pipe\gaiadesk-api-&lt;user&gt;</c> (<c>$USERNAME</c>, else <paramref name="username"/>).</summary>
    public static string PipeName(IDictionary<string, string?> env, string username = "")
    {
        if (Get(env, "GAIADESK_API_PIPE") is { Length: > 0 } p) return p;
        var user = Get(env, "USERNAME") is { Length: > 0 } u ? u : username;
        return $@"\\.\pipe\gaiadesk-api-{PipeUser(user)}";
    }

    private static string? Get(IDictionary<string, string?> env, string key) => env.TryGetValue(key, out var v) ? v : null;

    private static bool IsAbsolute(string p, bool windows) =>
        windows
            ? p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/') || p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)
            : p.StartsWith("/", StringComparison.Ordinal);

    private static string Join(string dir, string name, bool windows) => $"{dir.TrimEnd('/', '\\')}{(windows ? '\\' : '/')}{name}";

    /// <summary>The directory of the local API's socket and token: <c>$GAIADESK_API_DIR</c> when it is absolute, else <c>&lt;home&gt;/.gaiadesk</c>.</summary>
    public static string ApiDirectory(IDictionary<string, string?> env, string home, bool windows)
    {
        var d = Get(env, "GAIADESK_API_DIR");
        if (!string.IsNullOrEmpty(d) && IsAbsolute(d!, windows)) return d!;
        return Join(home, ".gaiadesk", windows);
    }

    /// <summary>The local API's Unix socket (macOS, Linux).</summary>
    public static string SocketPath(IDictionary<string, string?> env, string home, bool windows) => Join(ApiDirectory(env, home, windows), "api.sock", windows);

    /// <summary>The file holding the desk's local admin token (<c>gdlocal_</c> + 64 hex digits).</summary>
    public static string TokenPath(IDictionary<string, string?> env, string home, bool windows) => Join(ApiDirectory(env, home, windows), "api-token", windows);

    internal static IDictionary<string, string?> ProcessEnvironment()
    {
        var o = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry kv in System.Environment.GetEnvironmentVariables()) o[(string)kv.Key] = kv.Value as string;
        return o;
    }

    internal static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    internal static string Home() => System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
}

public sealed partial class GaiaDeskClient
{
    /// <summary>
    /// The desk's own API (code running on the desk): its Unix socket or Windows named pipe, with an agent token
    /// (<see cref="LocalOptions.DeskToken"/>) or else the desk's local admin token. Desk operations and
    /// <c>ListDesksAsync</c> only; never sealed (nothing leaves the machine). Needs .NET 5 or later.
    /// </summary>
    public static GaiaDeskClient Local(LocalOptions? options = null)
    {
#if NET5_0_OR_GREATER
        var o = options ?? new LocalOptions();
        var explicitPath = o.SocketPath is null ? null : Check.NonEmpty(o.SocketPath, "SocketPath");
        var token = o.Token is null ? null : Check.NonEmpty(o.Token, "Token");
        var deskToken = Check.OptionalToken(o.DeskToken);
        var env = o.Environment ?? LocalApi.ProcessEnvironment();
        var windows = LocalApi.IsWindows;
        var where = explicitPath ?? (windows ? LocalApi.PipeName(env, System.Environment.UserName) : LocalApi.SocketPath(env, LocalApi.Home(), false));

        UnreachableException NotServing(string detail) => new($"{LocalApi.Unavailable} ({detail})",
            new ErrorDetails { Kind = ErrorKinds.Unreachable, Reason = Reasons.LocalApiUnavailable, ExitCode = 255 });

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectCallback = async (_, ct) =>
            {
                if (windows)
                {
                    var name = where.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) ? where.Substring(9) : where;
                    var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.ConnectAsync(2000, ct).ConfigureAwait(false);
                        return pipe;
                    }
                    catch (Exception e) when (e is TimeoutException or IOException)
                    {
                        pipe.Dispose();
                        throw NotServing($"{where}: {e.Message}");
                    }
                }
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await s.ConnectAsync(new UnixDomainSocketEndPoint(where), ct).ConfigureAwait(false);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch (SocketException e)
                {
                    s.Dispose();
                    if (!File.Exists(where) || e.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable or SocketError.NotSocket)
                        throw NotServing(where);
                    throw new UnreachableException($"the desk's local API ({where}) could not be reached: {e.Message}",
                        new ErrorDetails { Kind = ErrorKinds.Network, Reason = "network", ExitCode = 255 });
                }
            },
        };

        async Task<string> AdminToken(CancellationToken ct)
        {
            if (token is not null) return token;
            var file = LocalApi.TokenPath(env, LocalApi.Home(), windows);
            string t;
            try
            {
                t = (await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)).Trim();
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                throw NotServing($"no local admin token at {file}; or give an agent token as DeskToken");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new GaiaDeskException($"cannot read the local admin token {file}: {e.Message}", new ErrorDetails { Kind = ErrorKinds.Local, Reason = ErrorKinds.Local });
            }
            if (t.Length == 0) throw new GaiaDeskException($"the local admin token file {file} is empty", new ErrorDetails { Kind = ErrorKinds.Local, Reason = ErrorKinds.Local });
            return t;
        }

        async Task<Dictionary<string, string>> Credentials(string? callToken, CancellationToken ct)
        {
            var t = callToken ?? deskToken;
            if (t is not null) return new Dictionary<string, string> { ["X-GaiaDesk-Desk-Token"] = t };
            return new Dictionary<string, string> { ["Authorization"] = $"Bearer {await AdminToken(ct).ConfigureAwait(false)}" };
        }

        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new GaiaDeskClient(new HttpCore(TransportKind.Local, "http://localhost/v1", "the desk's local API", http, true, Credentials, o.Retry, null, o.Timeouts));
#else
        _ = options;
        throw Errors.Usage("the local transport needs .NET 5 or later (SocketsHttpHandler.ConnectCallback); this build is netstandard2.1");
#endif
    }
}
