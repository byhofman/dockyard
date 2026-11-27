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
}
