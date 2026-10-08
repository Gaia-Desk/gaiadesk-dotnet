# GaiaDesk SDK for .NET

The official .NET (C#) SDK for the [GaiaDesk](https://gaiadesk.net) Platform API: list, wake and
audit your desks, run commands on them (buffered or streamed), copy files, run background jobs,
read their stats, mint scoped agent tokens, create support sessions for the embed SDKs, and manage
and verify webhooks. Desk operations are **end-to-end encrypted** to the desk, so GaiaDesk's servers
relay only ciphertext.

- One client, three transports with the same methods, results and typed errors: the **hosted API**
  (`https://api.gaiadesk.net/v1`), the **desk's own API** (its Unix socket or Windows named pipe, for
  code running on the desk) and a desk's **LAN gateway** (HTTPS with a pinned certificate).
- `async` everywhere with `CancellationToken`, `IAsyncEnumerable` for streamed output, `Stream` for
  files, `HttpClient` injection (`IHttpClientFactory`-friendly), retries with back-off.
- Targets **net8.0** and **netstandard2.1**. MIT licensed.

## Contents

- [Install](#install)
- [Quick start](#quick-start)
- [Credentials](#credentials)
- [Desks: list, read, reach, wake](#desks-list-read-reach-wake)
- [Running commands](#running-commands)
- [Streaming output](#streaming-output)
- [Running as administrator](#running-as-administrator)
- [Files](#files)
- [Background jobs](#background-jobs)
- [Stats](#stats)
- [Agent tokens](#agent-tokens)
- [Support sessions](#support-sessions)
- [Audit](#audit)
- [Webhooks](#webhooks)
- [End-to-end encryption](#end-to-end-encryption)
- [Local and LAN](#local-and-lan)
- [Errors](#errors)
- [Retries, idempotency, cancellation](#retries-idempotency-cancellation)
- [HttpClient and dependency injection](#httpclient-and-dependency-injection)
- [Dependencies](#dependencies)
- [Not covered](#not-covered)
- [Examples](#examples)
- [Development](#development)

## Install

```sh
dotnet add package GaiaDesk
```

## Quick start

```csharp
using GaiaDesk;

using var gd = new GaiaDeskClient(new GaiaDeskOptions
{
    ApiKey = Environment.GetEnvironmentVariable("GAIADESK_API_KEY"),        // ak_…
    DeskToken = Environment.GetEnvironmentVariable("GAIADESK_DESK_TOKEN"),  // gdagt_…, verified by the desk
});

var desks = await gd.ListDesksAsync();
foreach (var d in desks.Devices) Console.WriteLine($"{d.DeskId} {d.Name} online={d.Online}");

var r = await gd.ExecAsync("123456789", "uname -a");
Console.Write(r.Stdout);          // exit code in r.Exit, stderr in r.Stderr
```

`GaiaDeskClient` is thread-safe; create one and reuse it.

## Credentials

Every request carries `Authorization: Bearer <ApiKey>` and, when set,
`X-GaiaDesk-Desk-Token: <DeskToken>`.

- An **API key** (`ak_…`, from gaiadesk.net/account → API keys) carries explicit scopes:
  `desks:read`, `desks:write` (wake), `exec`, `files`, `jobs`, `tokens`, `audit:read`, `support`,
  `webhooks`.
- **Desk operations** (exec, files, jobs, stats, tokens) are verified by the desk itself: from an
  API key they also need a **scoped agent token** (`gdagt_…`) for that desk. A signed-in person's
  session token (passed as `ApiKey`) works on their own desks without one.
- **Token administration** (`CreateTokenAsync`, `ListTokensAsync`, `RevokeTokenAsync`) is the desk
  owner's: a signed-in person's own desk; an agent token is refused (`agent_cannot_admin`).
- Per call, `CallOptions.DeskToken` overrides the client's token, and `CallOptions.Wake` (0-120 s)
  rings a sleeping desk and waits for it (`wake_s`).

## Desks: list, read, reach, wake

```csharp
DeskList all = await gd.ListDesksAsync();                 // GET /desks (online first)
DeskList one = await gd.ListDesksAsync("123456789");      // filtered to one
DeskDetail d = await gd.GetDeskAsync("123456789");        // GET /desks/{id}: reach, wake hints, e2e key
ReachLog log = await gd.GetReachAsync("123456789", since: DateTimeOffset.UtcNow.AddDays(-7), limit: 50);
WakeResult w = await gd.WakeAsync("123456789", wait: TimeSpan.FromSeconds(30));
Console.WriteLine(w.Woke ? "awake" : w.AlreadyOnline ? "was awake" : "rang, still asleep");
```

An offline desk has `OfflineSince`, `OfflineReason` (`closed`, `silent`, `updating`, …) and
`OfflineReasonText`. A desk with nothing to ring is an `UnreachableException` with reason
`no_wake_path`.

## Running commands

```csharp
// A command line, given to the desk's shell verbatim:
ExecResult r = await gd.ExecAsync(desk, "make test", new ExecOptions
{
    Cwd = "src/app",                                         // relative: from the desk user's home
    Env = new Dictionary<string, string> { ["CI"] = "1" },   // sent in the request, never logged
    Shell = Shell.Bash,                                      // Default, None, Sh, Bash, Zsh, Cmd, Pwsh, PowerShell (= pwsh)
    Stdin = "input text\n",
    Timeout = TimeSpan.FromMinutes(10),                      // TimeSpan.Zero: no limit (calls are held under 15 min)
    Check = true,                                            // non-zero exit → CommandException
});

// An argument list, each argument quoted for the desk's shell (Shell.None: run directly):
await gd.ExecAsync(desk, new[] { "ls", "-la", "My Documents" });
```

A command that ran returns whatever its exit code (`Exit`, `RemoteCode`, `TimedOut`, `Truncated`
at 8 MB of output). One that never ran (refused, unreachable, a missing `Cwd`) throws its typed
error. For work longer than 15 minutes, start a [job](#background-jobs).

## Streaming output

```csharp
await using var run = gd.ExecStream(desk, "npm run build", cancellationToken: ct);
await foreach (OutputChunk chunk in run)
    (chunk.Source == OutputSource.Stdout ? Console.Out : Console.Error).Write(chunk.Text);
StreamExit exit = await run.WaitAsync();
Console.WriteLine($"exit {exit.ExitCode}");   // exit.Result: the run's ExecExit; exit.Error: what went wrong
exit.ThrowIfError();                            // or branch on exit.Succeeded

// Or all at once:
StreamResult all = await gd.ExecStream(desk, "make").CollectAsync();
```

The request starts at once; output is buffered until you read it, and UTF-8 characters split across
events are joined. `Kill()`, disposing the stream, or cancelling the token closes the request, which
stops the command (`exit.Killed`, exit code 130). A failure after the stream started (the desk lost,
a refusal) ends it with `exit.Error`; `WaitAsync` never throws for the operation's own failure.
Stdin is given up front (`ExecOptions.Stdin`); writing stdin while it runs is not part of the API.

## Running as administrator

```csharp
var r = await gd.ExecAsync(desk, "whoami", new ExecOptions { Admin = true });   // root / SYSTEM
```

`Admin = true` sends `"admin": true`: the command runs as administrator (root on macOS / Linux,
SYSTEM on Windows) in the desk's privileged GaiaDesk process. It needs **both** a desk token with the
`admin` scope (never implied: mint it with `TokenScopes.Admin`) **and** the desk owner's Admin
access switch, which can only be turned on at the desk. By default the person at the desk is asked
each time. A refusal is a `RefusedException` (exit 254) with `Reason`:

| `Reason` | meaning |
|---|---|
| `admin_scope_missing` | the token has no `admin` scope (or it is a person's call) |
| `admin_not_enabled` | Admin access is off on the desk |
| `admin_denied` | the person at the desk said no, nobody answered, or nobody was there |
| `admin_unavailable` | no privileged process on the desk, or a desk too old for the field (it refuses rather than run as its user) |

Windows Smart App Control / WDAC still refuse unsigned new programs (`blocked_by_os_policy`).
Background jobs never run as administrator. A confined token (`Cwd`, `LowPriv`) cannot carry the
`admin` scope; `CreateTokenAsync` refuses that combination up front.

## Files

One file of at most 256 MB each way (`GaiaDeskClient.ApiFileLimit`); larger files and folders go
through `gaiadesk-cli`.

```csharp
CopyResult up = await gd.UploadFileAsync(desk, "report.csv", "/tmp/");          // a remote ending in / keeps the name
await gd.UploadAsync(desk, "/tmp/data.bin", File.OpenRead("data.bin"));          // any Stream
await gd.UploadBytesAsync(desk, "/tmp/hello.txt", "hi"u8.ToArray());

await using Stream s = await gd.OpenReadAsync(desk, "/var/log/system.log");      // read as it arrives
long n = await gd.DownloadAsync(desk, "/tmp/data.bin", File.Create("copy.bin"));
byte[] bytes = await gd.DownloadBytesAsync(desk, "/tmp/hello.txt");
CopyResult down = await gd.DownloadFileAsync(desk, "/tmp/data.bin", "./downloads/");   // a folder keeps the name
```

A seekable upload stream is streamed (from its current position) and can be sent again by a
retry; any other stream is read into memory first. A download that breaks, or (sealed) ends before
the desk said it was complete, is a `ConnectionLostException` from the read, never a clean short
file; `DownloadFileAsync` then deletes the partial file.

## Background jobs

```csharp
Job job = await gd.RunJobAsync(desk, "nightly", "./build.sh --release", new JobOptions
{
    Priority = JobPriority.Low, CpuPercent = 50, Memory = "2G", KeepAwake = true,
    Cwd = "src", Shell = Shell.Bash, Env = new Dictionary<string, string> { ["CI"] = "1" },
});
IReadOnlyList<Job> jobs = await gd.ListJobsAsync(desk);
JobLogs logs = await gd.JobLogsAsync(desk, "nightly", tail: 4096);    // the last 4 KiB

await using var follow = gd.FollowJobLogs(desk, "nightly");             // until it ends
await foreach (var chunk in follow) Console.Write(chunk.Text);
Console.WriteLine((await follow.WaitAsync()).Job?.ExitCode);

JobWaitResult done = await gd.WaitJobAsync(desk, "nightly", TimeSpan.FromHours(2));
if (done.TimedOut) Console.WriteLine("still running");
await gd.KillJobAsync(desk, "nightly");
```

`WaitJobAsync` asks again every 870 s (the most one request holds) until the job ends or the
timeout passes; no timeout waits until it ends; `TimeSpan.Zero` answers at once. A held answer that
failed after its 200 (`GaiaDesk-Held`) is thrown as its typed error. A job's end is also the
`job.finished` webhook.

## Stats

```csharp
StatsReport st = await gd.StatsAsync(desk);
Console.WriteLine($"{st.Hostname}: {st.CpuPercent}% CPU, {st.MemFreeMb}/{st.MemTotalMb} MB free, up {st.UptimeSecs}s");
```

## Agent tokens

```csharp
MintResult minted = await gd.CreateTokenAsync(new TokenCreateOptions
{
    Desks = new[] { "123456789", "987654321" },       // one token per desk
    Name = "ci-bot",
    Expires = TimeSpan.FromDays(30),                   // default 7 days
    Scopes = new[] { TokenScopes.Exec, TokenScopes.Cp, TokenScopes.Jobs },   // the default
    Cwd = "/srv/builds",                               // optional confinement
    LowPriv = true,                                    // optional low-privilege agent user
});
foreach (var t in minted.Tokens) Console.WriteLine($"{t.Desk}: {t.Secret}");   // shown once

IReadOnlyList<TokenInfo> tokens = await gd.ListTokensAsync("123456789");
await gd.RevokeTokenAsync("123456789", tokens[0].Id);   // by id or name; its sessions and jobs end
```

If a later desk fails, the error's `Json` carries the tokens already minted (`tokens`): their
secrets are shown only there.

## Support sessions

For the embed SDKs (web `@gaiadesk/embed`, and the native embed for desktop apps):

```csharp
SupportSessionCreated s = await gd.CreateSupportSessionAsync(new SupportSessionCreate
{
    Mode = SupportModes.Cobrowse,                       // or View (default)
    Customer = new Dictionary<string, object?> { ["name"] = "Ada", ["plan"] = "pro" },
    ExpiresIn = TimeSpan.FromMinutes(30),
    Origin = "https://app.example.com",                 // web embed only; leave out for a desktop app
});
// Hand s.EmbedToken (shown once) to the customer's page or app; agents join with s.JoinCode / s.JoinUrl.

IReadOnlyList<SupportSession> open = await gd.ListSupportSessionsAsync();          // open ones
IReadOnlyList<SupportSession> all = await gd.ListSupportSessionsAsync(includeEnded: true, limit: 100);
SupportSession now = await gd.GetSupportSessionAsync(s.Id);
```

## Audit

```csharp
var events = await gd.ListAuditAsync(new AuditQuery { Desk = desk, Action = "api.*", Since = DateTimeOffset.UtcNow.AddDays(-1), Limit = 100 });

// Every matching event, newest first, a page at a time:
await foreach (AuditEvent e in gd.EnumerateAuditAsync(new AuditQuery { Actor = "ci@example.com" }))
    Console.WriteLine($"{DateTimeOffset.FromUnixTimeMilliseconds(e.OccurredAtMs):u} {e.Action}");
```

The API pages by time (`since_ms` / `until_ms` / `limit`, at most 500), not by cursor;
`EnumerateAuditAsync` moves `until_ms` back past the oldest event seen and de-duplicates by id.

## Webhooks

```csharp
WebhookCreated hook = await gd.CreateWebhookAsync("https://example.com/hooks/gaiadesk",
    new[] { WebhookEventTypes.DeskOffline, WebhookEventTypes.JobFinished }, "ops channel");
string secret = hook.Secret;            // whsec_…, shown once
await gd.ListWebhooksAsync();
await gd.DeleteWebhookAsync(hook.Id);
```

**Verify every delivery** over its raw bytes:

```csharp
app.MapPost("/hooks/gaiadesk", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var body = ms.ToArray();
    if (!WebhookSignature.Verify(secret, req.Headers[WebhookSignature.HeaderName], body))
        return Results.Unauthorized();          // bad signature, or t more than 5 minutes off
    var ev = WebhookEvent.Parse(body);          // de-duplicate by ev.Id
    if (ev.Type == WebhookEventTypes.DeskOffline) Console.WriteLine($"{ev.Desk!.DeskId}: {ev.Desk.ReasonText}");
    if (ev.Type == WebhookEventTypes.JobFinished) Console.WriteLine($"{ev.Job!.Name} exited {ev.Job.ExitCode}");
    return Results.Ok();
});
```

`WebhookSignature.Sign` makes a signature, for testing your endpoint.

## End-to-end encryption

On the hosted API, desk operations are **sealed** so GaiaDesk's servers relay only ciphertext: the
command, its `Env` and `Stdin`, file paths and bytes, and all output and results are readable by
you and the desk only. The server still sees the credentials, the route (the operation and the
desk; a job name or token id in the path), `stream`/`follow`/`wake`, sizes, and how the operation
ended (an exit, or an error's kind and reason). The local and LAN transports never leave the desk or
the LAN and are not sealed.

Before an operation the SDK reads the desk's X25519 key (`e2e_pub` on `GET /desks/{id}`, cached for
5 minutes) and seals the request to a fresh ephemeral key: X25519, HKDF-SHA256 and
XChaCha20-Poly1305, every message bound to the desk, the operation and its place in the stream.
Results, streams, errors and file bytes come back exactly as in the clear; a desk's error carries
its own message. The SDK reproduces the protocol's fixed test vectors byte for byte.

```csharp
var gd = new GaiaDeskClient(new GaiaDeskOptions
{
    ApiKey = apiKey, DeskToken = deskToken,
    E2e = E2eMode.Require,                                                  // Auto (default) | Require | Off
    E2eKeys = new Dictionary<string, string> { ["123456789"] = "B6N8vBQgk8i3…" },   // optional: pin a desk's e2e_pub
    OnWarning = msg => logger.LogWarning("{Message}", msg),
});
```

- `Auto` (default): sealed when the desk lists a key; otherwise sent in the clear with a one-time
  warning per desk (`OnWarning`, default standard error), unless the desk **requires** end-to-end
  encryption: then it is woken (`POST /desks/{id}/wake`) and asked again, and sealed or refused.
- `Require`: never in the clear. A desk that lists no key (asleep, offline, or a GaiaDesk from before
  end-to-end encryption) is woken and asked again; still none is an `E2eException` (a
  `RefusedException`, reason `e2e_unavailable`) and nothing is sent.
- `Off`: plaintext.
- `E2eKeys`: a pinned key is sealed to even while the desk lists none; a different key from the
  server is an `E2eException` (`e2e_key_mismatch`) and nothing is sent.
- A plaintext call refused `e2e_required` (409) is sealed and sent once more; a sealed one the desk
  could not open (`e2e_decrypt_failed`: its key rotated) is sealed to the key read again, once.
  Answers that do not open (altered, reordered, or a plaintext answer to a sealed call) are a
  `ProtocolException` (`e2e_decrypt_failed`, `e2e_malformed`, `e2e_unsealed_answer`).
- Reading a desk's key needs the API key's `desks:read` scope (and waking it `desks:write`); in
  `Auto`, a key that cannot be read means plaintext with the warning.
- Uploads are sealed as they are sent (48 KiB frames) and downloads opened as they are read, so
  neither needs the whole file in memory.

## Local and LAN

The desk serves the same desk operations itself, with the same results and errors. Hosted-only
methods (`GetDeskAsync`, `GetReachAsync`, `WakeAsync`, audit, webhooks, support sessions) are a
`UsageException` on these transports, before anything is sent.

**Local: code running on the desk** (Settings → GaiaDesk API → Local API; .NET 5 or later):

```csharp
using var gd = GaiaDeskClient.Local();      // or new LocalOptions { DeskToken = "gdagt_…", SocketPath = … }
var me = (await gd.ListDesksAsync()).Devices.Single();
await gd.ExecAsync(me.DeskId, "hostname");
```

- It connects over HTTP/1.1 on the Unix socket `$GAIADESK_API_DIR/api.sock` (when that is an
  absolute directory), else `~/.gaiadesk/api.sock`; on Windows the named pipe `$GAIADESK_API_PIPE`,
  else `\\.\pipe\gaiadesk-api-<user>` (`LocalApi.PipeName`). `SocketPath` overrides either.
- Credentials: a `DeskToken` (agent token) is sent as `X-GaiaDesk-Desk-Token`; without one, the
  desk's local admin token (`gdlocal_…`, read from `api-token` beside the socket on each request, or
  given as `Token`) as `Authorization: Bearer`.
- No socket or pipe, or no token file and no `DeskToken`, is an `UnreachableException` with reason
  `local_api_unavailable`, saying how to turn the local API on.

**LAN: a desk's opt-in LAN gateway**, its self-signed certificate pinned by the SHA-256 fingerprint
the desk shows in its Settings:

```csharp
using var gd = GaiaDeskClient.Lan(new LanOptions
{
    BaseUrl = "https://gaiadesk-123456789.local:7443/v1",
    Fingerprint = "ab:cd:…",                 // 32 hex pairs; colons, spaces and case optional
    DeskToken = "gdagt_…",                   // required: agent tokens only on the LAN
});
```

The fingerprint is checked during the TLS handshake, **before any byte of the request is sent**; a
mismatch is a `FingerprintMismatchException` (an `UnreachableException`, reason
`fingerprint_mismatch`, with `Expected` and `Actual`): it may not be your desk, so do not proceed.

## Errors

Every failure is a `GaiaDeskException`; the class follows the API's error kind:

| Class | Kind | HTTP | Means |
|---|---|---|---|
| `UsageException` | `usage` | 400 | fix the request (also raised by the SDK before sending) |
| `RefusedException` | `refused` | 401, 403, 429 | credentials, scopes, rate limits, desk settings, admin refusals |
| `E2eException` (a `RefusedException`) | `refused` | — | would not send in the clear / a pinned key mismatch |
| `UnreachableException` | `unreachable`, `network`, `offline`, … | 404, 409, 503, 504 | the desk or the API could not be reached |
| `FingerprintMismatchException` | `unreachable` | — | the LAN gateway is not the pinned desk |
| `ConnectionLostException` | `connection_lost` | 502 | the desk went away mid-operation |
| `OperationFailedException` | `failed` | 422 | it ran and did not succeed (no such job, a file failed) |
| `ProtocolException` | `protocol` | 409, 502 | a desk too old (`desk_too_old`), or an answer that is not what the API documents |
| `CommandException` | `failed` | — | `ExecOptions.Check` and a non-zero exit (`Result` has the output) |

Each carries `Kind` (the finest known: the reason when it is one of `ErrorKinds`), `Reason`
(`Reasons.*`: `missing_scope`, `rate_limited`, `desk_opted_out`, `admin_denied`, …), `Desk`,
`Status` (HTTP), `RequestId` (`req_…`, quote it to support), `RetryAfter`, `ExitCode`
(gaiadesk-cli's: 1 failed, 254 refused, 255 the rest), `Operation` (`POST /desks/{id}/exec`) and
`Json` (the error envelope).

```csharp
try { await gd.ExecAsync(desk, "deploy"); }
catch (RefusedException e) when (e.Reason == Reasons.MissingScope) { /* add the scope */ }
catch (UnreachableException e) when (e.Kind == ErrorKinds.Offline) { await gd.WakeAsync(desk); }
catch (GaiaDeskException e) { Console.Error.WriteLine($"{e.Message} ({e.RequestId})"); }
```

Cancelling a call's `CancellationToken` throws `OperationCanceledException`. An `HttpClient`
timeout is an `UnreachableException` with kind `timeout`.

## Retries, idempotency, cancellation

**Retries.** A request is sent again only when that cannot run anything twice:
- **The connection was never made** (DNS, refused, TLS handshake): any method — nothing was sent.
- **The connection was lost after sending, or the answer was 502, 503 or 504**: GETs only (reads).
  A 503 that says the API or desk operations are switched off is not retried.
- **429** (`rate_limited`, `desk_busy`) and **409** `idempotency_key_in_flight`: any method — the server refused
  it before acting.

Timeouts are never retried, and nothing is retried once its answer has begun. A call that changes something
(POST, PUT, DELETE) is never sent again after it may have reached the server; an `Idempotency-Key` is sent but
does not make a call retryable. 429 and 503 wait for `Retry-After`; one longer than `RetryOptions.MaxRetryWait`
(default 60 s) is not waited for — the error carries it. Otherwise the wait is exponential backoff with jitter:
`RetryOptions.BaseDelay` (default 250 ms) doubling up to `RetryOptions.MaxDelay` (default 8 s), times a random 0.5–1.0.
`RetryOptions.MaxRetries` (default 2, so 3 attempts in all) sets how many times; 0 (or `RetryOptions.None`) turns
retries off. Each retry of a sealed operation is sealed afresh.

.NET's HTTP stack itself re-sends only GETs: `SocketsHttpHandler` re-sends a request that has no content when
its connection closes before any answer, so the SDK gives every other request a body (empty if need be) and a
POST, PUT or DELETE is never re-sent by the stack. (A connect failure is told apart by `HttpRequestError` on
.NET 8 and later; on older runtimes running the netstandard2.1 build, a TLS handshake that breaks off is not
retried.)

**Timeouts** (`TimeoutOptions`, `Timeouts` on every options class) make a server or proxy that stops
answering an error, never a hang:

- `ResponseTimeout` (default 16 minutes, above the API's 15-minute call limit): the longest wait for
  an answer to begin, sending the request included. Exceeded: `UnreachableException`, kind `timeout`.
- `IdleTimeout` (default 90 s; streams and held waits send a keep-alive every 15 s): the longest
  silence while reading a body (JSON, a download, an event stream). Exceeded mid-answer:
  `ConnectionLostException`, kind `timeout` (a stream ends with that error in `StreamExit.Error`).
- A connection closed or reset before any answer is an `UnreachableException` (kind `network`) at
  once. .NET's HTTP stack itself may re-send a GET when the connection it used was closed before
  any answer; it never re-sends a POST, PUT or DELETE (see Retries).

POSTs take `CallOptions.IdempotencyKey` (`Idempotency-Key`): a retry of yours with the same key and
the same request within 24 hours gets the first answer again. Streamed calls are never replayed.
Every method takes a `CancellationToken`; a stream's token stops it.

## HttpClient and dependency injection

Pass your own `HttpClient` (the SDK never disposes it). Set its `Timeout` to
`Timeout.InfiniteTimeSpan` (or above 15 minutes): job waits and streams are long requests.

```csharp
services.AddHttpClient("gaiadesk", c => c.Timeout = Timeout.InfiniteTimeSpan);
services.AddSingleton(sp => new GaiaDeskClient(new GaiaDeskOptions
{
    ApiKey = config["GaiaDesk:ApiKey"],
    DeskToken = config["GaiaDesk:DeskToken"],
    HttpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("gaiadesk"),
    UserAgent = "my-service/1.0",
}));
```

## Dependencies

- **[BouncyCastle.Cryptography](https://www.nuget.org/packages/BouncyCastle.Cryptography)** 2.6.x
  (MIT): X25519 and ChaCha20-Poly1305. It is pure managed code, so it works the same on Windows,
  macOS, Linux (x64 and Arm64) and every runtime the package targets, with no native library to
  load; neither .NET 8 nor netstandard2.1 has X25519, and .NET's own `ChaCha20Poly1305` is not
  supported on every platform. XChaCha20-Poly1305 is ChaCha20-Poly1305 under the HChaCha20
  subkey (draft-irtf-cfrg-xchacha-03 §2.3), checked against the draft's vectors. HKDF-SHA256 comes
  from .NET (`System.Security.Cryptography.HKDF`; BouncyCastle's on netstandard2.1). NSec
  (libsodium) was the alternative: it targets net8.0 only and ships a native library per platform.
- netstandard2.1 only: **System.Text.Json** 8.0.5 and **System.Threading.Channels** 8.0.0 (MIT,
  part of .NET 8 itself).

## Not covered

- What only `gaiadesk-cli` or the native library does: interactive shells, port forwarding, screen
  sessions and MCP, folder (recursive) copies, files over 256 MB, `measure`, mesh.
- Writing stdin while a command runs: the API takes stdin up front.
- The local transport needs .NET 5 or later (`SocketsHttpHandler.ConnectCallback`); on
  netstandard2.1 it is a `UsageException`.

## Examples

`examples/GaiaDesk.Examples` (`dotnet run --project examples/GaiaDesk.Examples -- <name>`): `fleet`,
`exec`, `stream`, `copy`, `job`, `support`, `webhook-verify`, `local`. Set `GAIADESK_API_KEY`,
`GAIADESK_DESK_TOKEN` and `GAIADESK_DESK`.

## Development

```sh
dotnet build GaiaDesk.sln          # zero warnings (TreatWarningsAsErrors)
dotnet test GaiaDesk.sln           # xUnit: vectors, a Kestrel mock API that is also the desks, transports
dotnet pack src/GaiaDesk -c Release -o artifacts
```

The tests run a mock of the API and the desks behind it in process (Kestrel): every route, plaintext
and sealed, streams, held waits, a Unix socket / named pipe for the local transport and pinned TLS
for the LAN gateway. The end-to-end vectors are the protocol's own
(`tests/GaiaDesk.Tests/Fixtures/e2e-vectors.json`). With only a newer .NET installed, run the net8.0
tests with `DOTNET_ROLL_FORWARD=Major`.

## License

MIT. See [LICENSE](LICENSE).
