using System.Diagnostics;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Tests;

sealed class FakeRunner : ICommandRunner
{
    readonly Queue<ShellResult> results = new();

    public List<string> Commands { get; } = [];

    public FakeRunner Returns(string output, int exitCode = 0)
    {
        results.Enqueue(new ShellResult(exitCode, output, ""));
        return this;
    }

    public Task<ShellResult> Run(string file, params string[] args)
    {
        Commands.Add(Describe(file, args));
        return Task.FromResult(results.Count > 0 ? results.Dequeue() : new ShellResult(0, "", ""));
    }

    public Process Spawn(string file, params string[] args)
    {
        Commands.Add(Describe(file, args));
        throw new NotSupportedException("Spawn is not faked");
    }

    static string Describe(string file, string[] args) => string.Join(' ', [file, .. args]);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsHostTests
{
    [Fact]
    public void Keepalive_runs_dockyard_init_as_root()
    {
        var distro = new WslDistro(new FakeRunner());
        Assert.Equal(["-d", "dockyard", "-u", "root", "--", "/usr/local/sbin/dockyard-init"], distro.KeepAliveArgs());
        Assert.Equal(["-d", "dockyard", "-u", "root", "--", "env", "DOCKYARD_TCP=127.0.0.1:23750", "/usr/local/sbin/dockyard-init"],
            distro.KeepAliveArgs("127.0.0.1:23750"));
    }

    [Fact]
    public async Task Exists_reads_wsl_list_with_utf16_noise()
    {
        var runner = new FakeRunner().Returns("Ubuntu-24.04\0\r\n\0dockyard\0\r\n");
        Assert.True(await new WslDistro(runner).Exists());
        Assert.Equal("wsl.exe -l -q", runner.Commands[0]);
    }

    [Fact]
    public async Task Import_uses_wsl2_and_the_install_directory()
    {
        var runner = new FakeRunner();
        var dir = Path.Combine(Path.GetTempPath(), $"dy-{Guid.NewGuid():N}");
        await new WslDistro(runner).Import(@"C:\Downloads\dockyard-linux-1.0.0-x86_64.tar.gz", dir);
        Assert.Equal($@"wsl.exe --import dockyard {dir} C:\Downloads\dockyard-linux-1.0.0-x86_64.tar.gz --version 2", runner.Commands[0]);
        Directory.Delete(dir);
    }

    [Fact]
    public async Task Docker_version_comes_from_dockyard_release()
    {
        var runner = new FakeRunner().Returns("VERSION=1.2.0\nDOCKER_VERSION=29.9.2\n");
        Assert.Equal(new Version(29, 9, 2), await new WslDistro(runner).DockerVersion());
        Assert.Equal("wsl.exe -d dockyard -u root -- cat /etc/dockyard-release", runner.Commands[0]);
    }

    [Fact]
    public async Task Docker_version_is_null_when_distro_is_missing()
    {
        var runner = new FakeRunner().Returns("", exitCode: 1);
        Assert.Null(await new WslDistro(runner).DockerVersion());
    }

    [Fact]
    public async Task Stop_sends_sigterm_and_waits_for_dockerd()
    {
        var runner = new FakeRunner();
        await new WslDistro(runner).StopEngine();
        Assert.StartsWith("wsl.exe -d dockyard -u root -- sh -c pid=$(pidof dockerd) || exit 0; kill -TERM $pid;", runner.Commands[0]);
    }

    [Fact]
    public async Task Update_translates_windows_paths_and_runs_dockyard_update()
    {
        var runner = new FakeRunner()
            .Returns("/mnt/c/Temp/x/docker.tgz\n")
            .Returns("/mnt/c/Temp/x/plugins\n")
            .Returns("Docker 29.9.3\n");

        var output = await new WslDistro(runner).Update(@"C:\Temp\x\docker.tgz", @"C:\Temp\x\plugins");

        Assert.Equal("Docker 29.9.3", output);
        Assert.Equal("wsl.exe -d dockyard -u root -- wslpath -a C:/Temp/x/docker.tgz", runner.Commands[0]);
        Assert.Equal("wsl.exe -d dockyard -u root -- /usr/local/sbin/dockyard-update /mnt/c/Temp/x/docker.tgz /mnt/c/Temp/x/plugins", runner.Commands[2]);
    }

    [Fact]
    public async Task Legacy_engine_in_users_distro_is_stopped_and_disabled()
    {
        var runner = new FakeRunner().Returns("Ubuntu-24.04\n");
        Assert.True(await WslDistro.DisableLegacyEngine(runner, "Ubuntu-24.04"));
        Assert.Contains("systemctl stop docker.service docker.socket containerd.service", runner.Commands[1]);
        Assert.Contains("systemctl disable docker.socket", runner.Commands[1]);
    }

    [Fact]
    public async Task Legacy_migration_skips_missing_distro()
    {
        var runner = new FakeRunner().Returns("dockyard\n");
        Assert.False(await WslDistro.DisableLegacyEngine(runner, "Ubuntu-24.04"));
        Assert.Single(runner.Commands);
    }

    [Fact]
    public void Static_index_picks_newest_release_older_than_14_days()
    {
        const string html = """
            <a href="docker-29.9.1.tgz">docker-29.9.1.tgz</a>        2026-10-14 18:57
            <a href="docker-29.9.2.tgz">docker-29.9.2.tgz</a>        2026-10-20 20:07
            <a href="docker-29.9.3.tgz">docker-29.9.3.tgz</a>        2026-11-03 22:08
            <a href="docker-29.9.3.zip">docker-29.9.3.zip</a>        2026-10-01 10:00
            """;
        var release = Updates.PickStatic(html, Updates.LinuxStaticIndex, "tgz", new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new Version(29, 9, 2), release.Version);
        Assert.Equal("https://download.docker.com/linux/static/stable/x86_64/docker-29.9.2.tgz", release.DownloadUrl);
    }
}
