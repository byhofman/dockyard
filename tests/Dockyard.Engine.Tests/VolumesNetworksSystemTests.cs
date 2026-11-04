using System.Net;
using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class VolumesNetworksSystemTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Volumes_list_remove_prune()
    {
        var stub = StubHandler.WithPing()
            .Json("""{"Volumes":[{"Name":"pgdata","Driver":"local","Mountpoint":"/var/lib/docker/volumes/pgdata/_data","CreatedAt":"2026-11-04T10:00:00Z","Labels":null,"Scope":"local"}],"Warnings":null}""")
            .Empty()
            .Json("""{"VolumesDeleted":["x"],"SpaceReclaimed":42}""");
        using var client = stub.Client();

        var volumes = await client.ListVolumesAsync(Ct);
        await client.RemoveVolumeAsync("pgdata", ct: Ct);
        var pruned = await client.PruneVolumesAsync(Ct);

        Assert.Equal("pgdata", Assert.Single(volumes).Name);
        Assert.Equal("/v1.47/volumes", stub.Path(1));
        Assert.Equal("/v1.47/volumes/pgdata?force=0", stub.Path(2));
        Assert.Equal("""/v1.47/volumes/prune?filters={"all":["true"]}""", Uri.UnescapeDataString(stub.Path(3)));
        Assert.Equal(42, pruned.SpaceReclaimed);
    }

    [Fact]
    public async Task Volumes_list_handles_null_array()
    {
        using var client = StubHandler.WithPing().Json("""{"Volumes":null,"Warnings":null}""").Client();
        Assert.Empty(await client.ListVolumesAsync(Ct));
    }

    [Fact]
    public async Task Networks_crud_and_connect()
    {
        var stub = StubHandler.WithPing()
            .Json("""[{"Name":"bridge","Id":"n1","Created":"2026-11-04T10:00:00Z","Scope":"local","Driver":"bridge","Internal":false,"IPAM":{"Config":[{"Subnet":"172.17.0.0/16","Gateway":"172.17.0.1"}]},"Containers":{},"Labels":{}}]""")
            .Json("""{"Name":"bridge","Id":"n1","Driver":"bridge","Containers":{"c1":{"Name":"web","IPv4Address":"172.17.0.2/16"}}}""")
            .Json("""{"Id":"n2","Warning":""}""", HttpStatusCode.Created)
            .Empty(HttpStatusCode.OK).Empty(HttpStatusCode.OK).Empty()
            .Json("""{"NetworksDeleted":["n3"]}""");
        using var client = stub.Client();

        var list = await client.ListNetworksAsync(Ct);
        var detail = await client.InspectNetworkAsync("n1", Ct);
        var id = await client.CreateNetworkAsync("app", ct: Ct);
        await client.ConnectNetworkAsync("n2", "c1", Ct);
        await client.DisconnectNetworkAsync("n2", "c1", ct: Ct);
        await client.RemoveNetworkAsync("n2", Ct);
        await client.PruneNetworksAsync(Ct);

        Assert.Equal("172.17.0.0/16", list[0].IPAM!.Config![0].Subnet);
        Assert.Equal("web", detail.Containers!["c1"].Name);
        Assert.Equal("n2", id);
        Assert.Equal("/v1.47/networks/create", stub.Path(3));
        Assert.Equal("""{"Name":"app","Driver":"bridge"}""", stub.Bodies[3]);
        Assert.Equal("/v1.47/networks/n2/connect", stub.Path(4));
        Assert.Equal("""{"Container":"c1"}""", stub.Bodies[4]);
        Assert.Equal("""{"Container":"c1","Force":false}""", stub.Bodies[5]);
        Assert.Equal(HttpMethod.Delete, stub.Requests[6].Method);
        Assert.Equal("/v1.47/networks/prune", stub.Path(7));
    }

    [Fact]
    public async Task System_df_and_prunes()
    {
        var stub = StubHandler.WithPing()
            .Json("""
                {"LayersSize":5000,
                 "Images":[{"Id":"sha256:a","RepoTags":["alpine:3.20"],"Size":3000,"Containers":1}],
                 "Containers":[{"Id":"c1","Names":["/web"],"Image":"nginx","SizeRw":10,"SizeRootFs":200,"State":"running"}],
                 "Volumes":[{"Name":"pgdata","Driver":"local","UsageData":{"Size":700,"RefCount":1}}],
                 "BuildCache":[{"ID":"b1","Size":900,"InUse":false,"Shared":false}]}
                """)
            .Json("""{"ContainersDeleted":["c2"],"SpaceReclaimed":11}""")
            .Json("""{"CachesDeleted":["b1"],"SpaceReclaimed":900}""");
        using var client = stub.Client();

        var df = await client.DiskUsageAsync(Ct);
        var containers = await client.PruneContainersAsync(Ct);
        var cache = await client.PruneBuildCacheAsync(Ct);

        Assert.Equal("/v1.47/system/df", stub.Path(1));
        Assert.Equal(5000, df.LayersSize);
        Assert.Equal(700, df.Volumes![0].UsageData!.Size);
        Assert.Equal(900, df.BuildCache![0].Size);
        Assert.Equal(10, df.Containers![0].SizeRw);
        Assert.Equal("/v1.47/containers/prune", stub.Path(2));
        Assert.Equal(11, containers.SpaceReclaimed);
        Assert.Equal("/v1.47/build/prune", stub.Path(3));
        Assert.Equal(900, cache.SpaceReclaimed);
    }
}
