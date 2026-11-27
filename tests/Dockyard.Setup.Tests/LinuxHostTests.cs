using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

public sealed class LinuxHostTests
{
    [Fact]
    public void Dockyard_is_installed_when_setup_finished_and_either_app_is_there()
    {
        Assert.True(LinuxLayout.IsInstalled(new HashSet<string>([LinuxLayout.InstalledMarker, PackagedCli]).Contains));
        Assert.True(LinuxLayout.IsInstalled(new HashSet<string>([LinuxLayout.PolkitRule, PackagedDashboard]).Contains));
        Assert.False(LinuxLayout.IsInstalled(new HashSet<string>([LinuxLayout.InstalledMarker]).Contains));
        Assert.False(LinuxLayout.IsInstalled(new HashSet<string>([PackagedCli, PackagedDashboard]).Contains));
    }

    static readonly string PackagedCli = Path.Combine(LinuxLayout.PackageDir, LinuxLayout.Cli);
    static readonly string PackagedDashboard = Path.Combine(LinuxLayout.DashboardPackageDir, LinuxLayout.DashboardName);
    static string Tray(string dir) => Path.Combine(dir, LinuxLayout.TrayName);

    [Fact]
    public void A_CLI_only_install_stands_alone_with_its_own_tray()
    {
        var files = new HashSet<string>([LinuxLayout.InstalledMarker, PackagedCli, Tray(LinuxLayout.PackageDir)]);
        Assert.True(LinuxLayout.IsInstalled(files.Contains));
        Assert.Equal(LinuxLayout.PackageDir, LinuxLayout.CliDirWhere(files.Contains));
        Assert.DoesNotContain(Path.Combine(LinuxLayout.DashboardDirWhere(files.Contains), LinuxLayout.DashboardName), files);
        Assert.Equal(Tray(LinuxLayout.PackageDir), LinuxLayout.TrayWhere(files.Contains));
    }

    [Fact]
    public void A_dashboard_only_install_stands_alone_with_its_own_tray()
    {
        var files = new HashSet<string>([LinuxLayout.PolkitRule, PackagedDashboard, Tray(LinuxLayout.DashboardPackageDir)]);
        Assert.True(LinuxLayout.IsInstalled(files.Contains));
        Assert.Equal(LinuxLayout.DashboardPackageDir, LinuxLayout.DashboardDirWhere(files.Contains));
        Assert.NotEqual(LinuxLayout.PackageDir, LinuxLayout.DashboardPackageDir);
        Assert.Equal(Tray(LinuxLayout.DashboardPackageDir), LinuxLayout.TrayWhere(files.Contains));
    }

    [Fact]
    public void Removing_one_app_keeps_the_other_and_the_autostart_follows_the_tray_that_stays()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix paths");
        var both = new HashSet<string>([LinuxLayout.InstalledMarker, PackagedCli, Tray(LinuxLayout.PackageDir), PackagedDashboard, Tray(LinuxLayout.DashboardPackageDir)]);
        Assert.Equal(Tray(LinuxLayout.DashboardPackageDir), LinuxLayout.TrayWhere(both.Contains));

        var withoutDashboard = new HashSet<string>(both.Where(f => !f.StartsWith(LinuxLayout.DashboardPackageDir + "/", StringComparison.Ordinal)));
        Assert.True(LinuxLayout.IsInstalled(withoutDashboard.Contains));
        Assert.Contains($"Exec=\"{Tray(LinuxLayout.PackageDir)}\"", DesktopEntry.Autostart(LinuxLayout.TrayWhere(withoutDashboard.Contains)));

