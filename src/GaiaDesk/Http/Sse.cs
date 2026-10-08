// Server-Sent Events (text/event-stream): an incremental parser (the WHATWG
// rules: fields, comments, blank-line dispatch; any chunking, even between
// `\r` and `\n`) and a reader over a response body.

using System.Runtime.CompilerServices;
using System.Text;

namespace GaiaDesk.Http;

/// <summary>One server-sent event: its <c>event:</c> name (default <c>message</c>) and its <c>data:</c> lines joined by <c>\n</c>.</summary>
internal sealed record SseEvent(string Event, string Data);

internal sealed class SseParser
{
    private readonly StringBuilder _buf = new();
    private string _event = "";
    private readonly List<string> _data = new();

    /// <summary>Feed decoded text; returns the events it completed.</summary>
    public List<SseEvent> Feed(string text)
    {
        _buf.Append(text);
        var o = new List<SseEvent>();
        var s = _buf.ToString();
        var start = 0;
        for (;;)
        {
            var i = s.IndexOfAny(new[] { '\r', '\n' }, start);
            if (i < 0) break;
            int len;
            if (s[i] == '\r')
            {
                // A trailing `\r` may be the first half of `\r\n`: wait for the next chunk.
                if (i == s.Length - 1) break;
                len = s[i + 1] == '\n' ? 2 : 1;
            }
            else len = 1;
            var ev = Line(s.Substring(start, i - start));
            if (ev is not null) o.Add(ev);
            start = i + len;
        }
        _buf.Clear();
        _buf.Append(s, start, s.Length - start);
        return o;
    }

    /// <summary>The end of the stream: an event the server did not finish with a blank line is still delivered.</summary>
    public List<SseEvent> End()
    {
        var o = new List<SseEvent>();
        if (_buf.Length > 0)
        {
            var rest = _buf.ToString().TrimEnd('\r');
            _buf.Clear();
            var ev = Line(rest);
            if (ev is not null) o.Add(ev);
        }
        var last = Line("");
        if (last is not null) o.Add(last);
        return o;
    }

    private SseEvent? Line(string line)
    {
        if (line.Length == 0)
        {
            if (_data.Count == 0)
            {
                _event = "";
                return null;
            }
            var ev = new SseEvent(_event.Length == 0 ? "message" : _event, string.Join("\n", _data));
            _event = "";
            _data.Clear();
            return ev;
        }
        if (line[0] == ':') return null; // a comment: keep-alive
        var c = line.IndexOf(':');
        var field = c < 0 ? line : line.Substring(0, c);
        var value = c < 0 ? "" : line.Substring(c + 1);
        if (value.StartsWith(" ", StringComparison.Ordinal)) value = value.Substring(1);
        if (field == "event") _event = value;
        else if (field == "data") _data.Add(value);
        return null;
    }

    /// <summary>The events of a <c>text/event-stream</c> body, as they arrive.</summary>
    public static async IAsyncEnumerable<SseEvent> Read(Stream body, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var p = new SseParser();
        var dec = new UTF8Encoding(false, false).GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        for (;;)
        {
            var n = await body.ReadAsync(bytes.AsMemory(0, bytes.Length), ct).ConfigureAwait(false);
            if (n == 0) break;
            var cn = dec.GetChars(bytes, 0, n, chars, 0, false);
            foreach (var ev in p.Feed(new string(chars, 0, cn))) yield return ev;
        }
        var tail = dec.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, true);
        foreach (var ev in p.Feed(new string(chars, 0, tail))) yield return ev;
        foreach (var ev in p.End()) yield return ev;
    }
}
