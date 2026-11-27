using System.Diagnostics;
using System.Text;

namespace Dockyard.InstallTests;

public sealed record Result(int Exit, string Output)
{
    public override string ToString() => $"exit {Exit}\n{Output}";
}

public static class Sh
{
    public static async Task<Result> Run(string file, IEnumerable<string> args, IDictionary<string, string?>? env = null, string? input = null, int seconds = 1200)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var (key, value) in env ?? new Dictionary<string, string?>())
        {
            if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{file} did not start");
        var output = new StringBuilder();
        void Add(string? line) { if (line is not null) lock (output) output.AppendLine(line); }
        process.OutputDataReceived += (_, e) => Add(e.Data);
        process.ErrorDataReceived += (_, e) => Add(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (input is not null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new TimeoutException($"{file} {string.Join(' ', args)} did not return within {seconds} s:\n{output}");
        }
        process.WaitForExit();
        return new Result(process.ExitCode, output.ToString().Replace("\0", "").Trim());
    }

    public static Task<Result> Run(string file, params string[] args) => Run(file, args, null);

    public static async Task<Result> Ok(string file, params string[] args)
    {
        var result = await Run(file, args, null);
        Assert.True(result.Exit == 0, $"{file} {string.Join(' ', args)}: {result}");
        return result;
    }

    public static async Task Until(Func<Task<bool>> condition, string what, int seconds = 120)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"{what} did not happen within {seconds} s");
            await Task.Delay(2000);
        }
    }
}

// Finds the dockyard command and runs setup against it. Nothing is installed unless the tests are switched on for this OS.
public abstract class InstallFixture(InstallOs os) : IAsyncLifetime
{
    protected bool Active => Gate.On(os);

    public string Cli { get; private set; } = "";

    public string? Assets { get; private set; }

    public virtual async ValueTask InitializeAsync()
    {
        if (!Active) return;
        var cli = Environment.GetEnvironmentVariable("DOCKYARD");
        Assets = Environment.GetEnvironmentVariable("DOCKYARD_ASSETS");
        if (cli is { Length: > 0 })
        {
            Cli = Path.GetFullPath(cli);
            Assets = Assets is { Length: > 0 } ? Path.GetFullPath(Assets) : null;
        }
        else if (Environment.GetEnvironmentVariable("DOCKYARD_VERSION") is { Length: > 0 } version)
        {
            Assets = null;
            Cli = await DownloadRelease(version);
        }
        else throw new InvalidOperationException("Set DOCKYARD (and DOCKYARD_ASSETS), or DOCKYARD_VERSION");
    }

    static async Task<string> DownloadRelease(string version)
    {
        var asset = OperatingSystem.IsWindows() ? "dockyard-windows-x64.exe" : OperatingSystem.IsMacOS() ? "dockyard-macos-arm64" : "dockyard-linux-x64";
        var folder = Directory.CreateTempSubdirectory("dockyard-release-").FullName;
        await Sh.Ok("gh", "release", "download", $"cli/{version}", "--repo", "byhofman/dockyard", "--pattern", asset, "--dir", folder);
        var path = Path.Combine(folder, asset);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public virtual IDictionary<string, string?> ChildEnv => new Dictionary<string, string?>();

    public Task<Result> Dockyard(params string[] args) => Sh.Run(Cli, args, ChildEnv);

    public Task<Result> Setup(params string[] args) =>
        Sh.Run(Cli, ["setup", "--silent", .. args, .. (Assets is null ? [] : new[] { "--assets", Assets })], ChildEnv);

    public async Task SetupOk(params string[] args)
    {
        var result = await Setup(args);
        Assert.True(result.Exit == 0, $"setup {string.Join(' ', args)}: {result}");
    }

    public async Task<Result> Ok(string file, params string[] args)
    {
        var result = await Sh.Run(file, args, ChildEnv);
        Assert.True(result.Exit == 0, $"{file} {string.Join(' ', args)}: {result}");
        return result;
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
