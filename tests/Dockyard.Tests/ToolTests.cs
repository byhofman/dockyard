using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Tests;

public sealed class ToolTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dockyard-tool-tests").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    sealed class FakePackages(bool available = true, Version? installed = null, Version? newest = null, bool fails = false) : IPackageManager
    {
        public Version? InstalledVersion = installed;
        public List<string> Calls { get; } = [];
        public string Name => "fake";
        public Task<bool> IsAvailable() => Task.FromResult(available);
        public Task<Version?> Installed(CliTool tool) => Task.FromResult(InstalledVersion);
        public Task<Version?> Available(CliTool tool) => Task.FromResult(newest);

        public Task Install(Action<string> log, CliTool tool)
        {
            Calls.Add("install " + tool.Command);
            if (fails) throw new InvalidOperationException("blocked by policy");
            InstalledVersion = newest ?? new Version(1, 0, 0);
            return Task.CompletedTask;
        }

        public Task Remove(Action<string> log, CliTool tool)
        {
            Calls.Add("remove " + tool.Command);
            InstalledVersion = null;
            return Task.CompletedTask;
        }
    }

    sealed class FakeOwn(Version? installed = null) : IOwnCopies
    {
        public Version? InstalledVersion = installed;
        public static readonly Release Pin = new(new Version(0, 33, 0), "v0.33.0", "https://example.com/kind");
        public List<string> Calls { get; } = [];
        public Task<Version?> Installed(CliTool tool) => Task.FromResult(InstalledVersion);
        public Release? Pinned(CliTool tool) => Pin;

        public Task Install(Action<string> log, CliTool tool)
        {
            Calls.Add("install " + tool.Command);
            InstalledVersion = Pin.Version;
            return Task.CompletedTask;
        }

        public Task Remove(Action<string> log, CliTool tool)
        {
            Calls.Add("remove " + tool.Command);
            InstalledVersion = null;
            return Task.CompletedTask;
        }
    }

    static readonly List<string> Log = [];

    [Fact]
    public async Task The_package_manager_brings_a_tool_and_takes_the_place_of_Dockyard_s_own_copy()
    {
        var packages = new FakePackages(newest: new Version(0, 34, 0));
        var own = new FakeOwn(installed: new Version(0, 33, 0));

        Assert.True(await new ToolComponents(packages, own).Ensure(Log.Add, Tools.Kind));

        Assert.Equal(["install kind"], packages.Calls);
        Assert.Equal(["remove kind"], own.Calls);
    }

    [Fact]
    public async Task A_rerun_leaves_a_package_alone_when_there_is_nothing_newer()
    {
        var packages = new FakePackages(installed: new Version(1, 37, 2), newest: new Version(1, 37, 2));

        Assert.True(await new ToolComponents(packages, new FakeOwn()).Ensure(Log.Add, Tools.Kubectl));

        Assert.Empty(packages.Calls);
    }

    [Fact]
    public async Task A_package_manager_that_fails_leaves_the_tool_to_Dockyard_s_own_copy()
    {
        var own = new FakeOwn();
        List<string> log = [];

        Assert.False(await new ToolComponents(new FakePackages(fails: true), own).Install(log.Add, Tools.Helm));

        Assert.Equal(["install helm"], own.Calls);
        Assert.Contains(log, l => l.Contains("blocked by policy") && l.Contains("own copy"));
    }

    [Fact]
    public async Task An_upgrade_that_fails_keeps_the_package_and_brings_back_no_copy_of_Dockyard_s()
    {
        var own = new FakeOwn();
        List<string> log = [];

        Assert.True(await new ToolComponents(new FakePackages(installed: new Version(0, 33, 0), newest: new Version(0, 34, 0), fails: true), own).Ensure(log.Add, Tools.Kind));

        Assert.Empty(own.Calls);
        Assert.Contains(log, l => l.Contains("installed version stays"));
    }

    [Fact]
    public async Task Without_a_package_manager_Dockyard_s_copy_only_changes_with_its_pin()
    {
        var own = new FakeOwn(installed: FakeOwn.Pin.Version);
        var tools = new ToolComponents(new FakePackages(available: false), own);

        await tools.Ensure(Log.Add, Tools.Kind);
        Assert.Empty(own.Calls);

        own.InstalledVersion = new Version(0, 31, 0);
        await tools.Ensure(Log.Add, Tools.Kind);
        Assert.Equal(["install kind"], own.Calls);
    }

    [Fact]
    public async Task Choosing_not_to_use_the_package_manager_keeps_Dockyard_s_copies_and_leaves_the_packages_alone()
    {
        var packages = new FakePackages(installed: new Version(1, 37, 2), newest: new Version(1, 38, 0));
        var own = new FakeOwn();
        var tools = new ToolComponents(packages, own, usePackages: () => false);

        Assert.Null(tools.Packages);
        Assert.False(await tools.IsManaged(Tools.Kubectl));
        Assert.False(await tools.Install(Log.Add, Tools.Kubectl));
        await tools.Remove(Log.Add, Tools.Kubectl);

        Assert.Empty(packages.Calls);
        Assert.Equal(["install kubectl", "remove kubectl"], own.Calls);
        Assert.Equal(FakeOwn.Pin, await tools.Latest(Tools.Kubectl));
    }

    [Fact]
    public async Task A_tool_left_out_loses_only_Dockyard_s_own_copy_since_the_package_may_be_the_user_s()
    {
        var packages = new FakePackages(installed: new Version(1, 37, 2));
        var own = new FakeOwn(installed: FakeOwn.Pin.Version);

        await new ToolComponents(packages, own).RemoveOwn(Log.Add, Tools.Kubectl);

        Assert.Empty(packages.Calls);
        Assert.Equal(["remove kubectl"], own.Calls);
    }

    [Fact]
    public async Task Removing_a_tool_takes_away_whichever_copies_there_are()
    {
        var packages = new FakePackages(installed: new Version(1, 37, 2));
        var own = new FakeOwn(installed: FakeOwn.Pin.Version);

        await new ToolComponents(packages, own).Remove(Log.Add, Tools.Kubectl);

        Assert.Equal(["remove kubectl"], packages.Calls);
        Assert.Equal(["remove kubectl"], own.Calls);
    }

    [Fact]
    public async Task A_tool_says_whether_the_package_manager_or_Dockyard_put_it_there()
    {
        Assert.Equal("fake", await new ToolComponents(new FakePackages(installed: new Version(1, 0, 0)), new FakeOwn()).Source(Tools.Kind));
        Assert.Equal("Dockyard", await new ToolComponents(new FakePackages(), new FakeOwn(installed: FakeOwn.Pin.Version)).Source(Tools.Kind));
        Assert.Equal("Dockyard", await new ToolComponents(new FakePackages(installed: new Version(1, 0, 0)), new FakeOwn(installed: FakeOwn.Pin.Version), () => false).Source(Tools.Kind));
        Assert.Null(await new ToolComponents(new FakePackages(), new FakeOwn()).Source(Tools.Kind));
    }

    [Fact]
    public async Task A_managed_tool_s_latest_is_what_the_package_manager_offers()
    {
        var tools = new ToolComponents(new FakePackages(installed: new Version(4, 3, 2), newest: new Version(4, 4, 0)), new FakeOwn());

        Assert.Equal(new Version(4, 4, 0), (await tools.Latest(Tools.Helm))!.Version);
        Assert.Equal(new Version(4, 3, 2), await tools.Installed(Tools.Helm));
    }

    [Theory]
    [InlineData("Name           Id                 Version Source\n------------------------------------------------\nKubernetes CLI Kubernetes.kubectl 1.37.2  winget\n")]
    [InlineData("Naam           Id                 Versie  Bron\n------------------------------------------------\nKubernetes CLI Kubernetes.kubectl 1.37.2  winget\n")]
    [InlineData("   - \r   \\ \rKubernetes CLI Kubernetes.kubectl 1.37.2 winget")]
    public void Winget_s_version_is_read_from_the_package_row_in_any_language(string output) =>
        Assert.Equal(new Version(1, 37, 2), Winget.VersionAfterId(output, "Kubernetes.kubectl"));

    [Fact]
    public void Winget_s_command_is_its_link_or_else_the_file_in_its_package_folder()
    {
        var links = Directory.CreateDirectory(Path.Combine(dir, "Links")).FullName;
        var packages = Directory.CreateDirectory(Path.Combine(dir, "Packages")).FullName;
        var package = Directory.CreateDirectory(Path.Combine(packages, "Helm.Helm_Microsoft.Winget.Source_8wekyb3d8bbwe", "windows-amd64")).FullName;
        Directory.CreateDirectory(Path.Combine(packages, "Other.Helm_Microsoft.Winget.Source_8wekyb3d8bbwe"));
        File.WriteAllText(Path.Combine(packages, "Other.Helm_Microsoft.Winget.Source_8wekyb3d8bbwe", "helm.exe"), "");

        Assert.Null(Winget.Command(Tools.Helm, [links], [packages]));

        File.WriteAllText(Path.Combine(package, "helm.exe"), "");
        Assert.Equal(Path.Combine(package, "helm.exe"), Winget.Command(Tools.Helm, [links], [packages]));

        File.WriteAllText(Path.Combine(links, "helm.exe"), "");
        Assert.Equal(Path.Combine(links, "helm.exe"), Winget.Command(Tools.Helm, [links], [packages]));
    }

    [Fact]
    public void Winget_without_the_package_has_no_version() =>
        Assert.Null(Winget.VersionAfterId("No package found matching input criteria.", "Kubernetes.kubectl"));

    [Fact]
    public void Homebrew_s_version_is_its_stable_one()
    {
        var json = """{"formulae":[{"name":"kind","versions":{"stable":"0.33.0","head":"HEAD","bottle":true}}],"casks":[]}""";
        Assert.Equal(new Version(0, 33, 0), Homebrew.StableVersion(json));
        Assert.Null(Homebrew.StableVersion("not json"));
    }

    [Fact]
    public void Apt_s_version_is_its_candidate()
    {
        var policy = "kubectl:\n  Installed: 1.37.1-1.1\n  Candidate: 1.37.2-1.1\n  Version table:\n";
#pragma warning disable CA1416 // A parser, plain text on any system.
        Assert.Equal(new Version(1, 37, 2), LinuxPackages.CandidateVersion(policy));
        Assert.Null(LinuxPackages.CandidateVersion("N: Unable to locate package kubectl"));
#pragma warning restore CA1416
    }

    [Fact]
    public void One_file_comes_out_of_a_zip()
    {
        var archive = Path.Combine(dir, "helm.archive");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("windows-amd64/helm.exe").Open())) w.Write("helm");
            using (var w = new StreamWriter(zip.CreateEntry("windows-amd64/LICENSE").Open())) w.Write("license");
        }
        var target = Path.Combine(dir, "helm.exe");

        Archives.Extract(archive, "windows-amd64/helm.exe", target);

        Assert.Equal("helm", File.ReadAllText(target));
        Assert.Equal(["helm.archive", "helm.exe"], Directory.GetFiles(dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void One_file_comes_out_of_a_tar_gz()
    {
        var archive = Path.Combine(dir, "helm.archive");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip))
        {
            foreach (var (name, body) in new[] { ("./linux-amd64/README.md", "readme"), ("./linux-amd64/helm", "helm") })
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(body)) });
        }
        var target = Path.Combine(dir, "helm");

        Archives.Extract(archive, "linux-amd64/helm", target);

        Assert.Equal("helm", File.ReadAllText(target));
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void An_archive_without_the_file_fails_and_leaves_nothing()
    {
        var archive = Path.Combine(dir, "helm.archive");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) zip.CreateEntry("other/helm.exe");

        Assert.Throws<InvalidOperationException>(() => Archives.Extract(archive, "windows-amd64/helm.exe", Path.Combine(dir, "helm.exe")));
        Assert.Equal(["helm.archive"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Docker_finds_a_package_manager_s_plugins_through_its_config_and_keeps_the_rest()
    {
        var updated = DockerPlugins.WithExtraDir("""{"credsStore":"wincred","cliPluginsExtraDirs":["C:\\other"]}""", @"C:\Users\me\AppData\Local\Microsoft\WinGet\Links");
        var config = JsonNode.Parse(updated!)!;

        Assert.Equal("wincred", config["credsStore"]!.GetValue<string>());
        Assert.Equal([@"C:\other", @"C:\Users\me\AppData\Local\Microsoft\WinGet\Links"], config["cliPluginsExtraDirs"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Null(DockerPlugins.WithExtraDir(updated, @"c:\users\me\appdata\local\microsoft\winget\links"));
        Assert.Null(DockerPlugins.WithExtraDir("{ not json", "/opt/homebrew/lib/docker/cli-plugins"));
        Assert.NotNull(DockerPlugins.WithExtraDir(null, "/opt/homebrew/lib/docker/cli-plugins"));
    }

    [Fact]
    public void The_choice_of_package_manager_is_a_flag_that_is_remembered()
    {
        Assert.Null(SetupOptions.Parse(["--silent"]).PackageManager);
        Assert.False(SetupOptions.Parse(["--silent", "--no-package-manager"]).PackageManager);
        Assert.True(SetupOptions.Parse(["--silent", "--package-manager"]).PackageManager);
        Assert.Throws<SetupUsageException>(() => SetupOptions.Parse(["--package-manager", "--no-package-manager"]));
        Assert.True(new Settings().UsePackageManager);
    }

    [Fact]
    public void Every_tool_has_a_winget_id_and_a_formula()
    {
        Assert.Equal(["Docker.DockerCLI", "Docker.DockerCompose", "Docker.Buildx", "Kubernetes.kind", "Kubernetes.kubectl", "Helm.Helm"], Tools.All.Select(t => t.Winget));
        Assert.Equal(["docker", "docker-compose", "docker-buildx", "kind", "kubernetes-cli", "helm"], Tools.All.Select(t => t.Brew));
        Assert.All(Tools.All, t => Assert.True(Enum.IsDefined(typeof(Component), t.Id)));
    }
}
