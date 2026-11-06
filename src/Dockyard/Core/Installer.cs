using Dockyard.Hosts;

namespace Dockyard.Core;

// The window-less wizard: everything the wizard window does on Install, with the outcome in the exit code.
static class Installer
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int WrongArguments = 2;
    public const int RebootRequired = 3010;

    public static async Task<int> RunSilent(IEngineHost host, SetupOptions options, Action<string> log, Action<string> error)
    {
        try
        {
            if (options.Action == SetupAction.Uninstall)
            {
                await host.Uninstall(log, options.RemoveData);
                log(options.RemoveData ? "Dockyard was removed, with its images and containers." : "Dockyard was removed. Images and containers were kept.");
                return Ok;
            }

            if (options.Resume)
            {
                await host.Resume(log);
                if (options.Start) host.StartApp();
                log(host.DoneMessage(reboot: false));
                return Ok;
            }

            var settings = Settings.Load();
            if (ComponentRules.Problems(host.Components, options.With, options.Without, settings.RemovedComponents) is { Count: > 0 } problems)
            {
                foreach (var problem in problems) error(problem);
                return WrongArguments;
            }
            var removedBefore = settings.RemovedComponents.ToList();
            var removed = ComponentRules.Removed(host.Components, removedBefore, options.With, options.Without);
            if (!removed.SequenceEqual(settings.RemovedComponents))
            {
                settings.RemovedComponents = removed;
                settings.Save();
            }

            var dashboard = DashboardChoice.Resolve(options.Dashboard, host.IsInstalled, host.HasDashboard, host.RunsFromInstallLocation);
            var reboot = await host.Install(log, new InstallRequest(dashboard, options.Image, Silent: true) { Without = removed });
            foreach (var flag in options.With)
                // Removed earlier counts too: something else on the machine may answer for it meanwhile, such as a plugin of another package.
                if (ComponentRules.ByFlag(host.Components, flag) is { } added && (removedBefore.Contains(added.Id) || await host.Installed(added.Id) is null))
                    await host.SetComponent(log, added.Id, installed: true);
            foreach (var flag in options.Without)
                if (ComponentRules.ByFlag(host.Components, flag) is { } gone && await host.Installed(gone.Id) is not null)
                    await host.SetComponent(log, gone.Id, installed: false, options.RemoveData);
            // Installing never fails halfway unnoticed: a step that quietly did nothing would otherwise report success.
            if (!reboot && !host.IsInstalled) throw new InvalidOperationException("Setup finished, but Dockyard is not installed");
            if (host.HasDashboard != dashboard)
                throw new InvalidOperationException(dashboard ? "The dashboard was not installed" : "The dashboard was not removed");
            if (options.Start && !reboot) host.StartApp();
            log(host.DoneMessage(reboot));
            return reboot ? RebootRequired : Ok;
        }
        catch (Exception ex)
        {
            error("ERROR: " + ex.Message);
            return Failed;
        }
    }
}
