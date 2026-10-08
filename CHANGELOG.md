# Changelog

## 0.1.0 (unreleased)

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
- Retries (`RetryOptions`): 429s for every operation after `Retry-After`; lost connections and
  502/503/504 for reads only. `Idempotency-Key` per call. `CancellationToken` everywhere.
- Timeouts (`TimeoutOptions`): `ResponseTimeout` (16 min) bounds the wait for an answer to begin,
  `IdleTimeout` (90 s) every read of its body, so a peer that drops or stalls a connection is a typed
  error (`UnreachableException` / `ConnectionLostException`, kind `timeout`), never a hang. Proven on a
  raw-socket server: closed or reset before any response byte (reads retried, bodies sent once),
  stalled mid-body, mid-JSON, mid-stream, and silent.
- `HttpClient` injection (never disposed by the SDK), `UserAgent`.
- Dependencies: BouncyCastle.Cryptography 2.6.1 (MIT); on netstandard2.1 also System.Text.Json 8.0.5
  and System.Threading.Channels 8.0.0 (MIT).
