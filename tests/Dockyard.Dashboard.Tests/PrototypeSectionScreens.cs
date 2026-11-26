using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Dockyard.Dashboard.Features.Builds;
using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;
using Dockyard.Engine.Transport;

namespace Dockyard.Dashboard.Tests;

// The other sections in the real window, over an engine that answers the Docker API from fixtures and never opens a socket:
// a compose project, a kind cluster, images, volumes, networks, the build history and the disk. Each in light and dark,
// at the window's default and its minimum size.
public sealed partial class PrototypeScreens
{
    sealed class FixtureEngine : HttpMessageHandler
    {
        readonly Dictionary<string, string> answers = [];

        public FixtureEngine Answer(string path, string json)
        {
            answers[path] = json;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            if (path == "_ping")
            {
                var ping = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
                ping.Headers.Add("Api-Version", "1.51");
                ping.Headers.Add("Ostype", "linux");
                return Task.FromResult(ping);
            }
            path = path[(path.IndexOf('/') + 1)..];
            var key = answers.Keys.FirstOrDefault(k => path == k || path.StartsWith(k + "?", StringComparison.Ordinal));
            return Task.FromResult(key is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"not here\"}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answers[key], Encoding.UTF8, "application/json") });
        }
    }

    const string Sha = "sha256:";
    static string Id(char c) => new(c, 64);

    static ContainerSummary Box(string id, string name, string image, string state, Dictionary<string, string>? labels = null, params (int Private, int Public)[] ports) => new()
    {
        Id = id,
        Names = ["/" + name],
        Image = image,
        ImageID = Sha + id[..1] + new string('0', 63),
        State = state,
        Status = state == "running" ? "Up 3 hours" : "Exited (0) 2 hours ago",
        Labels = labels ?? [],
        Ports = ports.Select(p => new PortMapping { IP = "127.0.0.1", PrivatePort = p.Private, PublicPort = p.Public, Type = "tcp" }).ToList(),
    };

    static ContainerSummary Mount(ContainerSummary container, string volume, string at)
    {
        container.Mounts = [new() { Type = "volume", Name = volume, Source = $"/var/lib/docker/volumes/{volume}/_data", Destination = at, RW = true }];
        return container;
    }

    static Dictionary<string, string> Compose(string project, string service) =>
        new() { [ContainerItem.ProjectLabel] = project, [ContainerItem.ServiceLabel] = service, ["com.docker.compose.project.working_dir"] = $"/Users/me/src/{project}" };

    static Dictionary<string, string> Kind(string cluster, string role) => new() { [KindClusters.ClusterLabel] = cluster, [KindClusters.RoleLabel] = role };

    static readonly List<ContainerSummary> Containers =
    [
        Mount(Box(Id('1'), "contoso-db-1", "postgres:17-alpine", "running", Compose("contoso", "db"), (5432, 5433)), "contoso_pgdata", "/var/lib/postgresql/data"),
        Box(Id('2'), "contoso-web-1", "contoso/web:2.4", "running", Compose("contoso", "web"), (8080, 8080)),
        Box(Id('3'), "contoso-worker-1", "contoso/worker:2.4", "exited", Compose("contoso", "worker")),
        Box(Id('4'), "dev-control-plane", "kindest/node:v1.34.0", "running", Kind("dev", "control-plane"), (6443, 6443)),
        Box(Id('5'), "dev-worker", "kindest/node:v1.34.0", "running", Kind("dev", "worker")),
        Box(Id('6'), "elated_hopper", "alpine:3.22", "exited"),
        Box(Id('7'), "redis", "redis:7", "running", null, (6379, 6379)),
        Box(Id('8'), "northwind-reporting-service-with-a-very-long-container-name", "registry.example.com/northwind/reporting:2026.11.19-build.4711", "running", null, (3000, 3000)),
    ];

    static readonly List<ImageSummary> Images =
    [
        new() { Id = Sha + Id('a'), RepoTags = ["contoso/web:2.4"], Size = 116_000_000, Created = When.ToUnixTimeSeconds() },
        new() { Id = Sha + Id('b'), RepoTags = ["contoso/worker:2.4"], Size = 98_400_000, Created = When.AddDays(-1).ToUnixTimeSeconds() },
        new() { Id = Sha + Id('c'), RepoTags = ["postgres:17-alpine"], Size = 278_000_000, Created = When.AddDays(-12).ToUnixTimeSeconds() },
        new() { Id = Sha + Id('d'), RepoTags = ["kindest/node:v1.34.0"], Size = 1_020_000_000, Created = When.AddDays(-40).ToUnixTimeSeconds() },
        new() { Id = Sha + Id('e'), RepoTags = ["redis:7"], Size = 137_000_000, Created = When.AddDays(-20).ToUnixTimeSeconds() },
        new() { Id = Sha + Id('f'), RepoTags = ["<none>:<none>"], Size = 74_500_000, Created = When.AddDays(-9).ToUnixTimeSeconds() },
    ];

