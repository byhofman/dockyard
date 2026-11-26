namespace Dockyard.Dashboard.Shell;

static class EngineSwitch
{
    public static string Name(string engine) => engine == "win" ? "Windows" : "Linux";

    public static string Other(string engine) => engine == "win" ? "linux" : "win";

    public static string Message(string engine, bool stopsOther)
    {
        var other = Name(Other(engine));
        return $"The dashboard, the docker CLI and tools that follow the docker context then use the {Name(engine)} engine. "
            + (stopsOther
                ? $"The {other} engine stops, and its running containers with it. That is set in Settings."
                : $"The {other} engine keeps running.");
    }
}
