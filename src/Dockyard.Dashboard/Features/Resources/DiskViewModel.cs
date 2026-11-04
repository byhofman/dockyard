using System.Collections.ObjectModel;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Resources;

sealed class DiskViewModel : SectionViewModel
{
    bool loading;
    bool loaded;

    public DiskViewModel(ShellViewModel shell, EngineSession session) : base(shell, session)
    {
        PruneCommand = new Command<DiskCategory>(c =>
        {
            Ask($"Remove unused {c.Name.ToLowerInvariant()} ({c.ReclaimableText})?", "Prune", () => Run($"Pruning {c.Name}", async client =>
            {
                var reclaimed = await Prune(client, c.Name);
                Shell.ShowInfo($"Reclaimed {Format.Bytes(reclaimed)}");
                await Reload();
            }));
            return Task.CompletedTask;
        });
        CleanAllCommand = new Command(() =>
        {
            var list = string.Join(", ", Categories.Select(c => $"{c.Name.ToLowerInvariant()} {c.ReclaimableText}"));
            Ask($"Remove everything unused: stopped containers, unused images, unused volumes and their data, unused networks and build cache ({list})?",
                "Clean everything", () => Run("Cleaning up", async client =>
                {
                    long total = 0;
                    foreach (var name in new[] { "Containers", "Images", "Volumes", "Build cache" }) total += await Prune(client, name);
                    await client.PruneNetworksAsync();
                    Shell.ShowInfo($"Reclaimed {Format.Bytes(total)}");
                    await Reload();
                }));
            return Task.CompletedTask;
        });
        RefreshCommand = new Command(Reload);
        session.Connected += () => { if (loaded) _ = Reload(); };
    }

    public ObservableCollection<DiskCategory> Categories { get; } = [];
    public Command<DiskCategory> PruneCommand { get; }
    public Command CleanAllCommand { get; }
    public Command RefreshCommand { get; }
    public bool IsLoading { get => loading; private set => Set(ref loading, value); }
    public string TotalText => Format.Bytes(Categories.Sum(c => c.Total));
    public string ReclaimableText => Format.Bytes(Categories.Sum(c => c.Reclaimable));

    public override void OnShown()
    {
        if (!loaded) _ = Reload();
    }

    protected override void ApplyFilter() { }

    public override async Task Reload()
    {
        IsLoading = true;
        await SafeReload(async c =>
        {
            var df = await c.DiskUsageAsync();
            Categories.Clear();
            foreach (var cat in DiskSummary.From(df, Session.Containers.Items)) Categories.Add(cat);
            loaded = true;
            Raise(nameof(TotalText), nameof(ReclaimableText));
        });
        IsLoading = false;
    }

    static async Task<long> Prune(DockerClient client, string category) => category switch
    {
        "Containers" => (await client.PruneContainersAsync()).SpaceReclaimed,
        "Images" => (await client.PruneImagesAsync(danglingOnly: false)).SpaceReclaimed,
        "Volumes" => (await client.PruneVolumesAsync()).SpaceReclaimed,
        "Build cache" => (await client.PruneBuildCacheAsync()).SpaceReclaimed,
        _ => 0,
    };
}
