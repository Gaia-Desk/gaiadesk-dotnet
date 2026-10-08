// Desk operations: exec (buffered and streamed), background jobs, stats and
// agent tokens. Relayed to the desk, which verifies the credential itself;
// on the hosted API sealed end to end when the desk can open them.

using System.Text.Json;
using System.Text.Json.Nodes;
using GaiaDesk.E2e;
using GaiaDesk.Http;

namespace GaiaDesk;

public sealed partial class GaiaDeskClient
{
    private static string WireShell(Shell s) => s switch
    {
        Shell.Default => "default",
        Shell.None => "none",
        Shell.Sh => "sh",
        Shell.Bash => "bash",
        Shell.Zsh => "zsh",
        Shell.Cmd => "cmd",
        Shell.Pwsh or Shell.PowerShell => "pwsh",
        _ => throw Errors.Usage($"not a shell: {s}"),
    };

    private static JsonArray Strings(IEnumerable<string> v) => new(v.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    private static JsonObject EnvJson(IReadOnlyDictionary<string, string> env)
    {
        var o = new JsonObject();
        foreach (var kv in Check.Env(env)) o[kv.Key] = kv.Value;
        return o;
    }

    private static JsonObject ExecSpec(string? command, IReadOnlyList<string>? argv, ExecOptions o)
    {
        JsonObject spec;
        if (command is not null)
        {
            if (command.Trim().Length == 0) throw Errors.Usage("exec needs a command");
            spec = new JsonObject { ["command"] = command };
        }
        else spec = new JsonObject { ["argv"] = Strings(Check.Command(argv, "exec")) };
        if (o.Shell is { } sh) spec["shell"] = WireShell(sh);
        if (o.Env is not null) spec["env"] = EnvJson(o.Env);
        if (o.Cwd is not null) spec["cwd"] = Check.Cwd(o.Cwd);
        if (o.Timeout is { } t) spec["timeout_secs"] = Check.Seconds(t, "Timeout");
        if (o.Stdin is not null) spec["stdin"] = o.Stdin;
        return spec;
    }

    /// <summary>
    /// <c>POST /desks/{id}/exec</c>: run one command line (given to the desk's shell verbatim) and wait for it.
    /// A command that ran answers whatever its exit code; one that never ran (refused, unreachable, a
    /// <c>cwd</c> that is not there, a refusal) is its typed error. <see cref="ExecOptions.Check"/>:
    /// a non-zero exit is a <see cref="CommandException"/>.
    /// </summary>
    public Task<ExecResult> ExecAsync(string deskId, string command, ExecOptions? options = null, CancellationToken cancellationToken = default) =>
        Exec(deskId, command, null, options ?? new ExecOptions(), cancellationToken);

    /// <summary><c>POST /desks/{id}/exec</c> with an argument list (each quoted for the desk's shell; <see cref="Shell.None"/>: run directly).</summary>
    public Task<ExecResult> ExecAsync(string deskId, IReadOnlyList<string> argv, ExecOptions? options = null, CancellationToken cancellationToken = default) =>
        Exec(deskId, null, argv, options ?? new ExecOptions(), cancellationToken);

    private async Task<ExecResult> Exec(string deskId, string? command, IReadOnlyList<string>? argv, ExecOptions o, CancellationToken ct)
    {
        var desk = Check.Desk(deskId);
        var spec = ExecSpec(command, argv, o);
        var r = new ApiRequest("POST", $"{DeskPath(desk)}/exec") { Json = spec }.With(o);
        r.E2e = (desk, "exec", new JsonObject { ["op"] = "exec", ["spec"] = spec.DeepClone() });
        var json = await _core.JsonAsync(r, ct).ConfigureAwait(false);
        if (!GaiaDeskJson.TryProp(json, "exit", out var x) || x.ValueKind != JsonValueKind.Number)
            throw new ProtocolException("the GaiaDesk API answered exec without a result", new ErrorDetails { Operation = r.Operation, Json = json, ExitCode = 255 });
        var res = GaiaDeskJson.To<ExecResult>(json, r.Operation);
        // It never ran: no code of its own, not merely out of time, and an error says why.
        if (res.RemoteCode is null && !res.TimedOut && res.Error is { } e)
        {
            throw Errors.ForKind(e.Kind, string.IsNullOrEmpty(e.Message) ? "the command did not run" : e.Message, new ErrorDetails
            {
                Kind = ErrorKinds.For(e.Kind, e.Reason), ExitCode = res.Exit, Operation = r.Operation, Json = json,
                Reason = e.Reason, Desk = e.Desk ?? res.Desk,
            });
        }
        if (o.Check && res.Exit != 0)
        {
            var why = res.TimedOut ? "timed out" : $"exited {res.Exit}";
            throw new CommandException($"command on desk {res.Desk} {why}", res,
                new ErrorDetails { ExitCode = res.Exit, Operation = r.Operation, Json = json, Desk = res.Desk, Kind = ErrorKinds.Failed });
        }
        return res;
    }

    /// <summary>
    /// <c>POST /desks/{id}/exec?stream=1</c>: run a command line and read its output as it comes
    /// (Server-Sent Events). Stdin is given up front (<see cref="ExecOptions.Stdin"/>). Cancelling
    /// <paramref name="cancellationToken"/> (or <see cref="DeskStream.Kill"/>) stops the command.
    /// </summary>
    public DeskStream ExecStream(string deskId, string command, ExecOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamExec(deskId, command, null, options ?? new ExecOptions(), cancellationToken);

    /// <summary><c>POST /desks/{id}/exec?stream=1</c> with an argument list.</summary>
    public DeskStream ExecStream(string deskId, IReadOnlyList<string> argv, ExecOptions? options = null, CancellationToken cancellationToken = default) =>
        StreamExec(deskId, null, argv, options ?? new ExecOptions(), cancellationToken);

    private DeskStream StreamExec(string deskId, string? command, IReadOnlyList<string>? argv, ExecOptions o, CancellationToken ct)
    {
        if (o.Check) throw Errors.Usage("Check applies to ExecAsync; a stream's end is in WaitAsync (StreamExit.Succeeded)");
        var desk = Check.Desk(deskId);
        var spec = ExecSpec(command, argv, o);
        var path = $"{DeskPath(desk)}/exec";
        var op = $"POST {path}";
        var r = new ApiRequest("POST", path) { Json = spec, Accept = "text/event-stream" }.With(o);
        r.IdempotencyKey = null; // a streamed call is never replayed
        r.Query["stream"] = "1";
        r.E2e = (desk, "exec", new JsonObject { ["op"] = "exec", ["spec"] = spec.DeepClone(), ["stream"] = true });
        return new DeskStream(op, false, async c => await StartOf(await _core.SendAsync(r, c).ConfigureAwait(false), false, op, c).ConfigureAwait(false), ct);
    }

    private async Task<StreamStart> StartOf(ApiResponse res, bool logs, string op, CancellationToken ct)
    {
        var body = await _core.OpenBody(res.Message, op, null, ct).ConfigureAwait(false);
        if (res.Seal is not { } seal) return new StreamStart(body, res, null);
        return new StreamStart(body, res, ev => E2eAnswers.UnsealSse(ev, seal, logs, op, ct));
    }

    // ───────────────────────────── jobs ─────────────────────────────

    /// <summary><c>POST /desks/{id}/jobs</c>: start a background job running a command line (it outlives the call; its end is the <c>job.finished</c> webhook).</summary>
    public Task<Job> RunJobAsync(string deskId, string name, string command, JobOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (command is null || command.Trim().Length == 0) throw Errors.Usage("run needs a command");
        return RunJob(deskId, name, new[] { command }, options ?? new JobOptions(), cancellationToken);
    }

    /// <summary><c>POST /desks/{id}/jobs</c> with an argument list.</summary>
    public Task<Job> RunJobAsync(string deskId, string name, IReadOnlyList<string> command, JobOptions? options = null, CancellationToken cancellationToken = default) =>
        RunJob(deskId, name, Check.Command(command, "run"), options ?? new JobOptions(), cancellationToken);

    private Task<Job> RunJob(string deskId, string name, IReadOnlyList<string> command, JobOptions o, CancellationToken ct)
    {
        var desk = Check.Desk(deskId);
        Check.Job(name);
        var limits = new JsonObject();
        if (o.Priority is { } p) limits["priority"] = p.ToString().ToLowerInvariant();
        if (o.CpuPercent is { } cpu)
        {
            if (cpu is < 1 or > 100) throw Errors.Usage("CpuPercent is a share of the whole machine, 1 to 100");
            limits["cpu_percent"] = cpu;
        }
        if (o.Memory is not null) limits["mem_mb"] = Check.MemMb(o.Memory);
        if (o.KeepAwake is { } ka) limits["keep_awake"] = ka;
        var spec = new JsonObject { ["name"] = name, ["command"] = Strings(command), ["limits"] = limits };
        if (o.Cwd is not null) spec["cwd"] = Check.Cwd(o.Cwd);
        if (o.Shell is { } sh)
        {
            if (sh is Shell.Default or Shell.None) throw Errors.Usage("a job's shell is one of sh, bash, zsh, cmd, pwsh, powershell");
            spec["shell"] = WireShell(sh);
        }
        if (o.Env is not null) spec["env"] = EnvJson(o.Env);
        var r = new ApiRequest("POST", $"{DeskPath(desk)}/jobs") { Json = spec }.With(o);
        r.E2e = (desk, "job_start", new JsonObject { ["op"] = "job_start", ["spec"] = spec.DeepClone() });
        return Json<Job>(r, ct);
    }

    /// <summary><c>GET /desks/{id}/jobs</c>: the desk's background jobs.</summary>
    public async Task<IReadOnlyList<Job>> ListJobsAsync(string deskId, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var desk = Check.Desk(deskId);
        var r = new ApiRequest("GET", $"{DeskPath(desk)}/jobs").With(options);
        r.E2e = (desk, "job_list", new JsonObject { ["op"] = "job_list" });
        return ListOf<Job>(await _core.JsonAsync(r, cancellationToken).ConfigureAwait(false), "jobs", r.Operation);
    }

    /// <summary><c>DELETE /desks/{id}/jobs/{name}</c>: stop a job and everything it started; the stopped job.</summary>
    public Task<Job> KillJobAsync(string deskId, string name, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var desk = Check.Desk(deskId);
        Check.Job(name);
        var r = new ApiRequest("DELETE", $"{DeskPath(desk)}/jobs/{Uri.EscapeDataString(name)}").With(options);
        r.E2e = (desk, "job_kill", new JsonObject { ["op"] = "job_kill", ["name"] = name });
        return Json<Job>(r, cancellationToken);
    }

    private static JsonObject LogsRequest(string name, int? tail, bool follow)
    {
        if (tail is < 0) throw Errors.Usage("tail is a number of bytes");
        var request = new JsonObject { ["op"] = "job_logs", ["name"] = name };
        if (tail is { } t) request["tail"] = t;
        if (follow) request["follow"] = true;
        return request;
    }

    /// <summary><c>GET /desks/{id}/jobs/{name}/logs</c>: the job and the end of its output (<paramref name="tail"/>: the last this many bytes).</summary>
    public Task<JobLogs> JobLogsAsync(string deskId, string name, int? tail = null, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var desk = Check.Desk(deskId);
        Check.Job(name);
        var request = LogsRequest(name, tail, false);
        var r = new ApiRequest("GET", $"{DeskPath(desk)}/jobs/{Uri.EscapeDataString(name)}/logs").With(options);
        if (tail is { } t) r.Query["tail"] = t.ToString(System.Globalization.CultureInfo.InvariantCulture);
        r.E2e = (desk, "job_logs", request);
        return Json<JobLogs>(r, cancellationToken);
    }

    /// <summary>
    /// <c>GET /desks/{id}/jobs/{name}/logs?follow=1</c>: the job's output as it comes, until it ends
    /// (<see cref="StreamExit.Job"/>). Killing the stream stops following; the job goes on.
    /// </summary>
    public DeskStream FollowJobLogs(string deskId, string name, int? tail = null, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var desk = Check.Desk(deskId);
        Check.Job(name);
        var request = LogsRequest(name, tail, true);
        var path = $"{DeskPath(desk)}/jobs/{Uri.EscapeDataString(name)}/logs";
        var op = $"GET {path}";
        var r = new ApiRequest("GET", path) { Accept = "text/event-stream" }.With(options);
        r.Query["follow"] = "1";
        if (tail is { } t) r.Query["tail"] = t.ToString(System.Globalization.CultureInfo.InvariantCulture);
        r.E2e = (desk, "job_logs", request);
        return new DeskStream(op, true, async c => await StartOf(await _core.SendAsync(r, c).ConfigureAwait(false), true, op, c).ConfigureAwait(false), cancellationToken, name);
    }

    /// <summary>
    /// <c>GET /desks/{id}/jobs/{name}/wait</c>: the job once it is no longer running, or (<see cref="JobWaitResult.TimedOut"/>)
    /// as it stands when <paramref name="timeout"/> runs out. One request holds at most 870 s, so a longer (or no)
    /// timeout asks again until the job ends. A held answer's failure (<c>GaiaDesk-Held</c>) is thrown as its typed error.
    /// </summary>
    public async Task<JobWaitResult> WaitJobAsync(string deskId, string name, TimeSpan? timeout = null, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var desk = Check.Desk(deskId);
        Check.Job(name);
        var path = $"{DeskPath(desk)}/jobs/{Uri.EscapeDataString(name)}/wait";
        long? total = timeout is { } t ? Check.Seconds(t, "timeout") : null;
        var started = DateTime.UtcNow;
        for (;;)
        {
            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            var left = total is { } tt ? Math.Max(0, tt - elapsed) : ApiWaitMax;
            var secs = (long)Math.Min(ApiWaitMax, Math.Ceiling(left));
            var r = new ApiRequest("GET", path).With(options);
            r.Query["timeout"] = secs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            r.E2e = (desk, "job_wait", new JsonObject { ["op"] = "job_wait", ["name"] = name, ["timeout_ms"] = secs * 1000 });
            var json = await _core.JsonAsync(r, cancellationToken).ConfigureAwait(false);
            // A held wait that failed after its 200 began: the envelope, in the body.
            if (ErrorEnvelope.From(json) is { } env)
                throw Errors.FromEnvelope(env, "the wait failed", new ErrorDetails { ExitCode = Errors.DeskOpExit(env.Kind), Operation = r.Operation, Json = json });
            if (!GaiaDeskJson.TryProp(json, "job", out var j) || j.ValueKind != JsonValueKind.Object
                || !GaiaDeskJson.TryProp(json, "timed_out", out var to) || (to.ValueKind != JsonValueKind.True && to.ValueKind != JsonValueKind.False))
                throw new ProtocolException("the GaiaDesk API answered a wait without a job", new ErrorDetails { Operation = r.Operation, Json = json, ExitCode = 255 });
            var res = GaiaDeskJson.To<JobWaitResult>(json, r.Operation);
            var over = total is { } t2 && (DateTime.UtcNow - started).TotalSeconds >= t2;
            if (!res.TimedOut || over || total == 0) return res;
        }
    }

    // ───────────────────────────── stats ─────────────────────────────

    /// <summary><c>GET /desks/{id}/stats</c>: CPU, load, memory, disks, uptime and running jobs, as the desk measures them.</summary>
    public Task<StatsReport> StatsAsync(string deskId, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var desk = Check.Desk(deskId);
        var r = new ApiRequest("GET", $"{DeskPath(desk)}/stats").With(options);
        r.E2e = (desk, "stats", new JsonObject { ["op"] = "stats" });
        return Json<StatsReport>(r, cancellationToken);
    }

    // ───────────────────────────── tokens ─────────────────────────────

    /// <summary>
    /// <c>POST /desks/{id}/tokens</c>, once per desk: a scoped agent token on each (the secrets are shown once).
    /// Token administration is the desk owner's (a signed-in person's own desk; an agent token is refused). If a
    /// later desk fails, the error's <see cref="GaiaDeskException.Json"/> carries the tokens already minted (<c>tokens</c>).
    /// </summary>
    public async Task<MintResult> CreateTokenAsync(TokenCreateOptions options, CancellationToken cancellationToken = default)
    {
        if (options is null) throw Errors.Usage("options are required");
        if (options.Desks is null || options.Desks.Count == 0) throw Errors.Usage("at least one desk is required");
        var desks = options.Desks.Select(Check.Desk).ToList();
        if (options.Name is null || options.Name.Trim().Length == 0) throw Errors.Usage("CreateTokenAsync needs a Name over the API");
        var scopes = options.Scopes ?? new[] { TokenScopes.Exec, TokenScopes.Cp, TokenScopes.Jobs };
        if (scopes.Count == 0) throw Errors.Usage("Scopes must not be empty");
        var spec = new JsonObject
        {
            ["name"] = options.Name,
            ["expires_secs"] = Check.Seconds(options.Expires ?? TimeSpan.FromDays(7), "Expires"),
            ["scopes"] = Strings(scopes),
        };
        if (options.Cwd is not null) spec["cwd"] = options.Cwd;
        if (options.LowPriv) spec["low_priv"] = true;
        var tokens = new List<MintedToken>();
        var raw = new JsonArray();
        foreach (var d in desks)
        {
            var r = new ApiRequest("POST", $"{DeskPath(d)}/tokens") { Json = spec }.With(options);
            r.E2e = (d, "token_mint", new JsonObject { ["op"] = "token_mint", ["spec"] = spec.DeepClone() });
            try
            {
                var json = await _core.JsonAsync(r, cancellationToken).ConfigureAwait(false);
                tokens.AddRange(ListOf<MintedToken>(json, "tokens", "token_mint"));
                foreach (var t in json.GetProperty("tokens").EnumerateArray()) raw.Add(JsonNode.Parse(t.GetRawText()));
            }
            catch (GaiaDeskException e) when (tokens.Count > 0)
            {
                var o = e.Json is { } ej && ej.ValueKind == JsonValueKind.Object ? (JsonObject)JsonNode.Parse(ej.GetRawText())! : new JsonObject();
                o["tokens"] = raw;
                e.Json = GaiaDeskJson.Element(o);
                throw;
            }
        }
        return new MintResult { Tokens = tokens };
    }

    /// <summary><c>GET /desks/{id}/tokens</c>: the desk's agent tokens (never their secrets).</summary>
    public async Task<IReadOnlyList<TokenInfo>> ListTokensAsync(string deskId, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var desk = Check.Desk(deskId);
        var r = new ApiRequest("GET", $"{DeskPath(desk)}/tokens").With(options);
        r.E2e = (desk, "token_list", new JsonObject { ["op"] = "token_list" });
        return ListOf<TokenInfo>(await _core.JsonAsync(r, cancellationToken).ConfigureAwait(false), "tokens", r.Operation);
    }

    /// <summary><c>DELETE /desks/{id}/tokens/{token_id}</c>: revoke a token by id or name; its live sessions and jobs end.</summary>
    public Task<TokenRevokeResult> RevokeTokenAsync(string deskId, string token, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var desk = Check.Desk(deskId);
        if (token is null || token.Length == 0 || token.StartsWith("-", StringComparison.Ordinal)) throw Errors.Usage("a token name or id is required");
        var r = new ApiRequest("DELETE", $"{DeskPath(desk)}/tokens/{Uri.EscapeDataString(token)}").With(options);
        r.E2e = (desk, "token_revoke", new JsonObject { ["op"] = "token_revoke", ["token"] = token });
        return Json<TokenRevokeResult>(r, cancellationToken);
    }
}
