// The mock's desk operations: what the desk does (canned, deterministic
// events per operation), and how the API answers them, plaintext or sealed.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GaiaDesk.E2e;
using Microsoft.AspNetCore.Http;

namespace GaiaDesk.Tests.Fixtures;

internal sealed partial class MockApi
{
    private static JsonObject Out(byte[] b, string stream = "stdout") => new() { ["event"] = stream, ["data"] = Convert.ToBase64String(b) };
    private static JsonObject Out(string s) => Out(Encoding.UTF8.GetBytes(s));
    private static JsonObject Exit(JsonNode result) => new() { ["event"] = "exit", ["result"] = result };
    private static JsonObject Err(string kind, string message, string? reason = null)
    {
        var o = new JsonObject { ["event"] = "error", ["kind"] = kind, ["message"] = message };
        if (reason is not null) o["reason"] = reason;
        return o;
    }

    private static string? S(JsonNode? n, string k) => n?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>What the desk does: its events for one operation.</summary>
    private List<JsonObject> Run(string desk, MockDesk d, JsonObject req, byte[] input)
    {
        switch (S(req, "op"))
        {
            case "exec":
            {
                var spec = req["spec"]?.AsObject() ?? new JsonObject();
                var cmd = S(spec, "command") ?? string.Join(" ", spec["argv"]?.AsArray().Select(x => x!.GetValue<string>()) ?? Array.Empty<string>());
                if (cmd == "refuse") return new() { Err("refused", $"this token (bot) has no exec scope on desk {desk}", "token_refused") };
                var admin = spec["admin"] is JsonValue av && av.GetValue<bool>();
                if (admin && d.AdminRefusal is { } why)
                    return new() { Exit(new JsonObject
                    {
                        ["desk"] = desk, ["exit"] = 254, ["remote_code"] = null, ["duration_ms"] = 3, ["notes"] = new JsonArray(), ["stdout"] = "", ["stderr"] = "",
                        ["timed_out"] = false, ["truncated"] = false, ["error"] = new JsonObject { ["kind"] = "refused", ["message"] = $"run as administrator refused: {why}", ["reason"] = why, ["desk"] = desk },
                    }) };
                var text = admin ? "root\n" : $"ran: {cmd} é\n";
                foreach (var kv in spec["env"]?.AsObject() ?? new JsonObject()) text += $"env: {kv.Key}={kv.Value!.GetValue<string>()}\n";
                if (S(spec, "stdin") is { } stdin) text += $"stdin: {stdin}\n";
                if (S(spec, "cwd") is { } cwd) text += $"cwd: {cwd}\n";
                var code = cmd == "exit3" ? 3 : 0;
                JsonObject Result() => new()
                {
                    ["desk"] = desk, ["exit"] = code, ["remote_code"] = code, ["duration_ms"] = 7, ["notes"] = new JsonArray(), ["stdout"] = text, ["stderr"] = "warn\n",
                    ["timed_out"] = false, ["truncated"] = false, ["error"] = null, ["mode"] = "pipes", ["route"] = "the GaiaDesk server", ["shell"] = S(spec, "shell"),
                };
                if (req["stream"] is null) return new() { Exit(Result()) };
                // The output in pieces, a character split across two of them.
                var b = Encoding.UTF8.GetBytes(text);
                var cut = Encoding.UTF8.GetByteCount(text.Substring(0, text.IndexOf('é'))) + 1;
                var events = new List<JsonObject> { Out(b.AsSpan(0, cut).ToArray()), Out(Encoding.UTF8.GetBytes("warn\n"), "stderr"), Out(b.AsSpan(cut).ToArray()) };
                if (cmd == "lose") return events; // the desk goes away (the server says so in the clear)
                if (cmd == "sleep") return new() { events[0], new JsonObject { ["event"] = "hang" } };
                events.Add(Exit(Result()));
                return events;
            }
            case "job_start":
                return new() { Exit(new JsonObject { ["name"] = S(req["spec"], "name"), ["state"] = "running", ["pid"] = 42, ["command"] = req["spec"]!["command"]!.DeepClone(), ["started_at_ms"] = 1791000000000, ["desk"] = desk }) };
            case "job_list":
                return new() { Exit(new JsonObject { ["jobs"] = new JsonArray(new JsonObject { ["name"] = "build", ["state"] = "running", ["pid"] = 42, ["command"] = "make", ["started_at_ms"] = 1 }) }) };
            case "job_kill":
                return new() { Exit(new JsonObject { ["name"] = S(req, "name"), ["state"] = "killed", ["command"] = "make", ["started_at_ms"] = 1, ["desk"] = desk }) };
            case "job_wait":
                if (S(req, "name") == "held-gone") return new() { Err("failed", $"no job named \"{S(req, "name")}\"") };
                if (S(req, "name") == "still")
                    return new() { Exit(new JsonObject { ["job"] = new JsonObject { ["name"] = "still", ["state"] = "running", ["command"] = "x", ["started_at_ms"] = 1 }, ["timed_out"] = true }) };
                return new() { Exit(new JsonObject { ["job"] = new JsonObject { ["name"] = S(req, "name"), ["state"] = "exited", ["exit_code"] = 3, ["command"] = "make", ["started_at_ms"] = 1 }, ["timed_out"] = false }) };
            case "job_logs":
            {
                var name = S(req, "name");
                if (name == "missing") return new() { Err("failed", "no job named \"missing\"") };
                if (req["follow"] is null)
                    return new() { Exit(new JsonObject { ["job"] = new JsonObject { ["name"] = name, ["state"] = "running", ["command"] = "make", ["started_at_ms"] = 1 }, ["output"] = $"tail {req["tail"]?.ToJsonString() ?? "all"}\n" }) };
                var l2 = Encoding.UTF8.GetBytes("line2 é\n");
                return new() { Out("line1\n"), Out(l2.AsSpan(0, 7).ToArray()), Out(l2.AsSpan(7).ToArray()),
                    Exit(new JsonObject { ["job"] = new JsonObject { ["name"] = name, ["state"] = "exited", ["exit_code"] = 0, ["command"] = "make", ["started_at_ms"] = 1 } }) };
            }
            case "stats":
                return new() { Exit(new JsonObject { ["desk"] = desk, ["hostname"] = "studio", ["os"] = "macos", ["cpu_percent"] = 5, ["cpus"] = 8, ["mem_total_mb"] = 16384, ["mem_free_mb"] = 1024, ["uptime_secs"] = 99, ["jobs_running"] = 1, ["disks"] = new JsonArray(new JsonObject { ["mount"] = "/", ["total_mb"] = 1000, ["free_mb"] = 500 }) }) };
            case "file_put":
                lock (Files) Files[S(req, "path")!] = input;
                if (S(req, "path") == "fails")
                    return new() { Exit(new JsonObject { ["direction"] = "upload", ["desk"] = desk, ["destination"] = "fails", ["files"] = 0, ["dirs"] = 0, ["bytes"] = 0, ["resumed_bytes"] = 0, ["failed"] = new JsonArray(new JsonObject { ["path"] = "fails", ["message"] = "disk full" }), ["seconds"] = 0 }) };
                return new() { Exit(new JsonObject { ["direction"] = "upload", ["desk"] = desk, ["destination"] = S(req, "path"), ["files"] = 1, ["dirs"] = 0, ["bytes"] = input.Length, ["resumed_bytes"] = 0, ["failed"] = new JsonArray(), ["seconds"] = 0 }) };
            case "file_get":
            {
                var path = S(req, "path")!;
                if (path == "missing") return new() { Err("failed", "no such file: missing", "not_found") };
                byte[]? data;
                lock (Files) Files.TryGetValue(path, out data);
                data ??= Encoding.UTF8.GetBytes($"contents of {path}\n");
                var ev = new List<JsonObject>();
                for (var i = 0; i < data.Length; i += 48 * 1024) ev.Add(Out(data.AsSpan(i, Math.Min(48 * 1024, data.Length - i)).ToArray()));
                ev.Add(Exit(new JsonObject { ["direction"] = "download", ["desk"] = desk, ["destination"] = path, ["files"] = 1, ["dirs"] = 0, ["bytes"] = data.Length, ["resumed_bytes"] = 0, ["failed"] = new JsonArray(), ["seconds"] = 0 }));
                return ev;
            }
            case "token_mint":
                if (desk == "888888888") return new() { Err("refused", "this desk refuses", "desk_opted_out") };
                return new() { Exit(new JsonObject { ["tokens"] = new JsonArray(new JsonObject { ["desk"] = desk, ["secret"] = "gdagt_minted_secret", ["token"] = new JsonObject { ["id"] = "tok1", ["label"] = S(req["spec"], "name"), ["scopes"] = req["spec"]!["scopes"]!.DeepClone(), ["issued_at_ms"] = 1, ["expires_at_ms"] = 2 } }) }) };
            case "token_list":
                return new() { Exit(new JsonObject { ["tokens"] = new JsonArray(new JsonObject { ["id"] = "tok1", ["label"] = "bot", ["issued_at_ms"] = 1, ["expires_at_ms"] = 2 }) }) };
            case "token_revoke":
                return new() { Exit(new JsonObject { ["revoked"] = S(req, "token"), ["stopped_sessions"] = 0 }) };
        }
        return new() { Err("protocol", "unknown operation", "unknown_op") };
    }

