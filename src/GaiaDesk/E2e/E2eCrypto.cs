// End-to-end encrypted desk operations, v1: the caller's side of the sealing
// the GaiaDesk API relays without reading (the contract: the API reference's
// "End-to-end encryption"; the reference implementation: the protocol crate's
// e2e.rs, whose fixed test vectors the tests reproduce byte for byte).
//
// Per operation: an ephemeral X25519 key pair; shared = X25519(eph, desk);
// prk = HKDF-SHA256-Extract("gaiadesk desk-op e2e v1", shared); one key per
// use (`request`, `input`, `event`) = HKDF-Expand(prk, label 0x00 eph_pub
// desk_pub, 32). Every message is XChaCha20-Poly1305 with a random 24-byte
// nonce and associated data naming the use, the desk, the operation and (for
// the streams) the message's place.
//
// The primitives: X25519 and ChaCha20-Poly1305 (RFC 8439) from
// BouncyCastle.Cryptography (MIT, pure managed, every platform); HKDF from
// .NET (System.Security.Cryptography.HKDF; BouncyCastle's on netstandard2.1);
// XChaCha20-Poly1305 is ChaCha20-Poly1305 under the HChaCha20 subkey
// (draft-irtf-cfrg-xchacha-03 §2.3), its test vector in the tests.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto;
using BcChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;
using Org.BouncyCastle.Crypto.Parameters;
using BcX25519 = Org.BouncyCastle.Math.EC.Rfc7748.X25519;

namespace GaiaDesk.E2e;

/// <summary>Why a sealed message did not open (the protocol's reasons).</summary>
internal sealed class E2eOpenException : Exception
{
    public E2eOpenException(string reason, string message) : base(message) { Reason = reason; }
    public string Reason { get; }
}

/// <summary>A sealed request: <c>{"e2e": …}</c> of a POST body, or the GaiaDesk-E2E header's JSON.</summary>
internal sealed record SealedRequest(int V, string Pub, string Nonce, string Ciphertext)
{
    public JsonObject ToJson() => new() { ["v"] = V, ["pub"] = Pub, ["nonce"] = Nonce, ["ciphertext"] = Ciphertext };
}

/// <summary>A sealed frame after the request: an event coming back or a piece of input going up.</summary>
internal sealed record SealedFrame(ulong Seq, string Nonce, string Ciphertext)
{
    public string ToJson() => $"{{\"seq\":{Seq},\"nonce\":\"{Nonce}\",\"ciphertext\":\"{Ciphertext}\"}}";
}

/// <summary>One operation's three keys.</summary>
internal sealed record OpKeys(byte[] Request, byte[] Input, byte[] Event);

internal static class E2eCrypto
{
    public const string Feature = "desk_op_e2e";
    public const int Version = 1;
    /// <summary>The HTTP header that carries a sealed request on a call without a JSON body.</summary>
    public const string Header = "GaiaDesk-E2E";
    public const string FramesContentType = "application/x-ndjson";
    public const string HkdfSalt = "gaiadesk desk-op e2e v1";
    /// <summary>The most file bytes one sealed input frame carries.</summary>
    public const int InputChunk = 48 * 1024;

