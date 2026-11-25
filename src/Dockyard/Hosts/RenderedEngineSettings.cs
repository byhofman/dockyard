using Dockyard.Core;

namespace Dockyard.Hosts;

// The Linux engine in WSL or the Mac's VM, whose daemon.json Dockyard writes before every start: the user's part is kept in
// Dockyard's settings, and an engine that will not start with a new one gets the previous one back.
static class RenderedEngineSettings
{
    public static string Read() => EngineSettings.Load(EngineSettings.Linux, Settings.Load());

    public static async Task Apply(Action<string> log, string json, Func<Action<string>, Task> restart)
    {
        if (EngineSettings.Problem(json) is { } problem) throw new InvalidOperationException(problem);
        var previous = Read();
        EngineSettings.Save(EngineSettings.Linux, json);
        try
        {
            await restart(log);
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            EngineSettings.Save(EngineSettings.Linux, previous);
            await restart(log);
            throw new InvalidOperationException($"The Linux engine did not start with these settings, so it has the previous ones again ({e.Message})");
        }
        log("Saved the Linux engine's settings");
    }
}
