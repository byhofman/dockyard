using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Tests;

// Through the automation peers, which is what UI Automation, VoiceOver and the end-to-end screenshots see.
public sealed class SectionSwitchingTests
{
    public static TheoryData<string, Type> Sections => new()
    {
        { "Images", typeof(ImagesView) },
        { "Volumes", typeof(VolumesView) },
        { "Networks", typeof(NetworksView) },
        { "Clusters", typeof(ClustersView) },
        { "Disk", typeof(DiskView) },
        { "Containers", typeof(ContainersView) },
    };

    [Theory]
    [MemberData(nameof(Sections))]
    public async Task Selecting_a_sidebar_item_by_its_name_shows_that_section(string title, Type view)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SectionSwitchingTests).Assembly);
        var (shown, current) = await session.Dispatch(() =>
        {
            var vm = new ShellViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                if (title == "Containers") vm.CurrentSection = vm.Sections[1];
                Dispatcher.UIThread.RunJobs();

                var item = Descendants(ControlAutomationPeer.CreatePeerForElement(window))
                    .SingleOrDefault(p => p.GetAutomationControlType() == AutomationControlType.ListItem && p.GetName() == title);
                Assert.True(item is not null, $"no list item named '{title}'");
                var selection = item.GetProvider<ISelectionItemProvider>();
                Assert.NotNull(selection);
                selection.Select();
                Dispatcher.UIThread.RunJobs();

                return (window.GetVisualDescendants().Any(view.IsInstanceOfType), vm.CurrentSection.Title);
            }
            finally { window.Close(); }
        }, CancellationToken.None);

        Assert.Equal(title, current);
        Assert.True(shown, $"{view.Name} is not on screen");
    }

    static IEnumerable<AutomationPeer> Descendants(AutomationPeer peer) =>
        peer.GetChildren().SelectMany(c => Descendants(c).Prepend(c));
}