    // ───────────────────────────── bytes ─────────────────────────────

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    public static byte[] Hex(string s)
    {
        if (s.Length % 2 != 0) throw new FormatException("not hex");
        var o = new byte[s.Length / 2];
        for (var i = 0; i < o.Length; i++) o[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return o;
    }

    public static string ToHex(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (var x in b) sb.Append(x.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Standard base64 with padding (a desk event's <c>data</c>).</summary>
    public static string B64(ReadOnlySpan<byte> b) => Convert.ToBase64String(b.ToArray());

    /// <summary>base64url without padding: every binary field of the envelope.</summary>
    public static string B64Url(ReadOnlySpan<byte> b) => B64(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Standard or url-safe base64, padded or not; null when it is not base64.</summary>
    public static byte[]? B64Decode(string? s)
    {
        if (s is null) return null;
        var t = s.Trim().Replace('-', '+').Replace('_', '/').TrimEnd('=');
        foreach (var c in t)
            if (!(c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '+' || c == '/')) return null;
        if (t.Length % 4 == 1) return null;
        t += new string('=', (4 - t.Length % 4) % 4);
        try { return Convert.FromBase64String(t); }
        catch (FormatException) { return null; }
    }

    /// <summary>A desk key as given (<c>e2e_pub</c>, base64url): its 32 bytes, or null.</summary>
    public static byte[]? DeskKey(string? s)
    {
        var k = B64Decode(s);
        return k is { Length: 32 } ? k : null;
    }

    public static byte[] Random(int n)
    {
        var b = new byte[n];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(b);
        return b;
    }

    // ───────────────────────────── primitives ─────────────────────────────

    /// <summary>The X25519 public key of a 32-byte secret.</summary>
    public static byte[] X25519Public(byte[] secret)
    {
        if (secret.Length != 32) throw new ArgumentException("an X25519 secret is 32 bytes");
        var pub = new byte[32];
        BcX25519.ScalarMultBase(secret, 0, pub, 0);
        return pub;
    }

    /// <summary>X25519, refusing a non-contributory (all-zero) result.</summary>
    public static byte[] X25519(byte[] secret, byte[] pub)
    {
        if (secret.Length != 32 || pub.Length != 32) throw new E2eOpenException(Reasons.E2eMalformed, "an X25519 key is 32 bytes");
        var shared = new byte[32];
        if (!BcX25519.CalculateAgreement(secret, 0, pub, 0, shared, 0) || shared.All(b => b == 0))
            throw new E2eOpenException("e2e_weak_key", "the key exchange gave no shared secret (a low-order key)");
        return shared;
    }

    public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int length)
    {
#if NET5_0_OR_GREATER
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, info);
#else
        var g = new Org.BouncyCastle.Crypto.Generators.HkdfBytesGenerator(new Org.BouncyCastle.Crypto.Digests.Sha256Digest());
        g.Init(new HkdfParameters(ikm, salt, info));
        var o = new byte[length];
        g.GenerateBytes(o, 0, length);
        return o;
#endif
    }

    /// <summary>HChaCha20 (draft-irtf-cfrg-xchacha-03 §2.2): a 32-byte subkey from a key and a 16-byte nonce.</summary>
    public static byte[] HChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce16)
    {
        Span<uint> x = stackalloc uint[16];
        x[0] = 0x61707865; x[1] = 0x3320646e; x[2] = 0x79622d32; x[3] = 0x6b206574;
        for (var i = 0; i < 8; i++) x[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));
        for (var i = 0; i < 4; i++) x[12 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce16.Slice(i * 4, 4));
        for (var r = 0; r < 10; r++)
        {
            Quarter(x, 0, 4, 8, 12); Quarter(x, 1, 5, 9, 13); Quarter(x, 2, 6, 10, 14); Quarter(x, 3, 7, 11, 15);
            Quarter(x, 0, 5, 10, 15); Quarter(x, 1, 6, 11, 12); Quarter(x, 2, 7, 8, 13); Quarter(x, 3, 4, 9, 14);
        }
        var o = new byte[32];
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(i * 4, 4), x[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(16 + i * 4, 4), x[12 + i]);
        }
        return o;
    }

