using Dockyard.Engine;

namespace Dockyard.Dashboard.Store;

enum RouteKind { None, Refresh, Remove, Images, Volumes, Networks }

readonly record struct RouteAction(RouteKind Kind, string Id = "")
{
    public static readonly RouteAction None = new(RouteKind.None);
}

static class EventRouter
{
    static readonly HashSet<string> Refreshing =
        ["create", "start", "restart", "die", "stop", "kill", "pause", "unpause", "rename", "health_status", "update", "oom"];

    public static RouteAction Route(DockerEvent e)
    {
        var action = e.Action.Split(':', 2)[0].Trim();
        return e.Type switch
        {
            "container" when action == "destroy" => new RouteAction(RouteKind.Remove, e.Actor.ID),
            "container" when Refreshing.Contains(action) => new RouteAction(RouteKind.Refresh, e.Actor.ID),
            "image" => new RouteAction(RouteKind.Images),
            "volume" => new RouteAction(RouteKind.Volumes),
            "network" => new RouteAction(RouteKind.Networks),
            _ => RouteAction.None,
        };
    }
}

static class Backoff
{
    static readonly TimeSpan First = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan Max = TimeSpan.FromSeconds(10);

    public static TimeSpan Next(TimeSpan? previous) =>
        previous is { } p ? TimeSpan.FromTicks(Math.Min(p.Ticks * 2, Max.Ticks)) : First;
}
