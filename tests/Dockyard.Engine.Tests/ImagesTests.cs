using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class ImagesTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Collect : IProgress<PullProgress>
    {
        public List<PullProgress> Items { get; } = [];
        public void Report(PullProgress value) => Items.Add(value);
    }

    [Fact]
    public async Task List_parses_images_including_untagged()
    {
        var stub = StubHandler.WithPing().Json("""
            [{"Id":"sha256:a","ParentId":"","RepoTags":["alpine:3.20"],"RepoDigests":["alpine@sha256:x"],"Created":1793785824,"Size":7800000,"Labels":null,"Containers":-1},
             {"Id":"sha256:b","ParentId":"","RepoTags":null,"RepoDigests":null,"Created":1793785000,"Size":100,"Labels":{},"Containers":-1}]
            """);
        using var client = stub.Client();

        var images = await client.ListImagesAsync(Ct);

        Assert.Equal("/v1.47/images/json", stub.Path(1));
        Assert.Equal("alpine:3.20", images[0].RepoTags![0]);
        Assert.Null(images[1].RepoTags);
        Assert.Equal(7800000, images[0].Size);
    }

    [Fact]
    public async Task Remove_and_prune_use_expected_paths()
    {
        var stub = StubHandler.WithPing().Json("""[{"Untagged":"alpine:3.20"}]""").Json("""{"ImagesDeleted":null,"SpaceReclaimed":1234}""");
        using var client = stub.Client();

        await client.RemoveImageAsync("alpine:3.20", force: true, Ct);
        var pruned = await client.PruneImagesAsync(danglingOnly: true, Ct);

        Assert.Equal("/v1.47/images/alpine%3A3.20?force=1", stub.Path(1));
        Assert.Equal(HttpMethod.Delete, stub.Requests[1].Method);
        Assert.Equal("""/v1.47/images/prune?filters={"dangling":["true"]}""", Uri.UnescapeDataString(stub.Path(2)));
        Assert.Equal(1234, pruned.SpaceReclaimed);
    }

    [Theory]
    [InlineData("alpine", "alpine", "latest")]
    [InlineData("alpine:3.20", "alpine", "3.20")]
    [InlineData("localhost:5000/app", "localhost:5000/app", "latest")]
    [InlineData("localhost:5000/app:v1", "localhost:5000/app", "v1")]
    [InlineData("alpine@sha256:abc", "alpine@sha256:abc", "")]
    public void Splits_references(string reference, string image, string tag) =>
        Assert.Equal((image, tag), DockerClient.SplitReference(reference));

    [Fact]
    public async Task Pull_reports_aggregated_progress()
    {
        var stub = StubHandler.WithPing().Lines(
            """{"status":"Pulling from library/alpine","id":"3.20"}""",
            """{"status":"Downloading","progressDetail":{"current":50,"total":100},"progress":"[==>  ]","id":"l1"}""",
            """{"status":"Downloading","progressDetail":{"current":10,"total":40},"id":"l2"}""",
            """{"status":"Pull complete","progressDetail":{},"id":"l1"}""",
            """{"status":"Pull complete","progressDetail":{},"id":"l2"}""",
            """{"status":"Status: Downloaded newer image for alpine:3.20"}""");
        using var client = stub.Client();
        var progress = new Collect();

        await client.PullImageAsync("alpine:3.20", progress, Ct);

        Assert.Equal("/v1.47/images/create?fromImage=alpine&tag=3.20", stub.Path(1));
        Assert.Equal(HttpMethod.Post, stub.Requests[1].Method);
        Assert.Equal(new PullProgress("Downloading", 60, 140), progress.Items[2]);
        Assert.Equal(new PullProgress("Status: Downloaded newer image for alpine:3.20", 140, 140), progress.Items[^1]);
    }

    [Fact]
    public async Task Pull_error_line_throws()
    {
        using var client = StubHandler.WithPing().Lines(
            """{"errorDetail":{"message":"manifest unknown"},"error":"manifest unknown"}""").Client();
        var ex = await Assert.ThrowsAsync<DockerException>(() => client.PullImageAsync("nope:1", null, Ct));
        Assert.Equal("manifest unknown", ex.Message);
    }
}
