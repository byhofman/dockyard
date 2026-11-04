using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Dockyard.Core;

// Unattended install/uninstall: no window, progress on the calling console.
static partial class Silent
{
    public static bool IsSilent(string[] args) => args.Contains("--install") || args.Contains("--uninstall");

    public static int Run(string[] args)
    {
        AttachConsole(-1);
        Console.WriteLine();
        void Log(string line) => Console.WriteLine(line);
        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

        try
        {
            if (args.Contains("--uninstall"))
            {
                UserSetup.Uninstall(Log).GetAwaiter().GetResult();
                MachineStep(Log, uninstall: true);
                Log("Dockyard and both Docker engines were removed.");
                return 0;
            }

            var image = Opt("--image");
            var reboot = MachineStep(Log, uninstall: false);
            UserSetup.Install(Log, image).GetAwaiter().GetResult();
            if (!args.Contains("--no-start")) Shell.StartUnelevated(Paths.AppExe);
            Log(reboot ? "Done. Reboot to finish enabling Windows containers." : "Done. Sign out and back in once so docker-users applies to the Windows engine.");
            return reboot ? Elevation.RebootExitCode : 0;
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            return 1;
        }
    }

    static bool MachineStep(Action<string> log, bool uninstall)
    {
        if (!IsElevated)
            return Elevation.Run(log, uninstall ? "--machine-uninstall" : "--machine-install").GetAwaiter().GetResult();

        if (uninstall)
        {
            MachineSetup.Uninstall(log, Environment.ProcessId).GetAwaiter().GetResult();
            return false;
        }
        var identity = WindowsIdentity.GetCurrent();
        return MachineSetup.Install(log, identity.User!.Value, identity.Name, Environment.ProcessId).GetAwaiter().GetResult();
    }

    static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
