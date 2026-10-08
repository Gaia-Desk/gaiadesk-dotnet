// A streamed desk operation: `POST /desks/{id}/exec?stream=1` (ExecEvents) or
// `GET …/jobs/{name}/logs?follow=1` (JobLogEvents), read as Server-Sent
// Events. Output arrives as OutputChunks (IAsyncEnumerable); WaitAsync gives
// how it ended. A sealed stream's events are opened before they get here.

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GaiaDesk.Http;

namespace GaiaDesk;

/// <summary>Which output a chunk is.</summary>
public enum OutputSource
{
    /// <summary>Standard output (a job's log is all stdout).</summary>
    Stdout,
    /// <summary>Standard error.</summary>
    Stderr,
}

/// <summary>A piece of output, as it came.</summary>
public sealed class OutputChunk
{
    internal OutputChunk(OutputSource source, string text)
    {
        Source = source;
        Text = text;
    }

    /// <summary>stdout or stderr.</summary>
    public OutputSource Source { get; }
    /// <summary>The text (UTF-8 characters split across events are already joined).</summary>
    public string Text { get; }
    /// <summary>The text as UTF-8 bytes.</summary>
    public byte[] Data => Encoding.UTF8.GetBytes(Text);

    /// <inheritdoc />
    public override string ToString() => $"{Source}: {Text}";
}

/// <summary>How a stream ended.</summary>
public sealed class StreamExit
{
    /// <summary>The exit code: the command's own (exec), 0 for a log stream that ended, 130 when stopped, 254 refused, 255 the rest.</summary>
    public int ExitCode { get; init; }
    /// <summary>A short account of the end (an error's message, <c>job build exited (exit 0)</c>, <c>interrupted</c>).</summary>
    public string Message { get; init; } = "";
    /// <summary>Exec: the run's end without its output (the <c>exit</c> event), when it ended on the desk.</summary>
    public ExecExit? Result { get; init; }
    /// <summary>What went wrong, when something did (the desk's error, the desk lost, a sealed event that did not open).</summary>
    public ErrorInfo? Error { get; init; }
    /// <summary>Logs: the job as it ended (the <c>end</c> event).</summary>
    public Job? Job { get; init; }
    /// <summary>It was stopped by <see cref="DeskStream.Kill"/> or the stream's cancellation token.</summary>
    public bool Killed { get; init; }

    /// <summary>Ended with exit code 0 and no error.</summary>
    public bool Succeeded => ExitCode == 0 && Error is null && !Killed;

    /// <summary>Throws the typed error for <see cref="Error"/>, if any (<see cref="RefusedException"/>, <see cref="ConnectionLostException"/>, …).</summary>
    public void ThrowIfError()
    {
        if (Error is null) return;
        throw Errors.ForKind(Error.Kind, string.IsNullOrEmpty(Error.Message) ? Message : Error.Message, new ErrorDetails
        {
            Kind = ErrorKinds.For(Error.Kind, Error.Reason), Reason = Error.Reason, Desk = Error.Desk, ExitCode = ExitCode,
        });
    }
}

/// <summary>A stream's output, collected (<see cref="DeskStream.CollectAsync"/>).</summary>
public sealed class StreamResult
{
    /// <summary>All of stdout.</summary>
    public string Stdout { get; init; } = "";
    /// <summary>All of stderr.</summary>
    public string Stderr { get; init; } = "";
    /// <summary>How it ended.</summary>
    public StreamExit Exit { get; init; } = new();
}

internal sealed record StreamStart(Stream? Body, IDisposable Response, Func<IAsyncEnumerable<SseEvent>, IAsyncEnumerable<SseEvent>>? Unseal);

/// <summary>
/// A streamed desk operation. Enumerate it (<c>await foreach</c>) for the output as it comes, then
/// <see cref="WaitAsync"/> for how it ended. <see cref="Kill"/> (or disposing it, or cancelling the
/// token it was started with) closes the request: the API stops the command, or stops following the
/// job (which goes on). The request starts at once; output is buffered until read. One reader at a time.
/// </summary>
public sealed class DeskStream : IAsyncEnumerable<OutputChunk>, IAsyncDisposable, IDisposable
{
    private readonly Channel<OutputChunk> _chunks = Channel.CreateUnbounded<OutputChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenRegistration _outer;
    private readonly Task<StreamExit> _exit;
    private readonly bool _logs;
    private readonly string _jobName;
    private volatile bool _killed;

