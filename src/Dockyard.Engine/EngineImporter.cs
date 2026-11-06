using System.Text.Json.Nodes;

namespace Dockyard.Engine;

public sealed record ImportSummary(int Images, int Volumes, int Containers, int Networks, IReadOnlyList<string> Skipped);

// Copies what another engine holds, through its API, while that engine keeps running. The source only gains what this
// removes again: a holder per volume and a committed image per container.
public sealed class EngineImporter(DockerClient source, DockerClient target, Action<string> log)
{
    const string HolderMount = "/dockyard-import";
    static readonly HashSet<string> BuiltInNetworks = ["bridge", "host", "none"];

    public async Task<ImportSummary> RunAsync(CancellationToken ct = default)
    {
        List<string> skipped = [];
        var networks = await ImportNetworks(ct);
        var containers = await source.ListContainersAsync(all: true, ct);
        containers = containers.Where(c => c.Labels?.ContainsKey("dockyard.helper") != true).ToList();

        var existing = (await target.ListContainersAsync(all: true, ct)).Select(c => c.Name).ToHashSet();
        var toCopy = containers.Where(c =>
        {
            if (!existing.Contains(c.Name)) return true;
            skipped.Add($"container {c.Name}: one with that name is already there");
            return false;
        }).ToList();

        // A commit keeps what changed inside a container since it started, which pulling its image again would lose.
        Dictionary<string, string> committed = [];
        try
        {
            foreach (var container in toCopy)
            {
                var reference = $"dockyard-import/{container.Name.ToLowerInvariant()}:latest";
                await source.PostNodeAsync($"commit?container={Uri.EscapeDataString(container.Id)}&repo={Uri.EscapeDataString($"dockyard-import/{container.Name.ToLowerInvariant()}")}&tag=latest&pause=false", new JsonObject(), ct);
                committed[container.Id] = reference;
            }
            var images = await ImportImages(committed.Values, ct);
            var volumes = await ImportVolumes(skipped, ct);
            var created = 0;
            foreach (var container in toCopy)
            {
                await CreateContainer(container, committed[container.Id], ct);
                created++;
            }
            return new ImportSummary(images, volumes, created, networks, skipped);
        }
        finally
        {
            foreach (var reference in committed.Values)
            {
                try { await source.RemoveImageAsync(reference, force: true, CancellationToken.None); }
                catch (DockerException) { log($"The temporary image {reference} is still on the source"); }
            }
        }
    }

    async Task<int> ImportNetworks(CancellationToken ct)
    {
        var there = (await target.GetNodeAsync("networks", ct)).AsArray().Select(n => n!["Name"]!.GetValue<string>()).ToHashSet();
        var count = 0;
        foreach (var network in (await source.GetNodeAsync("networks", ct)).AsArray())
        {
            var name = network!["Name"]!.GetValue<string>();
            if (BuiltInNetworks.Contains(name) || there.Contains(name)) continue;
            // No subnet: the source's may overlap with what the target already uses, and containers find each other by name.
            var body = new JsonObject
            {
                ["Name"] = name,
                ["Driver"] = network["Driver"]?.GetValue<string>() ?? "bridge",
                ["Internal"] = network["Internal"]?.GetValue<bool>() ?? false,
                ["Attachable"] = network["Attachable"]?.GetValue<bool>() ?? false,
                ["Labels"] = network["Labels"]?.DeepClone(),
            };
            await target.PostNodeAsync("networks/create", body, ct);
            count++;
        }
        if (count > 0) log($"Networks: {count}");
        return count;
    }

    async Task<int> ImportImages(IEnumerable<string> extra, CancellationToken ct)
    {
        var tags = (await source.ListImagesAsync(ct))
            .SelectMany(i => i.RepoTags ?? [])
            .Where(t => t != "<none>:<none>" && !t.StartsWith("dockyard-empty:", StringComparison.Ordinal))
            .Concat(extra).Distinct().ToList();
        if (tags.Count == 0) return 0;
        log($"Copying {tags.Count} images");
        // All in one request: images that share layers then carry them once.
        var names = string.Join('&', tags.Select(t => "names=" + Uri.EscapeDataString(t)));
        using var saved = await source.OpenAsync($"images/get?{names}", ct);
        await using var tar = await saved.Content.ReadAsStreamAsync(ct);
        await target.SendTarAsync(HttpMethod.Post, "images/load?quiet=1", tar, ct);
        return tags.Count;
    }

