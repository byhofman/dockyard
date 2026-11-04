using System.Net;
using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class DockerClientTests
{
    [Fact]
    public async Task Ping_reads_api_version_and_os()
    {
        var stub = StubHandler.WithPing("1.47", "windows");
        using var client = stub.Client();
        var ping = await client.PingAsync(TestContext.Current.CancellationToken);
        Assert.Equal("1.47", ping.ApiVersion);
        Assert.Equal("windows", client.OsType);
        Assert.True(client.IsWindows);
        Assert.Equal("/_ping", stub.Path(0));
    }

    [Fact]
    public async Task First_call_pings_then_uses_versioned_path()
    {
        var stub = StubHandler.WithPing("1.48").Json("""{"Version":"28.5.1","ApiVersion":"1.48","Os":"linux","Arch":"amd64"}""");
        using var client = stub.Client();
        var version = await client.VersionAsync(TestContext.Current.CancellationToken);
        Assert.Equal("28.5.1", version.Version);
        Assert.Equal("/v1.48/version", stub.Path(1));
    }

    [Fact]
    public async Task Too_old_engine_is_rejected()
    {
        using var client = StubHandler.WithPing("1.41").Client();
        var ex = await Assert.ThrowsAsync<DockerException>(() => client.PingAsync(TestContext.Current.CancellationToken));
        Assert.Contains("1.44", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, typeof(NotFoundException))]
    [InlineData(HttpStatusCode.Conflict, typeof(ConflictException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(DockerException))]
    public async Task Errors_map_to_typed_exceptions_with_engine_message(HttpStatusCode status, Type expected)
    {
        using var client = StubHandler.WithPing().Json("""{"message":"No such container: x"}""", status).Client();
        var ex = await Assert.ThrowsAnyAsync<DockerException>(() => client.VersionAsync(TestContext.Current.CancellationToken));
        Assert.IsType(expected, ex);
        Assert.Equal("No such container: x", ex.Message);
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public void Filters_are_json_and_escaped()
    {
        var q = DockerClient.Filters(("type", "container"), ("type", "image"), ("dangling", "true"));
        Assert.Equal("""{"type":["container","image"],"dangling":["true"]}""", Uri.UnescapeDataString(q));
    }

    [Fact]
    public async Task Unreachable_engine_throws_EngineUnavailable()
    {
        using var client = new DockerClient(Transport.DockerEndpoint.Parse("tcp://127.0.0.1:1"));
        await Assert.ThrowsAsync<EngineUnavailableException>(() => client.PingAsync(TestContext.Current.CancellationToken));
    }
}
