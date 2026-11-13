namespace Dockyard.Dashboard.Features.Resources;

static class NetworkDrivers
{
    static readonly HashSet<string> BuiltIn = ["bridge", "host", "none", "nat"];

    public static IReadOnlyList<string> For(bool windows) => windows ? ["nat", "transparent", "l2bridge"] : ["bridge", "macvlan", "ipvlan"];

    public static bool IsBuiltIn(string name) => BuiltIn.Contains(name);
}
