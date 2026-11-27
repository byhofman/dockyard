using System.Reflection;

namespace Dockyard.Core;

// The CLI and the dashboard are equal apps over this library; the tray and anything else (a test host) run it too.
enum RunningApp { Cli, Dashboard, Tray, Other }

static class ThisApp
{
    public static readonly RunningApp Current = Of(Assembly.GetEntryAssembly()?.GetName().Name);

    public static RunningApp Of(string? entryAssembly) => entryAssembly switch
    {
        "dockyard-cli" => RunningApp.Cli,
        "Dockyard.Dashboard" => RunningApp.Dashboard,
        "Dockyard.Tray" => RunningApp.Tray,
        _ => RunningApp.Other,
    };

    public static bool IsApp(RunningApp app) => app is RunningApp.Cli or RunningApp.Dashboard;

    // The two apps' components, of which at least one stays.
    public static readonly string[] Components = [nameof(Component.DockyardCli), ComponentRules.DashboardId];

    // Dockyard is installed only with an app in place to update and uninstall it; each host knows where its two go.
    public static bool EitherInstalled(Func<string, bool> exists, string cliExe, string dashboardExe) =>
        exists(cliExe) || exists(dashboardExe);

    // Dockyard itself for updates and the installed version. The tray is part of neither app, so it goes by whichever is
    // installed, the dashboard first: at least one of the two always stays.
    public static string SelfComponent(RunningApp app, bool hasDashboard) => app switch
    {
        RunningApp.Dashboard => ComponentRules.DashboardId,
        RunningApp.Tray when hasDashboard => ComponentRules.DashboardId,
        _ => nameof(Component.DockyardCli),
    };

    // The app that hands steps changing its own files to another process, as the facade's Caller.
    public static string? Caller(RunningApp app) => app switch
    {
        RunningApp.Dashboard => ComponentRules.DashboardId,
        RunningApp.Tray => ComponentRules.TrayId,
        _ => null,
    };

    // The app that finishes a step the running one hands over: the CLI's own installed copy for the CLI, and for anything
    // else the dashboard first, as Dockyard itself is for the tray. A step that removes the running app goes to the other,
    // which stays: at least one does.
    public static (RunningApp App, string Exe) HandOffTarget(RunningApp running, bool removesRunning, Func<string, bool> exists, string cliExe, string? dashboardExe)
    {
        RunningApp[] order = running == RunningApp.Cli ? [RunningApp.Cli, RunningApp.Dashboard] : [RunningApp.Dashboard, RunningApp.Cli];
        foreach (var app in order.Where(a => !(removesRunning && a == running)))
            if ((app == RunningApp.Cli ? cliExe : dashboardExe) is { } exe && exists(exe)) return (app, exe);
        throw new InvalidOperationException("Neither the dockyard command nor the dashboard is installed to finish this: run Dockyard's setup again");
    }
}
