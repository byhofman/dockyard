using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Dockyard.Dashboard.Features.Files;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;
using Dockyard.Engine.Transport;

namespace Dockyard.Dashboard.Tests;

// The screens of the image and volume files, drawn by the real views over fictive data: the prototype for a dashboard
// that has no prototype mode. Every run checks they render; with DOCKYARD_SCREENSHOTS set they are also saved there.
public sealed class PrototypeScreens
{
    static readonly DateTimeOffset When = new(2026, 11, 3, 9, 30, 0, TimeSpan.Zero);

    static FileEntry Dir(string name, long size) => new(name, true, size, null, When, default, 0, 0);
    static FileEntry File(string name, long size, string? link = null) => new(name, false, size, link, When, default, 0, 0);

    sealed class FakeSource(string root, bool canWrite, Dictionary<string, FileEntry[]> folders, Dictionary<string, string> texts) : IFileSource
    {
        public string Root => root;
        public bool CanWrite => canWrite;
        public Task<IReadOnlyList<FileEntry>> ListAsync(string directory, IProgress<string> status, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FileEntry>>(folders[directory]);
        public Task<FileContent> ReadAsync(string path, int maxBytes, CancellationToken ct) =>
            Task.FromResult(new FileContent(System.Text.Encoding.UTF8.GetBytes(texts[path]), false, File(path[(path.LastIndexOf('/') + 1)..], texts[path].Length)));
        public Task SaveToAsync(string path, Stream destination, CancellationToken ct) => Task.CompletedTask;
        public Task WriteAsync(string directory, string name, Stream content, FileEntry? like, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static FakeSource ImageFiles() => new("/", false, new()
    {
        ["/"] = [Dir("app", 48_200_000), Dir("bin", 1_100_000), Dir("etc", 412_000), Dir("usr", 61_000_000), Dir("var", 2_300_000)],
        ["/app"] = [Dir("wwwroot", 3_900_000), File("Contoso.Web.dll", 1_240_000), File("Contoso.Web.deps.json", 18_400), File("appsettings.json", 642)],
    }, new()
    {
        ["/app/appsettings.json"] = "{\n  \"Logging\": { \"LogLevel\": { \"Default\": \"Information\" } },\n  \"AllowedHosts\": \"*\",\n  \"Shop\": { \"Name\": \"Contoso Coffee\", \"Currency\": \"EUR\" }\n}\n",
    });

    static FakeSource VolumeFiles() => new("/volume", false, new()
    {
        ["/volume"] = [Dir("pgdata", 0), File("PG_VERSION", 3), File("northwind-export.sql", 2_400_000)],
    }, new() { ["/volume/PG_VERSION"] = "17\n" });

    static void Set(object target, string member, object? value)
    {
        var type = target.GetType();
        if (type.GetField(member, BindingFlags.Instance | BindingFlags.NonPublic) is { } field) field.SetValue(target, value);
        else type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    static void Call(object target, string method) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, []);

    static EngineSession Session(string context, string endpoint)
    {
        var session = new EngineSession();
        Set(session, "client", new DockerClient(DockerEndpoint.Parse(endpoint)));
        Set(session, "ContextName", context);
        return session;
    }

    // Any container starting or stopping rebuilds the rows; that must not close what is being looked at.
    [Fact]
    public async Task Files_stay_open_when_the_rows_are_rebuilt_and_close_for_another_item()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PrototypeScreens).Assembly);
        await session.Dispatch(() =>
        {
            var vm = new VolumesViewModel(new ShellViewModel(), Session("dockyard", "unix:///nonexistent.sock"));
            Set(vm, "volumes", new List<Volume> { new() { Name = "contoso-db", Driver = "local" }, new() { Name = "fabrikam", Driver = "local" } });
            Call(vm, "Recompute");
            vm.Selected = vm.Rows[0];
            Set(vm, "Files", new FileBrowserViewModel(VolumeFiles(), _ => { }));
            vm.SelectedTab = VolumesViewModel.FilesTab;
            Call(vm, "Recompute");
            Assert.True(vm.HasFiles);
            vm.Selected = vm.Rows[1];
            Assert.False(vm.HasFiles);
            Assert.Equal(0, vm.SelectedTab);

            var images = new ImagesViewModel(new ShellViewModel(), Session("dockyard", "unix:///nonexistent.sock"));
            Set(images, "images", new List<ImageSummary> { new() { Id = "sha256:" + new string('a', 64), RepoTags = ["contoso/web:2.4"] }, new() { Id = "sha256:" + new string('b', 64), RepoTags = ["fabrikam/worker:1.0"] } });
            Call(images, "Recompute");
            images.Selected = images.Rows[0];
            Set(images, "Files", new FileBrowserViewModel(ImageFiles(), _ => { }));
            Call(images, "Recompute");
            Assert.True(images.HasFiles);
            images.Selected = images.Rows[1];
            Assert.False(images.HasFiles);
        }, TestContext.Current.CancellationToken);
    }

