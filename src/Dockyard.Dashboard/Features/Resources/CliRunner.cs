using System.Diagnostics;
using Avalonia.Threading;

namespace Dockyard.Dashboard.Features.Resources;

static class CliRunner
{
    public static async Task<int> Run(IReadOnlyList<string> args, Action<string> onLine)
    {
        var psi = new ProcessStartInfo(Containers.DockerCli.Path())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
}
