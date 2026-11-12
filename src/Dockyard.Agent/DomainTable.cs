using System.Net;
using System.Text.Json;
using Dockyard.Engine;

namespace Dockyard.Agent;

// Names to containers, and which port to use for each, kept up to date from the engine's events.
sealed class DomainTable(Func<DockerClient> connect, string choicesFile, Action<string> log)
{
    readonly object gate = new();
    Dictionary<string, DomainTarget> byDomain = [];
    Dictionary<string, int> chosen = LoadChoices(choicesFile);

    static Dictionary<string, int> LoadChoices(string file)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(file), AgentApiJson.Default.DictionaryStringInt32) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public DomainTarget? Find(string domain)
    {
        lock (gate) return byDomain.GetValueOrDefault(domain);
    }

    public bool Contains(string domain) => Find(domain) is not null;

    // Chosen by name, so it outlives the container: Compose replaces containers whenever their configuration changes.
    public int? Port(DomainTarget target)
    {
        int? choice;
        lock (gate) choice = chosen.TryGetValue(target.ContainerName, out var c) ? c : null;
        return DomainNames.PickPort(choice, target.Label, Listening(target), target.Exposed);
    }

    static IEnumerable<int> Listening(DomainTarget target)
    {
        if (target.Pid is not { } pid) return [];
        List<int> ports = [];
        foreach (var file in new[] { "tcp", "tcp6" })
        {
            try { ports.AddRange(ListeningPorts.Parse(File.ReadAllText($"/proc/{pid}/net/{file}"), udp: false)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return ports;
    }

    public List<DomainInfo> List()
    {
        List<DomainTarget> targets;
        lock (gate) targets = byDomain.Values.ToList();
        return targets.Select(t =>
        {
            bool isChosen;
            lock (gate) isChosen = chosen.ContainsKey(t.ContainerName);
            return new DomainInfo(t.ContainerId, t.Domain, Port(t), isChosen);
        }).OrderBy(d => d.Domain).ToList();
    }

    public void Choose(string containerId, int? port)
    {
        lock (gate)
        {
            var name = byDomain.Values.FirstOrDefault(t => t.ContainerId == containerId)?.ContainerName;
            if (name is null) return;
            if (port is { } p) chosen[name] = p;
            else chosen.Remove(name);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(choicesFile)!);
                File.WriteAllText(choicesFile, JsonSerializer.Serialize(chosen, AgentApiJson.Default.DictionaryStringInt32));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"cannot keep the port choice: {e.Message}"); }
        }
    }

    public async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var docker = connect();
                var since = DateTimeOffset.UtcNow.AddSeconds(-1);
                await Refresh(docker, ct);
                await foreach (var e in docker.EventsAsync(["container"], since, ct))
                    if (e.Action is "start" or "die" or "destroy" or "rename") await Refresh(docker, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await Task.Delay(1000, ct);
            }
        }
    }

    async Task Refresh(DockerClient docker, CancellationToken ct)
    {
        Dictionary<string, DomainTarget> next = [];
        foreach (var summary in (await docker.ListContainersAsync(all: false, ct)).Where(c => c.State == "running"))
        {
            var inspect = await docker.GetNodeAsync($"containers/{Uri.EscapeDataString(summary.Id)}/json", ct);
            var address = inspect["NetworkSettings"]?["Networks"]?.AsObject()
                .Select(n => n.Value?["IPAddress"]?.GetValue<string>())
                .FirstOrDefault(ip => ip is { Length: > 0 });
            var exposed = inspect["Config"]?["ExposedPorts"]?.AsObject()
                .Select(p => p.Key.Split('/'))
                .Where(p => p.Length == 2 && p[1] == "tcp" && int.TryParse(p[0], out _))
                .Select(p => int.Parse(p[0])).ToList() ?? [];
            var hostNetwork = summary.HostConfig?.NetworkMode == "host";
            var labels = summary.Labels;
            foreach (var domain in DomainNames.For(summary.Name, labels?.GetValueOrDefault("com.docker.compose.project"), labels?.GetValueOrDefault("com.docker.compose.service")))
                // The first running replica answers for a Compose service's shared name.
                next.TryAdd(domain, new DomainTarget(summary.Id, summary.Name, domain, address is null ? null : IPAddress.Parse(address),
                    inspect["State"]?["Pid"]?.GetValue<int>(), exposed, DomainNames.LabelPort(labels), hostNetwork));
        }
        lock (gate) byDomain = next;
    }
}