    internal DeskStream(string operation, bool logs, Func<CancellationToken, Task<StreamStart>> start, CancellationToken ct, string jobName = "")
    {
        Operation = operation;
        _logs = logs;
        _jobName = jobName;
        if (ct.IsCancellationRequested) Kill();
        else _outer = ct.Register(Kill);
        _exit = Task.Run(() => Run(start));
    }

    /// <summary>The request, as <c>METHOD /path</c>.</summary>
    public string Operation { get; }

    private async Task<StreamExit> Run(Func<CancellationToken, Task<StreamStart>> start)
    {
        StreamStart? s = null;
        try
        {
            s = await start(_cts.Token).ConfigureAwait(false);
            if (s.Body is null) throw new ProtocolException("the GaiaDesk API sent an event stream with no body", new ErrorDetails { Operation = Operation });
            var events = SseParser.Read(s.Body, _cts.Token);
            if (s.Unseal is not null) events = s.Unseal(events);
            return _logs ? await LogEvents(events).ConfigureAwait(false) : await ExecEvents(events).ConfigureAwait(false);
        }
        catch (Exception) when (_killed)
        {
            return new StreamExit { ExitCode = 130, Message = "interrupted", Killed = true };
        }
        catch (GaiaDeskException e)
        {
            return ForError(e);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            return ForError(new UnreachableException($"the GaiaDesk API could not be reached: {e.Message}",
                new ErrorDetails { Kind = ErrorKinds.Network, Reason = "network", ExitCode = 255, Operation = Operation }));
        }
        finally
        {
            s?.Response.Dispose();
            _chunks.Writer.TryComplete();
        }
    }

    private void Push(OutputSource source, string text)
    {
        if (text.Length > 0) _chunks.Writer.TryWrite(new OutputChunk(source, text));
    }

    private static JsonElement? Object(SseEvent ev)
    {
        JsonElement v;
        try { v = GaiaDeskJson.Parse(ev.Data); }
        catch (JsonException) { return null; }
        return v.ValueKind == JsonValueKind.Object ? v : null;
    }

    private static string Name(JsonElement o, SseEvent ev) => GaiaDeskJson.Str(o, "event") ?? ev.Event;

    private async Task<StreamExit> ExecEvents(IAsyncEnumerable<SseEvent> events)
    {
        JsonElement? last = null;
        string lastName = "";
        await foreach (var sse in events.ConfigureAwait(false))
        {
            if (Object(sse) is not { } o) continue;
            var name = Name(o, sse);
            if ((name == "stdout" || name == "stderr") && GaiaDeskJson.Str(o, "data") is { } data)
                Push(name == "stdout" ? OutputSource.Stdout : OutputSource.Stderr, data);
            else if (name == "exit" || name == "error")
            {
                last = o;
                lastName = name;
            }
        }
        if (last is not { } end) return Lost("the event stream ended before the command did");
        if (lastName == "exit")
        {
            var r = GaiaDeskJson.To<ExecExit>(end, Operation);
            r.AdditionalProperties?.Remove("event");
            return new StreamExit { ExitCode = r.Exit, Message = r.Error?.Message ?? "", Result = r, Error = r.Error };
        }
        var error = ErrorOf(end) ?? new ErrorInfo { Kind = ErrorKinds.Protocol, Message = "the desk reported an error" };
        var exit = GaiaDeskJson.TryProp(end, "exit", out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var code)
            ? code : Errors.DeskOpExit(error.Kind);
        return new StreamExit { ExitCode = exit, Message = error.Message, Error = error };
    }