    /// <summary>The status /v1 answers a desk's error with (protocol desk_op_http.rs).</summary>
    private static int StatusOf(string kind, string? reason) => E2eAnswers.DeskErrorStatus(kind, reason).Status;

    /// <summary>The route's operation and its plaintext request.</summary>
    private static (string Op, JsonObject Req, string? Stream)? RouteOp(string method, string rest, Dictionary<string, string> q, byte[] body)
    {
        JsonObject Json() => body.Length > 0 ? JsonNode.Parse(body)!.AsObject() : new JsonObject();
        if (rest == "/exec" && method == "POST")
        {
            var r = new JsonObject { ["op"] = "exec", ["spec"] = Json() };
            var stream = q.TryGetValue("stream", out var s) && s == "1";
            if (stream) r["stream"] = true;
            return ("exec", r, stream ? "exec" : null);
        }
        if (rest == "/jobs" && method == "POST") return ("job_start", new JsonObject { ["op"] = "job_start", ["spec"] = Json() }, null);
        if (rest == "/jobs" && method == "GET") return ("job_list", new JsonObject { ["op"] = "job_list" }, null);
        if (rest == "/stats" && method == "GET") return ("stats", new JsonObject { ["op"] = "stats" }, null);
        if (rest == "/files" && method == "PUT") return ("file_put", new JsonObject { ["op"] = "file_put", ["path"] = q.GetValueOrDefault("path"), ["size"] = body.Length }, null);
        if (rest == "/files" && method == "GET") return ("file_get", new JsonObject { ["op"] = "file_get", ["path"] = q.GetValueOrDefault("path") }, null);
        if (rest == "/tokens" && method == "POST") return ("token_mint", new JsonObject { ["op"] = "token_mint", ["spec"] = Json() }, null);
        if (rest == "/tokens" && method == "GET") return ("token_list", new JsonObject { ["op"] = "token_list" }, null);
        var m = System.Text.RegularExpressions.Regex.Match(rest, "^/tokens/([^/]+)$");
        if (m.Success && method == "DELETE") return ("token_revoke", new JsonObject { ["op"] = "token_revoke", ["token"] = Uri.UnescapeDataString(m.Groups[1].Value) }, null);
        m = System.Text.RegularExpressions.Regex.Match(rest, "^/jobs/([^/]+)(/logs|/wait)?$");
        if (!m.Success) return null;
        var name = Uri.UnescapeDataString(m.Groups[1].Value);
        if (!m.Groups[2].Success && method == "DELETE") return ("job_kill", new JsonObject { ["op"] = "job_kill", ["name"] = name }, null);
        if (m.Groups[2].Value == "/wait")
            return ("job_wait", new JsonObject { ["op"] = "job_wait", ["name"] = name, ["timeout_ms"] = q.TryGetValue("timeout", out var t) ? long.Parse(t) * 1000 : null }, null);
        if (m.Groups[2].Value == "/logs")
        {
            var r = new JsonObject { ["op"] = "job_logs", ["name"] = name };
            if (q.TryGetValue("tail", out var tail)) r["tail"] = long.Parse(tail);
            var follow = q.TryGetValue("follow", out var f) && f == "1";
            if (follow) r["follow"] = true;
            return ("job_logs", r, follow ? "logs" : null);
        }
        return null;
    }

