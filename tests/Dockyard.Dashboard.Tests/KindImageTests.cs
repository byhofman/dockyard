using Dockyard.Kind;

namespace Dockyard.Dashboard.Tests;

public sealed class KindImageTests
{
    [Fact]
    public void The_node_images_belong_to_the_pinned_kind()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "image", "versions.env"))) dir = dir.Parent;
        var pinned = File.ReadAllLines(Path.Combine(dir!.FullName, "image", "versions.env")).Single(l => l.StartsWith("KIND=", StringComparison.Ordinal))[5..];

        Assert.Equal(pinned, KindImages.KindVersion);
        Assert.All(KindImages.All, i => Assert.Matches(@"^kindest/node:v\d+\.\d+\.\d+@sha256:[0-9a-f]{64}$", i.Reference));
        Assert.All(KindImages.All, i => Assert.Equal(i.Version, KindImages.VersionOf(i.Reference)));
    }

    [Fact]
    public void An_upgrade_goes_to_the_newest_unless_a_newer_one_is_asked_for()
    {
        Assert.Equal("v1.37.0", KindImages.UpgradeTarget("v1.35.8", null).Version);
        Assert.Equal("v1.36.4", KindImages.UpgradeTarget("v1.35.8", "1.36.4").Version);
        Assert.Contains("not newer", Assert.Throws<InvalidOperationException>(() => KindImages.UpgradeTarget("v1.36.4", "v1.35.8")).Message);
        Assert.Contains("the newest", Assert.Throws<InvalidOperationException>(() => KindImages.UpgradeTarget("v1.37.0", null)).Message);
        Assert.Contains("v1.37.0, v1.36.4", Assert.Throws<InvalidOperationException>(() => KindImages.Choose("v1.20.0")).Message);
        Assert.Equal(KindImages.All, KindImages.NewerThan(null));
    }

    [Fact]
    public void A_cluster_is_made_on_the_image_of_its_version_and_keeps_the_version_in_its_definition()
    {
        var image = KindImages.Choose("v1.36.4");
        Assert.Equal(["create", "cluster", "--name", "dev", "--kubeconfig", "own", "--image", image.Reference], KindCli.Create("dev", "own", image: image));

        var folder = Directory.CreateTempSubdirectory("dockyard-defs-").FullName;
        try
        {
            ClusterDefinitions.Save("dev", new ClusterDefinition("kind: Cluster", ClusterStorage.None, [], KubernetesVersion: "v1.36.4"), folder);
            Assert.Equal("v1.36.4", ClusterDefinitions.Load("dev", folder)!.KubernetesVersion);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
