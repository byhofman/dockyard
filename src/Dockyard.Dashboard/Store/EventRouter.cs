using Dockyard.Engine;

namespace Dockyard.Dashboard.Store;

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
