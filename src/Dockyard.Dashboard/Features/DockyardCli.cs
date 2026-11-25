using System.Diagnostics;
using System.Text;

namespace Dockyard.Dashboard.Features;

// The dockyard command next to the dashboard: changing an engine is the tray's work, which it does for the CLI.
static class DockyardCli
{
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Dockyard.exe" : "Dockyard");

    public static bool IsThere => File.Exists(Path);

    public sealed record Result(int ExitCode, string Output, string Error)
    {
        public bool Ok => ExitCode == 0;
        public string Message => (Error.Trim().Length > 0 ? Error : Output).Trim();
    }

    public static async Task<Result> Run(IReadOnlyList<string> args, string? input = null, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo(Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Dockyard");
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); onLine?.Invoke(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { error.AppendLine(e.Data); onLine?.Invoke(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync();
        return new Result(process.ExitCode, output.ToString(), error.ToString());
    }
}
