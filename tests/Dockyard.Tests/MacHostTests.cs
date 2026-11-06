using System.Runtime.Versioning;
using System.Text;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Tests;

public sealed class MacHostTests
{
    [Fact]
    public void Launch_agent_starts_the_tray_at_login()
    {
        var plist = MacLayout.LaunchAgentPlist("/Applications/Dockyard.app/Contents/MacOS/Dockyard");
        Assert.Contains("<string>com.byhofman.dockyard</string>", plist);
        Assert.Contains("<string>/Applications/Dockyard.app/Contents/MacOS/Dockyard</string>\n        <string>--tray</string>", plist.Replace("    ", "    "));
        Assert.Contains("<key>RunAtLoad</key>\n    <true/>", plist);
    }

    [Fact]
    public void Admin_shell_escapes_for_applescript()
    {
        var args = MacLayout.AdminShell(MacLayout.PathsEntry("/Users/me/.dockyard/bin"));
        Assert.Equal("-e", args[0]);
        Assert.Equal("do shell script \"echo '/Users/me/.dockyard/bin' > /etc/paths.d/dockyard && chmod 644 /etc/paths.d/dockyard\" with administrator privileges", args[1]);
        Assert.Contains("\\\"x\\\"", MacLayout.AdminShell("echo \"x\"")[1]);
    }

    [Fact]
    public void Reads_docker_version_from_release_file() =>
        Assert.Equal(new Version(29, 9, 2), MacLayout.DockerVersion("VERSION=1.0.0\nDOCKER_VERSION=29.9.2\n"));
}

public sealed class MacUpdateTests
{
    [Fact]
    public void Finds_the_enclosing_app_bundle()
    {
        var app = Path.Combine(Path.GetTempPath(), "Applications", "Dockyard.app");
        Assert.Equal(app, MacLayout.Bundle(Path.Combine(app, "Contents", "MacOS") + Path.DirectorySeparatorChar));
        Assert.Null(MacLayout.Bundle(Path.Combine(Path.GetTempPath(), "Downloads", "Dockyard") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Release_zip_names_per_edition()
    {
        Assert.Matches(MacLayout.ZipAsset(engineOnly: false), "Dockyard-1.2.0-macos.zip");
        Assert.DoesNotMatch(MacLayout.ZipAsset(engineOnly: false), "Dockyard-1.2.0-macos-engine-only.zip");
        Assert.Matches(MacLayout.ZipAsset(engineOnly: true), "Dockyard-1.2.0-macos-engine-only.zip");
    }

    [Fact]
    public void Swap_script_waits_for_the_tray_then_replaces_and_reopens()
    {
        var script = MacLayout.SwapScript(4242, "/tmp/u/Dockyard.app", "/Users/me/Applications/Dockyard.app");
        Assert.Contains("while kill -0 4242", script);
        Assert.Contains("mv '/tmp/u/Dockyard.app' '/Users/me/Applications/Dockyard.app'", script);
        Assert.Contains("open '/Users/me/Applications/Dockyard.app' --args --tray", script);
    }
}

public sealed class MacInstallTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dockyard-mac-tests").FullName;
    string Plugins => Directory.CreateDirectory(Path.Combine(dir, "cli-plugins")).FullName;
    string Links => Path.Combine(dir, "links");

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string OtherAppPlugin(string content = "other app")
    {
        var inOtherApp = Path.Combine(dir, "OtherApp-docker-compose");
        File.WriteAllText(inOtherApp, content);
        var link = Path.Combine(Plugins, "docker-compose");
        File.CreateSymbolicLink(link, inOtherApp);
        return link;
    }

    static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task A_download_replaces_a_symlink_without_writing_through_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symlinks need a privilege on Windows");
        var link = OtherAppPlugin();

        await Updates.WriteReplacing(Bytes("dockyard"), link);

        Assert.Equal("other app", File.ReadAllText(Path.Combine(dir, "OtherApp-docker-compose")));
        Assert.Null(new FileInfo(link).LinkTarget);
        Assert.Equal("dockyard", File.ReadAllText(link));
        Assert.False(File.Exists(link + ".download"));
    }

    [Fact]
    public async Task A_failed_download_keeps_the_existing_file()
    {
        var target = Path.Combine(dir, "docker");
        File.WriteAllText(target, "working");

        await Assert.ThrowsAsync<IOException>(() => Updates.WriteReplacing(new FailingStream(), target));

        Assert.Equal("working", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".download"));
    }

