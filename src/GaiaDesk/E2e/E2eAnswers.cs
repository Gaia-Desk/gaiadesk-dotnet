// Turning sealed answers back into exactly what the plaintext call answers:
// JSON results, error envelopes, SSE events (as the server maps a desk's
// events: protocol/src/desk_op_http.rs), and file bytes; and sealing an
// upload's body.

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GaiaDesk.Http;

namespace GaiaDesk.E2e;

internal static class E2eAnswers
{
    /// <summary>A sealed event opened, or the ProtocolException for one that does not.</summary>
    public static JsonElement Open(CallerSeal seal, JsonElement frame, string operation)
    {
        try
        {
            return seal.OpenDeskEvent(frame);
        }
        catch (E2eOpenException e)
        {
            throw new ProtocolException($"the desk's end-to-end encrypted answer did not open: {e.Message}",
                new ErrorDetails { Reason = e.Reason, Operation = operation, ExitCode = 255 });
        }
    }

    private static JsonElement? EventsOf(JsonElement json)
    {
        JsonElement e2e = default;
        var has = GaiaDeskJson.TryProp(json, "e2e", out e2e) && e2e.ValueKind == JsonValueKind.Object;
        if (!has && GaiaDeskJson.TryProp(json, "error", out var err) && GaiaDeskJson.TryProp(err, "e2e", out e2e) && e2e.ValueKind == JsonValueKind.Object) has = true;
        return has && e2e.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array ? ev : null;
    }

    /// <summary>
    /// An error envelope with the desk's real message: a desk's error comes with a placeholder
    /// <c>message</c> and <c>e2e.events</c>, whose last opens to the <c>error</c>. An envelope
    /// without events (the server's own error) is as it is.
    /// </summary>
    public static JsonElement OpenErrorEnvelope(JsonElement json, CallerSeal seal, string operation)
    {
        if (!GaiaDeskJson.TryProp(json, "error", out var err) || err.ValueKind != JsonValueKind.Object) return json;
        if (EventsOf(json) is not { } events) return json;
        JsonElement? last = null;
        try
        {
            foreach (var f in events.EnumerateArray()) last = Open(seal, f, operation);
        }
        catch (ProtocolException)
        {
            last = null;
        }
        var error = (JsonObject)JsonNode.Parse(err.GetRawText())!;
        error.Remove("e2e");
        var message = last is { } l && GaiaDeskJson.Str(l, "event") == "error"
            ? GaiaDeskJson.Str(l, "message") ?? ""
            : $"{GaiaDeskJson.Str(err, "message") ?? "the desk reported an error"} (its end-to-end encrypted message did not open)";
        error["message"] = message;
        var outer = (JsonObject)JsonNode.Parse(json.GetRawText())!;
        outer.Remove("e2e");
        outer["error"] = error;
        return GaiaDeskJson.Element(outer);
    }

    /// <summary>A sealed JSON answer (<c>{"e2e": {"events"}}</c>): the result the plaintext call answers; a held body's envelope, opened.</summary>
    public static JsonElement OpenAnswer(JsonElement json, CallerSeal seal, string operation)
    {
        if (ErrorEnvelope.From(json) is not null) return OpenErrorEnvelope(json, seal, operation);
        var events = EventsOf(json);
        if (events is not { } ev || ev.GetArrayLength() == 0)
            throw new ProtocolException("the GaiaDesk API answered an end-to-end encrypted operation without sealed events",
                new ErrorDetails { Reason = Reasons.E2eUnsealedAnswer, Operation = operation, Json = json, ExitCode = 255 });
        JsonElement? last = null;
        foreach (var f in ev.EnumerateArray()) last = Open(seal, f, operation);
        var name = last is { } l ? GaiaDeskJson.Str(l, "event") : null;
        if (name == "exit" && last!.Value.TryGetProperty("result", out var result)) return result.Clone();
        if (name == "error") throw DeskError(last!.Value, seal.Desk, operation);
        throw new ProtocolException("the desk's sealed answer has no result", new ErrorDetails { Reason = Reasons.E2eMalformed, Operation = operation, ExitCode = 255 });
    }

    /// <summary>The HTTP status <c>/v1</c> gives a desk's error (protocol desk_op_http.rs <c>desk_error_status</c>).</summary>
    public static (int Status, string Kind) DeskErrorStatus(string kind, string? reason) => kind switch
    {
        ErrorKinds.Usage => (400, kind),
        ErrorKinds.Refused => (reason == Reasons.DeskBusy ? 429 : reason == Reasons.E2eRequired ? 409 : 403, kind),
        ErrorKinds.Unreachable => (409, kind),
        ErrorKinds.ConnectionLost or ErrorKinds.Protocol => (502, kind),
        _ => (422, ErrorKinds.Failed),
    };

