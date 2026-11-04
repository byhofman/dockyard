using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class EventsTests
{
    [Fact]
    public async Task Streams_events_with_filters_and_since()
    {
        var stub = StubHandler.WithPing().Lines(
            """{"status":"start","id":"c1","Type":"container","Action":"start","Actor":{"ID":"c1","Attributes":{"name":"web","image":"nginx"}},"scope":"local","time":1793785824,"timeNano":1793785824000000001}""",
            """{"Type":"container","Action":"health_status: healthy","Actor":{"ID":"c1","Attributes":{}},"time":1793785825,"timeNano":1793785825000000000}""");
        using var client = stub.Client();

        var events = new List<DockerEvent>();
        await foreach (var e in client.EventsAsync(["container", "image"], DateTimeOffset.FromUnixTimeSeconds(1793785800), TestContext.Current.CancellationToken))
            events.Add(e);

        var path = Uri.UnescapeDataString(stub.Path(1));
        Assert.Equal("""/v1.47/events?since=1793785800&filters={"type":["container","image"]}""", path);
        Assert.Equal(2, events.Count);
        Assert.Equal("start", events[0].Action);
        Assert.Equal("web", events[0].Actor.Attributes!["name"]);
        Assert.Equal(1793785824000000001, events[0].TimeNano);
        Assert.Equal("health_status: healthy", events[1].Action);
    }

    [Fact]
    public async Task No_filters_means_plain_path()
    {
        var stub = StubHandler.WithPing().Lines();
        using var client = stub.Client();
        await foreach (var _ in client.EventsAsync(ct: TestContext.Current.CancellationToken)) { }
        Assert.Equal("/v1.47/events", stub.Path(1));
    }
}