    private static void Quarter(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 7);
    }

    private static uint Rotl(uint v, int n) => (v << n) | (v >> (32 - n));

    private static BcChaCha20Poly1305 Aead(bool encrypt, byte[] key, byte[] nonce24, byte[] aad)
    {
        if (key.Length != 32 || nonce24.Length != 24) throw new ArgumentException("XChaCha20-Poly1305 takes a 32-byte key and a 24-byte nonce");
        var sub = HChaCha20(key, nonce24.AsSpan(0, 16));
        var n12 = new byte[12];
        Array.Copy(nonce24, 16, n12, 4, 8);
        var c = new BcChaCha20Poly1305();
        c.Init(encrypt, new AeadParameters(new KeyParameter(sub), 128, n12, aad));
        return c;
    }

    /// <summary>XChaCha20-Poly1305 encryption: the ciphertext and its 16-byte tag.</summary>
    public static byte[] AeadSeal(byte[] key, byte[] nonce, byte[] aad, byte[] plaintext)
    {
        var c = Aead(true, key, nonce, aad);
        var o = new byte[c.GetOutputSize(plaintext.Length)];
        var n = c.ProcessBytes(plaintext, 0, plaintext.Length, o, 0);
        n += c.DoFinal(o, n);
        return n == o.Length ? o : o.Take(n).ToArray();
    }

    /// <summary>XChaCha20-Poly1305 decryption of base64url fields; E2eOpenException when it does not authenticate.</summary>
    public static byte[] AeadOpen(byte[] key, string nonce, string ciphertext, byte[] aad)
    {
        var n = B64Decode(nonce);
        var ct = B64Decode(ciphertext);
        if (n is not { Length: 24 } || ct is null || ct.Length < 16) throw new E2eOpenException(Reasons.E2eMalformed, "a sealed message is malformed");
        try
        {
            var c = Aead(false, key, n, aad);
            var o = new byte[c.GetOutputSize(ct.Length)];
            var len = c.ProcessBytes(ct, 0, ct.Length, o, 0);
            len += c.DoFinal(o, len);
            return len == o.Length ? o : o.Take(len).ToArray();
        }
        catch (InvalidCipherTextException)
        {
            throw new E2eOpenException(Reasons.E2eDecryptFailed, "a sealed message did not open: it was altered, reordered, or sealed for another desk or operation");
        }
    }

    /// <summary>The associated data: <c>"gaiadesk-e2e/v1 &lt;use&gt;" 0 desk 0 op</c>, then <c>0 seq</c> (u64 big-endian) for input and events.</summary>
    public static byte[] AssociatedData(string use, string desk, string op, ulong? seq = null)
    {
        var head = Concat(Utf8($"gaiadesk-e2e/v1 {use}"), new byte[] { 0 }, Utf8(desk), new byte[] { 0 }, Utf8(op));
        if (seq is null) return head;
        var s = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(s, seq.Value);
        return Concat(head, new byte[] { 0 }, s);
    }

    /// <summary>The keys from the exchange's shared secret and both public keys.</summary>
    public static OpKeys DeriveKeys(byte[] shared, byte[] ephPub, byte[] deskPub)
    {
        var salt = Utf8(HkdfSalt);
        byte[] Key(string label) => Hkdf(shared, salt, Concat(Utf8(label), new byte[] { 0 }, ephPub, deskPub), 32);
        return new OpKeys(Key("request"), Key("input"), Key("event"));
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var o = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, o, at, p.Length);
            at += p.Length;
        }
        return o;
    }

    // ───────────────────────────── the caller ─────────────────────────────

    /// <summary>Seal <paramref name="plaintext"/> to desk <paramref name="desk"/> (key <paramref name="deskPub"/>) as <paramref name="op"/>, with a given ephemeral secret and nonce: the test vectors' entry point. Never reuse either.</summary>
    public static (SealedRequest Request, CallerSeal Seal) SealRequestWith(byte[] eph, byte[] nonce, byte[] deskPub, string desk, string op, byte[] plaintext)
    {
        var ephPub = X25519Public(eph);
        var keys = DeriveKeys(X25519(eph, deskPub), ephPub, deskPub);
        var ct = AeadSeal(keys.Request, nonce, AssociatedData("request", desk, op), plaintext);
        return (new SealedRequest(Version, B64Url(ephPub), B64Url(nonce), B64Url(ct)), new CallerSeal(keys, desk, op));
    }

    /// <summary>Seal a desk operation's request (<c>{"op": …}</c>) now: <c>{"v":1,"ts":&lt;now&gt;,"request":…}</c> under a fresh ephemeral key.</summary>
    public static (SealedRequest Request, CallerSeal Seal) SealRequest(byte[] deskPub, string desk, string op, JsonObject request, DateTimeOffset? now = null)
    {
        var ts = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var inner = new JsonObject { ["v"] = Version, ["ts"] = ts, ["request"] = request.DeepClone() };
        return SealRequestWith(Random(32), Random(24), deskPub, desk, op, Utf8(inner.ToJsonString()));
    }

    /// <summary>The GaiaDesk-E2E header value of a sealed request: base64url of its JSON.</summary>
    public static string RequestHeader(SealedRequest r) =>
        B64Url(Utf8($"{{\"v\":{r.V},\"pub\":\"{r.Pub}\",\"nonce\":\"{r.Nonce}\",\"ciphertext\":\"{r.Ciphertext}\"}}"));
}