    static readonly List<Volume> Volumes =
    [
        new() { Name = "contoso_pgdata", Driver = "local", Mountpoint = "/var/lib/docker/volumes/contoso_pgdata/_data", CreatedAt = "2026-11-01T09:30:00Z", Labels = Compose("contoso", "db"), UsageData = new() { Size = 412_000_000, RefCount = 1 } },
        new() { Name = "fabrikam-uploads", Driver = "local", Mountpoint = "/var/lib/docker/volumes/fabrikam-uploads/_data", CreatedAt = "2026-10-20T14:00:00Z", UsageData = new() { Size = 1_800_000_000, RefCount = 0 } },
        new() { Name = Id('9')[..64], Driver = "local", Mountpoint = "/var/lib/docker/volumes/x/_data", CreatedAt = "2026-09-02T08:00:00Z", UsageData = new() { Size = 12_000, RefCount = 0 } },
    ];

    static readonly List<NetworkSummary> Networks =
    [
        new() { Id = Id('n'), Name = "bridge", Driver = "bridge", Scope = "local", IPAM = new() { Config = [new() { Subnet = "172.17.0.0/16", Gateway = "172.17.0.1" }] } },
        new() { Id = Id('m'), Name = "contoso_default", Driver = "bridge", Scope = "local", Labels = Compose("contoso", ""), IPAM = new() { Config = [new() { Subnet = "172.19.0.0/16", Gateway = "172.19.0.1" }] },
            Containers = new() { [Id('1')] = new() { Name = "contoso-db-1", IPv4Address = "172.19.0.2/16" }, [Id('2')] = new() { Name = "contoso-web-1", IPv4Address = "172.19.0.3/16" } } },
        new() { Id = Id('k'), Name = "kind", Driver = "bridge", Scope = "local", IPAM = new() { Config = [new() { Subnet = "172.18.0.0/16", Gateway = "172.18.0.1" }] },
            Containers = new() { [Id('4')] = new() { Name = "dev-control-plane", IPv4Address = "172.18.0.2/16" }, [Id('5')] = new() { Name = "dev-worker", IPv4Address = "172.18.0.3/16" } } },
        new() { Id = Id('h'), Name = "host", Driver = "host", Scope = "local" },
        new() { Id = Id('o'), Name = "none", Driver = "null", Scope = "local" },
    ];

    static FixtureEngine Engine()
    {
        var df = new SystemDf
        {
            Images = Images,
            Containers = Containers.Select(c => new DfContainer { Id = c.Id, Names = c.Names, Image = c.Image, State = c.State, SizeRw = 48_000_000 }).ToList(),
            Volumes = Volumes,
            BuildCache = [new() { ID = "a", Size = 2_300_000_000, InUse = false }, new() { ID = "b", Size = 310_000_000, InUse = true }],
        };
        var engine = new FixtureEngine()
            .Answer("containers/json", JsonSerializer.Serialize(Containers, ContainerJson.Default.ListContainerSummary))
            .Answer("images/json", JsonSerializer.Serialize(Images, ImageJson.Default.ListImageSummary))
            .Answer("volumes", JsonSerializer.Serialize(new VolumeList { Volumes = Volumes }, VolumeJson.Default.VolumeList))
            .Answer("networks", JsonSerializer.Serialize(Networks, NetworkJson.Default.ListNetworkSummary))
            .Answer("system/df", JsonSerializer.Serialize(df, SystemJson.Default.SystemDf));
        foreach (var n in Networks) engine.Answer("networks/" + n.Id, JsonSerializer.Serialize(n, NetworkJson.Default.NetworkSummary));
        return engine;
    }

    static readonly List<BuildRecord> Builds =
    [
        new("q1w2e3", "contoso/web:2.4", When.AddMinutes(-12), When.AddMinutes(-11), "Completed", 14, 14, 9),
        new("r4t5y6", "contoso/worker:2.4", When.AddMinutes(-40), When.AddMinutes(-38), "Completed", 11, 11, 2),
        new("u7i8o9", "fabrikam/api:dev", When.AddHours(-3), When.AddHours(-3).AddSeconds(52), "Error", 9, 6, 4),
        new("p0a1s2", "northwind/db-tools:latest", When.AddDays(-1), When.AddDays(-1).AddMinutes(4), "Completed", 7, 7, 0),
    ];

    public static TheoryData<string> SectionScreens => new()
    {
        "containers", "containers-cluster", "containers-no-kind", "images", "volumes", "networks", "builds", "disk",
    };