    /// <summary>The plaintext SSE events of a desk's events (as the server's ExecEvents / LogEvents).</summary>
    private sealed class PlainMap
    {
        private readonly Dictionary<string, Decoder> _dec = new() { ["stdout"] = new UTF8Encoding(false).GetDecoder(), ["stderr"] = new UTF8Encoding(false).GetDecoder() };
        private readonly bool _logs;
        private readonly string _desk;
        public PlainMap(bool logs, string desk) { _logs = logs; _desk = desk; }

        private string Decode(string s, byte[] b, bool flush)
        {
            var chars = new char[b.Length + 8];
            return new string(chars, 0, _dec[s].GetChars(b, 0, b.Length, chars, 0, flush));
        }

        public List<(string, JsonObject)> Map(JsonObject e)
        {
            var ev = S(e, "event");
            if (ev is "stdout" or "stderr")
            {
                var s = _logs ? "stdout" : ev;
                var t = Decode(s, Convert.FromBase64String(S(e, "data")!), false);
                if (t.Length == 0) return new();
                return _logs ? new() { ("output", new JsonObject { ["event"] = "output", ["data"] = t }) } : new() { (ev, new JsonObject { ["event"] = ev, ["data"] = t }) };
            }
            if (ev == "exit")
            {
                var r = e["result"]!.AsObject();
                if (_logs)
                {
                    var t = Decode("stdout", Array.Empty<byte>(), true);
                    var o = new List<(string, JsonObject)>();
                    if (t.Length > 0) o.Add(("output", new JsonObject { ["event"] = "output", ["data"] = t }));
                    o.Add(r["interrupted"] is not null ? ("interrupted", new JsonObject { ["event"] = "interrupted" }) : ("end", new JsonObject { ["event"] = "end", ["job"] = r["job"]?.DeepClone() }));
                    return o;
                }
                var v = new List<(string, JsonObject)>();
                foreach (var s in new[] { "stdout", "stderr" })
                {
                    var t = Decode(s, Array.Empty<byte>(), true);
                    if (t.Length > 0) v.Add((s, new JsonObject { ["event"] = s, ["data"] = t }));
                }
                var rest = (JsonObject)r.DeepClone();
                rest.Remove("stdout"); rest.Remove("stderr"); rest.Remove("truncated");
                rest["event"] = "exit";
                v.Add(("exit", rest));
                return v;
            }
            if (ev != "error") return new();
            var error = new JsonObject { ["kind"] = S(e, "kind"), ["message"] = S(e, "message"), ["desk"] = _desk };
            if (S(e, "reason") is { } reason) error["reason"] = reason;
            return new() { ("error", _logs ? new JsonObject { ["event"] = "error", ["error"] = error } : new JsonObject { ["event"] = "error", ["exit"] = S(e, "kind") == "refused" ? 254 : 255, ["error"] = error }) };
        }
    }

