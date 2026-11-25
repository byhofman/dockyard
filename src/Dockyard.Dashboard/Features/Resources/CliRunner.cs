using System.Diagnostics;
using Avalonia.Threading;

namespace Dockyard.Dashboard.Features.Resources;

static class CliRunner
{
    public static async Task<int> Run(IReadOnlyList<string> args, Action<string> onLine)
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

    public static async Task<(int Exit, string Output)> Capture(IReadOnlyList<string> args, string stdin, CancellationToken cancel)
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