        var withoutCli = new HashSet<string>(both.Where(f => !f.StartsWith(LinuxLayout.PackageDir + "/", StringComparison.Ordinal)));
        Assert.True(LinuxLayout.IsInstalled(withoutCli.Contains));
        Assert.Equal(Tray(LinuxLayout.DashboardPackageDir), LinuxLayout.TrayWhere(withoutCli.Contains));
    }

    [Fact]
    public void Without_apt_or_dnf_either_app_lives_in_the_home_folder_with_its_own_version_file()
    {
        var cli = Path.Combine(LinuxLayout.HomeDir, LinuxLayout.Cli);
        var dashboard = Path.Combine(LinuxLayout.DashboardHomeDir, LinuxLayout.DashboardName);
        var files = new HashSet<string>([LinuxLayout.InstalledMarker, dashboard, Tray(LinuxLayout.DashboardHomeDir)]);
        Assert.True(LinuxLayout.IsInstalled(files.Contains));
        Assert.Equal(LinuxLayout.DashboardHomeDir, LinuxLayout.DashboardDirWhere(files.Contains));
        Assert.Equal(Path.Combine(LinuxLayout.DashboardHomeDir, "version"), LinuxLayout.VersionFileIn(LinuxLayout.DashboardDirWhere(files.Contains)));
        Assert.Equal(Tray(LinuxLayout.DashboardHomeDir), LinuxLayout.TrayWhere(files.Contains));

        files = [LinuxLayout.InstalledMarker, cli];
        Assert.True(LinuxLayout.IsInstalled(files.Contains));
        Assert.Equal(Path.Combine(LinuxLayout.HomeDir, "version"), LinuxLayout.VersionFileIn(LinuxLayout.CliDirWhere(files.Contains)));
    }

    [Fact]
    public void A_stray_file_in_the_packaged_folder_does_not_hide_the_app_in_the_home_folder()
    {
        var cli = Path.Combine(LinuxLayout.HomeDir, LinuxLayout.Cli);
        var files = new HashSet<string>([LinuxLayout.InstalledMarker, Tray(LinuxLayout.PackageDir), cli]);
        Assert.Equal(LinuxLayout.HomeDir, LinuxLayout.CliDirWhere(files.Contains));
        Assert.True(LinuxLayout.IsInstalled(files.Contains));
        Assert.Equal(LinuxLayout.PackageDir, LinuxLayout.CliDirWhere(new HashSet<string>([PackagedCli, cli]).Contains));
    }

    [Theory]
    // running, runs from its install, cli wanted, cli there, dashboard wanted, dashboard there → place cli, place dashboard, remove cli, remove dashboard
    [InlineData("Cli", false, true, false, false, false, true, false, false, false)]
    [InlineData("Cli", false, true, true, true, true, true, false, false, false)]
    [InlineData("Cli", true, true, true, true, false, false, true, false, false)]
    [InlineData("Dashboard", true, false, false, true, true, false, false, false, false)]
    [InlineData("Dashboard", false, false, false, true, true, false, true, false, false)]
    [InlineData("Dashboard", true, true, false, true, true, true, false, false, false)]
    [InlineData("Dashboard", true, false, true, true, true, false, false, true, false)]
    [InlineData("Cli", true, true, true, false, true, false, false, false, true)]
    public void Setup_puts_in_place_only_the_apps_chosen_and_the_running_one_brings_itself(string running, bool fromInstall,
        bool cli, bool cliThere, bool dashboard, bool dashboardThere, bool placeCli, bool placeDashboard, bool removeCli, bool removeDashboard) =>
        Assert.Equal((placeCli, placeDashboard, removeCli, removeDashboard),
            LinuxLayout.AppsToChange(Enum.Parse<Dockyard.Core.RunningApp>(running), fromInstall, cli, cliThere, dashboard, dashboardThere));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Uninstall_removes_both_home_folders_the_running_app_s_last(bool cli)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix paths");
        var running = Path.Combine(cli ? LinuxLayout.HomeDir : LinuxLayout.DashboardHomeDir, cli ? LinuxLayout.Cli : LinuxLayout.DashboardName);
        var order = LinuxLayout.HomeDirsToRemove(running);
        Assert.Equal(new[] { LinuxLayout.HomeDir, LinuxLayout.DashboardHomeDir }.Order(), order.Order());
        Assert.Equal(cli ? LinuxLayout.HomeDir : LinuxLayout.DashboardHomeDir, order[^1]);
    }

    [Theory]
    [InlineData("Dockyard-1.2.3-linux-x64.tar.gz", true, false)]
    [InlineData("dockyard-dashboard-1.2.3-linux-x64.tar.gz", false, true)]
    [InlineData("dockyard_1.2.3_amd64.deb", false, false)]
    public void Each_app_has_a_tarball_of_its_own(string asset, bool cli, bool dashboard)
    {
        Assert.Equal(cli, System.Text.RegularExpressions.Regex.IsMatch(asset, LinuxLayout.Tarball));
        Assert.Equal(dashboard, System.Text.RegularExpressions.Regex.IsMatch(asset, LinuxLayout.DashboardTarball));
    }

    [Theory]
    [InlineData("ID=ubuntu\nID_LIKE=debian\nVERSION_CODENAME=noble\nUBUNTU_CODENAME=noble\n", "ubuntu", "apt", "noble")]
    [InlineData("ID=debian\nVERSION_CODENAME=bookworm\n", "debian", "apt", "bookworm")]
    [InlineData("ID=linuxmint\nID_LIKE=\"ubuntu debian\"\nVERSION_CODENAME=wilma\nUBUNTU_CODENAME=noble\n", "linuxmint", "apt", "noble")]
    [InlineData("ID=fedora\nVERSION_ID=41\n", "fedora", "dnf", null)]
    [InlineData("ID=\"rhel\"\nID_LIKE=\"fedora\"\n", "rhel", "dnf", null)]
    [InlineData("ID=arch\n", "arch", null, null)]
    public void Parses_os_release(string text, string id, string? packageManager, string? codename)
    {
        var os = OsRelease.Parse(text);
        Assert.Equal(id, os.Id);
        Assert.Equal(packageManager, os.PackageManager);
        Assert.Equal(codename, os.Codename);
    }

    [Theory]
    [InlineData("dockyard_1.2.3_amd64.deb", true)]
    [InlineData("dockyard-dashboard_1.2.3_amd64.deb", false)]
    [InlineData("dockyard-linux-x64", false)]
    [InlineData("Dockyard-0.3.0-linux-x64.tar.gz", false)]
    public void Update_check_looks_for_the_package_with_the_command_and_the_tray(string asset, bool matches) =>
        Assert.Equal(matches, System.Text.RegularExpressions.Regex.IsMatch(asset, LinuxLayout.AppPackage("apt")));

    [Fact]
    public void Autostart_entry_runs_the_tray()
    {
        var entry = DesktopEntry.Autostart("/usr/lib/dockyard/dockyard-tray");
        Assert.Contains("Exec=\"/usr/lib/dockyard/dockyard-tray\"\n", entry);
        Assert.StartsWith("[Desktop Entry]\nType=Application", entry);
    }

    [Fact]
    public void Setup_script_is_embedded_with_the_polkit_rule()
    {
        using var stream = typeof(LinuxHost).Assembly.GetManifestResourceStream("setup-linux.sh");
        Assert.NotNull(stream);
        var script = new StreamReader(stream).ReadToEnd();
        Assert.Contains("org.freedesktop.systemd1.manage-units", script);
        Assert.Contains("subject.isInGroup(\"docker\")", script);
        Assert.Contains("systemctl enable --now docker.socket", script);
    }

    [Fact]
    public void Uninstall_removes_the_CLI_last()
    {
        using var stream = typeof(LinuxHost).Assembly.GetManifestResourceStream("setup-linux.sh");
        var script = new StreamReader(stream!).ReadToEnd();
        var uninstall = script[script.IndexOf("  uninstall)", StringComparison.Ordinal)..];
        var packages = uninstall[uninstall.IndexOf("case \"$family\" in", StringComparison.Ordinal)..];
        Assert.Contains("dockyard-dashboard dockyard", packages);
        Assert.DoesNotContain("rm -", packages);
        Assert.DoesNotContain("systemctl", packages);
        Assert.Contains("rm -rf /usr/local/lib/dockyard", uninstall[..uninstall.IndexOf("case \"$family\" in", StringComparison.Ordinal)]);
    }
}
