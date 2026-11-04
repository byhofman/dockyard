using System.Net;
using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class ContainersTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_parses_summaries()
    {
        var stub = StubHandler.WithPing().Json("""
            [{"Id":"abc123def4567890","Names":["/shop-api-1"],"Image":"node:22","ImageID":"sha256:1",
              "Command":"node server.js","Created":1793785824,"State":"running","Status":"Up 2 minutes",
              "Ports":[{"IP":"0.0.0.0","PrivatePort":80,"PublicPort":8080,"Type":"tcp"},{"PrivatePort":9229,"Type":"tcp"}],
              "Labels":{"com.docker.compose.project":"shop"}}]
            """);
        using var client = stub.Client();

        var list = await client.ListContainersAsync(ct: Ct);

        Assert.Equal("/v1.47/containers/json?all=1", stub.Path(1));
        var c = Assert.Single(list);
        Assert.Equal("shop-api-1", c.Name);
        Assert.Equal("running", c.State);
        Assert.Equal(8080, c.Ports[0].PublicPort);
        Assert.Null(c.Ports[1].PublicPort);
        Assert.Equal("shop", c.Labels!["com.docker.compose.project"]);
    }

    [Fact]
    public async Task Find_filters_by_id()
    {
        var stub = StubHandler.WithPing().Json("""[{"Id":"abc","Names":["/web"],"State":"running"}]""").Json("[]");
        using var client = stub.Client();

        var found = await client.FindContainerAsync("abc", Ct);
        var missing = await client.FindContainerAsync("zzz", Ct);

        Assert.Equal("""/v1.47/containers/json?all=1&filters={"id":["abc"]}""", Uri.UnescapeDataString(stub.Path(1)));
        Assert.Equal("web", found!.Name);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Inspect_parses_details()
    {
        var stub = StubHandler.WithPing().Json("""
            {"Id":"abc","Name":"/web","Created":"2026-11-04T10:00:00.123456789Z","Platform":"linux",
             "State":{"Status":"running","Running":true,"ExitCode":0,"StartedAt":"2026-11-04T10:00:01Z","FinishedAt":"0001-01-01T00:00:00Z","Health":{"Status":"healthy"}},
             "Config":{"Image":"nginx","Env":["A=1"],"Cmd":["nginx","-g","daemon off;"],"Entrypoint":null,"Labels":{},"Tty":false,"WorkingDir":""},
             "HostConfig":{"RestartPolicy":{"Name":"unless-stopped"}},
             "NetworkSettings":{"Ports":{"80/tcp":[{"HostIp":"0.0.0.0","HostPort":"8080"}],"443/tcp":null},"Networks":{"bridge":{"IPAddress":"172.17.0.2","NetworkID":"n1"}}},
             "Mounts":[{"Type":"bind","Source":"/src","Destination":"/app","RW":true}]}
            """);
        using var client = stub.Client();

        var c = await client.InspectContainerAsync("abc", Ct);

        Assert.Equal("/v1.47/containers/abc/json", stub.Path(1));
        Assert.True(c.State.Running);
        Assert.Equal("healthy", c.State.Health!.Status);
        Assert.Equal("8080", c.NetworkSettings.Ports!["80/tcp"]![0].HostPort);
        Assert.Null(c.NetworkSettings.Ports["443/tcp"]);
        Assert.Equal("172.17.0.2", c.NetworkSettings.Networks!["bridge"].IPAddress);
        Assert.Equal("unless-stopped", c.HostConfig.RestartPolicy!.Name);
        Assert.Equal("/app", c.Mounts[0].Destination);
    }

    [Fact]
    public async Task Lifecycle_calls_use_expected_paths()
    {
        var stub = StubHandler.WithPing()
            .Json("""{"Id":"new1","Warnings":[]}""", HttpStatusCode.Created)
            .Empty().Empty().Empty().Empty();
        using var client = stub.Client();

        var id = await client.CreateContainerAsync(new CreateContainerRequest { Image = "alpine", Cmd = ["true"] }, "t1", Ct);
        await client.StartContainerAsync(id, Ct);
        await client.StopContainerAsync(id, 5, Ct);
        await client.RestartContainerAsync(id, ct: Ct);
        await client.RemoveContainerAsync(id, force: true, ct: Ct);

        Assert.Equal("new1", id);
        Assert.Equal("/v1.47/containers/create?name=t1", stub.Path(1));
        Assert.Equal("""{"Image":"alpine","Cmd":["true"]}""", stub.Bodies[1]);
        Assert.Equal("/v1.47/containers/new1/start", stub.Path(2));
        Assert.Equal("/v1.47/containers/new1/stop?t=5", stub.Path(3));
        Assert.Equal("/v1.47/containers/new1/restart", stub.Path(4));
        Assert.Equal("/v1.47/containers/new1?force=1&v=0", stub.Path(5));
        Assert.Equal(HttpMethod.Delete, stub.Requests[5].Method);
    }

    [Fact]
    public async Task Start_of_running_container_is_not_an_error()
    {
        using var client = StubHandler.WithPing().Empty(HttpStatusCode.NotModified).Client();
        await client.StartContainerAsync("abc", Ct);
    }

    [Fact]
    public async Task Remove_conflict_surfaces_engine_message()
    {
        using var client = StubHandler.WithPing()
            .Json("""{"message":"cannot remove container: container is running"}""", HttpStatusCode.Conflict).Client();
        var ex = await Assert.ThrowsAsync<ConflictException>(() => client.RemoveContainerAsync("abc", ct: Ct));
        Assert.Contains("container is running", ex.Message);
    }
}