    async Task<int> ImportVolumes(List<string> skipped, CancellationToken ct)
    {
        var volumes = await source.ListVolumesAsync(ct);
        if (volumes.Count == 0) return 0;
        var there = (await target.ListVolumesAsync(ct)).Select(v => v.Name).ToHashSet();
        var sourceImage = await source.EnsureEmptyImageAsync(ct);
        var targetImage = await target.EnsureEmptyImageAsync(ct);
        var count = 0;
        try
        {
            foreach (var volume in volumes)
            {
                if (there.Contains(volume.Name))
                {
                    skipped.Add($"volume {volume.Name}: one with that name is already there");
                    continue;
                }
                log($"Copying volume {volume.Name}");
                await target.PostNodeAsync("volumes/create", new JsonObject { ["Name"] = volume.Name, ["Labels"] = await VolumeLabels(volume.Name, ct) }, ct);
                var from = await source.CreateVolumeHolderAsync(sourceImage, volume.Name, HolderMount, ct);
                var to = await target.CreateVolumeHolderAsync(targetImage, volume.Name, HolderMount, ct);
                try
                {
                    using var archive = await source.GetArchiveAsync(from, HolderMount + "/", ct);
                    await using var tar = await archive.Content.ReadAsStreamAsync(ct);
                    await target.PutArchiveAsync(to, "/", tar, ct);
                    count++;
                }
                finally
                {
                    await source.RemoveContainerAsync(from, force: true, ct: CancellationToken.None);
                    await target.RemoveContainerAsync(to, force: true, ct: CancellationToken.None);
                }
            }
        }
        finally
        {
            // What was copied must not turn into a failure over a helper image that is already gone.
            foreach (var engine in new[] { source, target })
            {
                try { await engine.RemoveImageAsync(sourceImage, force: true, CancellationToken.None); }
                catch (DockerException e) { log($"The temporary image {sourceImage} stays behind: {e.Message}"); }
            }
        }
        return count;
    }

    async Task<JsonNode?> VolumeLabels(string name, CancellationToken ct) =>
        (await source.GetNodeAsync($"volumes/{Uri.EscapeDataString(name)}", ct))["Labels"]?.DeepClone();

    async Task CreateContainer(ContainerSummary container, string image, CancellationToken ct)
    {
        var inspect = await source.GetNodeAsync($"containers/{Uri.EscapeDataString(container.Id)}/json", ct);
        var body = CreateRequest(inspect, image);
        await target.PostNodeAsync($"containers/create?name={Uri.EscapeDataString(container.Name)}", body, ct);
        log($"Container {container.Name} is there, stopped");
    }

    // Internal so that what crosses over can be tested without two engines.
    public static JsonObject CreateRequest(JsonNode inspect, string image)
    {
        var body = inspect["Config"]!.DeepClone().AsObject();
        body["Image"] = image;
        // Docker sets the hostname to the short id unless one was asked for, and the id changes here.
        if (body["Hostname"]?.GetValue<string>() is { } hostname && inspect["Id"]?.GetValue<string>()?.StartsWith(hostname, StringComparison.Ordinal) == true)
            body.Remove("Hostname");
        body["HostConfig"] = inspect["HostConfig"]?.DeepClone();
        if (inspect["NetworkSettings"]?["Networks"] is JsonObject networks)
        {
            var endpoints = new JsonObject();
            foreach (var (name, endpoint) in networks)
            {
                if (BuiltInNetworks.Contains(name)) continue;
                var aliases = endpoint?["Aliases"]?.DeepClone();
                endpoints[name] = aliases is null ? new JsonObject() : new JsonObject { ["Aliases"] = aliases };
            }
            if (endpoints.Count > 0) body["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = endpoints };
        }
        return body;
    }
}
