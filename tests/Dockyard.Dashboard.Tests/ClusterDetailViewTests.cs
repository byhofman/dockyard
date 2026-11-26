using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Shell;
using Dockyard.Kind;

namespace Dockyard.Dashboard.Tests;

public sealed class ClusterDetailViewTests
{
    [Fact]
    public async Task The_logs_tab_is_the_cluster_s_own_and_picking_it_reaches_the_detail()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ClusterDetailViewTests).Assembly);
        var (tab, context) = await session.Dispatch(() =>
        {
            var shell = new ShellViewModel();
            var containers = shell.Containers;
            var cluster = new KindCluster("dev", [new KindNode("dev-control-plane", "control-plane", "running")], "v1.33.1", 6443);
            using var detail = new ClusterDetail(shell.Clusters, cluster, containers, () => { });
            var window = new Window { Content = new ClusterDetailView { DataContext = detail } };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = ClusterDetail.LogsTab;
                Dispatcher.UIThread.RunJobs();
                var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("log"));
                return (detail.SelectedTab, list.DataContext);
            }
            finally { window.Close(); }
        }, CancellationToken.None);

        Assert.Equal(ClusterDetail.LogsTab, tab);
        Assert.IsType<ClusterDetail>(context);
    }

    [Fact]
    public async Task The_detail_gives_the_copy_buttons_the_clipboard()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ClusterDetailViewTests).Assembly);
        var copy = await session.Dispatch(() =>
        {
            var shell = new ShellViewModel();
            var cluster = new KindCluster("dev", [new KindNode("dev-control-plane", "control-plane", "running")], "v1.33.1", 6443);
            using var detail = new ClusterDetail(shell.Clusters, cluster, shell.Containers, () => { });
            _ = new ClusterDetailView { DataContext = detail };
            return shell.Clusters.CopyText;
        }, CancellationToken.None);

        Assert.NotNull(copy);
    }
}
