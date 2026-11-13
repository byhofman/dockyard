namespace Dockyard.Core;

static class DashboardChoice
{
    // A copy running from where it is installed was put there by an earlier script, which already chose the edition.
    public static bool Resolve(bool? requested, bool isInstalled, bool hasDashboard, bool runsFromInstallLocation) =>
        requested ?? (!isInstalled && !runsFromInstallLocation || hasDashboard);
}
