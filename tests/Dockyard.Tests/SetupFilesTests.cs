using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Tests;

public sealed class DashboardFilesTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string Zip(params string[] entries)
    {
        var zip = Path.Combine(Directory.CreateTempSubdirectory().FullName, "d.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write(entry);
        }
        return zip;
    }

    [Fact]
    public void Adding_and_removing_leaves_the_rest_of_the_install_alone()
    {
        File.WriteAllText(Path.Combine(dir, "Dockyard.exe"), "tray");
        DashboardFiles.Extract(Zip("Dockyard.Dashboard.exe", "libSkiaSharp.dll"), dir);
        Assert.True(File.Exists(Path.Combine(dir, "Dockyard.Dashboard.exe")));
        Assert.True(File.Exists(Path.Combine(dir, "libSkiaSharp.dll")));

        DashboardFiles.Remove(dir, "Dockyard.Dashboard.exe", _ => { });
        Assert.Equal(["Dockyard.exe"], Directory.GetFiles(dir).Select(Path.GetFileName));

        DashboardFiles.Extract(Zip("Dockyard.Dashboard.exe"), dir);
        Assert.True(File.Exists(Path.Combine(dir, "Dockyard.Dashboard.exe")));
    }

    [Fact]
    public void Extracting_stays_inside_the_app_directory()
    {
        var outside = Path.Combine(Path.GetDirectoryName(dir)!, $"{Guid.NewGuid():N}.txt");
        DashboardFiles.Extract(Zip($"../{Path.GetFileName(outside)}"), dir);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void A_manifest_cannot_remove_files_outside_the_app_directory()
    {
        var outside = Path.Combine(Path.GetDirectoryName(dir)!, $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "keep");
        File.WriteAllLines(Path.Combine(dir, DashboardFiles.ManifestName), [$"../{Path.GetFileName(outside)}"]);
        DashboardFiles.Remove(dir, "x", _ => { });
        Assert.True(File.Exists(outside));
        File.Delete(outside);
    }
}

public sealed class ChecksumsTests
{
    [Fact]
    public void Verifies_against_sha256sum_output()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory().FullName, "a.bin");
        File.WriteAllText(file, "content");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("content")));

        Checksums.Verify($"0000  other\n{hash}  a.bin\n", file);
        Assert.Throws<InvalidOperationException>(() => Checksums.Verify($"{new string('0', 64)}  a.bin\n", file));
        Assert.Throws<InvalidOperationException>(() => Checksums.Verify($"{hash}  other\n", file));
    }
}

public sealed class WslConfigTests
{
    [Fact]
    public void Creates_the_section_when_there_is_no_file()
    {
        Assert.Equal($"[wsl2]\n{WslConfig.MirroredLine}\n", WslConfig.WithMirroredNetworking(null));
    }

    [Fact]
    public void Adds_to_an_existing_section_and_keeps_the_rest()
    {
        var updated = WslConfig.WithMirroredNetworking("[wsl2]\nmemory=8GB\n");
        Assert.Equal($"[wsl2]\n{WslConfig.MirroredLine}\nmemory=8GB\n", updated);
    }

    [Fact]
    public void Adds_a_section_after_other_ones()
    {
        var updated = WslConfig.WithMirroredNetworking("[experimental]\nsparseVhd=true");
        Assert.Equal($"[experimental]\nsparseVhd=true\n[wsl2]\n{WslConfig.MirroredLine}\n", updated);
    }

    [Theory]
    [InlineData("[wsl2]\nnetworkingMode=nat\n")]
    [InlineData("[wsl2]\n networkingMode = mirrored\n")]
    public void Leaves_a_choice_the_user_made_alone(string existing)
    {
        Assert.Null(WslConfig.WithMirroredNetworking(existing));
    }

    [Fact]
    public void A_commented_out_setting_is_no_choice()
    {
        Assert.NotNull(WslConfig.WithMirroredNetworking("[wsl2]\n# networkingMode=nat\n"));
    }

