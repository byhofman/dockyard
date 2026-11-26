using Dockyard.Dashboard.Features;
using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Tests;

public sealed class ClusterAddonTests
{
    [Fact]
    public void An_addon_goes_in_from_its_cached_chart_through_kind_s_own_kubeconfig()
    {
        var args = ClusterAddons.InstallArgs(ClusterAddons.MetricsServer, "/charts/metrics-server-3.14.0.tgz", "/tmp/kind/config", "dev");

        Assert.Equal(
        [
            "upgrade", "--install", "metrics-server", "/charts/metrics-server-3.14.0.tgz",
            "--namespace", "kube-system", "--create-namespace", "--kubeconfig", "/tmp/kind/config", "--kube-context", "kind-dev",
            "--set", "args[0]=--kubelet-insecure-tls",
        ], args);
        Assert.DoesNotContain("--repo", args);
    }

    [Fact]
    public void A_chart_is_pulled_at_its_pinned_version_with_none_of_the_user_s_repositories()
    {
        var args = ClusterAddons.PullArgs(ClusterAddons.Traefik, "/into", "/tmp/kind/helm");

        Assert.Equal(
        [
            "pull", "traefik", "--repo", "https://traefik.github.io/charts", "--version", "43.0.0", "--destination", "/into",
            "--repository-config", Path.Combine("/tmp/kind/helm", "repositories.yaml"), "--repository-cache", Path.Combine("/tmp/kind/helm", "cache"),
        ], args);
    }

    [Fact]
    public async Task A_cached_chart_is_used_without_running_helm()
    {
        var cache = Directory.CreateTempSubdirectory("dockyard-charts-test-").FullName;
        try
        {
            var file = ClusterAddons.ChartFile(ClusterAddons.Traefik, cache);
            await File.WriteAllTextAsync(file, "chart");
            var lines = new List<string>();

            var found = await ClusterAddons.Fetch("/no/such/helm", ClusterAddons.Traefik, cache, lines.Add, cache);

            Assert.Equal(file, found);
            Assert.Empty(lines);
            Assert.Equal(Path.Combine(cache, "traefik-43.0.0.tgz"), file);
        }
        finally { Directory.Delete(cache, recursive: true); }
    }

    [Fact]
    public void A_command_is_Dockyard_s_own_copy_first_then_a_package_manager_s_then_one_on_the_path()
    {
        var home = Path.Combine(Path.GetTempPath(), "home");
        var dirs = CliLocator.KnownDirs(home).ToList();
        string In(string d) => Path.Combine(d, OperatingSystem.IsWindows() ? "helm.exe" : "helm");

        Assert.Equal(In(dirs[0]), CliLocator.Find("helm", f => f == In(dirs[0]) || f == In(dirs[1]), "other", home));
        Assert.Equal(In(dirs[1]), CliLocator.Find("helm", f => f == In(dirs[1]) || f == In("other"), "other", home));
        Assert.Equal(In("other"), CliLocator.Find("helm", f => f == In("other"), "other", home));
        Assert.Null(CliLocator.Find("helm", _ => false, "other", home));
    }

    [Fact]
    public void A_check_field_is_a_box_with_its_label_and_tells_the_dialog_when_it_changes()
    {
        var field = DialogField.Check("Traefik");
        var changed = 0;
        field.Changed += () => changed++;

        field.IsChecked = true;

        Assert.True(field.IsCheck);
        Assert.False(field.IsText);
        Assert.False(field.HasLabel);
        Assert.Equal(1, changed);
        Assert.True(DialogField.Check("metrics-server", isChecked: true).IsChecked);
    }
}
