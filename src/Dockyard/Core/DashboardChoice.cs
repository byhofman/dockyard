namespace Dockyard.Core;

static class DashboardChoice
{
    public static bool Resolve(bool? requested, bool isInstalled, bool hasDashboard) =>
        requested ?? (!isInstalled || hasDashboard);
}
