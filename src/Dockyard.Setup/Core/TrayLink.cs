using Dockyard.Hosts;
using Dockyard.Shared;

namespace Dockyard.Core;

// Setup opens on an install only to bring it to a newer version; everything else is the installed app's own window.
static class TrayLink
{
    static readonly Version BranchBuild = new(0, 0, 0);

    // A build of a branch is 0.0.0 and installs over any release, so it hands over only to an install of itself.
    public static bool HandsOver(Version? mine, Version? installed) =>
        mine is not null && installed is not null && (installed == mine || mine != BranchBuild && installed > mine);

    public static async Task<bool> ShowSettings(IEngineHost host, TimeSpan wait)
    {
        var settings = new TrayCommand(TrayCommandKind.Settings, Assets: Updates.LocalAssets is { } folder ? Path.GetFullPath(folder) : null).ToString();
        if (await Task.Run(() => LocalPipe.TrySend(LocalPipe.TrayName, settings))) return true;
        host.StartApp();
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(500);
            if (await Task.Run(() => LocalPipe.TrySend(LocalPipe.TrayName, settings))) return true;
        }
        return false;
    }
}