    /// <summary>A desk's opened <c>error</c> event as the error the plaintext call throws.</summary>
    public static GaiaDeskException DeskError(JsonElement e, string desk, string operation)
    {
        var (status, kind) = DeskErrorStatus(GaiaDeskJson.Str(e, "kind") ?? ErrorKinds.Failed, GaiaDeskJson.Str(e, "reason"));
        var reason = GaiaDeskJson.Str(e, "reason") ?? kind;
        var message = GaiaDeskJson.Str(e, "message") ?? "";
        var json = new JsonObject { ["error"] = new JsonObject { ["kind"] = kind, ["message"] = message, ["reason"] = reason, ["desk"] = desk } };
        return Errors.ForKind(kind, message, new ErrorDetails
        {
            Kind = ErrorKinds.For(kind, reason), Reason = reason, Desk = desk, Status = status, Operation = operation,
            Json = GaiaDeskJson.Element(json), ExitCode = Errors.DeskOpExit(kind),
        });
    }

    private static SseEvent Sse(string name, JsonObject v) => new(name, v.ToJsonString());

    /// <summary>
    /// A sealed SSE stream as the plaintext one: each <c>sealed</c> event opened (in order) and mapped as the
    /// API maps a desk's events (exec: stdout, stderr, exit, error; logs: output, end, interrupted, error),
    /// split UTF-8 characters carried. A plaintext <c>error</c> (the server's: the desk was lost) passes; any
    /// other plaintext output in a sealed stream is refused.
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> UnsealSse(IAsyncEnumerable<SseEvent> events, CallerSeal seal, bool logs, string operation,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var dec = new Dictionary<string, Decoder> { ["stdout"] = new UTF8Encoding(false, false).GetDecoder(), ["stderr"] = new UTF8Encoding(false, false).GetDecoder() };
        string Decode(string s, byte[] b, bool flush)
        {
            var d = dec[s];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(b.Length) + 4];
            var n = d.GetChars(b, 0, b.Length, chars, 0, flush);
            return new string(chars, 0, n);
        }
        await foreach (var ev in events.WithCancellation(ct).ConfigureAwait(false))
        {
            if (ev.Event == "error")
            {
                yield return ev;
                continue;
            }
            if (ev.Event != "sealed")
            {
                if (ev.Event is "stdout" or "stderr" or "exit" or "output" or "end" or "interrupted" or "message")
                    throw new ProtocolException($"the GaiaDesk API sent a plaintext `{ev.Event}` event in an end-to-end encrypted stream",
                        new ErrorDetails { Reason = Reasons.E2eUnsealedAnswer, Operation = operation, ExitCode = 255 });
                continue;
            }
            JsonElement frame;
            try { frame = GaiaDeskJson.Parse(ev.Data); }
            catch (JsonException) { frame = default; }
            // Its data names it too (`"event": "sealed"`), as every /v1 SSE event's does.
            if (GaiaDeskJson.Str(frame, "event") is { } named && named != "sealed") frame = default;
            var e = Open(seal, frame, operation);
            var name = GaiaDeskJson.Str(e, "event");
            if (name is "stdout" or "stderr")
            {
                var b = E2eCrypto.B64Decode(GaiaDeskJson.Str(e, "data"));
                if (b is null) continue; // not base64: the desk's bug, dropped (as the server does)
                var stream = logs ? "stdout" : name;
                var text = Decode(stream, b, false);
                if (text.Length == 0) continue;
                yield return logs ? Sse("output", new JsonObject { ["event"] = "output", ["data"] = text }) : Sse(name, new JsonObject { ["event"] = name, ["data"] = text });
            }
            else if (name == "exit")
            {
                var result = e.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? (JsonObject)JsonNode.Parse(r.GetRawText())! : new JsonObject();
                if (!logs)
                {
                    foreach (var s in new[] { "stdout", "stderr" })
                    {
                        var t = Decode(s, Array.Empty<byte>(), true);
                        if (t.Length > 0) yield return Sse(s, new JsonObject { ["event"] = s, ["data"] = t });
                    }
                    result.Remove("stdout");
                    result.Remove("stderr");
                    result.Remove("truncated");
                    result["event"] = "exit";
                    yield return Sse("exit", result);
                }
                else
                {
                    var t = Decode("stdout", Array.Empty<byte>(), true);
                    if (t.Length > 0) yield return Sse("output", new JsonObject { ["event"] = "output", ["data"] = t });
                    var interrupted = result.TryGetPropertyValue("interrupted", out var i) && i is JsonValue iv && iv.TryGetValue<bool>(out var ib) && ib;
                    if (interrupted) yield return Sse("interrupted", new JsonObject { ["event"] = "interrupted" });
                    else
                    {
                        result.TryGetPropertyValue("job", out var job);
                        yield return Sse("end", new JsonObject { ["event"] = "end", ["job"] = job?.DeepClone() });
                    }
                }
            }
            else if (name == "error")
            {
                var kind = GaiaDeskJson.Str(e, "kind") ?? ErrorKinds.Failed;
                var error = new JsonObject { ["kind"] = kind, ["message"] = GaiaDeskJson.Str(e, "message") ?? "", ["desk"] = seal.Desk };
                if (GaiaDeskJson.Str(e, "reason") is { } reason) error["reason"] = reason;
                yield return logs
                    ? Sse("error", new JsonObject { ["event"] = "error", ["error"] = error })
                    : Sse("error", new JsonObject { ["event"] = "error", ["exit"] = kind == ErrorKinds.Refused ? 254 : 255, ["error"] = error });
            }
        }
    }
}