    private static async Task Write(HttpContext ctx, string s)
    {
        await ctx.Response.WriteAsync(s, ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    private async Task DeskOp(HttpContext ctx, Recorded rec, string id, string rest, MockDesk d, byte[] body)
    {
        // A sealed request: the POST body's `e2e`, or the header.
        JsonElement? sealedReq = null;
        if (rec.Method == "POST" && body.Length > 0)
        {
            var j = JsonDocument.Parse(body).RootElement;
            if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty("e2e", out var e2e)) sealedReq = e2e.Clone();
        }
        if (rec.Header("gaiadesk-e2e") is { } h) sealedReq = JsonDocument.Parse(E2eCrypto.B64Decode(h)!).RootElement.Clone();
        var r = RouteOp(rec.Method, rest, rec.Query, sealedReq is null ? body : Array.Empty<byte>());
        if (r is not { } route) { await Fail(ctx, 400, "usage", "no route", "no_route"); return; }
        if (!d.Online && sealedReq is null) d.Online = true; // the API wakes it for the operation
        var op = route.Req;
        DeskSeal? seal = null;
        var input = body;
        if (sealedReq is { } sr)
        {
            if (d.Secret is null) { await Fail(ctx, 409, "protocol", "the desk cannot open end-to-end encrypted operations", "e2e_unsupported", id); return; }
            (byte[] Plain, DeskSeal Seal)? opened = null;
            foreach (var k in new[] { d.Secret }.Concat(d.Previous))
            {
                try { opened = DeskSeal.OpenRequest(k, id, route.Op, sr); break; }
                catch (E2eOpenException) { }
            }
            if (opened is not { } o) { await Fail(ctx, 403, "refused", "the end-to-end encrypted request did not open: it was altered, or sealed to another key (fetch the desk's e2e_pub again)", "e2e_decrypt_failed", id); return; }
            var inner = JsonNode.Parse(o.Plain)!.AsObject();
            if (inner["v"]!.GetValue<int>() != 1 || Math.Abs(inner["ts"]!.GetValue<long>() - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) > 600) { await Fail(ctx, 403, "refused", "stale", "e2e_stale", id); return; }
            op = inner["request"]!.AsObject();
            if (S(op, "op") != route.Op) { await Fail(ctx, 403, "refused", "op mismatch", "e2e_op_mismatch", id); return; }
            seal = o.Seal;
            if (route.Op == "file_put")
            {
                var parts = new MemoryStream();
                var last = false;
                foreach (var line in rec.BodyText.Split('\n').Where(l => l.Trim().Length > 0))
                {
                    var f = seal.OpenInput(JsonDocument.Parse(line).RootElement);
                    parts.Write(f.Data);
                    last = f.Last;
                }
                if (!last) { await Fail(ctx, 400, "usage", "the upload ended early", "body_interrupted", id); return; }
                input = parts.ToArray();
            }
            lock (Sealed) Sealed.Add(route.Op);
        }
        else
        {
            if (d.Required && Mode == MockMode.Hosted) { await Fail(ctx, 409, "refused", "This desk requires end-to-end encryption for API commands.", "e2e_required", id); return; }
            lock (Plain) Plain.Add(route.Op);
        }
        var events = Run(id, d, op, input);
        JsonObject SealOne(JsonObject e)
        {
            var f = seal!.SealEvent(e);
            var ct = f.Ciphertext;
            if (Tamper == "flip")
            {
                var c = E2eCrypto.B64Decode(ct)!;
                c[0] ^= 1;
                ct = E2eCrypto.B64Url(c);
            }
            return new JsonObject { ["seq"] = f.Seq, ["nonce"] = f.Nonce, ["ciphertext"] = ct };
        }
        var final = events.Last();
        var failed = S(final, "event") == "error" ? final : null;
        JsonObject ErrorAnswer(JsonObject? extra = null)
        {
            var kind = S(failed, "kind")!;
            var k = kind is "refused" or "usage" or "protocol" or "unreachable" or "connection_lost" ? kind : "failed";
            var env = Envelope(k, seal is not null ? "The desk reported an error (end-to-end encrypted)." : S(failed, "message")!, S(failed, "reason"), id);
            foreach (var kv in extra ?? new JsonObject()) env["error"]![kv.Key] = kv.Value?.DeepClone();
            if (seal is not null) env["e2e"] = new JsonObject { ["v"] = 1, ["events"] = new JsonArray(events.Select(e => (JsonNode?)SealOne(e)).ToArray()) };
            return env;
        }

        // Streams.
        if (route.Stream is { } kindOfStream)
        {
            if (S(events[0], "event") == "error") { failed = events[0]; await Send(ctx, StatusOf(S(failed, "kind")!, S(failed, "reason")), ErrorAnswer()); return; }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["X-Request-Id"] = RequestId();
            var map = new PlainMap(kindOfStream == "logs", id);
            foreach (var e in events)
            {
                if (S(e, "event") == "hang")
                {
                    while (true) { await Write(ctx, ": keep-alive\n\n"); await Task.Delay(20, ctx.RequestAborted); }
                }
                await Write(ctx, ": keep-alive\r\n\r\n");
                if (seal is not null && Tamper != "plaintext")
                {
                    var f = SealOne(e);
                    f["event"] = "sealed";
                    await Write(ctx, $"event: sealed\ndata: {f.ToJsonString()}\n\n");
                }
                else foreach (var (name, v) in map.Map(e))
                {
                    // Split each event across two writes, to exercise the client's parser.
                    var text = $"event: {name}\r\ndata: {v.ToJsonString()}\r\n\r\n";
                    await Write(ctx, text.Substring(0, text.Length / 2));
                    await Write(ctx, text.Substring(text.Length / 2));
                }
                await Task.Delay(2);
            }
            if (S(final, "event") is not ("exit" or "error"))
            {
                var lost = new JsonObject { ["event"] = "error", ["error"] = new JsonObject { ["kind"] = "connection_lost", ["message"] = "The desk went away during this operation.", ["desk"] = id, ["reason"] = "desk_disconnected" } };
                if (kindOfStream == "exec") lost["exit"] = 255;
                await Write(ctx, $"event: error\ndata: {lost.ToJsonString()}\n\n");
            }
            return;
        }

        // A download.
        if (route.Op == "file_get")
        {
            if (S(events[0], "event") == "error") { failed = events[0]; await Send(ctx, StatusOf(S(failed, "kind")!, S(failed, "reason")), ErrorAnswer()); return; }
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers["X-Request-Id"] = RequestId();
            var path = S(op, "path");
            if (seal is null)
            {
                ctx.Response.ContentType = "application/octet-stream";
                foreach (var e in events.Where(e => S(e, "event") == "stdout"))
                {
                    await ctx.Response.Body.WriteAsync(Convert.FromBase64String(S(e, "data")!));
                    await ctx.Response.Body.FlushAsync();
                    if (path == "broken") { ctx.Abort(); return; }
                }
                return;
            }
            ctx.Response.ContentType = "application/x-ndjson";
            var keep = path == "truncated" ? events.Take(events.Count - 1) : events;
            foreach (var e in keep) await Write(ctx, SealOne(e).ToJsonString() + "\n");
            return;
        }

        var okStatus = route.Op is "job_start" or "token_mint" ? 201 : 200;
        var held = route.Op == "job_wait" && S(op, "name") is "held" or "held-gone";
        if (held)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers["X-Request-Id"] = RequestId();
            ctx.Response.Headers["GaiaDesk-Held"] = "1";
            for (var i = 0; i < 3; i++) { await Write(ctx, " "); await Task.Delay(2); }
            if (failed is not null) { await Write(ctx, ErrorAnswer(new JsonObject { ["status"] = StatusOf(S(failed, "kind")!, S(failed, "reason")) }).ToJsonString()); return; }
            JsonNode heldBody = seal is not null ? new JsonObject { ["e2e"] = new JsonObject { ["v"] = 1, ["events"] = new JsonArray(events.Select(e => (JsonNode?)SealOne(e)).ToArray()) } } : final["result"]!.DeepClone();
            await Write(ctx, heldBody.ToJsonString());
            return;
        }
        if (failed is not null) { await Send(ctx, StatusOf(S(failed, "kind")!, S(failed, "reason")), ErrorAnswer()); return; }
        if (seal is not null && Tamper != "plaintext") { await Send(ctx, okStatus, new JsonObject { ["e2e"] = new JsonObject { ["v"] = 1, ["events"] = new JsonArray(events.Select(e => (JsonNode?)SealOne(e)).ToArray()) } }); return; }
        await Send(ctx, okStatus, final["result"]!.DeepClone());
    }
}
