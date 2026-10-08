// Files: `PUT /desks/{id}/files?path=` and `GET /desks/{id}/files?path=`, one
// file of at most 256 MB each way. Sealed end to end on the hosted API: the
// upload as NDJSON input frames (48 KiB each), the download as NDJSON events,
// both streamed.

using System.Text.Json;
using System.Text.Json.Nodes;
using GaiaDesk.E2e;
using GaiaDesk.Http;

namespace GaiaDesk;

public sealed partial class GaiaDeskClient
{
    private static string BaseName(string p)
    {
        var parts = p.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "" : parts[parts.Length - 1];
    }

    private static string Remote(string? remotePath)
    {
        if (string.IsNullOrEmpty(remotePath)) throw Errors.Usage("a remote path is required");
        if (remotePath!.Length > 4096) throw Errors.Usage("a remote path is at most 4096 characters");
        return remotePath;
    }

    /// <summary>
    /// <c>PUT /desks/{id}/files?path=</c>: upload <paramref name="content"/> (read from its current position) as the file
    /// <paramref name="remotePath"/> on the desk (a path naming an existing folder receives it under that folder's last
    /// name). A seekable stream is streamed (and can be sent again by a retry); any other stream is read into memory first.
    /// </summary>
    public async Task<CopyResult> UploadAsync(string deskId, string remotePath, Stream content, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (content is null) throw Errors.Usage("content is required");
        UploadSource src;
        if (content.CanSeek)
        {
            var start = content.Position;
            var length = content.Length - start;
            if (length > ApiFileLimit) throw Errors.Usage($"the upload is {length} bytes; the API takes files up to 256 MB", "upload");
            src = new UploadSource(() => { content.Position = start; return content; }, length);
        }
        else
        {
            var mem = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await content.ReadAsync(buf, 0, buf.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                mem.Write(buf, 0, n);
                if (mem.Length > ApiFileLimit) throw Errors.Usage("the upload is over 256 MB; the API takes files up to 256 MB", "upload");
            }
            var bytes = mem.ToArray();
            src = new UploadSource(() => new MemoryStream(bytes, false), bytes.Length);
        }
        return await Upload(deskId, Remote(remotePath), src, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>PUT /desks/{id}/files?path=</c> with bytes in memory.</summary>
    public Task<CopyResult> UploadBytesAsync(string deskId, string remotePath, byte[] data, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (data is null) throw Errors.Usage("data is required");
        if (data.LongLength > ApiFileLimit) throw Errors.Usage("the API takes files up to 256 MB", "upload");
        return Upload(deskId, Remote(remotePath), new UploadSource(() => new MemoryStream(data, false), data.LongLength), options, cancellationToken);
    }

    /// <summary>
    /// <c>PUT /desks/{id}/files?path=</c>: one local file (at most 256 MB). A <paramref name="remotePath"/> that is empty or
    /// ends in <c>/</c> is a folder: the file keeps its name.
    /// </summary>
    public async Task<CopyResult> UploadFileAsync(string deskId, string localPath, string remotePath, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(localPath)) throw Errors.Usage("a local path is required");
        if (remotePath is null) throw Errors.Usage("a remote path is required");
        if (Directory.Exists(localPath)) throw NotServed($"uploading the folder {localPath}", "the API copies single files; copy folders with gaiadesk-cli");
        FileInfo fi;
        try
        {
            fi = new FileInfo(localPath);
            if (!fi.Exists) throw new FileNotFoundException("no such file", localPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new GaiaDeskException($"cannot read {localPath}: {e.Message}", new ErrorDetails { Kind = ErrorKinds.Local, Reason = ErrorKinds.Local, Operation = "upload" });
        }
        if (fi.Length > ApiFileLimit) throw Errors.Usage($"{localPath} is {fi.Length} bytes; the API takes files up to 256 MB (copy larger ones with gaiadesk-cli)", "upload");
        var target = remotePath.Length == 0 || remotePath.EndsWith("/", StringComparison.Ordinal) || remotePath.EndsWith("\\", StringComparison.Ordinal)
            ? remotePath + Path.GetFileName(localPath) : remotePath;
        using var file = OpenLocal(localPath);
        var length = file.Length;
        return await Upload(deskId, Remote(target), new UploadSource(() => { file.Position = 0; return file; }, length), options, cancellationToken).ConfigureAwait(false);
    }

    private static FileStream OpenLocal(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new GaiaDeskException($"cannot read {path}: {e.Message}", new ErrorDetails { Kind = ErrorKinds.Local, Reason = ErrorKinds.Local, Operation = "upload" });
        }
    }

    private async Task<CopyResult> Upload(string deskId, string remote, UploadSource src, CallOptions? options, CancellationToken ct)
    {
        var desk = Check.Desk(deskId);
        var r = new ApiRequest("PUT", $"{DeskPath(desk)}/files") { Upload = src }.With(options);
        r.IdempotencyKey = null;
        r.Query["path"] = remote;
        r.E2e = (desk, "file_put", new JsonObject { ["op"] = "file_put", ["path"] = remote, ["size"] = src.Length });
        var res = await Json<CopyResult>(r, ct).ConfigureAwait(false);
        if (res.Failed.Count > 0)
            throw new OperationFailedException($"{res.Failed.Count} file(s) failed to copy: {res.Failed[0].Message}",
                new ErrorDetails { ExitCode = 1, Operation = r.Operation, Json = JsonSerializer.SerializeToElement(res, GaiaDeskJson.Options), Desk = desk });
        return res;
    }

    /// <summary>
    /// <c>GET /desks/{id}/files?path=</c>: the file as a stream, read as it arrives (dispose it to end the request).
    /// A transfer that breaks (or, sealed, ends before the desk said it was complete) is a <see cref="ConnectionLostException"/>
    /// from a read, never a clean short file.
    /// </summary>
    public async Task<Stream> OpenReadAsync(string deskId, string remotePath, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var desk = Check.Desk(deskId);
        var remote = Remote(remotePath);
        var r = new ApiRequest("GET", $"{DeskPath(desk)}/files") { Accept = "application/octet-stream" }.With(options);
        r.Query["path"] = remote;
        r.E2e = (desk, "file_get", new JsonObject { ["op"] = "file_get", ["path"] = remote });
        var res = await _core.SendAsync(r, cancellationToken).ConfigureAwait(false);
        try
        {
            var body = await HttpCore.ReadStream(res.Message.Content, cancellationToken).ConfigureAwait(false);
            return res.Seal is { } seal ? new SealedDownloadStream(body, res, seal, r.Operation) : new ResponseStream(body, res, r.Operation, desk);
        }
        catch
        {
            res.Dispose();
            throw;
        }
    }

    /// <summary><c>GET /desks/{id}/files?path=</c> into <paramref name="destination"/>: the bytes written.</summary>
    public async Task<long> DownloadAsync(string deskId, string remotePath, Stream destination, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (destination is null) throw Errors.Usage("destination is required");
#if NET5_0_OR_GREATER
        await using var src = await OpenReadAsync(deskId, remotePath, options, cancellationToken).ConfigureAwait(false);
#else
        using var src = await OpenReadAsync(deskId, remotePath, options, cancellationToken).ConfigureAwait(false);
#endif
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buf, 0, buf.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buf, 0, n, cancellationToken).ConfigureAwait(false);
            total += n;
        }
        return total;
    }

    /// <summary><c>GET /desks/{id}/files?path=</c>: the file's bytes.</summary>
    public async Task<byte[]> DownloadBytesAsync(string deskId, string remotePath, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var mem = new MemoryStream();
        await DownloadAsync(deskId, remotePath, mem, options, cancellationToken).ConfigureAwait(false);
        return mem.ToArray();
    }

    /// <summary>
    /// <c>GET /desks/{id}/files?path=</c> into a local file (a <paramref name="localPath"/> that is a folder, or ends in a
    /// separator, keeps the remote name). A download that fails leaves no partial file.
    /// </summary>
    public async Task<CopyResult> DownloadFileAsync(string deskId, string remotePath, string localPath, CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(localPath)) throw Errors.Usage("a local path is required");
        var desk = Check.Desk(deskId);
        var started = DateTime.UtcNow;
        var isDir = localPath.EndsWith("/", StringComparison.Ordinal) || localPath.EndsWith("\\", StringComparison.Ordinal) || Directory.Exists(localPath);
        var dest = isDir ? Path.Combine(localPath.TrimEnd('/', '\\'), BaseName(Remote(remotePath))) : localPath;
#if NET5_0_OR_GREATER
        await using var src = await OpenReadAsync(desk, remotePath, options, cancellationToken).ConfigureAwait(false);
#else
        using var src = await OpenReadAsync(desk, remotePath, options, cancellationToken).ConfigureAwait(false);
#endif
        long bytes = 0;
        try
        {
            using (var file = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buf, 0, n, cancellationToken).ConfigureAwait(false);
                    bytes += n;
                }
            }
        }
        catch (Exception e)
        {
            try { File.Delete(dest); } catch (Exception) { /* nothing to clean */ }
            if (e is IOException or UnauthorizedAccessException && e is not GaiaDeskException)
                throw new GaiaDeskException($"cannot write {dest}: {e.Message}", new ErrorDetails { Kind = ErrorKinds.Local, Reason = ErrorKinds.Local, Operation = "download" }, e);
            throw;
        }
        return new CopyResult
        {
            Direction = "download", Desk = desk, Destination = dest, Files = 1, Dirs = 0, Bytes = bytes, ResumedBytes = 0,
            Failed = new List<CopyFailure>(), Seconds = (DateTime.UtcNow - started).TotalSeconds,
        };
    }
}
