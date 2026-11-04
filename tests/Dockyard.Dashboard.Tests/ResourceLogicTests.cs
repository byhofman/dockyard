using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Tests;

public sealed class ResourceLogicTests
{
    static ContainerItem Item(string id, string name, string state = "running", string? project = null, string? service = null,
        string imageId = "sha256:img", string? files = null, params string[] volumes)
    {
        var labels = new Dictionary<string, string>();
        if (project is not null)
        {
            labels[ContainerItem.ProjectLabel] = project;
            labels[ContainerItem.ServiceLabel] = service ?? name;
            labels[ComposeCatalog.WorkingDirLabel] = @"C:\src\" + project;
            if (files is not null) labels[ComposeCatalog.ConfigFilesLabel] = files;
        }
        var item = new ContainerItem(id);
        item.Update(new ContainerSummary
        {
            Id = id, Names = ["/" + name], State = state, ImageID = imageId, Labels = labels,
            Mounts = volumes.Select(v => new MountPoint { Type = "volume", Name = v, Destination = "/d" }).ToList(),
        });
        return item;
    }

    [Fact]
    public void Compose_projects_group_services_and_merge_recents()
    {
        var containers = new[]
        {
            Item("1", "shop-api-1", project: "shop", service: "api", files: @"C:\src\shop\compose.yaml"),
            Item("2", "shop-db-1", "exited", project: "shop", service: "db"),
            Item("3", "redis"),
        };
        var recents = new[]
        {
            new RecentCompose { Path = @"C:\src\shop\compose.yaml", Project = "shop" },
            new RecentCompose { Path = @"C:\src\blog\compose.yaml", Project = "blog" },
        };

        var projects = ComposeCatalog.Build(containers, recents);

        Assert.Equal(["blog", "shop"], projects.Select(p => p.Name));
        var shop = projects[1];
        Assert.Equal(["api", "db"], shop.Services.Select(s => s.Name));
        Assert.Equal("1/2 running", shop.Summary);
        Assert.True(shop.CanUp);
        Assert.Equal("not running", projects[0].Summary);
        Assert.Equal([@"C:\src\blog\compose.yaml"], projects[0].ConfigFiles);
    }

    [Fact]
    public void Compose_cli_arguments()
    {
        var withFiles = new ComposeProject("shop", @"C:\src\shop", [@"C:\src\shop\compose.yaml"], []);
        Assert.Equal(["--context", "linux", "compose", "-p", "shop", "-f", @"C:\src\shop\compose.yaml", "--project-directory", @"C:\src\shop", "up", "-d"],
            ComposeCli.Args("linux", withFiles, "up", "-d"));

        var byName = new ComposeProject("shop", null, [], []);
        Assert.Equal(["--context", "win", "compose", "-p", "shop", "down"], ComposeCli.Args("win", byName, "down"));
        Assert.False(byName.CanUp);
    }

    [Theory]
    [InlineData(@"C:\src\My Shop!\compose.yaml", "myshop")]
    [InlineData(@"C:\src\api_v2-x\docker-compose.yml", "api_v2-x")]
    public void Default_project_name_follows_compose(string file, string expected) =>
        Assert.Equal(expected, ComposeCli.DefaultProjectName(file));

    [Fact]
    public void Recents_are_newest_first_deduplicated_and_capped()
    {
        var s = new DashboardSettings();
        for (var i = 0; i < 12; i++) s.AddRecent($@"C:\p{i}\compose.yaml", $"p{i}");
        s.AddRecent(@"C:\P5\compose.yaml", "p5");

        Assert.Equal(10, s.RecentCompose.Count);
        Assert.Equal("p5", s.RecentCompose[0].Project);
        Assert.Single(s.RecentCompose, r => r.Project == "p5");
    }

    [Fact]
    public void Image_rows_expand_tags_and_count_users()
    {
        var images = new[]
        {
            new ImageSummary { Id = "sha256:aaaaaaaaaaaaaaaa", RepoTags = ["nginx:alpine", "web:latest"], Size = 100, Created = 0 },
            new ImageSummary { Id = "sha256:bbbbbbbbbbbbbbbb", RepoTags = null, Size = 5, Created = 0 },
        };
        var containers = new[] { Item("1", "web", imageId: "sha256:aaaaaaaaaaaaaaaa"), Item("2", "web2", imageId: "sha256:aaaaaaaaaaaaaaaa") };

        var rows = ImageUsage.Rows(images, containers);

        Assert.Equal(["nginx:alpine", "web:latest", "<none>"], rows.Select(r => r.Reference));
        Assert.Equal("2 containers", rows[0].UsageText);
        Assert.True(rows[2].IsDangling);
        Assert.Equal("aaaaaaaaaaaa", rows[0].ShortId);
    }

    [Fact]
    public void Volume_rows_map_users_and_sizes()
    {
        var anon = new string('a', 64);
        var volumes = new[] { new Volume { Name = "pgdata", Driver = "local" }, new Volume { Name = anon, Driver = "local" } };
        var containers = new[] { Item("1", "db", volumes: ["pgdata"]), Item("2", "db-backup", "exited", volumes: ["pgdata"]) };

        var rows = VolumeRows.Rows(volumes, containers, new Dictionary<string, long> { ["pgdata"] = 2048 });

        Assert.Equal(["db", "db-backup"], rows[0].UsedBy);
        Assert.False(rows[0].CanRemove);
        Assert.Equal("2 KB", rows[0].SizeText);
        Assert.True(rows[1].IsAnonymous);
        Assert.Equal("aaaaaaaaaaaa…", rows[1].DisplayName);
        Assert.Equal("", rows[1].SizeText);
    }

    [Fact]
    public void Network_drivers_and_builtins()
    {
        Assert.Equal(["nat", "transparent", "l2bridge"], NetworkDrivers.For(windows: true));
        Assert.Equal("bridge", NetworkDrivers.For(windows: false)[0]);
        Assert.True(NetworkDrivers.IsBuiltIn("host"));
        Assert.False(NetworkDrivers.IsBuiltIn("shop_default"));
    }

    [Fact]
    public void Disk_summary_reclaimable_per_category()
    {
        var df = new SystemDf
        {
            Images = [new ImageSummary { Id = "used", Size = 100 }, new ImageSummary { Id = "unused", Size = 40 }],
            Containers = [new DfContainer { SizeRw = 5, State = "running" }, new DfContainer { SizeRw = 7, State = "exited" }],
            Volumes = [new Volume { UsageData = new VolumeUsage { Size = 300, RefCount = 1 } }, new Volume { UsageData = new VolumeUsage { Size = 20, RefCount = 0 } },
                new Volume { UsageData = new VolumeUsage { Size = -1, RefCount = -1 } }],
            BuildCache = [new BuildCacheRecord { Size = 900, InUse = false }, new BuildCacheRecord { Size = 100, InUse = true }],
        };

        var summary = DiskSummary.From(df, [Item("1", "web", imageId: "used")]);

        Assert.Equal([(140L, 40L), (12L, 7L), (320L, 20L), (1000L, 900L)], summary.Select(c => (c.Total, c.Reclaimable)));
    }
}