    public static TheoryData<string> Screens => new() { "image-details", "image-files", "image-file-open", "volume-mac", "volume-windows", "volume-linux", "volume-files" };

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Screen_renders(string name)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PrototypeScreens).Assembly);
        await session.Dispatch(() =>
        {
            var shell = new ShellViewModel();
            Control view;
            if (name.StartsWith("image", StringComparison.Ordinal))
            {
                var vm = new ImagesViewModel(shell, Session("dockyard", "unix:///nonexistent.sock"));
                Set(vm, "images", new List<ImageSummary>
                {
                    new() { Id = "sha256:3f1c9a7be2d4" + new string('0', 52), RepoTags = ["contoso/web:2.4"], Size = 116_000_000, Created = When.ToUnixTimeSeconds() },
                    new() { Id = "sha256:8b20e4c1aa90" + new string('0', 52), RepoTags = ["fabrikam/worker:1.0"], Size = 74_500_000, Created = When.AddDays(-9).ToUnixTimeSeconds() },
                    new() { Id = "sha256:c4d5e6f7a8b9" + new string('0', 52), RepoTags = ["northwind/db-tools:latest"], Size = 9_800_000, Created = When.AddDays(-30).ToUnixTimeSeconds() },
                });
                Call(vm, "Recompute");
                vm.Selected = vm.Rows[0];
                if (name != "image-details")
                {
                    var files = new FileBrowserViewModel(ImageFiles(), _ => { });
                    Set(vm, "Files", files);
                    files.EnsureLoaded();
                    Dispatcher.UIThread.RunJobs();
                    Dispatcher.UIThread.RunJobs();
                    var app = files.Rows.First(r => r.Name == "app");
                    files.Selected = app;
                    Dispatcher.UIThread.RunJobs();
                    if (name == "image-file-open") files.Selected = files.Rows.First(r => r.Name == "appsettings.json");
                    Dispatcher.UIThread.RunJobs();
                }
                view = new ImagesView { DataContext = vm };
            }
            else
            {
                var (host, context, endpoint) = name switch
                {
                    "volume-windows" => (HostOs.Windows, "linux", "npipe:////./pipe/dockyard_linux"),
                    "volume-linux" => (HostOs.Linux, "default", "unix:///var/run/docker.sock"),
                    _ => (HostOs.MacOS, "dockyard", "unix:///nonexistent.sock"),
                };
                var vm = new VolumesViewModel(shell, Session(context, endpoint)) { Host = host };
                Set(vm, "volumes", new List<Volume>
                {
                    new() { Name = "contoso-db", Driver = "local", Mountpoint = "/var/lib/docker/volumes/contoso-db/_data" },
                    new() { Name = "fabrikam-uploads", Driver = "local", Mountpoint = "/var/lib/docker/volumes/fabrikam-uploads/_data" },
                });
                Call(vm, "Recompute");
                vm.Selected = vm.Rows[0];
                if (name == "volume-files")
                {
                    var files = new FileBrowserViewModel(VolumeFiles(), _ => { }, () => Task.CompletedTask);
                    Set(vm, "Files", files);
                    vm.SelectedTab = VolumesViewModel.FilesTab;
                    files.EnsureLoaded();
                    Dispatcher.UIThread.RunJobs();
                }
                view = new VolumesView { DataContext = vm };
            }

            var window = new Window { Width = 1100, Height = 640, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            if (Environment.GetEnvironmentVariable("DOCKYARD_SCREENSHOTS") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                window.CaptureRenderedFrame()?.Save(Path.Combine(folder, name + ".png"));
            }
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}
