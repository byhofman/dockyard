using System.Diagnostics;
using Avalonia.Threading;

namespace Dockyard.Dashboard.Features.Resources;

static class CliRunner
{
    public static async Task<int> Run(IReadOnlyList<string> args, Action<string> onLine, bool windowsEngine = false)
    {
        var psi = new ProcessStartInfo(DockerCli.Path())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["COMPOSE_ANSI"] = "never";
        if (OperatingSystem.IsWindows() && !windowsEngine) psi.Environment["COMPOSE_CONVERT_WINDOWS_PATHS"] = "1";
        psi.Environment["COMPOSE_PROGRESS"] = "plain";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start docker");
        void Forward(string? line)
        {
            if (line is not null) Dispatcher.UIThread.Post(() => onLine(line));
        }
        process.OutputDataReceived += (_, e) => Forward(e.Data);
        process.ErrorDataReceived += (_, e) => Forward(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    public static async Task<(int Exit, string Output, string Error)> Output(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(DockerCli.Path())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start docker");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    public static async Task<(int Exit, string Output)> Capture(IReadOnlyList<string> args, string stdin, CancellationToken cancel, bool windowsEngine = false)
    {
        var psi = new ProcessStartInfo(DockerCli.Path())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["COMPOSE_ANSI"] = "never";
        if (OperatingSystem.IsWindows() && !windowsEngine) psi.Environment["COMPOSE_CONVERT_WINDOWS_PATHS"] = "1";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start docker");
        var output = process.StandardOutput.ReadToEndAsync(cancel);
        var error = process.StandardError.ReadToEndAsync(cancel);
        await process.StandardInput.WriteAsync(stdin.AsMemory(), cancel);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync(cancel); }
        catch (OperationCanceledException) { try { process.Kill(); } catch (InvalidOperationException) { } throw; }
        return (process.ExitCode, ((await output) + (await error)).Trim());
    }
}
