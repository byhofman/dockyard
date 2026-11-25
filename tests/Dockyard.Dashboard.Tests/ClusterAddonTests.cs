using Dockyard.Dashboard.Features;
using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Tests;

public sealed class ClusterAddonTests
{
    [Fact]
    public void An_addon_goes_in_with_helm_at_its_pinned_version_through_kind_s_own_kubeconfig_and_none_of_the_user_s_repositories()
    {
        var args = ClusterAddons.InstallArgs(ClusterAddons.MetricsServer, "/tmp/kind/config", "dev", "/tmp/kind/helm");

        Assert.Equal(
        [
            "upgrade", "--install", "metrics-server", "metrics-server", "--repo", "https://kubernetes-sigs.github.io/metrics-server/", "--version", "3.14.0",
            "--namespace", "kube-system", "--create-namespace", "--kubeconfig", "/tmp/kind/config", "--kube-context", "kind-dev",
            "--repository-config", Path.Combine("/tmp/kind/helm", "repositories.yaml"), "--repository-cache", Path.Combine("/tmp/kind/helm", "cache"),
            "--set", "args[0]=--kubelet-insecure-tls",
        ], args);
    }

    [Fact]
    public void Traefik_gets_its_own_namespace_and_its_chart_s_defaults()
    {
        var args = ClusterAddons.InstallArgs(ClusterAddons.Traefik, "config", "dev", "helm");

        Assert.Equal(["upgrade", "--install", "traefik", "traefik", "--repo", "https://traefik.github.io/charts"], args[..6]);
        Assert.Contains("traefik", args.SkipWhile(a => a != "--namespace").Skip(1).Take(1));
        Assert.DoesNotContain("--set", args);
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
