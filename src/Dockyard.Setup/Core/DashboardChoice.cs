namespace Dockyard.Core;

static class DashboardChoice
{
    // A new install has what its catalog defaults to; an existing one keeps what it has.
    public static bool Resolve(bool? requested, bool isInstalled, bool hasDashboard, bool defaultOn = true) =>
        requested ?? (isInstalled ? hasDashboard : defaultOn);
}
