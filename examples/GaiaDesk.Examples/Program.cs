// GaiaDesk .NET SDK examples. Each one is a function below; run one with
//
//   export GAIADESK_API_KEY=ak_…          # an API key (desks:read, exec, files, jobs, …)
//   export GAIADESK_DESK_TOKEN=gdagt_…    # a scoped agent token for the desk
//   export GAIADESK_DESK=123456789        # the desk's id
//   dotnet run --project examples/GaiaDesk.Examples -- exec
//
// Examples: fleet, exec, stream, copy, job, support, webhook-verify, local.

using GaiaDesk;

var name = args.Length > 0 ? args[0] : "";
var examples = new Dictionary<string, Func<Task>>
{
    ["fleet"] = Examples.Fleet,
    ["exec"] = Examples.Exec,
    ["stream"] = Examples.Stream,
    ["copy"] = Examples.Copy,
    ["job"] = Examples.Job,
    ["support"] = Examples.Support,
    ["webhook-verify"] = Examples.WebhookVerify,
    ["local"] = Examples.Local,
};
if (!examples.TryGetValue(name, out var run))
{
    Console.Error.WriteLine($"usage: dotnet run -- <{string.Join("|", examples.Keys)}>");
    return 2;
}
try
{
    await run();
    return 0;
}
catch (GaiaDeskException e)
{
    // Every failure is typed: branch on the class, then Kind / Reason.
    Console.Error.WriteLine($"{e.GetType().Name} ({e.Kind}{(e.Reason is null ? "" : $", {e.Reason}")}): {e.Message}");
    if (e.RequestId is not null) Console.Error.WriteLine($"request id: {e.RequestId}");
    return e.ExitCode ?? 1;
}

internal static class Examples
{
    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"set {name}");

    private static GaiaDeskClient Client() => new(new GaiaDeskOptions
    {
        ApiKey = Env("GAIADESK_API_KEY"),
        DeskToken = Environment.GetEnvironmentVariable("GAIADESK_DESK_TOKEN"),
        E2e = E2eMode.Auto, // sealed end to end whenever the desk publishes its key
    });

    private static string Desk => Env("GAIADESK_DESK");

    /// <summary>List the desks, then wake an offline one.</summary>
    public static async Task Fleet()
    {
        using var gd = Client();
        var list = await gd.ListDesksAsync();
        foreach (var d in list.Devices)
            Console.WriteLine($"{d.DeskId}  {d.Name,-20} {(d.Online == true ? "online" : $"offline ({d.OfflineReasonText})")}");
        var desk = await gd.GetDeskAsync(Desk);
        if (desk.Online != true)
        {
            var woke = await gd.WakeAsync(Desk, TimeSpan.FromSeconds(30));
            Console.WriteLine(woke.Woke ? "woke it" : "rang it; not online yet");
        }
    }

    /// <summary>Run a command and read its result.</summary>
    public static async Task Exec()
    {
        using var gd = Client();
        var r = await gd.ExecAsync(Desk, "uname -a", new ExecOptions { Timeout = TimeSpan.FromMinutes(1), Check = true });
        Console.Write(r.Stdout);
        Console.WriteLine($"exit {r.Exit} in {r.DurationMs} ms");
    }

    /// <summary>Stream a command's output as it comes; Ctrl+C stops it.</summary>
    public static async Task Stream()
    {
        using var gd = Client();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        await using var run = gd.ExecStream(Desk, new[] { "ping", "-c", "5", "127.0.0.1" }, null, cts.Token);
        await foreach (var chunk in run)
            (chunk.Source == OutputSource.Stdout ? Console.Out : Console.Error).Write(chunk.Text);
        var exit = await run.WaitAsync();
        Console.WriteLine($"exit {exit.ExitCode}{(exit.Killed ? " (stopped)" : "")}");
        exit.ThrowIfError();
    }

    /// <summary>Upload a file, then stream it back down.</summary>
    public static async Task Copy()
    {
        using var gd = Client();
        var local = Path.GetTempFileName();
        await File.WriteAllTextAsync(local, "hello from .NET\n");
        var up = await gd.UploadFileAsync(Desk, local, "/tmp/");
        Console.WriteLine($"uploaded {up.Bytes} bytes to {up.Destination}");
        await using var down = await gd.OpenReadAsync(Desk, $"/tmp/{Path.GetFileName(local)}");
        using var reader = new StreamReader(down);
        Console.Write(await reader.ReadToEndAsync());
    }

    /// <summary>Start a background job, follow its log, and wait for it.</summary>
    public static async Task Job()
    {
        using var gd = Client();
        await gd.RunJobAsync(Desk, "dotnet-example", "for i in 1 2 3; do echo tick $i; sleep 1; done",
            new JobOptions { Priority = JobPriority.Low, KeepAwake = true, Shell = Shell.Sh });
        await using var logs = gd.FollowJobLogs(Desk, "dotnet-example");
        await foreach (var chunk in logs) Console.Write(chunk.Text);
        var done = await gd.WaitJobAsync(Desk, "dotnet-example", TimeSpan.FromMinutes(5));
        Console.WriteLine($"{done.Job.State}, exit {done.Job.ExitCode}");
    }

    /// <summary>Create a support session for the embed SDK and hand the page its token.</summary>
    public static async Task Support()
    {
        using var gd = Client();
        var s = await gd.CreateSupportSessionAsync(new SupportSessionCreate
        {
            Mode = SupportModes.Cobrowse,
            Customer = new Dictionary<string, object?> { ["name"] = "Ada", ["plan"] = "pro" },
            ExpiresIn = TimeSpan.FromMinutes(30),
        });
        Console.WriteLine($"session {s.Id}: embed token {s.EmbedToken}, agents join with {s.JoinCode} ({s.JoinUrl})");
        foreach (var open in await gd.ListSupportSessionsAsync())
            Console.WriteLine($"{open.Id} {open.State} customer present: {open.CustomerPresent}");
    }

    /// <summary>Verify a webhook delivery (here, one signed locally) and read it.</summary>
    public static Task WebhookVerify()
    {
        const string secret = "whsec_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var body = System.Text.Encoding.UTF8.GetBytes("{\"id\":\"evt_1b2c3d4e5f60718293a4b5c6\",\"type\":\"desk.offline\",\"created\":1791300000,\"data\":{\"desk\":{\"desk_id\":\"123456789\",\"owner\":\"you@example.com\",\"reason\":\"silent\",\"reason_text\":\"nothing heard\"}}}");
        var header = WebhookSignature.Sign(secret, body, DateTimeOffset.UtcNow);
        // In your endpoint: the raw body bytes and the GaiaDesk-Signature header.
        if (!WebhookSignature.Verify(secret, header, body)) throw new InvalidOperationException("bad signature");
        var ev = WebhookEvent.Parse(body);
        Console.WriteLine($"{ev.Type}: desk {ev.Desk!.DeskId} ({ev.Desk.ReasonText})");
        return Task.CompletedTask;
    }

    /// <summary>On the desk itself: its own API over its socket or pipe, no server involved.</summary>
    public static async Task Local()
    {
        using var gd = GaiaDeskClient.Local();
        var me = (await gd.ListDesksAsync()).Devices.Single();
        var stats = await gd.StatsAsync(me.DeskId);
        Console.WriteLine($"{stats.Hostname}: {stats.CpuPercent:0}% CPU, {stats.MemFreeMb} MB free, {stats.JobsRunning} jobs");
    }
}
