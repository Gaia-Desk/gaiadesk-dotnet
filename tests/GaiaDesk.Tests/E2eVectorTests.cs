// End-to-end encryption's crypto: the protocol's fixed test vectors byte for
// byte (protocol/src/e2e/vectors.json, copied to Fixtures/e2e-vectors.json),
// the XChaCha20 draft's own vectors, round trips, and every way a message must
// fail to open.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GaiaDesk.E2e;
using GaiaDesk.Tests.Fixtures;
using Xunit;

namespace GaiaDesk.Tests;

public class E2eVectorTests
{
    private static readonly JsonElement V = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "e2e-vectors.json"))).RootElement;
    private static string S(JsonElement e, string k) => e.GetProperty(k).GetString()!;
    private static readonly byte[] DeskSecret = E2eCrypto.Hex(S(V, "desk_secret_hex"));
    private static string DeskId => S(V, "desk_id");
    private static string Op => S(V, "op");
    private static byte[] B(string s) => E2eCrypto.B64Decode(s)!;

    private static (SealedRequest Request, CallerSeal Seal) VectorSeal(string? desk = null, string? op = null) =>
        E2eCrypto.SealRequestWith(E2eCrypto.Hex(S(V, "eph_secret_hex")), B(S(V.GetProperty("request"), "nonce")), E2eCrypto.X25519Public(DeskSecret),
            desk ?? DeskId, op ?? Op, Encoding.UTF8.GetBytes(S(V, "request_plaintext")));

    private static JsonElement Event(int i) => V.GetProperty("events")[i];
    private static JsonElement Frame(JsonElement e, ulong? seq = null, string? nonce = null, string? ct = null) =>
        JsonDocument.Parse(new JsonObject { ["seq"] = seq ?? e.GetProperty("seq").GetUInt64(), ["nonce"] = nonce ?? S(e, "nonce"), ["ciphertext"] = ct ?? S(e, "ciphertext") }.ToJsonString()).RootElement;

    [Fact]
    public void Vectors_DeskKey_SealedRequest_AndHeader_ByteForByte()
    {
        Assert.Equal(S(V, "desk_pub"), E2eCrypto.B64Url(E2eCrypto.X25519Public(DeskSecret)));
        var (req, _) = VectorSeal();
        var want = V.GetProperty("request");
        Assert.Equal(want.GetProperty("v").GetInt32(), req.V);
        Assert.Equal(S(want, "pub"), req.Pub);
        Assert.Equal(S(want, "nonce"), req.Nonce);
        Assert.Equal(S(want, "ciphertext"), req.Ciphertext);
        Assert.Equal(S(V, "request_header"), E2eCrypto.RequestHeader(req));
    }

    [Fact]
    public void Vectors_AssociatedData()
    {
        Assert.Equal(S(V, "aad_request_hex"), E2eCrypto.ToHex(E2eCrypto.AssociatedData("request", DeskId, Op)));
        Assert.Equal(S(V, "aad_event_1_hex"), E2eCrypto.ToHex(E2eCrypto.AssociatedData("event", DeskId, Op, 1)));
    }

    [Fact]
    public void Vectors_EventsOpen_AndInputFramesSealToTheSameCiphertext()
    {
        var (_, seal) = VectorSeal();
        foreach (var e in V.GetProperty("events").EnumerateArray()) Assert.Equal(S(e, "plaintext"), Encoding.UTF8.GetString(seal.OpenEvent(e)));
        foreach (var i in V.GetProperty("inputs").EnumerateArray())
        {
            var f = seal.SealInputWith(B(S(i, "nonce")), i.GetProperty("last").GetBoolean(), Encoding.UTF8.GetBytes(S(i, "data")));
            Assert.Equal(i.GetProperty("seq").GetUInt64(), f.Seq);
            Assert.Equal(S(i, "nonce"), f.Nonce);
            Assert.Equal(S(i, "ciphertext"), f.Ciphertext);
        }
        var (_, again) = VectorSeal();
        var e0 = again.OpenDeskEvent(Event(0));
        Assert.Equal("stdout", S(e0, "event"));
        Assert.Equal("dmVjdG9yCg==", S(e0, "data"));
        Assert.Equal("{\"event\":\"exit\",\"result\":{\"exit\":0}}", again.OpenDeskEvent(Event(1)).GetRawText());
    }

    [Fact]
    public void Vectors_TheDeskSideOpensTheRequestAndInputs_AndSealsTheSameEvent()
    {
        var (plain, desk) = DeskSeal.OpenRequest(DeskSecret, DeskId, Op, V.GetProperty("request"));
        Assert.Equal(S(V, "request_plaintext"), Encoding.UTF8.GetString(plain));
        var i0 = desk.OpenInput(V.GetProperty("inputs")[0]);
        Assert.False(i0.Last);
        Assert.Equal("hello ", Encoding.UTF8.GetString(i0.Data));
        var i1 = desk.OpenInput(V.GetProperty("inputs")[1]);
        Assert.True(i1.Last);
        Assert.Equal("world", Encoding.UTF8.GetString(i1.Data));
        var ev = desk.SealEventWith(B(S(Event(0), "nonce")), Encoding.UTF8.GetBytes(S(Event(0), "plaintext")));
        Assert.Equal(S(Event(0), "ciphertext"), ev.Ciphertext);
    }

    [Fact]
    public void HChaCha20_DraftVector()
    {
        // draft-irtf-cfrg-xchacha-03 §2.2.1
        var key = E2eCrypto.Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        var nonce = E2eCrypto.Hex("000000090000004a0000000031415927");
        Assert.Equal("82413b4227b27bfed30e42508a877d73a0f9e4d58a74a853c12ec41326d3ecdc", E2eCrypto.ToHex(E2eCrypto.HChaCha20(key, nonce)));
    }

    [Fact]
    public void XChaCha20Poly1305_DraftVector()
    {
        // draft-irtf-cfrg-xchacha-03 §A.3.1
        var pt = Encoding.ASCII.GetBytes("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var aad = E2eCrypto.Hex("50515253c0c1c2c3c4c5c6c7");
        var key = E2eCrypto.Hex("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
        var iv = E2eCrypto.Hex("404142434445464748494a4b4c4d4e4f5051525354555657");
        var ct = E2eCrypto.AeadSeal(key, iv, aad, pt);
        Assert.Equal("bd6d179d3e83d43b9576579493c0e939572a1700252bfaccbed2902c21396cbb731c7f1b0b4aa6440bf3a82f4eda7e39ae64c6708c54c216cb96b72e1213b4522f8c9ba40db5d945b11b69b982c1bb9e3f3fac2bc369488f76b2383565d3fff921f9664c97637da9768812f615c68b13b52e"
            + "c0875924c1c7987947deafd8780acf49", E2eCrypto.ToHex(ct));
        Assert.Equal(pt, E2eCrypto.AeadOpen(key, E2eCrypto.B64Url(iv), E2eCrypto.B64Url(ct), aad));
    }

    [Fact]
    public void RoundTrip_FreshKeys_RequestInputEventsInOrder()
    {
        var deskPub = E2eCrypto.X25519Public(DeskSecret);
        var (req, seal) = E2eCrypto.SealRequest(deskPub, "123456789", "file_put", new JsonObject { ["op"] = "file_put", ["path"] = "/tmp/x", ["size"] = 3 });
        var (plain, desk) = DeskSeal.OpenRequest(DeskSecret, "123456789", "file_put", req);
        var inner = JsonNode.Parse(plain)!;
        Assert.Equal(1, inner["v"]!.GetValue<int>());
        Assert.True(Math.Abs(inner["ts"]!.GetValue<long>() - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) < 5);
        Assert.Equal("{\"op\":\"file_put\",\"path\":\"/tmp/x\",\"size\":3}", inner["request"]!.ToJsonString());
        var f = seal.SealInput(true, Encoding.UTF8.GetBytes("abc"));
        var opened = desk.OpenInput(JsonDocument.Parse(f.ToJson()).RootElement);
        Assert.True(opened.Last);
        Assert.Equal("abc", Encoding.UTF8.GetString(opened.Data));
        foreach (var e in new JsonNode[] { new JsonObject { ["event"] = "stdout", ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("é")) }, new JsonObject { ["event"] = "exit", ["result"] = new JsonObject { ["ok"] = 1 } } })
            Assert.Equal(e.ToJsonString(), seal.OpenDeskEvent(JsonDocument.Parse(desk.SealEvent(e).ToJson()).RootElement).GetRawText());
        var (other, _) = E2eCrypto.SealRequest(deskPub, "123456789", "exec", new JsonObject { ["op"] = "exec" });
        Assert.NotEqual(other.Pub, req.Pub);
        Assert.NotEqual(other.Nonce, req.Nonce);
    }

    private static string Flip(string s, int at = 0)
    {
        var u = B(s);
        u[at] ^= 1;
        return E2eCrypto.B64Url(u);
    }

    [Fact]
    public void Tampering_DoesNotOpen()
    {
        var (req, _) = VectorSeal();
        foreach (var bad in new[] { req with { Ciphertext = Flip(req.Ciphertext, 5) }, req with { Nonce = Flip(req.Nonce) }, req with { Pub = Flip(req.Pub, 3) } })
            Assert.Throws<E2eOpenException>(() => DeskSeal.OpenRequest(DeskSecret, DeskId, Op, bad));
        foreach (var e in new[] { Frame(Event(0), ct: Flip(S(Event(0), "ciphertext"), 2)), Frame(Event(0), nonce: Flip(S(Event(0), "nonce"), 23)) })
            Assert.Equal(Reasons.E2eDecryptFailed, Assert.Throws<E2eOpenException>(() => VectorSeal().Seal.OpenEvent(e)).Reason);
        Assert.Equal(Reasons.E2eMalformed, Assert.Throws<E2eOpenException>(() => VectorSeal().Seal.OpenEvent(Frame(Event(0), ct: "AAAA"))).Reason);
        Assert.Equal(Reasons.E2eMalformed, Assert.Throws<E2eOpenException>(() => VectorSeal().Seal.OpenEvent(JsonDocument.Parse("{\"seq\":0}").RootElement)).Reason);
    }

    [Fact]
    public void AssociatedData_AnotherDeskOrOperationDoesNotOpen()
    {
        var (req, _) = VectorSeal();
        Assert.Throws<E2eOpenException>(() => DeskSeal.OpenRequest(DeskSecret, "481902775", Op, req));
        Assert.Throws<E2eOpenException>(() => DeskSeal.OpenRequest(DeskSecret, DeskId, "job_start", req));
        Assert.Equal(Reasons.E2eDecryptFailed, Assert.Throws<E2eOpenException>(() => VectorSeal(desk: "481902775").Seal.OpenEvent(Event(0))).Reason);
        Assert.Equal(Reasons.E2eDecryptFailed, Assert.Throws<E2eOpenException>(() => VectorSeal(op: "stats").Seal.OpenEvent(Event(0))).Reason);
    }

    [Fact]
    public void EventFrames_ReorderedReplayedRenumberedOrSkipped_DoNotOpen()
    {
        Assert.Throws<E2eOpenException>(() => VectorSeal().Seal.OpenEvent(Event(1)));
        var s = VectorSeal().Seal;
        s.OpenEvent(Event(0));
        Assert.Throws<E2eOpenException>(() => s.OpenEvent(Event(0)));
        Assert.Throws<E2eOpenException>(() => VectorSeal().Seal.OpenEvent(Frame(Event(1), seq: 0)));
        s = VectorSeal().Seal;
        s.OpenEvent(Event(0));
        Assert.Throws<E2eOpenException>(() => s.OpenEvent(Frame(Event(0), seq: 1)));
        s = VectorSeal().Seal;
        s.OpenEvent(Event(0));
        Assert.Equal(S(Event(1), "plaintext"), Encoding.UTF8.GetString(s.OpenEvent(Event(1))));
        var (_, desk) = DeskSeal.OpenRequest(DeskSecret, DeskId, Op, V.GetProperty("request"));
        Assert.ThrowsAny<Exception>(() => desk.OpenInput(Frame(V.GetProperty("inputs")[1], seq: 0)));
    }

    [Fact]
    public void Keys_LowOrderKeyGivesNoSecret_DeskKeyReads32Bytes()
    {
        Assert.Equal("e2e_weak_key", Assert.Throws<E2eOpenException>(() => E2eCrypto.X25519(E2eCrypto.Hex(S(V, "eph_secret_hex")), new byte[32])).Reason);
        Assert.Equal(32, E2eCrypto.DeskKey(S(V, "desk_pub"))!.Length);
        Assert.Equal(32, E2eCrypto.DeskKey(S(V, "desk_pub") + "=")!.Length);
        Assert.Null(E2eCrypto.DeskKey("AAAA"));
        Assert.Null(E2eCrypto.DeskKey("not base64!"));
        Assert.Null(E2eCrypto.DeskKey(null));
    }

    [Fact]
    public void Base64_StandardPadded_UrlSafeUnpadded()
    {
        foreach (var s in new[] { "", "f", "fo", "foo", "foob", "fooba", "foobar" })
        {
            var b = Encoding.UTF8.GetBytes(s);
            Assert.Equal(Convert.ToBase64String(b), E2eCrypto.B64(b));
            Assert.Equal(b, E2eCrypto.B64Decode(E2eCrypto.B64Url(b)));
            Assert.Equal(b, E2eCrypto.B64Decode(E2eCrypto.B64(b)));
        }
        Assert.Equal("-_8", E2eCrypto.B64Url(new byte[] { 0xfb, 0xff }));
        Assert.Null(E2eCrypto.B64Decode("a"));
    }
}
