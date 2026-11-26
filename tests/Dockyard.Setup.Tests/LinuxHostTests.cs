using Dockyard.Hosts;

namespace Dockyard.Tests;

public sealed class LinuxHostTests
{
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
    [InlineData("Dockyard-linux-x64", true)]
    [InlineData("Dockyard.exe", false)]
    [InlineData("Dockyard-0.3.0-linux-x64.tar.gz", false)]
    public void Update_check_looks_for_the_linux_binary(string asset, bool matches) =>
        Assert.Equal(matches, System.Text.RegularExpressions.Regex.IsMatch(asset, LinuxLayout.AppAsset));

    [Fact]
    public void Autostart_entry_runs_the_tray()
    {
        var entry = DesktopEntry.Autostart("/usr/lib/dockyard/Dockyard");
        Assert.Contains("Exec=\"/usr/lib/dockyard/Dockyard\" --tray", entry);
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
}
