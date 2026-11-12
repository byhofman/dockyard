using System.Diagnostics;
using System.Text;

namespace Dockyard.Core;

static class Shell
{
    // For a value inside a POSIX shell script, whatever it holds.
    public static string ShQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    // For a value inside a PowerShell command, whatever it holds.
    public static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            sb.Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    static ProcessStartInfo Info(string file, IEnumerable<string> args, bool redirect)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
        };
        if (redirect)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        psi.Environment["WSL_UTF8"] = "1";
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static Process Spawn(string file, params string[] args) =>
        Process.Start(Info(file, args, redirect: false)) ?? throw new InvalidOperationException($"Could not start {file}");

    public static async Task<ShellResult> Run(string file, params string[] args)
    {
        using var p = Process.Start(Info(file, args, redirect: true)) ?? throw new InvalidOperationException($"Could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return new ShellResult(p.ExitCode, await stdout, await stderr);
    }

    public static async Task<ShellResult> RunWithInput(string file, string input, params string[] args)
    {
        var info = Info(file, args, redirect: true);
        info.RedirectStandardInput = true;
        using var p = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.StandardInput.WriteAsync(input);
        p.StandardInput.Close();
        await p.WaitForExitAsync();
        return new ShellResult(p.ExitCode, await stdout, await stderr);
    }

    public static async Task<string> Check(string file, params string[] args)
    {
        var r = await Run(file, args);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} {string.Join(' ', args)} failed ({r.ExitCode}): {(r.Error + r.Output).Trim()}");
        return r.Output;
    }

    // An elevated process would start the tray elevated too; explorer.exe launches it as the signed-in user.
    public static void StartUnelevated(string exe) =>
        Process.Start(new ProcessStartInfo("explorer.exe", Quote(exe)) { UseShellExecute = false });
}