/// <summary>An upload's body, sealed as it is sent: one input frame per line, at most 48 KiB of the file each, the last flagged.</summary>
internal sealed class SealedUploadContent : HttpContent
{
    private readonly CallerSeal _seal;
    private readonly Func<Stream> _open;
    private readonly long _length;

    public SealedUploadContent(CallerSeal seal, Func<Stream> open, long length)
    {
        _seal = seal;
        _open = open;
        _length = length;
        Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(E2eCrypto.FramesContentType);
    }

    private static long B64UrlLength(long n) => (4 * n + 2) / 3;

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var src = _open();
        var buf = new byte[E2eCrypto.InputChunk];
        long at = 0;
        ulong seq = 0;
        do
        {
            var want = (int)Math.Min(buf.Length, _length - at);
            var got = 0;
            while (got < want)
            {
                var n = await src.ReadAsync(buf.AsMemory(got, want - got)).ConfigureAwait(false);
                if (n == 0) throw new IOException($"the upload's source ended after {at + got} of {_length} bytes");
                got += n;
            }
            at += got;
            var frame = _seal.SealInputAt(seq++, E2eCrypto.Random(24), at >= _length, buf.AsSpan(0, got));
            var line = Encoding.UTF8.GetBytes(frame.ToJson() + "\n");
            await stream.WriteAsync(line, 0, line.Length).ConfigureAwait(false);
        } while (at < _length);
    }

    protected override bool TryComputeLength(out long length)
    {
        // Each line: {"seq":N,"nonce":"<32>","ciphertext":"<b64url(1 + chunk + 16)>"}\n
        length = 0;
        long at = 0;
        ulong seq = 0;
        do
        {
            var chunk = Math.Min(E2eCrypto.InputChunk, _length - at);
            at += chunk;
            length += "{\"seq\":".Length + seq.ToString(System.Globalization.CultureInfo.InvariantCulture).Length
                + ",\"nonce\":\"".Length + 32 + "\",\"ciphertext\":\"".Length + B64UrlLength(1 + chunk + 16) + "\"}\n".Length;
            seq++;
        } while (at < _length);
        return true;
    }
}

/// <summary>A sealed download (<c>application/x-ndjson</c>, one sealed event per line), read as the file's bytes.</summary>
internal sealed class SealedDownloadStream : Stream
{
    private readonly Stream _body;
    private readonly IDisposable _response;
    private readonly CallerSeal _seal;
    private readonly string _operation;
    private readonly StreamReader _reader;
    private byte[] _pending = Array.Empty<byte>();
    private int _pendingAt;
    private bool _done;

    public SealedDownloadStream(Stream body, IDisposable response, CallerSeal seal, string operation)
    {
        _body = body;
        _response = response;
        _seal = seal;
        _operation = operation;
        _reader = new StreamReader(body, new UTF8Encoding(false, true), false, 64 * 1024);
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

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_pendingAt >= _pending.Length)
        {
            if (_done) return 0;
            await Next(cancellationToken).ConfigureAwait(false);
        }
        var n = Math.Min(buffer.Length, _pending.Length - _pendingAt);
        _pending.AsMemory(_pendingAt, n).CopyTo(buffer);
        _pendingAt += n;
        return n;
    }

    private async Task Next(CancellationToken ct)
    {
        for (;;)
        {
            ct.ThrowIfCancellationRequested();
            string? line;
            try
            {
                line = await _reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                throw new ConnectionLostException($"the download broke before the desk said it was complete: {e.Message}",
                    new ErrorDetails { Reason = "incomplete", Operation = _operation, Desk = _seal.Desk, ExitCode = 255 });
            }
            if (line is null)
                throw new ConnectionLostException("the download ended before the desk said it was complete",
                    new ErrorDetails { Reason = "incomplete", Operation = _operation, Desk = _seal.Desk, ExitCode = 255 });
            if (line.Trim().Length == 0) continue;
            JsonElement frame;
            try { frame = GaiaDeskJson.Parse(line); }
            catch (JsonException)
            {
                throw new ProtocolException("a sealed download has a line that is not JSON", new ErrorDetails { Reason = Reasons.E2eMalformed, Operation = _operation, ExitCode = 255 });
            }
            var e2 = E2eAnswers.Open(_seal, frame, _operation);
            switch (GaiaDeskJson.Str(e2, "event"))
            {
                case "stdout":
                    _pending = E2eCrypto.B64Decode(GaiaDeskJson.Str(e2, "data"))
                        ?? throw new ProtocolException("a sealed download carries bytes that are not base64", new ErrorDetails { Reason = Reasons.E2eMalformed, Operation = _operation, ExitCode = 255 });
                    _pendingAt = 0;
                    return;
                case "error":
                    throw E2eAnswers.DeskError(e2, _seal.Desk, _operation);
                case "exit":
                    _done = true;
                    return;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reader.Dispose();
            _body.Dispose();
            _response.Dispose();
        }
        base.Dispose(disposing);
    }
}