/// <summary>The caller's side of one operation after its request is sealed: its input going up, the desk's events coming back.</summary>
internal sealed class CallerSeal
{
    private readonly OpKeys _keys;
    private ulong _nextInput;
    private ulong _nextEvent;
    private readonly object _lock = new();

    public CallerSeal(OpKeys keys, string desk, string op)
    {
        _keys = keys;
        Desk = desk;
        Op = op;
    }

    public string Desk { get; }
    public string Op { get; }

    /// <summary>Input frame <paramref name="seq"/> (<paramref name="last"/> on the final one, which may be empty), with a given nonce.</summary>
    public SealedFrame SealInputAt(ulong seq, byte[] nonce, bool last, ReadOnlySpan<byte> data)
    {
        var plain = new byte[data.Length + 1];
        plain[0] = last ? (byte)1 : (byte)0;
        data.CopyTo(plain.AsSpan(1));
        var ct = E2eCrypto.AeadSeal(_keys.Input, nonce, E2eCrypto.AssociatedData("input", Desk, Op, seq), plain);
        return new SealedFrame(seq, E2eCrypto.B64Url(nonce), E2eCrypto.B64Url(ct));
    }

    /// <summary>The next piece of input, with a given nonce.</summary>
    public SealedFrame SealInputWith(byte[] nonce, bool last, ReadOnlySpan<byte> data)
    {
        ulong seq;
        lock (_lock) seq = _nextInput++;
        return SealInputAt(seq, nonce, last, data);
    }

    /// <summary>The next piece of input under a fresh nonce.</summary>
    public SealedFrame SealInput(bool last, ReadOnlySpan<byte> data) => SealInputWith(E2eCrypto.Random(24), last, data);

    /// <summary>Open the desk's next event (it must be the next in order): its plaintext.</summary>
    public byte[] OpenEvent(JsonElement f)
    {
        if (f.ValueKind != JsonValueKind.Object
            || !f.TryGetProperty("seq", out var seqE) || seqE.ValueKind != JsonValueKind.Number || !seqE.TryGetUInt64(out var seq)
            || GaiaDeskJson.Str(f, "nonce") is not { } nonce || GaiaDeskJson.Str(f, "ciphertext") is not { } ct)
            throw new E2eOpenException(Reasons.E2eMalformed, "a sealed event is malformed");
        lock (_lock)
        {
            if (seq != _nextEvent) throw new E2eOpenException(Reasons.E2eDecryptFailed, $"a sealed event is out of order (got {seq}, expected {_nextEvent})");
            var plain = E2eCrypto.AeadOpen(_keys.Event, nonce, ct, E2eCrypto.AssociatedData("event", Desk, Op, seq));
            _nextEvent++;
            return plain;
        }
    }

    /// <summary>Open the next event as the desk event it carries (<c>{"event": "stdout" | "stderr" | "exit" | "error", …}</c>).</summary>
    public JsonElement OpenDeskEvent(JsonElement f)
    {
        var plain = OpenEvent(f);
        JsonElement v;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(plain);
            v = GaiaDeskJson.Parse(text);
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or ArgumentException)
        {
            throw new E2eOpenException(Reasons.E2eMalformed, "a sealed event is not JSON");
        }
        var ev = GaiaDeskJson.Str(v, "event");
        if (ev is not ("stdout" or "stderr" or "exit" or "error")) throw new E2eOpenException(Reasons.E2eMalformed, "a sealed event is not a desk event");
        return v;
    }
}
