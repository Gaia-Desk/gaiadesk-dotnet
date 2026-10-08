// One /v1 API over HTTP: the hosted API, the desk's own socket or pipe, or a
// desk's LAN gateway. Builds requests (credentials, query, body, a sealed
// request in place of the plaintext), sends them with retries, and turns
// every failure into the typed error from its envelope.

using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GaiaDesk.E2e;

namespace GaiaDesk.Http;

/// <summary>A local file or stream to upload: how to open it (again, for a retry) and its length.</summary>
internal sealed class UploadSource
{
    public UploadSource(Func<Stream> open, long length)
    {
        Open = open;
        Length = length;
    }

    public Func<Stream> Open { get; }
    public long Length { get; }
}

internal sealed class ApiRequest
{
    public ApiRequest(string method, string path)
    {
        Method = method;
        Path = path;
    }

    public string Method { get; }
    public string Path { get; }
    public Dictionary<string, string?> Query { get; } = new(StringComparer.Ordinal);
    public JsonNode? Json { get; set; }
    public UploadSource? Upload { get; set; }
    public string Accept { get; set; } = "application/json";
    public string? DeskToken { get; set; }
    public int? Wake { get; set; }
    public string? IdempotencyKey { get; set; }
    /// <summary>A desk operation (sealed end to end when the hosted API does): its desk, its name, and its request (<c>{"op": …}</c>).</summary>
    public (string Desk, string Op, JsonObject Request)? E2e { get; set; }
    public string Operation => $"{Method} {Path}";

    public ApiRequest With(CallOptions? o)
    {
        if (o is null) return this;
        DeskToken = Check.OptionalToken(o.DeskToken);
        Wake = o.Wake;
        IdempotencyKey = o.IdempotencyKey;
        return this;
    }
}

/// <summary>A successful answer, and (sealed) the seal its events open with.</summary>
internal sealed class ApiResponse : IDisposable
{
    public ApiResponse(HttpResponseMessage message, CallerSeal? seal)
    {
        Message = message;
        Seal = seal;
    }

    public HttpResponseMessage Message { get; }
    public CallerSeal? Seal { get; }
    public void Dispose() => Message.Dispose();
}

internal delegate Task<Dictionary<string, string>> Credentials(string? callToken, CancellationToken ct);

