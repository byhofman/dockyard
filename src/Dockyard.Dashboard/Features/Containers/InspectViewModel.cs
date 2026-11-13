using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Containers;

sealed class InspectViewModel(ContainerInspect c)
{
    public IReadOnlyList<InspectSection> Sections { get; } = Build(c);

    static List<InspectSection> Build(ContainerInspect c)
    {
        var command = string.Join(' ', (c.Config.Entrypoint ?? []).Concat(c.Config.Cmd ?? []));
        List<InspectSection> sections =
        [
            new("General",
            [
                new("ID", c.Id),
                new("Image", c.Config.Image),
                new("Command", command),
                new("Status", c.State.Health is { } h ? $"{c.State.Status} ({h.Status})" : c.State.Status),
                new("Created", c.Created),
                new("Restart policy", c.HostConfig.RestartPolicy?.Name is { Length: > 0 } r ? r : "no"),
                .. (c.Config.WorkingDir is { Length: > 0 } wd ? new[] { new KeyValue("Working dir", wd) } : []),
            ]),
        ];

        var ports = (c.NetworkSettings.Ports ?? [])
            .SelectMany(p => (p.Value ?? []).Select(b => new KeyValue(p.Key, $"{(b.HostIp is "" or "0.0.0.0" ? "*" : b.HostIp)}:{b.HostPort}")))
            .DistinctBy(k => (k.Key, k.Value.TrimStart('*', ':', '[', ']')))
            .ToList();
        if (ports.Count > 0) sections.Add(new("Ports", ports));

        var env = (c.Config.Env ?? []).Select(e =>
        {
            var eq = e.IndexOf('=');
            return eq < 0 ? new KeyValue(e, "") : new KeyValue(e[..eq], e[(eq + 1)..]);
        }).ToList();
        if (env.Count > 0) sections.Add(new("Environment", env));

        var mounts = c.Mounts.Select(m => new KeyValue(m.Destination, $"{(m.Type == "volume" ? m.Name : m.Source)} ({m.Type}, {(m.RW ? "rw" : "ro")})")).ToList();
        if (mounts.Count > 0) sections.Add(new("Mounts", mounts));

        var networks = (c.NetworkSettings.Networks ?? []).Select(n => new KeyValue(n.Key, n.Value.IPAddress)).ToList();
        if (networks.Count > 0) sections.Add(new("Networks", networks));

        var labels = (c.Config.Labels ?? []).OrderBy(l => l.Key).Select(l => new KeyValue(l.Key, l.Value)).ToList();
        if (labels.Count > 0) sections.Add(new("Labels", labels));

        return sections;
    }
}
