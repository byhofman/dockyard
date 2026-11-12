using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace Dockyard.Core;

// The elevated half of Dockyard: the UI starts a copy of itself with "runas" and tails its log file.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static partial class Elevation
{
    public const int RebootExitCode = 3010;
    public static readonly string[] Modes = ["--machine-install", "--machine-update", "--machine-uninstall", "--machine-dashboard", "--machine-component", "--machine-trust-ca", "--machine-untrust-ca"];

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

    // The log path comes from the unelevated caller, which made the file. A link or junction on the way would turn this
    // elevated append into a write anywhere, so the file is not created here and must be the very path that was given.
    static FileStream OpenCallerLog(string path)
    {
        var expected = LongPath(Path.GetFullPath(path));
        var stream = new FileStream(expected, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var actual = Read(buffer => GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, Buffer, 0));
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            stream.Dispose();
            throw new UnauthorizedAccessException($"{path} leads somewhere else ({actual})");
        }
        stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    static string LongPath(string path) => Read(buffer => GetLongPathName(path, buffer, Buffer)) is { Length: > 0 } full ? full : path;

    const int Buffer = 32768;

    static unsafe string Read(Func<nint, uint> call)
    {
        var buffer = new char[Buffer];
        fixed (char* start = buffer)
        {
            var length = call((nint)start);
            return length is > 0 and < Buffer ? new string(start, 0, (int)length) : "";
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, nint path, int length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetLongPathName(string shortPath, nint longPath, int length);

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
        string[] assets = Updates.LocalAssets is { } dir ? ["--assets", dir] : [];
        string[] args = [mode, .. extra, .. assets, "--user-sid", sid, "--user-name", name, "--log", logFile, "--keep-pid", keepPid.ToString()];
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

        FileStream log;
        try { log = OpenCallerLog(Arg("--log")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Nowhere to report to; the caller reads the exit code and says the administrator step failed.
            return 1;
        }
        using var writer = new StreamWriter(log, Encoding.UTF8) { AutoFlush = true };
        void Log(string line) { lock (writer) writer.WriteLine(line); }

        try
        {
            Updates.LocalAssets = Opt("--assets");
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
            if (Opt("--machine-component") is { } component)
            {
                var change = args.SkipWhile(a => a != "--machine-component").Skip(2).First();
                MachineSetup.SetComponent(Log, Enum.Parse<Component>(component), change == "add", Arg("--user-sid"), Arg("--user-name")).GetAwaiter().GetResult();
                return 0;
            }
            if (Opt("--machine-dashboard") == "update")
            {
                MachineSetup.UpdateDashboard(Log, Arg("--source-dir"), keepPid).GetAwaiter().GetResult();
                return 0;
            }
            if (Opt("--machine-trust-ca") is { } certificate)
            {
                MachineSetup.TrustCa(Log, MachineSetup.DecodeCertificate(certificate));
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
            var reboot = MachineSetup.Install(Log, Arg("--user-sid"), Arg("--user-name"), keepPid, args.Contains("--engine-only"), Opt("--without")?.Split(',') ?? []).GetAwaiter().GetResult();
            return reboot ? RebootExitCode : 0;
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            return 1;
        }
    }
}