    [Fact]
    public void Uninstall_takes_back_only_its_own_line()
    {
        var updated = WslConfig.WithMirroredNetworking("[wsl2]\nmemory=8GB\n");
        Assert.Equal("[wsl2]\nmemory=8GB\n", WslConfig.WithoutDockyardLine(updated));
        Assert.Null(WslConfig.WithoutDockyardLine("[wsl2]\nnetworkingMode=mirrored\n"));
    }
}

public sealed class DaemonJsonTests
{
    const string Host = "npipe:////./pipe/docker_windows";

    [Fact]
    public void Names_the_user_so_access_works_without_signing_out()
    {
        Assert.Contains(@"""group"":""docker-users,PC\\ann""", DaemonJson.WithAccessFor(null, Host, @"PC\ann"));
    }

    [Fact]
    public void A_second_run_changes_nothing()
    {
        var once = DaemonJson.WithAccessFor(null, Host, @"PC\ann");
        Assert.Equal(once, DaemonJson.WithAccessFor(once, Host, @"PC\ANN"));
    }

    [Fact]
    public void Keeps_other_users_and_settings()
    {
        var config = DaemonJson.WithAccessFor("""{"group":"docker-users,PC\\bob","debug":true}""", Host, @"PC\ann");
        Assert.Contains(@"docker-users,PC\\bob,PC\\ann", config);
        Assert.Contains(@"""debug"":true", config);
    }

    [Fact]
    public void A_name_that_does_not_resolve_is_left_out()
    {
        Assert.Contains(@"""group"":""docker-users""", DaemonJson.WithAccessFor(null, Host, null));
    }

    [Fact]
    public void A_broken_file_does_not_stop_setup()
    {
        Assert.Contains("docker-users", DaemonJson.WithAccessFor("{not json", Host, null));
    }
}

public sealed class LinuxSetupTests
{
    [Theory]
    [InlineData(true, false, false, false, false, false, "")]
    [InlineData(false, true, false, false, true, false, "sudo -n")]
    [InlineData(false, false, true, true, true, true, "pkexec")]
    [InlineData(false, false, false, true, true, true, "sudo")]
    [InlineData(false, false, false, true, false, true, "pkexec")]
    public void Elevation_uses_the_least_interrupting_way(bool root, bool passwordless, bool display, bool pkexec, bool sudo, bool terminal, string expected)
    {
        Assert.Equal(expected, string.Join(' ', LinuxElevation.Prefix(root, passwordless, display, pkexec, sudo, terminal)!));
    }

    [Fact]
    public void Silent_without_any_way_to_ask_has_no_way_up()
    {
        Assert.Null(LinuxElevation.Prefix(false, false, false, true, true, false));
    }

    [Theory]
    [InlineData("apt", "dockyard_1.2.3_amd64.deb", "dockyard-dashboard_1.2.3_amd64.deb")]
    [InlineData("dnf", "dockyard-1.2.3-1.x86_64.rpm", "dockyard-dashboard-1.2.3-1.x86_64.rpm")]
    public void Package_patterns_match_the_release_assets_and_not_each_other(string manager, string app, string dashboard)
    {
        Assert.Matches(LinuxLayout.AppPackage(manager), app);
        Assert.DoesNotMatch(LinuxLayout.AppPackage(manager), dashboard);
        Assert.Matches(LinuxLayout.DashboardPackage(manager), dashboard);
        Assert.DoesNotMatch(LinuxLayout.DashboardPackage(manager), app);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData(":0", null, true)]
    [InlineData(null, "wayland-0", true)]
    public void Display_detection(string? display, string? wayland, bool expected)
    {
        Assert.Equal(expected, LinuxLayout.HasDisplay(display, wayland));
    }

    [Theory]
    [InlineData("ID=arch\n", false)]
    [InlineData("ID=ubuntu\n", true)]
    [InlineData("ID=fedora\n", true)]
    public void Only_apt_and_dnf_get_packages(string text, bool expected)
    {
        Assert.Equal(expected, OsRelease.Parse(text).HasPackages);
    }
}
