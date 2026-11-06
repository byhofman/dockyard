using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace Dockyard.Core;

// The elevated half of Dockyard: the UI starts a copy of itself with "runas" and tails its log file.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class Elevation
{
    public const int RebootExitCode = 3010;
    public static readonly string[] Modes = ["--machine-install", "--machine-update", "--machine-uninstall", "--machine-dashboard", "--machine-trust-ca", "--machine-untrust-ca"];

    public static bool IsWorker(string[] args) => args.Any(a => Modes.Contains(a));

    public static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static (string Sid, string Name) CurrentUser
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return (identity.User!.Value, identity.Name);
        }
    }

    // Returns true when the worker reports a reboot is needed.
    public static async Task<bool> Run(Action<string> log, string mode, params string[] extra)
    {
        var logFile = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(logFile, "");
        var worker = Start(mode, extra, logFile, Environment.ProcessId);

        using (worker)
        using (var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            while (true)
            {
                var exited = worker.HasExited;
                while (await reader.ReadLineAsync() is { } line) log(line);
                if (exited) break;
                await Task.Delay(250);
            }
            File.Delete(logFile);
            return worker.ExitCode switch
            {
                0 => false,
                RebootExitCode => true,
                _ => throw new InvalidOperationException("The administrator step failed (see the log)"),
            };
        }
    }

    // For replacing this very exe: the caller exits right after, and the worker relaunches the app.
    public static void StartDetached(string mode, params string[] extra) =>
        Start(mode, extra, Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}.log"), keepPid: 0).Dispose();

    static Process Start(string mode, string[] extra, string logFile, int keepPid)
    {
        var (sid, name) = CurrentUser;
        string[] args = [mode, .. extra, "--user-sid", sid, "--user-name", name, "--log", logFile, "--keep-pid", keepPid.ToString()];
        try
        {
            return Process.Start(new ProcessStartInfo(Paths.Self, string.Join(' ', args.Select(Shell.Quote)))
            {
                UseShellExecute = true,
                Verb = "runas",
            })!;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Administrator approval was declined");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not ask for administrator rights ({ex.Message}). In a session without a desktop, run this from an elevated shell.");
        }
    }

    public static int Worker(string[] args)
    {
        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        string Arg(string name) => Opt(name) ?? throw new ArgumentException($"Missing {name}");

        using var writer = new StreamWriter(new FileStream(Arg("--log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8) { AutoFlush = true };
        void Log(string line) { lock (writer) writer.WriteLine(line); }

        try
        {
            var keepPid = int.Parse(Arg("--keep-pid"));
            if (args.Contains("--machine-uninstall"))
            {
                MachineSetup.Uninstall(Log, keepPid).GetAwaiter().GetResult();
                return 0;
            }
            if (args.Contains("--machine-update"))
            {
                foreach (var name in Arg("--machine-update").Split(','))
                    MachineSetup.Update(Log, Enum.Parse<Component>(name), Opt("--source"), keepPid).GetAwaiter().GetResult();
                return 0;
            }
            if (Opt("--machine-trust-ca") is { } certificate)
            {
                MachineSetup.TrustCa(Log, certificate);
                return 0;
            }
            if (args.Contains("--machine-untrust-ca"))
            {
                MachineSetup.UntrustCa(Log);
                return 0;
            }
            if (Opt("--machine-dashboard") is { } dashboard)
            {
                MachineSetup.SetDashboard(Log, dashboard == "add", keepPid).GetAwaiter().GetResult();
                return 0;
            }
            var reboot = MachineSetup.Install(Log, Arg("--user-sid"), Arg("--user-name"), keepPid, args.Contains("--engine-only")).GetAwaiter().GetResult();
            return reboot ? RebootExitCode : 0;
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            return 1;
        }
    }
}
