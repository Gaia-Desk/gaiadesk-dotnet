# Changelog

## 0.1.1 (unreleased)

- Retries follow the one rule every GaiaDesk SDK now shares (README "Retries"):
  - **Now retried that was not:** a connection that was never made (DNS, refused, a TLS handshake that
    broke off; a desk's socket or pipe not there) for **every** method — before, only GETs; a 502 for
    GETs whatever its kind (before, only a 502 `connection_lost`, so a proxy's Bad Gateway was final);
    a 503's `Retry-After` is honoured like a 429's.
  - **No longer re-sent:** a bodiless DELETE (`KillJobAsync`, `RevokeTokenAsync`, `DeleteWebhookAsync`)
    whose connection closed before any answer — .NET's `SocketsHttpHandler` used to send it again by
    itself, up to 3 times; every non-GET now carries a (possibly empty) body, which turns that off. A
    connect that timed out is kind `timeout` and not retried. A 429 or 409 `idempotency_key_in_flight`
    on an end-to-end encrypted operation is sealed afresh for each attempt (before, the same sealed
    request was sent again).
  - Delays: `BaseDelay` 250 ms (was 0.5 s); `MaxDelay` is now only the backoff cap, 8 s (was 30 s, and
    also the `Retry-After` cap); new `MaxRetryWait` (60 s) caps `Retry-After` — a longer one is not
    waited for, the error carries it. Jitter 0.5–1.0, 3 attempts in all by default (unchanged).
    Negative delays are a `UsageException`.
  - Proven on the raw-socket server: connection refused then the server appears (a POST runs once),
    closed/reset before any answer, 502/503/504, permanent 503s, 429 and 409 per method, and a reused
    keep-alive connection closed on the next request (each POST, PUT and DELETE reaches the server once).
- Timeouts (`TimeoutOptions`): `ResponseTimeout` (16 min) bounds the wait for an answer to begin,
  `IdleTimeout` (90 s) every read of its body, so a peer that drops or stalls a connection is a typed
  error (`UnreachableException` / `ConnectionLostException`, kind `timeout`), never a hang. Proven on a
  raw-socket server: closed or reset before any response byte (reads retried, bodies sent once),
  stalled mid-body, mid-JSON, mid-stream, and silent.

## 0.1.0

The first release of the GaiaDesk SDK for .NET (NuGet `GaiaDesk`, net8.0 and netstandard2.1), at
parity with the TypeScript SDK's API transport, plus the hosted API's fleet routes.

- `GaiaDeskClient`: the hosted API from an API key (and a scoped agent token), the desk's own API
  (`GaiaDeskClient.Local`: Unix socket or Windows named pipe; local admin token or agent token) and a
  desk's LAN gateway (`GaiaDeskClient.Lan`: pinned SHA-256 certificate fingerprint, checked before any
  byte is sent; `FingerprintMismatchException`).
- Desks: `ListDesksAsync`, `GetDeskAsync`, `GetReachAsync`, `WakeAsync`.
- Desk operations: `ExecAsync` (command line or argument list; shell, env, cwd, stdin, timeout,
  `Check`, `Admin`), `ExecStream` (Server-Sent Events as `IAsyncEnumerable<OutputChunk>`, `WaitAsync`,
  `Kill`, `CollectAsync`), files (`UploadAsync` / `UploadBytesAsync` / `UploadFileAsync`,
  `OpenReadAsync` / `DownloadAsync` / `DownloadBytesAsync` / `DownloadFileAsync`; 256 MB), jobs
  (`RunJobAsync`, `ListJobsAsync`, `JobLogsAsync`, `FollowJobLogs`, `WaitJobAsync` with held answers
  and 870 s turns, `KillJobAsync`), `StatsAsync`, tokens (`CreateTokenAsync` per desk with partial
  results on failure, `ListTokensAsync`, `RevokeTokenAsync`).
- Admin access: `ExecOptions.Admin` (`"admin": true`), the `admin` token scope, and the refusal reasons
  `admin_scope_missing`, `admin_not_enabled`, `admin_denied`, `admin_unavailable` (`Reasons.*`).
- Support sessions (`CreateSupportSessionAsync`, `ListSupportSessionsAsync`,
  `GetSupportSessionAsync`), audit (`ListAuditAsync`, `EnumerateAuditAsync` paging by time), webhooks
  (`CreateWebhookAsync`, `ListWebhooksAsync`, `DeleteWebhookAsync`) and delivery verification
  (`WebhookSignature.Verify` / `Sign`, `WebhookEvent.Parse`).
- End-to-end encrypted desk operations (X25519, HKDF-SHA256, XChaCha20-Poly1305): `E2eMode` Auto /
  Require / Off, pinned keys (`E2eKeys`), a wake for a desk that must be sealed to, one retry each for
  `e2e_required` and a rotated key, sealed uploads and downloads streamed in 48 KiB frames; the
  protocol's test vectors reproduced byte for byte. `E2eException` for `e2e_unavailable` and
  `e2e_key_mismatch`.
- Typed errors from the API's one envelope (`UsageException`, `RefusedException`,
  `UnreachableException`, `ConnectionLostException`, `OperationFailedException`, `ProtocolException`,
  `CommandException`) with kind, reason, desk, HTTP status, request id, `Retry-After` and exit code.
- Retries (`RetryOptions`). `Idempotency-Key` per call. `CancellationToken` everywhere.
- `HttpClient` injection (never disposed by the SDK), `UserAgent`.
- Dependencies: BouncyCastle.Cryptography 2.6.1 (MIT); on netstandard2.1 also System.Text.Json 8.0.5
  and System.Threading.Channels 8.0.0 (MIT).
