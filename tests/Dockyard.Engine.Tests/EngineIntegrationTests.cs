using Dockyard.Engine.Transport;

namespace Dockyard.Engine.Tests;

[Trait("Category", "Engine")]
public sealed class EngineIntegrationTests
{
    [Fact]
    public Task Linux_container_lifecycle() =>
        Lifecycle("tcp://127.0.0.1:2375", "alpine:3.20", ["sh", "-c", "echo hello; echo oops >&2"]);

    [Fact]
    public Task Windows_container_lifecycle()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows engine only exists on Windows");
        return Lifecycle("npipe:////./pipe/docker_windows", "mcr.microsoft.com/windows/nanoserver:ltsc2025",
            ["cmd", "/c", "echo hello& echo oops 1>&2"]);
    }

    [Fact]
    public async Task Linux_lists_resources_and_disk_usage()
    {
        var client = await TryConnect("tcp://127.0.0.1:2375");
        if (client is null) Assert.Skip("Linux engine not running");
        using (client)
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.NotEmpty(await client.ListNetworksAsync(ct));
            await client.ListImagesAsync(ct);
            await client.ListVolumesAsync(ct);
            var df = await client.DiskUsageAsync(ct);
            Assert.True(df.LayersSize >= 0);
        }
    }

    // A throwaway engine named by the environment: CI's on Linux, or a VM started for this on a Mac. Never whatever happens to be the default.
    [Fact]
    public async Task Files_in_a_container_and_in_a_volume_can_be_listed_read_and_written()
    {
        if (Environment.GetEnvironmentVariable("DOCKYARD_TEST_ENDPOINT") is not { Length: > 0 } endpoint) { Assert.Skip("No test engine named"); return; }
        var ct = TestContext.Current.CancellationToken;
        using var client = new DockerClient(DockerEndpoint.Parse(endpoint));
        await client.PullImageAsync("alpine:3.24", ct: ct);

        var container = await client.CreateContainerAsync(new CreateContainerRequest { Image = "alpine:3.24", Cmd = ["true"] }, ct: ct);
        var volume = "dockyard-test-" + Guid.NewGuid().ToString("N")[..8];
        await client.PostNodeAsync("volumes/create", new System.Text.Json.Nodes.JsonObject { ["Name"] = volume }, ct);
        string? holder = null;
        var image = await client.EnsureEmptyImageAsync(ct);
        try
        {
            var etc = await ContainerFiles.ListAsync(client, container, "/etc", ct);
            Assert.Contains(etc, e => e is { Name: "passwd", IsDirectory: false });
            Assert.Contains(etc, e => e is { Name: "apk", IsDirectory: true });
            Assert.DoesNotContain(etc, e => e.Name.Contains('/'));
            Assert.Contains(await ContainerFiles.ListAsync(client, container, "/", ct), e => e is { Name: "etc", IsDirectory: true });

            var passwd = await ContainerFiles.ReadAsync(client, container, "/etc/passwd", 1 << 20, ct);
            Assert.Contains("root:", System.Text.Encoding.UTF8.GetString(passwd.Data));

            using (var edited = new MemoryStream("changed\n"u8.ToArray()))
                await ContainerFiles.WriteAsync(client, container, "/etc", "motd", edited, passwd.Entry, ct);
            Assert.Equal("changed\n", System.Text.Encoding.UTF8.GetString((await ContainerFiles.ReadAsync(client, container, "/etc/motd", 1024, ct)).Data));

            holder = await client.CreateVolumeHolderAsync(image, volume, "/volume", ct);
            Assert.Empty(await ContainerFiles.ListAsync(client, holder, "/volume", ct));
            using (var content = new MemoryStream("in the volume"u8.ToArray()))
                await ContainerFiles.WriteAsync(client, holder, "/volume", "note.txt", content, ct: ct);
            Assert.Equal(["note.txt"], (await ContainerFiles.ListAsync(client, holder, "/volume", ct)).Select(e => e.Name));
            using var saved = new MemoryStream();
            await ContainerFiles.SaveToAsync(client, holder, "/volume/note.txt", saved, ct);
            Assert.Equal("in the volume", System.Text.Encoding.UTF8.GetString(saved.ToArray()));
        }
        finally
        {
            await client.RemoveContainerAsync(container, force: true, ct: CancellationToken.None);
            if (holder is not null) await client.RemoveContainerAsync(holder, force: true, ct: CancellationToken.None);
            await client.RemoveVolumeAsync(volume, ct: CancellationToken.None);
            await client.RemoveImageAsync(image, ct: CancellationToken.None);
        }
    }

    static async Task<DockerClient?> TryConnect(string uri)
    {
        var client = new DockerClient(DockerEndpoint.Parse(uri));
        try
        {
            await client.PingAsync();
            return client;
        }
        catch (EngineUnavailableException)
        {
            client.Dispose();
            return null;
        }
    }

    static async Task Lifecycle(string uri, string image, List<string> cmd)
    {
        var client = await TryConnect(uri);
        if (client is null) Assert.Skip($"No engine at {uri}");
        using (client)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var ct = timeout.Token;
            var since = DateTimeOffset.UtcNow.AddSeconds(-2);

            await client.PullImageAsync(image, null, ct);
            var id = await client.CreateContainerAsync(
                new CreateContainerRequest { Image = image, Cmd = cmd, Labels = new() { ["dockyard.test"] = "1" } }, null, ct);
            try
            {
                await client.StartContainerAsync(id, ct);
                await WaitForEvent(client, id, "die", since, ct);

                var lines = new List<LogLine>();
                await foreach (var line in client.LogsAsync(id, new LogOptions(Follow: false, Tail: null), ct)) lines.Add(line);
                Assert.Contains(lines, l => l.Stream == LogStream.Stdout && l.Text.Trim() == "hello");
                Assert.Contains(lines, l => l.Stream == LogStream.Stderr && l.Text.Trim() == "oops");
                Assert.All(lines, l => Assert.NotNull(l.Time));

                Assert.Contains(await client.ListContainersAsync(all: true, ct), c => c.Id == id);
                Assert.False((await client.InspectContainerAsync(id, ct)).State.Running);
            }
            finally
            {
                await client.RemoveContainerAsync(id, force: true, ct: CancellationToken.None);
            }
            await WaitForEvent(client, id, "destroy", since, ct);
        }
    }

    static async Task WaitForEvent(DockerClient client, string id, string action, DateTimeOffset since, CancellationToken ct)
    {
        await foreach (var e in client.EventsAsync(["container"], since, ct))
            if (e.Actor.ID == id && e.Action == action) return;
    }
}
