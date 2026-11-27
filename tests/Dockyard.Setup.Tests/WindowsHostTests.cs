using System.Diagnostics;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

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
    [Theory]
    [InlineData("default via 172.20.0.1 dev eth0 proto kernel", "172.20.0.1")]
    [InlineData("", null)]
    public void The_way_to_windows_with_nat_is_the_gateway(string route, string? expected) =>
        Assert.Equal(expected, WslDistro.DefaultGateway(route));

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
    public void Without_the_tray_no_logon_entry_is_written()
    {
        Assert.Equal([Paths.AppName, "Dockyard Host"], UserSetup.LogonEntries(tray: true, host: true).Select(e => e.Name));
        Assert.Equal($"\"{Paths.AppExe}\"", UserSetup.LogonEntries(tray: true, host: false).Single().Command);
        Assert.Equal("dockyard-tray.exe", Path.GetFileName(Paths.AppExe));
        Assert.Equal(["Dockyard Host"], UserSetup.LogonEntries(tray: false, host: true).Select(e => e.Name));
        Assert.Empty(UserSetup.LogonEntries(tray: false, host: false));
    }

    [Theory]
    [InlineData("Cli", true, true, "Cli")]
    [InlineData("Cli", true, false, "Cli")]
    [InlineData("Cli", false, true, "Dashboard")]
    [InlineData("Dashboard", true, true, "Dashboard")]
    [InlineData("Dashboard", false, true, "Dashboard")]
    [InlineData("Dashboard", true, false, "Cli")]
    [InlineData("Tray", true, true, "Cli")]
    [InlineData("Tray", false, true, "Dashboard")]
    public void A_restart_resumes_with_the_app_that_ran_setup_when_it_stays(string running, bool cli, bool dashboard, string resumes) =>
        Assert.Equal(Enum.Parse<RunningApp>(resumes), UserSetup.ResumeApp(Enum.Parse<RunningApp>(running), cli, dashboard));

    [Fact]
    public void Each_app_resumes_in_its_own_words_and_opens_the_dashboard_only_when_chosen()
    {
        var cli = $"\"{Paths.DockyardCliExe}\"";
        var dashboard = $"\"{Paths.DashboardExe}\"";
        Assert.Equal($"{cli} setup --resume", UserSetup.ResumeCommand(RunningApp.Cli, openDashboard: false, assets: null));
        Assert.Equal($"{cli} setup --resume --open-dashboard --assets \"D:\\my files\"", UserSetup.ResumeCommand(RunningApp.Cli, openDashboard: true, assets: @"D:\my files"));
        Assert.Equal($"{dashboard} --setup --silent --resume", UserSetup.ResumeCommand(RunningApp.Dashboard, openDashboard: false, assets: null));
        Assert.Equal($"{dashboard} --setup --silent --resume --open-dashboard", UserSetup.ResumeCommand(RunningApp.Dashboard, openDashboard: true, assets: null));
    }

    [Fact]
    public void A_CLI_only_install_asks_for_no_dashboard_and_installs_the_running_CLI_itself()
    {
        var args = WindowsSetup.MachineInstallArgs(dashboard: false, without: [nameof(Component.Dashboard)], "dockyard", runningCli: @"C:\Downloads\dockyard.exe");
        Assert.Equal(["--engine-only", "--without", "Dashboard", "--source", "dockyard", "--cli-file", @"C:\Downloads\dockyard.exe"], args);
        Assert.True(MachineSetup.InstallsCli([nameof(Component.Dashboard)]));
    }

    [Fact]
    public void A_dashboard_only_install_leaves_the_CLI_out_whichever_app_runs_it()
    {
        string[] without = [nameof(Component.DockyardCli)];
        var fromCli = WindowsSetup.MachineInstallArgs(dashboard: true, without, "winget", runningCli: @"C:\Downloads\dockyard.exe");
        var fromDashboard = WindowsSetup.MachineInstallArgs(dashboard: true, without, "winget", runningCli: null);
        Assert.Equal(["--without", "DockyardCli", "--source", "winget"], fromCli);
        Assert.Equal(fromCli, fromDashboard);
        Assert.False(MachineSetup.InstallsCli(without));
    }

    [Fact]
    public void A_full_install_is_the_same_machine_step_from_either_app_but_for_the_file_the_CLI_brings()
    {
        var fromCli = WindowsSetup.MachineInstallArgs(dashboard: true, without: [], "winget", runningCli: @"C:\Downloads\dockyard.exe");
        var fromDashboard = WindowsSetup.MachineInstallArgs(dashboard: true, without: [], "winget", runningCli: null);
        Assert.Equal([.. fromDashboard, "--cli-file", @"C:\Downloads\dockyard.exe"], fromCli);
    }

    [Fact]
    public void Removing_one_app_keeps_the_other_and_Dockyard_installed()
    {
        Assert.True(WindowsHost.IsInstalledWhere(new HashSet<string>([Paths.InstalledMarker, Paths.DashboardExe]).Contains));
        Assert.True(WindowsHost.IsInstalledWhere(new HashSet<string>([Paths.InstalledMarker, Paths.DockyardCliExe]).Contains));
        Assert.Equal(Paths.DashboardExe, Elevation.WorkerExe(RunningApp.Tray, path => path == Paths.DashboardExe));
        Assert.Equal(Paths.DockyardCliExe, Elevation.WorkerExe(RunningApp.Tray, path => path == Paths.DockyardCliExe));
    }

    [Fact]
    public void Uninstall_takes_both_apps_along_from_the_one_folder()
    {
        foreach (var app in new[] { Paths.CommandDir, Paths.DashboardExe, Paths.AppExe, Paths.HostExe })
            Assert.Equal(Paths.AppDir, Path.GetDirectoryName(app));
    }

    [Fact]
    public void A_CLI_without_the_finished_mark_is_not_an_install_yet()
    {
        Assert.NotEqual(Paths.DockyardCliExe, Paths.InstalledMarker);
        Assert.False(WindowsHost.IsInstalledWhere(path => path == Paths.DockyardCliExe));
        Assert.False(WindowsHost.IsInstalledWhere(path => path == Paths.InstalledMarker));
        Assert.True(WindowsHost.IsInstalledWhere(new HashSet<string>([Paths.InstalledMarker, Paths.DockyardCliExe]).Contains));
        Assert.True(WindowsHost.IsInstalledWhere(new HashSet<string>([Paths.InstalledMarker, Paths.DashboardExe]).Contains));

        var marker = Path.Combine(Directory.CreateTempSubdirectory().FullName, "app", "installed");
        MachineSetup.MarkInstalled(marker);
        Assert.True(File.Exists(marker));
        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(marker))!, recursive: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Uninstall_removes_the_app_that_runs_it_last(bool cli)
    {
        var running = cli ? Paths.DockyardCliExe : Paths.DashboardExe;
        string[] files = [Paths.DockyardCliExe, Paths.AppExe, Paths.HostExe, Paths.DashboardExe, Path.Combine(Paths.AppDir, "bin", "other.dll")];
        var order = MachineSetup.RemovalOrder(files, running).ToList();
        Assert.Equal(running, order[^1]);
        Assert.Equal(files.Length, order.Count);
        Assert.Equal(files.Where(f => f != running), order.Take(files.Length - 1));
    }

    [Fact]
    public void Testcontainers_is_pinned_to_the_Linux_engine_only_where_there_is_one()
    {
        var dir = Directory.CreateTempSubdirectory("dockyard-tc-").FullName;
        try
        {
            var file = Path.Combine(dir, ".testcontainers.properties");
            UserSetup.PinTestcontainers(_ => { }, file, withLinux: false);
            Assert.False(File.Exists(file));

            UserSetup.PinTestcontainers(_ => { }, file, withLinux: true);
            Assert.Equal($"docker.host={Paths.LinuxHost}\n", File.ReadAllText(file));

            UserSetup.PinTestcontainers(_ => { }, file, withLinux: false);
            Assert.False(File.Exists(file));

            File.WriteAllText(file, "docker.host=tcp://elsewhere:2375\n");
            UserSetup.PinTestcontainers(_ => { }, file, withLinux: false);
            Assert.True(File.Exists(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