    [Fact]
    public void Another_apps_plugin_link_comes_back_on_uninstall()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symlinks need a privilege on Windows");
        var link = OtherAppPlugin();

        MacLayout.SetAsideLinkedPlugin(link, Links);
        Assert.False(File.Exists(link));
        File.WriteAllText(link, "dockyard");

        MacLayout.RemovePlugin(link, Links);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("other app", File.ReadAllText(link));
    }

    [Fact]
    public void A_link_to_an_uninstalled_app_is_set_aside_and_restored_too()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symlinks need a privilege on Windows");
        var link = OtherAppPlugin();
        File.Delete(Path.Combine(dir, "OtherApp-docker-compose"));

        MacLayout.SetAsideLinkedPlugin(link, Links);
        Assert.Null(new FileInfo(link).LinkTarget);

        MacLayout.RemovePlugin(link, Links);
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Removing_a_plugin_twice_leaves_another_apps_link_where_it_is()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symlinks need a privilege on Windows");
        var link = OtherAppPlugin();
        MacLayout.SetAsideLinkedPlugin(link, Links);
        File.WriteAllText(link, "dockyard");

        MacLayout.RemovePlugin(link, Links);
        MacLayout.RemovePlugin(link, Links);

        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Dockyards_own_plugin_is_just_removed()
    {
        var plugin = Path.Combine(Plugins, "docker-buildx");
        File.WriteAllText(plugin, "dockyard");

        MacLayout.SetAsideLinkedPlugin(plugin, Links);
        Assert.True(File.Exists(plugin));

        MacLayout.RemovePlugin(plugin, Links);
        Assert.False(File.Exists(plugin));
    }

    [Fact]
    public void Removing_plugins_when_the_folder_is_gone_is_fine() =>
        MacLayout.RemovePlugin(Path.Combine(dir, "gone", "docker-compose"), Links);

    [Theory]
    [InlineData("orbstack\n", "orbstack")]
    [InlineData(null, "default")]
    [InlineData(" ", "default")]
    public void Uninstall_returns_to_the_context_from_before_the_install(string? saved, string expected) =>
        Assert.Equal(expected, MacLayout.ContextToRestore(saved));

    [Theory]
    [InlineData("/Applications/Dockyard.app/Contents/MacOS/", "/Applications/Dockyard.app")]
    [InlineData("/Users/a/Applications/Dockyard.app/Contents/MacOS/", "/Users/a/Applications/Dockyard.app")]
    [InlineData("/Volumes/Dockyard/Dockyard.app/Contents/MacOS/", "/Users/a/Applications/Dockyard.app")]
    [InlineData("/Volumes/Dockyard 1/Dockyard.app/Contents/MacOS/", "/Users/a/Applications/Dockyard.app")]
    [InlineData("/private/var/folders/x/AppTranslocation/ABC/d/Dockyard.app/Contents/MacOS/", "/Users/a/Applications/Dockyard.app")]
    [InlineData("/Users/a/Library/Caches/Dockyard/installer/Dockyard.app/Contents/MacOS/", "/Users/a/Applications/Dockyard.app")]
    public void Install_location_keeps_an_applications_folder_and_moves_everything_else(string baseDirectory, string expected)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix paths");
        Assert.Equal(expected, MacLayout.InstallLocation(baseDirectory, "/Users/a/Applications"));
    }

    [Fact]
    public void Bundle_version_comes_from_the_installed_info_plist()
    {
        const string plist = "<key>CFBundleShortVersionString</key>\n  <string>1.2.3</string>";
        Assert.Equal(new Version(1, 2, 3), MacLayout.BundleVersion(plist));
        Assert.Null(MacLayout.BundleVersion("<key>Other</key><string>1.2.3</string>"));
    }

    [Fact]
    public void Dashboard_files_are_the_dashboard_and_its_libraries_only()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        foreach (var name in new[] { "Dockyard", "dockyard-vm", "Dockyard Dashboard", "Dockyard.Dashboard", "libSkiaSharp.dylib" }) File.WriteAllText(Path.Combine(dir, name), "");
        Assert.Equal(["Dockyard Dashboard", "Dockyard.Dashboard", "libSkiaSharp.dylib"], MacLayout.DashboardFiles(dir).Select(Path.GetFileName).Order());
        Directory.Delete(dir, recursive: true);
        Assert.Empty(MacLayout.DashboardFiles(dir));
    }

    [Fact]
    public void Self_update_downloads_the_offered_release_not_the_running_one()
    {
        Assert.EndsWith("/releases/tags/v2.0.0", Updates.ReleaseApiUrl("1.0.0", "v2.0.0"));
        Assert.EndsWith("/releases/tags/v1.0.0", Updates.ReleaseApiUrl("1.0.0", null));
        Assert.EndsWith("/releases/latest", Updates.ReleaseApiUrl("0.0.0", null));
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task Starting_an_engine_that_already_answers_starts_no_second_helper()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS host");
        var runner = new FakeRunner();

        await new MacHost(runner, ping: () => true).Start(MacHost.Engine);

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public void Uninstall_finds_the_app_wherever_it_was_installed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix paths");
        string Bundle(string path, string id = MacLayout.BundleId)
        {
            Directory.CreateDirectory(Path.Combine(path, "Contents"));
            File.WriteAllText(Path.Combine(path, "Contents", "Info.plist"), $"<key>CFBundleIdentifier</key><string>{id}</string>");
            return path;
        }
        var user = Path.Combine(dir, "user");
        var system = Path.Combine(dir, "system");
        var elsewhere = Path.Combine(dir, "elsewhere");
        var expected = new[] { Bundle(Path.Combine(user, "Dockyard.app")), Bundle(Path.Combine(user, "Dockyard.app.old")), Bundle(Path.Combine(system, "Dockyard.app")), Bundle(Path.Combine(elsewhere, "Dockyard.app")) };
        Bundle(Path.Combine(dir, "unrelated", "Dockyard.app"));
        Bundle(Path.Combine(system, "Dockyard.app.new"), "com.example.not-ours");

        var found = MacLayout.InstalledBundles(MacLayout.LaunchAgentPlist(Path.Combine(elsewhere, "Dockyard.app", "Contents", "MacOS", "Dockyard")), user, system);

        Assert.Equal(expected.Order(), found.Order());
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task Starting_while_another_start_finishes_waits_for_it_and_starts_no_helper()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS host");
        var runner = new FakeRunner();
        var polls = 0;

        await new MacHost(runner, ping: () => ++polls > 3, helperAnswers: () => true).Start(MacHost.Engine);

        Assert.Empty(runner.Commands);
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task Starting_while_a_helper_is_stopping_touches_nothing_until_it_has_gone()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS host");
        var runner = new FakeRunner();
        var answers = 3;
        

        // Past the wait, the host goes on to prepare and spawn; the home here has no install, which ends the attempt.
        await Assert.ThrowsAnyAsync<Exception>(() => new MacHost(runner, ping: () => false, helperAnswers: () => answers-- > 0).Start(MacHost.Engine));

        Assert.True(answers <= 0, "the helper was still answering when the start moved on");
        Assert.DoesNotContain(runner.Commands, c => c.Contains(" run "));
    }

    [Fact]
    public void The_display_check_reaches_the_system_libraries()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS");
        _ = Dockyard.Shared.DisplayGate.IsUsable();
    }

    sealed class FailingStream : MemoryStream
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            throw new IOException("connection lost");
    }
}
