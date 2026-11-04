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
