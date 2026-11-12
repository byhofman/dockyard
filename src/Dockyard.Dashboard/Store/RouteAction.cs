namespace Dockyard.Dashboard.Store;

readonly record struct RouteAction(RouteKind Kind, string Id = "")
{
    public static readonly RouteAction None = new(RouteKind.None);
}
