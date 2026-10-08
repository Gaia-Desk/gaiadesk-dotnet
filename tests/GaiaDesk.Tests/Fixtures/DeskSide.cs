// A desk's side of end-to-end encrypted desk operations, for the tests only:
// open a sealed request with the desk's static secret, seal events back,
// open input frames. Built from the SDK's own primitives, in the order the
// protocol's crypto.rs gives, so a mock API can act as a desk.

using System.Text.Json;
using System.Text.Json.Nodes;
using GaiaDesk.E2e;

namespace GaiaDesk.Tests.Fixtures;

internal sealed class DeskSeal
{
    private readonly OpKeys _keys;
    private ulong _nextInput;
    private ulong _nextEvent;

    public DeskSeal(OpKeys keys, string desk, string op)
    {
        _keys = keys;
        Desk = desk;
        Op = op;
    }

    public string Desk { get; }
    public string Op { get; }

    public SealedFrame SealEventWith(byte[] nonce, byte[] plaintext)
    {
        var seq = _nextEvent++;
        var ct = E2eCrypto.AeadSeal(_keys.Event, nonce, E2eCrypto.AssociatedData("event", Desk, Op, seq), plaintext);
        return new SealedFrame(seq, E2eCrypto.B64Url(nonce), E2eCrypto.B64Url(ct));
    }

    /// <summary>Seal a desk event (<c>{"event": …}</c>) as JSON.</summary>
    public SealedFrame SealEvent(JsonNode ev) => SealEventWith(E2eCrypto.Random(24), E2eCrypto.Utf8(ev.ToJsonString()));

    /// <summary>The caller's next input frame: (last, bytes).</summary>
    public (bool Last, byte[] Data) OpenInput(JsonElement f)
    {
        var seq = f.GetProperty("seq").GetUInt64();
        if (seq != _nextInput) throw new InvalidOperationException("input out of order");
        var plain = E2eCrypto.AeadOpen(_keys.Input, f.GetProperty("nonce").GetString()!, f.GetProperty("ciphertext").GetString()!,
            E2eCrypto.AssociatedData("input", Desk, Op, seq));
        _nextInput++;
        if (plain[0] is not (0 or 1)) throw new InvalidOperationException("bad input flag");
        return (plain[0] == 1, plain.AsSpan(1).ToArray());
    }

    /// <summary>Open a sealed request to <paramref name="desk"/> as <paramref name="op"/> with the desk's secret.</summary>
    public static (byte[] Plain, DeskSeal Seal) OpenRequest(byte[] deskSecret, string desk, string op, JsonElement req)
    {
        if (req.GetProperty("v").GetInt32() != 1) throw new InvalidOperationException("bad version");
        var ephPub = E2eCrypto.B64Decode(req.GetProperty("pub").GetString());
        if (ephPub is not { Length: 32 }) throw new InvalidOperationException("bad pub");
        var deskPub = E2eCrypto.X25519Public(deskSecret);
        var keys = E2eCrypto.DeriveKeys(E2eCrypto.X25519(deskSecret, ephPub), ephPub, deskPub);
        var plain = E2eCrypto.AeadOpen(keys.Request, req.GetProperty("nonce").GetString()!, req.GetProperty("ciphertext").GetString()!,
            E2eCrypto.AssociatedData("request", desk, op));
        return (plain, new DeskSeal(keys, desk, op));
    }

    public static (byte[] Plain, DeskSeal Seal) OpenRequest(byte[] deskSecret, string desk, string op, SealedRequest req) =>
        OpenRequest(deskSecret, desk, op, JsonDocument.Parse(req.ToJson().ToJsonString()).RootElement);
}
