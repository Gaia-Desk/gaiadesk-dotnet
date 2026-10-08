// Pure pieces: the SSE parser, the error envelope, webhook signatures and
// events.

using System.Text;
using System.Text.Json;
using GaiaDesk.Http;
using Xunit;

namespace GaiaDesk.Tests;

public class SseParserTests
{
    private static List<SseEvent> FeedAll(IEnumerable<string> chunks)
    {
        var p = new SseParser();
        var o = new List<SseEvent>();
        foreach (var c in chunks) o.AddRange(p.Feed(c));
        o.AddRange(p.End());
        return o;
    }

    [Fact]
    public void EventsSplitAnywhere_CrlfAcrossChunks_Comments_MultiLineData_AnUnterminatedLastEvent()
    {
        var text = ": keep-alive\r\n\r\nevent: stdout\r\ndata: {\"a\":1}\r\n\r\ndata: one\ndata: two\n\nevent: exit\rdata:x\r\rdata: tail";
        var whole = FeedAll(new[] { text });
        Assert.Equal(new[] { ("stdout", "{\"a\":1}"), ("message", "one\ntwo"), ("exit", "x"), ("message", "tail") }, whole.Select(e => (e.Event, e.Data)));
        for (var cut = 1; cut < text.Length; cut++)
        {
            var split = FeedAll(new[] { text.Substring(0, cut), text.Substring(cut) });
            Assert.Equal(whole, split);
        }
        Assert.Equal(whole, FeedAll(text.Select(c => c.ToString())));
    }

    [Fact]
    public async Task Read_DecodesUtf8SplitAcrossReads()
    {
        var bytes = Encoding.UTF8.GetBytes("data: é😀\n\n");
        var events = new List<SseEvent>();
        await foreach (var e in SseParser.Read(new OneByteStream(bytes))) events.Add(e);
        Assert.Equal("é😀", Assert.Single(events).Data);
    }

    private sealed class OneByteStream : MemoryStream
    {
        public OneByteStream(byte[] b) : base(b) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer.Slice(0, Math.Min(1, buffer.Length)), ct);
    }
}

public class ErrorTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Envelope_ReadsKindMessageReasonDeskRequestIdStatus_AndNothingElse()
    {
        var e = ErrorEnvelope.From(J("{\"error\":{\"kind\":\"unreachable\",\"message\":\"m\",\"reason\":\"offline\",\"desk\":\"1\",\"request_id\":\"req_1\",\"status\":422}}"))!;
        Assert.Equal(("unreachable", "m", "offline", "1", "req_1", 422), (e.Kind, e.Message, e.Reason!, e.Desk!, e.RequestId!, e.Status!.Value));
        Assert.Null(ErrorEnvelope.From(J("{\"error\":null,\"exit\":0}")));
        Assert.Null(ErrorEnvelope.From(J("[1]")));
        Assert.Null(ErrorEnvelope.From(J("{\"error\":{\"message\":\"no kind\"}}")));
    }

    [Fact]
    public void Kinds_ClassFollowsTheKind_KindIsTheFinestKnown()
    {
        Assert.Equal("offline", ErrorKinds.For("unreachable", "offline"));
        Assert.Equal("refused", ErrorKinds.For("refused", "admin_denied"));
        Assert.IsType<UnreachableException>(Errors.ForKind("unreachable", "x", new ErrorDetails()));
        Assert.IsType<ConnectionLostException>(Errors.ForKind("connection_lost", "x", new ErrorDetails()));
        Assert.IsType<OperationFailedException>(Errors.ForKind("failed", "x", new ErrorDetails()));
        Assert.IsType<ProtocolException>(Errors.ForKind("protocol", "x", new ErrorDetails()));
        Assert.IsType<UsageException>(Errors.ForKind("usage", "x", new ErrorDetails()));
        Assert.IsType<GaiaDeskException>(Errors.ForKind("other", "x", new ErrorDetails()));
        Assert.Equal((254, 1, 255), (Errors.DeskOpExit("refused"), Errors.DeskOpExit("failed"), Errors.DeskOpExit("protocol")));
        var x = new StreamExit { ExitCode = 254, Error = new ErrorInfo { Kind = "refused", Message = "no", Reason = Reasons.AdminDenied } };
        Assert.False(x.Succeeded);
        Assert.Equal(Reasons.AdminDenied, Assert.Throws<RefusedException>(x.ThrowIfError).Reason);
    }
}

public class WebhookTests
{
    private const string Secret = "whsec_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Body = "{\"id\":\"evt_2c3d4e5f60718293a4b5c6d7\",\"type\":\"job.finished\",\"created\":1791300300,\"data\":{\"desk\":{\"desk_id\":\"123456789\",\"owner\":\"owner@example.com\"},\"job\":{\"name\":\"nightly-build\",\"command\":\"make release\",\"state\":\"exited\",\"started_at_ms\":1791300000000,\"ended_at_ms\":1791300290000,\"exit_code\":0}}}";
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeSeconds(1791300300);

    [Fact]
    public void Signature_MatchesAnIndependentHmac_AndVerifies()
    {
        var sig = WebhookSignature.Sign(Secret, Encoding.UTF8.GetBytes(Body), T);
        using var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var want = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"1791300300.{Body}"))).ToLowerInvariant();
        Assert.Equal($"t=1791300300,v1={want}", sig);
        Assert.True(WebhookSignature.Verify(Secret, sig, Body, T));
        Assert.True(WebhookSignature.Verify(Secret, sig, Encoding.UTF8.GetBytes(Body), T.AddMinutes(4)));
    }

    [Fact]
    public void Signature_RejectsTamperingStaleOrMalformed()
    {
        var sig = WebhookSignature.Sign(Secret, Encoding.UTF8.GetBytes(Body), T);
        Assert.False(WebhookSignature.Verify(Secret, sig, Body + " ", T));
        Assert.False(WebhookSignature.Verify(Secret + "x", sig, Body, T));
        Assert.False(WebhookSignature.Verify(Secret, sig, Body, T.AddMinutes(6)));
        Assert.False(WebhookSignature.Verify(Secret, sig, Body, T.AddMinutes(-6)));
        Assert.True(WebhookSignature.Verify(Secret, sig, Body, T.AddMinutes(6), TimeSpan.FromMinutes(10)));
        Assert.False(WebhookSignature.Verify(Secret, "garbage", Body, T));
        Assert.False(WebhookSignature.Verify(Secret, null, Body, T));
        Assert.False(WebhookSignature.Verify(Secret, sig.Replace("v1=", "v1=zz"), Body, T));
    }

    [Fact]
    public void Event_ParsesWithTypedDataAccessors()
    {
        var e = WebhookEvent.Parse(Body);
        Assert.Equal(WebhookEventTypes.JobFinished, e.Type);
        Assert.Equal("123456789", e.Desk!.DeskId);
        Assert.Equal("nightly-build", e.Job!.Name);
        Assert.Equal(0, e.Job.ExitCode);
        Assert.Null(e.SupportSession);
        var s = WebhookEvent.Parse("{\"id\":\"evt_1\",\"type\":\"support.session.ended\",\"created\":1,\"data\":{\"support_session\":{\"id\":\"ss_1\",\"state\":\"ended\",\"mode\":\"view\",\"customer\":{\"name\":\"Ada\"},\"customer_verified\":true,\"owner\":\"o\",\"created_at\":1,\"expires_at\":2,\"end_reason\":\"stopped\"}}}");
        Assert.Equal("stopped", s.SupportSession!.EndReason);
        Assert.Throws<ProtocolException>(() => WebhookEvent.Parse("not json"));
    }
}