    private async Task<StreamExit> LogEvents(IAsyncEnumerable<SseEvent> events)
    {
        await foreach (var sse in events.ConfigureAwait(false))
        {
            if (Object(sse) is not { } o) continue;
            var name = Name(o, sse);
            if (name == "output" && GaiaDeskJson.Str(o, "data") is { } data) Push(OutputSource.Stdout, data);
            else if (name == "end")
            {
                Job? job = GaiaDeskJson.TryProp(o, "job", out var j) && j.ValueKind == JsonValueKind.Object ? GaiaDeskJson.To<Job>(j, Operation) : null;
                var jn = string.IsNullOrEmpty(job?.Name) ? _jobName : job!.Name;
                var tail = job?.ExitCode is { } ec ? $"job {jn} exited (exit {ec})" : $"job {jn} {(string.IsNullOrEmpty(job?.State) ? "ended" : job!.State)}";
                return new StreamExit { ExitCode = 0, Message = tail, Job = job };
            }
            else if (name == "interrupted") return new StreamExit { ExitCode = 0, Message = "stopped following; the job goes on" };
            else if (name == "error")
            {
                var error = ErrorOf(o) ?? new ErrorInfo { Kind = ErrorKinds.Protocol, Message = "the desk reported an error" };
                return new StreamExit { ExitCode = Errors.DeskOpExit(error.Kind), Message = error.Message, Error = error };
            }
        }
        return Lost("the event stream ended before the job did");
    }

    private ErrorInfo? ErrorOf(JsonElement o) =>
        GaiaDeskJson.TryProp(o, "error", out var e) && e.ValueKind == JsonValueKind.Object ? GaiaDeskJson.To<ErrorInfo>(e, Operation) : null;

    private static StreamExit Lost(string message) =>
        new() { ExitCode = 255, Message = message, Error = new ErrorInfo { Kind = ErrorKinds.ConnectionLost, Message = message } };

    /// <summary>The StreamExit for an error that ended (or prevented) a stream.</summary>
    internal static StreamExit ForError(GaiaDeskException e)
    {
        ErrorInfo error;
        var env = e.Json is { } j ? ErrorEnvelope.From(j) : null;
        if (env is not null) error = new ErrorInfo { Kind = env.Kind, Message = string.IsNullOrEmpty(env.Message) ? e.Message : env.Message, Reason = env.Reason, Desk = env.Desk };
        else
        {
            var kind = e.Kind == ErrorKinds.Network ? ErrorKinds.Unreachable : e is ProtocolException ? ErrorKinds.Protocol : e.Kind;
            error = new ErrorInfo { Kind = kind, Message = e.Message, Reason = e.Reason, Desk = e.Desk };
        }
        return new StreamExit { ExitCode = e.ExitCode ?? 255, Message = e.Message, Error = error };
    }

    /// <summary>Stop: closes the request (the server stops the command, or stops following the job).</summary>
    public void Kill()
    {
        _killed = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>How the stream ended (it never throws for the operation's failure: see <see cref="StreamExit.Error"/>).</summary>
    public Task<StreamExit> WaitAsync(CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled) return _exit;
        return WaitWith(cancellationToken);
    }

    private async Task<StreamExit> WaitWith(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            await Task.WhenAny(_exit, tcs.Task).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        return await _exit.ConfigureAwait(false);
    }

    /// <summary>The output as it comes.</summary>
    public async IAsyncEnumerator<OutputChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var r = _chunks.Reader;
        while (await r.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            while (r.TryRead(out var c)) yield return c;
    }

    /// <summary>Read everything, then how it ended.</summary>
    public async Task<StreamResult> CollectAsync(CancellationToken cancellationToken = default)
    {
        var o = new StringBuilder();
        var e = new StringBuilder();
        await foreach (var c in this.WithCancellation(cancellationToken).ConfigureAwait(false))
            (c.Source == OutputSource.Stdout ? o : e).Append(c.Text);
        return new StreamResult { Stdout = o.ToString(), Stderr = e.ToString(), Exit = await WaitAsync(cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>Kills the stream if it still runs.</summary>
    public void Dispose()
    {
        if (!_exit.IsCompleted) Kill();
        _outer.Dispose();
    }

    /// <summary>Kills the stream if it still runs, and waits for it to end.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        try { await _exit.ConfigureAwait(false); } catch (Exception) { /* its end is in WaitAsync */ }
    }
}