internal sealed class HttpCore : IDisposable
{
    private static readonly string SdkAgent = $"gaiadesk-dotnet/{typeof(HttpCore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0"}";
    private static readonly string[] SealedQuery = { "path", "tail", "timeout" };
    private static readonly HashSet<string> PermanentUnavailable = new(StringComparer.Ordinal) { "api_disabled", "desk_ops_disabled", "local_api_off" };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Credentials _credentials;
    private readonly RetryOptions _retry;
    private readonly string _userAgent;
    private readonly Func<HttpRequestMessage, HttpRequestException, GaiaDeskException?>? _connectError;
    private readonly Random _jitter = new();

    public HttpCore(TransportKind transport, string baseUrl, string where, HttpClient http, bool ownsHttp, Credentials credentials,
        RetryOptions? retry, string? userAgent, TimeoutOptions? timeouts, Func<HttpRequestMessage, HttpRequestException, GaiaDeskException?>? connectError = null)
    {
        Transport = transport;
        BaseUrl = baseUrl.TrimEnd('/');
        Where = where;
        _http = http;
        _ownsHttp = ownsHttp;
        _credentials = credentials;
        _retry = retry ?? new RetryOptions();
        if (_retry.MaxRetries < 0) throw Errors.Usage("Retry.MaxRetries must be zero or more");
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? SdkAgent : $"{userAgent!.Trim()} {SdkAgent}";
        _connectError = connectError;
        _timeouts = timeouts ?? new TimeoutOptions();
        _timeouts.Validate();
    }

    private readonly TimeoutOptions _timeouts;

    public TransportKind Transport { get; }
    public string BaseUrl { get; }
    public string Where { get; }
    /// <summary>End-to-end encryption of desk operations: the hosted API only.</summary>
    public E2eLayer? E2e { get; set; }

    // ───────────────────────────── sending ─────────────────────────────

    /// <summary>
    /// One request; an HTTP failure is the typed error from its envelope. A desk operation on the hosted
    /// API is sealed end to end when the desk can open it. A GET that failed in passing (no connection,
    /// 502/503/504) is sent again, freshly sealed.
    /// </summary>
    public async Task<ApiResponse> SendAsync(ApiRequest r, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (E2e is null || r.E2e is not { } e) return await SendPrepared(r, null, ct).ConfigureAwait(false);
                return await E2e.Call(e.Desk, e.Op, e.Request, r.DeskToken, r.Wake, s => SendPrepared(r, s, ct), ct).ConfigureAwait(false);
            }
            catch (GaiaDeskException ex) when (r.Method == "GET" && attempt < _retry.MaxRetries && Transient(ex) && DelayFor(ex, attempt) is { } d)
            {
                await Task.Delay(d, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool Transient(GaiaDeskException e) =>
        (e is UnreachableException && e.Status is null && e.Reason == "network")
        || e.Status == 504
        || (e.Status == 502 && e is ConnectionLostException)
        || (e.Status == 503 && (e.Reason is null || !PermanentUnavailable.Contains(e.Reason)));

    private static bool NothingRan(GaiaDeskException e) =>
        e.Status == 429 || e.Reason is Reasons.RateLimited or Reasons.DeskBusy or Reasons.IdempotencyKeyInFlight;

    private TimeSpan? DelayFor(GaiaDeskException e, int attempt)
    {
        if (e.RetryAfter is { } ra) return ra <= _retry.MaxDelay ? (ra < TimeSpan.Zero ? TimeSpan.Zero : ra) : null;
        double jitter;
        lock (_jitter) jitter = 0.5 + _jitter.NextDouble() * 0.5;
        var ms = _retry.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt) * jitter;
        return TimeSpan.FromMilliseconds(Math.Min(ms, _retry.MaxDelay.TotalMilliseconds));
    }

    /// <summary>Send one prepared request (sealed or not), again as it was after a 429 (nothing ran).</summary>
    private async Task<ApiResponse> SendPrepared(ApiRequest r, Sealed? s, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var msg = await Build(r, s, ct).ConfigureAwait(false);
            HttpResponseMessage res;
            // The answer must begin within ResponseTimeout (sending the request included); its body is
            // then read under IdleTimeout (OpenBody), so a peer that goes silent is an error, never a hang.
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_timeouts.ResponseTimeout != Timeout.InfiniteTimeSpan) headers.CancelAfter(_timeouts.ResponseTimeout);
            try
            {
                res = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException e)
            {
                var which = headers.IsCancellationRequested ? $"within {_timeouts.ResponseTimeout.TotalSeconds:0.###} s (Timeouts.ResponseTimeout)" : "in time (the HttpClient's Timeout)";
                throw new UnreachableException($"{Where} did not answer {r.Operation} {which}",
                    new ErrorDetails { Kind = ErrorKinds.Timeout, Reason = "timeout", ExitCode = 255, Operation = r.Operation }, e);
            }
            catch (HttpRequestException e)
            {
                throw ConnectFailure(msg, e, r.Operation);
            }
            if (res.IsSuccessStatusCode) return new ApiResponse(res, s?.Seal);
            GaiaDeskException err;
            using (res) err = await ApiError(res, r.Operation, s?.Seal, ct).ConfigureAwait(false);
            if (attempt < _retry.MaxRetries && NothingRan(err) && DelayFor(err, attempt) is { } d)
            {
                await Task.Delay(d, ct).ConfigureAwait(false);
                continue;
            }
            throw err;
        }
    }

    private GaiaDeskException ConnectFailure(HttpRequestMessage msg, HttpRequestException e, string operation)
    {
        if (_connectError?.Invoke(msg, e) is { } mapped)
        {
            mapped.Operation ??= operation;
            return mapped;
        }
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is GaiaDeskException own)
            {
                own.Operation ??= operation;
                return own;
            }
        }
        var why = e.InnerException?.Message ?? e.Message;
        return new UnreachableException($"{Where} could not be reached: {why}",
            new ErrorDetails { Kind = ErrorKinds.Network, Reason = "network", ExitCode = 255, Operation = operation }, e);
    }

    private async Task<HttpRequestMessage> Build(ApiRequest r, Sealed? s, CancellationToken ct)
    {
        var query = new Dictionary<string, string?>(r.Query, StringComparer.Ordinal);
        if (r.Wake is { } wake) query["wake_s"] = Check.Wake(wake, r.Operation).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (s is not null) foreach (var k in SealedQuery) query.Remove(k);
        var msg = new HttpRequestMessage(new HttpMethod(r.Method), Url(r.Path, query));
        foreach (var kv in await _credentials(r.DeskToken, ct).ConfigureAwait(false)) msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        msg.Headers.Accept.ParseAdd(r.Accept);
        msg.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        if (r.IdempotencyKey is { } key)
        {
            if (key.Length is < 1 or > 255 || key.Any(c => c < 0x20 || c > 0x7e)) throw Errors.Usage("an Idempotency-Key is 1 to 255 printable ASCII characters", r.Operation);
            msg.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }
        if (s is not null)
        {
            // The sealed request carries what the query would have; POST bodies become {"e2e": …}.
            if (r.Method == "POST") msg.Content = JsonContent(new JsonObject { ["e2e"] = s.Request.ToJson() });
            else
            {
                msg.Headers.TryAddWithoutValidation(E2eCrypto.Header, E2eCrypto.RequestHeader(s.Request));
                if (r.Upload is { } up) msg.Content = new SealedUploadContent(s.Seal, up.Open, up.Length);
            }
        }
        else if (r.Json is not null) msg.Content = JsonContent(r.Json);
        else if (r.Upload is { } up)
        {
            var c = new StreamContent(new NonClosingStream(up.Open()));
            c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            c.Headers.ContentLength = up.Length;
            msg.Content = c;
        }
        return msg;
    }

    private static StringContent JsonContent(JsonNode json)
    {
        var c = new StringContent(json.ToJsonString(), Encoding.UTF8);
        c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return c;
    }

    private string Url(string path, Dictionary<string, string?> query)
    {
        var q = string.Join("&", query.Where(kv => kv.Value is not null).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        return q.Length == 0 ? $"{BaseUrl}{path}" : $"{BaseUrl}{path}?{q}";
    }

    // ───────────────────────────── answers ─────────────────────────────

    private static Task<Stream> RawBody(HttpContent content, CancellationToken ct) =>
#if NET5_0_OR_GREATER
        content.ReadAsStreamAsync(ct);
#else
        content.ReadAsStreamAsync();
#endif

    /// <summary>An answer's body, every read of it bounded by <see cref="TimeoutOptions.IdleTimeout"/>.</summary>
    public async Task<Stream> OpenBody(HttpResponseMessage res, string operation, string? desk, CancellationToken ct)
    {
        var raw = await RawBody(res.Content, ct).ConfigureAwait(false);
        return new IdleTimeoutStream(raw, _timeouts.IdleTimeout, ct, () => new ConnectionLostException(
            $"{Where} stopped sending its answer to {operation}: nothing for {_timeouts.IdleTimeout.TotalSeconds:0.###} s (Timeouts.IdleTimeout)",
            new ErrorDetails { Kind = ErrorKinds.Timeout, Reason = "timeout", Operation = operation, Desk = desk, ExitCode = 255 }));
    }

    private async Task<string> ReadString(HttpResponseMessage res, string operation, CancellationToken ct)
    {
        using var body = await OpenBody(res, operation, null, ct).ConfigureAwait(false);
        using var reader = new StreamReader(body, new UTF8Encoding(false));
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    /// <summary>A request answered with JSON (a sealed answer opened into the plaintext one).</summary>
    public async Task<JsonElement> JsonAsync(ApiRequest r, CancellationToken ct)
    {
        using var res = await SendAsync(r, ct).ConfigureAwait(false);
        string text;
        try
        {
            text = await ReadString(res.Message, r.Operation, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            throw new ConnectionLostException($"the answer to {r.Operation} broke off: {e.Message}",
                new ErrorDetails { Reason = "network", Operation = r.Operation, ExitCode = 255 }, e);
        }
        JsonElement json;
        try
        {
            json = GaiaDeskJson.Parse(text);
        }
        catch (JsonException)
        {
            throw new ProtocolException($"the GaiaDesk API answered {r.Operation} with something that is not JSON",
                new ErrorDetails { Operation = r.Operation, Status = (int)res.Message.StatusCode, RequestId = Header(res.Message, "X-Request-Id"), ExitCode = 255 });
        }
        return res.Seal is { } seal ? E2eAnswers.OpenAnswer(json, seal, r.Operation) : json;
    }

    private static string? Header(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues(name, out var v) ? string.Join(", ", v) : null;

    private static TimeSpan? RetryAfter(HttpResponseMessage res)
    {
        var ra = res.Headers.RetryAfter;
        if (ra?.Delta is { } d) return d;
        if (ra?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>The typed error for a failed HTTP request: its error envelope, else a ProtocolException.</summary>
    public async Task<GaiaDeskException> ApiError(HttpResponseMessage res, string operation, CallerSeal? seal, CancellationToken ct)
    {
        string text;
        try { text = await ReadString(res, operation, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or HttpRequestException or ConnectionLostException) { text = ""; }
        JsonElement? json = null;
        try { json = GaiaDeskJson.Parse(text); }
        catch (JsonException) { }
        // A sealed operation's desk error: its real message opens from `e2e.events`.
        if (seal is not null && json is { } j) json = E2eAnswers.OpenErrorEnvelope(j, seal, operation);
        var status = (int)res.StatusCode;
        var headerId = Header(res, "X-Request-Id");
        var retryAfter = RetryAfter(res);
        var env = json is { } jj ? ErrorEnvelope.From(jj) : null;
        if (env is null)
        {
            var snippet = text.Length > 200 ? text.Substring(0, 200) + "…" : text;
            return new ProtocolException($"the GaiaDesk API answered {operation} with HTTP {status} and no error envelope{(snippet.Trim().Length > 0 ? $": {snippet.Trim()}" : "")}",
                new ErrorDetails { ExitCode = 255, Operation = operation, Json = json, Status = status, RequestId = headerId, RetryAfter = retryAfter });
        }
        return Errors.FromEnvelope(env, $"HTTP {status}", new ErrorDetails
        {
            ExitCode = Errors.DeskOpExit(env.Kind), Operation = operation, Json = json, Status = status,
            RequestId = env.RequestId ?? headerId, RetryAfter = retryAfter,
        });
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>A stream an HttpContent may dispose without closing the caller's.</summary>
internal sealed class NonClosingStream : Stream
{
    private readonly Stream _inner;
    public NonClosingStream(Stream inner) { _inner = inner; }
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A response body handed to the caller: disposing it ends the response; a broken transfer is a ConnectionLostException.</summary>
internal sealed class ResponseStream : Stream
{
    private readonly Stream _inner;
    private readonly IDisposable _response;
    private readonly string _operation;
    private readonly string? _desk;

    public ResponseStream(Stream inner, IDisposable response, string operation, string? desk)
    {
        _inner = inner;
        _response = response;
        _operation = operation;
        _desk = desk;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private ConnectionLostException Broken(Exception e) =>
        new($"the download broke off (a failure after the first byte resets the transfer): {e.Message}",
            new ErrorDetails { Reason = "incomplete", Operation = _operation, Desk = _desk, ExitCode = 255 }, e);

    public override int Read(byte[] buffer, int offset, int count)
    {
        try { return _inner.Read(buffer, offset, count); }
        catch (Exception e) when (e is IOException or HttpRequestException) { throw Broken(e); }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        try { return await _inner.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or HttpRequestException) { throw Broken(e); }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try { return await _inner.ReadAsync(buffer, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or HttpRequestException) { throw Broken(e); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _response.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>A response body whose every read must make progress within a time limit (or the caller's token).</summary>
internal sealed class IdleTimeoutStream : Stream
{
    private readonly Stream _inner;
    private readonly TimeSpan _idle;
    private readonly CancellationToken _outer;
    private readonly Func<GaiaDeskException> _timedOut;

    public IdleTimeoutStream(Stream inner, TimeSpan idle, CancellationToken outer, Func<GaiaDeskException> timedOut)
    {
        _inner = inner;
        _idle = idle;
        _outer = outer;
        _timedOut = timedOut;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _outer);
        if (_idle != Timeout.InfiniteTimeSpan) cts.CancelAfter(_idle);
        try
        {
            return await _inner.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_outer.IsCancellationRequested)
        {
            _inner.Dispose(); // the connection is abandoned, not returned to the pool
            throw _timedOut();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