    [Theory]
    [MemberData(nameof(SectionScreens))]
    public async Task Section_screen_renders(string screen)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PrototypeScreens).Assembly);
        await session.Dispatch(() =>
        {
            var app = Application.Current!;
            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                    foreach (var size in Sizes)
                    {
                        app.RequestedThemeVariant = theme;
                        RenderSection(screen, $"{screen}-{theme.Key.ToString()!.ToLowerInvariant()}-{size.Name}", size.Width, size.Height);
                    }
            }
            finally { app.RequestedThemeVariant = ThemeVariant.Default; }
        }, TestContext.Current.CancellationToken);
    }

    // Runs the dispatcher until the task is done: the engine's answers come back as dispatcher jobs.
    static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted, "A section did not finish loading from the fixtures");
    }

    static void RenderSection(string screen, string file, double width, double height)
    {
        var shell = new ShellViewModel(new FakeSetup(installed: true));
        var engine = shell.Session;
        Set(engine, "client", new DockerClient(DockerEndpoint.Parse("unix:///nonexistent.sock"), Engine()));
        // The field, not the property: Builds answers a context change by asking this machine's docker for its history.
        Set(engine, "contextName", "dockyard");
        Set(engine, "State", SessionState.Connected);
        engine.Containers.Reset(Containers);
        var usage = new Dictionary<string, (double, ulong)> { [Id('1')] = (6.8, 90_400_000), [Id('2')] = (1.5, 93_400_000), [Id('4')] = (3.2, 105_200_000), [Id('5')] = (2.2, 84_500_000), [Id('7')] = (5.8, 69_700_000), [Id('8')] = (1.5, 229_400_000) };
        foreach (var (id, (cpu, memory)) in usage) engine.Containers.Get(id)!.ApplyUsage(new ContainerUsage(cpu, memory, 8_000_000_000));

        // What this machine has installed must not show: every docker tool is there, and kind is unless the screen says not.
        Wait(shell.Tools.Refresh());
        Set(shell.Tools, "hasDocker", true);
        Set(shell.Tools, "hasCompose", true);
        Set(shell.Tools, "hasBuildx", true);
        Raise(shell.Tools, nameof(ToolCheck.Hint), nameof(ToolCheck.HasHint), nameof(ToolCheck.HasCompose), nameof(ToolCheck.HasBuildx));

        var kind = screen.Split('-')[0];
        // Without OnShown, which for Builds would ask this machine's docker for its build history.
        Set(shell, "currentSection", shell.Sections.First(s => s.Title.Equals(kind, StringComparison.OrdinalIgnoreCase)));
        Raise(shell, nameof(ShellViewModel.CurrentSection));
        foreach (var section in shell.Sections.Select(s => s.Content).OfType<SectionViewModel>().Where(s => s is not BuildsViewModel))
            Wait(section.Reload());
        Wait(shell.Clusters.Reload());
        Set(shell.Clusters, "kind", screen == "containers-no-kind" ? null : "/opt/homebrew/bin/kind");
        Raise(shell.Clusters, nameof(ClustersViewModel.HasKind), nameof(ClustersViewModel.MissingKind), nameof(ClustersViewModel.Hint), nameof(ClustersViewModel.HasHint), nameof(ClustersViewModel.HintLink));
        Call(shell.Clusters, "RefreshCommands");

        switch (kind)
        {
            case "containers" when screen == "containers-cluster":
                shell.Containers.SelectedRow = shell.Containers.Store.Rows.OfType<ClusterRow>().First();
                break;
            case "images":
                var images = (ImagesViewModel)shell.CurrentSection.Content;
                images.Selected = images.Rows.First(r => r.Reference == "contoso/web:2.4");
                break;
            case "volumes":
                var volumes = (VolumesViewModel)shell.CurrentSection.Content;
                volumes.Selected = volumes.Rows.First(r => r.Name == "contoso_pgdata");
                break;
            case "networks":
                var networks = (NetworksViewModel)shell.CurrentSection.Content;
                networks.Selected = networks.Rows.First(r => r.Name == "contoso_default");
                break;
            case "builds":
                var builds = (BuildsViewModel)shell.CurrentSection.Content;
                Set(builds, "all", Builds);
                Call(builds, "ApplyFilter");
                break;
        }

        var window = new MainWindow(WindowChrome.For(HostOs.MacOS)) { DataContext = shell, Width = width, Height = height };
        window.Show();
        for (var i = 0; i < 4; i++)
        {
            Thread.Sleep(40);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        if (Environment.GetEnvironmentVariable("DOCKYARD_SCREENSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            window.CaptureRenderedFrame()?.Save(Path.Combine(folder, file + ".png"));
        }
        window.Close();
        shell.Containers.Shutdown();
    }

    static void Raise(object target, params string[] names) =>
        typeof(Observable).GetMethod("Raise", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [names]);
}
