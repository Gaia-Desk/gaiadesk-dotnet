// Desk operation results: the objects `gaiadesk-cli … --json` prints, which
// the API's contract references (client/schema.json). Field names are the
// wire's snake_case; unknown fields are kept in AdditionalProperties.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace GaiaDesk;

/// <summary>Base of every result: fields this SDK version does not know are kept here.</summary>
public abstract class GaiaDeskObject
{
    /// <summary>Fields the API sent that this SDK version has no property for.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>What went wrong, as the API and gaiadesk-cli report it (exec's <c>error</c>, a stream's error).</summary>
public sealed class ErrorInfo : GaiaDeskObject
{
    /// <summary>One of six: usage, refused, unreachable, connection_lost, failed, protocol.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    /// <summary>For a person.</summary>
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    /// <summary>The finer cause (<c>offline</c>, <c>timeout</c>, <c>admin_denied</c>, …).</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    /// <summary>The desk it concerned.</summary>
    [JsonPropertyName("desk")] public string? Desk { get; set; }
}

/// <summary>How a run ended, without its output: exec's fields other than stdout/stderr (a stream's <c>exit</c> event).</summary>
public class ExecExit : GaiaDeskObject
{
    /// <summary>What gaiadesk-cli exits with: the command's code, 124 timed out, 130 interrupted, 254 refused.</summary>
    [JsonPropertyName("exit")] public int Exit { get; set; }
    /// <summary>The command's own exit code; null when it never ran or was killed.</summary>
    [JsonPropertyName("remote_code")] public int? RemoteCode { get; set; }
    /// <summary>How long it ran.</summary>
    [JsonPropertyName("duration_ms")] public long DurationMs { get; set; }
    /// <summary>The desk.</summary>
    [JsonPropertyName("desk")] public string Desk { get; set; } = "";
    /// <summary>The road it took.</summary>
    [JsonPropertyName("route")] public string? Route { get; set; }
    /// <summary>pipes / pty.</summary>
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    /// <summary>The shell that ran it.</summary>
    [JsonPropertyName("shell")] public string? Shell { get; set; }
    /// <summary>Its time limit ran out.</summary>
    [JsonPropertyName("timed_out")] public bool TimedOut { get; set; }
    /// <summary>Why it did not run or end on its own; null when it ran and ended on its own.</summary>
    [JsonPropertyName("error")] public ErrorInfo? Error { get; set; }
    /// <summary>Notes about the run.</summary>
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();
}

/// <summary><c>POST /desks/{id}/exec</c>: the run and its output (as <c>gaiadesk-cli exec --json</c>).</summary>
public sealed class ExecResult : ExecExit
{
    /// <summary>Its standard output (UTF-8 text).</summary>
    [JsonPropertyName("stdout")] public string Stdout { get; set; } = "";
    /// <summary>Its standard error.</summary>
    [JsonPropertyName("stderr")] public string Stderr { get; set; } = "";
    /// <summary>The output was over the 8 MB buffered for a JSON answer (stream it for more).</summary>
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
}

/// <summary>A background job's resource limits.</summary>
public sealed class JobLimits : GaiaDeskObject
{
    /// <summary>low, normal or high.</summary>
    [JsonPropertyName("priority")] public string? Priority { get; set; }
    /// <summary>Share of the whole machine, 1-100.</summary>
    [JsonPropertyName("cpu_percent")] public int? CpuPercent { get; set; }
    /// <summary>Memory cap in MB.</summary>
    [JsonPropertyName("mem_mb")] public long? MemMb { get; set; }
    /// <summary>Keep the desk awake while it runs.</summary>
    [JsonPropertyName("keep_awake")] public bool? KeepAwake { get; set; }
}

/// <summary>A background job (<c>run</c>, <c>ps</c>, <c>kill</c>).</summary>
public sealed class Job : GaiaDeskObject
{
    /// <summary>Its name.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Its command line (an argument list some desks send is joined with spaces).</summary>
    [JsonPropertyName("command"), JsonConverter(typeof(StringOrListConverter))] public string Command { get; set; } = "";
    /// <summary>running, exited, killed, lost, …</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "";
    /// <summary>Its process id while running.</summary>
    [JsonPropertyName("pid")] public long? Pid { get; set; }
    /// <summary>When it started (Unix ms).</summary>
    [JsonPropertyName("started_at_ms")] public long StartedAtMs { get; set; }
    /// <summary>When it ended (Unix ms).</summary>
    [JsonPropertyName("ended_at_ms")] public long? EndedAtMs { get; set; }
    /// <summary>Its exit code, once it exited (a result, not an error).</summary>
    [JsonPropertyName("exit_code")] public int? ExitCode { get; set; }
    /// <summary>Why it ended, when it did not exit on its own.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    /// <summary>Who started it.</summary>
    [JsonPropertyName("by")] public string? By { get; set; }
    /// <summary>Its limits.</summary>
    [JsonPropertyName("limits")] public JobLimits? Limits { get; set; }
    /// <summary>How the limits are enforced.</summary>
    [JsonPropertyName("enforcement")] public List<string>? Enforcement { get; set; }
    /// <summary>Bytes of log kept.</summary>
    [JsonPropertyName("log_bytes")] public long? LogBytes { get; set; }
}

/// <summary><c>GET …/jobs/{name}/wait</c>: the job as it ended, or (<see cref="TimedOut"/>) as it stands, still running.</summary>
public sealed class JobWaitResult : GaiaDeskObject
{
    /// <summary>The job.</summary>
    [JsonPropertyName("job")] public Job Job { get; set; } = new();
    /// <summary>The wait's time ran out first; the job still runs.</summary>
    [JsonPropertyName("timed_out")] public bool TimedOut { get; set; }
}

/// <summary><c>GET …/jobs/{name}/logs</c>: a job and the end of its output.</summary>
public sealed class JobLogs : GaiaDeskObject
{
    /// <summary>The job.</summary>
    [JsonPropertyName("job")] public Job? Job { get; set; }
    /// <summary>The end of its output (the last <c>tail</c> bytes, or all the desk keeps).</summary>
    [JsonPropertyName("output")] public string Output { get; set; } = "";
}

/// <summary>One disk in <see cref="StatsReport"/>.</summary>
public sealed class DiskStat : GaiaDeskObject
{
    /// <summary>Its mount point.</summary>
    [JsonPropertyName("mount")] public string Mount { get; set; } = "";
    /// <summary>Its size (MB).</summary>
    [JsonPropertyName("total_mb")] public long TotalMb { get; set; }
    /// <summary>Free space (MB).</summary>
    [JsonPropertyName("free_mb")] public long FreeMb { get; set; }
}

/// <summary><c>GET /desks/{id}/stats</c>: CPU, memory, disks, uptime and running jobs, as the desk measures them.</summary>
public sealed class StatsReport : GaiaDeskObject
{
    /// <summary>The desk.</summary>
    [JsonPropertyName("desk")] public string Desk { get; set; } = "";
    /// <summary>Its host name.</summary>
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    /// <summary>macos, windows, linux.</summary>
    [JsonPropertyName("os")] public string? Os { get; set; }
    /// <summary>Its OS version.</summary>
    [JsonPropertyName("os_version")] public string? OsVersion { get; set; }
    /// <summary>CPU use, percent of the whole machine.</summary>
    [JsonPropertyName("cpu_percent")] public double CpuPercent { get; set; }
    /// <summary>Logical CPUs.</summary>
    [JsonPropertyName("cpus")] public int Cpus { get; set; }
    /// <summary>Load averages (1, 5, 15 minutes), where the OS has them.</summary>
    [JsonPropertyName("load")] public List<double>? Load { get; set; }
    /// <summary>Memory (MB).</summary>
    [JsonPropertyName("mem_total_mb")] public long MemTotalMb { get; set; }
    /// <summary>Free memory (MB).</summary>
    [JsonPropertyName("mem_free_mb")] public long MemFreeMb { get; set; }
    /// <summary>Its disks.</summary>
    [JsonPropertyName("disks")] public List<DiskStat>? Disks { get; set; }
    /// <summary>Seconds since boot.</summary>
    [JsonPropertyName("uptime_secs")] public long UptimeSecs { get; set; }
    /// <summary>Background jobs running.</summary>
    [JsonPropertyName("jobs_running")] public int JobsRunning { get; set; }
}

/// <summary>A file that failed to copy.</summary>
public sealed class CopyFailure : GaiaDeskObject
{
    /// <summary>The file.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    /// <summary>Why.</summary>
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

/// <summary>What a copy did (<c>PUT …/files</c>, and a download into a local file).</summary>
public sealed class CopyResult : GaiaDeskObject
{
    /// <summary>upload or download.</summary>
    [JsonPropertyName("direction")] public string Direction { get; set; } = "";
    /// <summary>The desk.</summary>
    [JsonPropertyName("desk")] public string Desk { get; set; } = "";
    /// <summary>Where it was written.</summary>
    [JsonPropertyName("destination")] public string Destination { get; set; } = "";
    /// <summary>Files copied.</summary>
    [JsonPropertyName("files")] public int Files { get; set; }
    /// <summary>Folders made.</summary>
    [JsonPropertyName("dirs")] public int Dirs { get; set; }
    /// <summary>Bytes copied.</summary>
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    /// <summary>Bytes a resumed copy did not send again.</summary>
    [JsonPropertyName("resumed_bytes")] public long ResumedBytes { get; set; }
    /// <summary>Files that failed.</summary>
    [JsonPropertyName("failed")] public List<CopyFailure> Failed { get; set; } = new();
    /// <summary>How long it took.</summary>
    [JsonPropertyName("seconds")] public double Seconds { get; set; }
}

/// <summary>An agent token as the desk describes it (never its secret).</summary>
public sealed class TokenInfo : GaiaDeskObject
{
    /// <summary>Its id.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>Its name.</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary>What it may do: exec, shell, cp, jobs, screen, forward, admin, …</summary>
    [JsonPropertyName("scopes")] public List<string>? Scopes { get; set; }
    /// <summary>When it was minted (Unix ms).</summary>
    [JsonPropertyName("issued_at_ms")] public long IssuedAtMs { get; set; }
    /// <summary>When it expires (Unix ms).</summary>
    [JsonPropertyName("expires_at_ms")] public long ExpiresAtMs { get; set; }
    /// <summary>When it was last used (Unix ms).</summary>
    [JsonPropertyName("last_used_ms")] public long? LastUsedMs { get; set; }
    /// <summary>The folder it is confined to.</summary>
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    /// <summary>It runs as the desk's low-privilege agent user.</summary>
    [JsonPropertyName("low_priv")] public bool? LowPriv { get; set; }
    /// <summary>It was revoked.</summary>
    [JsonPropertyName("revoked")] public bool? Revoked { get; set; }
}

/// <summary>One minted token: its desk, its description, and its secret (shown once).</summary>
public sealed class MintedToken : GaiaDeskObject
{
    /// <summary>The desk.</summary>
    [JsonPropertyName("desk")] public string Desk { get; set; } = "";
    /// <summary>The token.</summary>
    [JsonPropertyName("token")] public TokenInfo? Token { get; set; }
    /// <summary>The secret (<c>gdagt_…</c>): shown once, keep it.</summary>
    [JsonPropertyName("secret")] public string Secret { get; set; } = "";
}

/// <summary><c>POST /desks/{id}/tokens</c>, once per desk: every desk's token.</summary>
public sealed class MintResult : GaiaDeskObject
{
    /// <summary>The tokens.</summary>
    [JsonPropertyName("tokens")] public List<MintedToken> Tokens { get; set; } = new();
}

/// <summary><c>DELETE /desks/{id}/tokens/{token_id}</c>.</summary>
public sealed class TokenRevokeResult : GaiaDeskObject
{
    /// <summary>The revoked token's id.</summary>
    [JsonPropertyName("revoked")] public string Revoked { get; set; } = "";
    /// <summary>Live sessions it had that were stopped.</summary>
    [JsonPropertyName("stopped_sessions")] public int StoppedSessions { get; set; }
}

internal sealed class JobList : GaiaDeskObject
{
    [JsonPropertyName("jobs")] public List<Job>? Jobs { get; set; }
}

internal sealed class TokenList : GaiaDeskObject
{
    [JsonPropertyName("tokens")] public List<TokenInfo>? Tokens { get; set; }
}
