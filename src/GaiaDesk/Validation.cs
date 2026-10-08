// Argument checks, the same rules gaiadesk-cli and the other SDKs apply, so a
// bad argument is a UsageException before anything is sent.

using System.Text.RegularExpressions;

namespace GaiaDesk;

internal static class Check
{
    private static readonly Regex JobName = new("^[A-Za-z0-9._][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Mem = new(@"^\s*(\d+)\s*([MG])?B?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A desk id: one token, no whitespace, not a flag.</summary>
    public static string Desk(string? deskId)
    {
        if (deskId is null || deskId.Trim().Length == 0) throw Errors.Usage("a desk id is required");
        var d = deskId.Trim();
        if (d.Any(char.IsWhiteSpace) || d.StartsWith("-", StringComparison.Ordinal)) throw Errors.Usage($"not a desk id: \"{deskId}\"");
        return d;
    }

    public static string Job(string? name)
    {
        if (name is null || !JobName.IsMatch(name) || name.Length > 64)
            throw Errors.Usage($"a job name is 1 to 64 letters, digits, . _ - (not starting with -): \"{name}\"");
        return name;
    }

    public static string Cwd(string? cwd)
    {
        if (cwd is null || cwd.Trim().Length == 0 || cwd.Contains('\0')) throw Errors.Usage($"cwd is a directory on the desk: \"{cwd}\"");
        return cwd;
    }

    /// <summary>Environment variables: a name is non-empty, without <c>=</c>, whitespace or NUL; a value has no NUL. Errors name the variable, never its value.</summary>
    public static Dictionary<string, string> Env(IReadOnlyDictionary<string, string>? env)
    {
        if (env is null) throw Errors.Usage("env is a dictionary of variable names to values");
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in env)
        {
            if (string.IsNullOrEmpty(kv.Key) || kv.Key.Any(c => c == '=' || c == '\0' || char.IsWhiteSpace(c)))
                throw Errors.Usage($"env: \"{kv.Key}\" is not an environment variable name");
            if (kv.Value is null) throw Errors.Usage($"env: the value of {kv.Key} must be a string");
            if (kv.Value.Contains('\0')) throw Errors.Usage($"env: the value of {kv.Key} contains a NUL byte");
            o[kv.Key] = kv.Value;
        }
        return o;
    }

    public static string NonEmpty(string? v, string what)
    {
        if (v is null || v.Trim().Length == 0) throw Errors.Usage($"{what} must be a non-empty string");
        return v.Trim();
    }

    public static string? OptionalToken(string? v)
    {
        if (v is null) return null;
        if (v.Trim().Length == 0) throw Errors.Usage("deskToken must be a non-empty string (a scoped agent token, gdagt_…)");
        return v.Trim();
    }

    public static int Wake(int wake, string operation)
    {
        if (wake < 0 || wake > 120) throw Errors.Usage("wake is whole seconds, 0 to 120", operation);
        return wake;
    }

    /// <summary>Whole seconds, rounded up; negative is a UsageException.</summary>
    public static long Seconds(TimeSpan t, string what)
    {
        if (t < TimeSpan.Zero) throw Errors.Usage($"{what} must be zero or more");
        return (long)Math.Ceiling(t.TotalSeconds);
    }

    /// <summary>A memory size: megabytes, or <c>512M</c> / <c>2G</c>.</summary>
    public static long MemMb(string mem)
    {
        var m = Mem.Match(mem ?? "");
        if (!m.Success) throw Errors.Usage($"mem: not a size: \"{mem}\"");
        var n = long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Success && m.Groups[2].Value.Equals("G", StringComparison.OrdinalIgnoreCase) ? n * 1024 : n;
    }

    public static IReadOnlyList<string> Command(IReadOnlyList<string>? argv, string what)
    {
        if (argv is null || argv.Count == 0 || (argv.Count == 1 && string.IsNullOrWhiteSpace(argv[0])))
            throw Errors.Usage($"{what} needs a command");
        foreach (var a in argv) if (a is null) throw Errors.Usage($"{what}: an argument is null");
        return argv;
    }
}
