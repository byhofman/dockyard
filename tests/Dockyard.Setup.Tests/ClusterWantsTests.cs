using Dockyard.Engine;
using Dockyard.Kind;

namespace Dockyard.Setup.Tests;

public sealed class ClusterWantsTests : IDisposable
{
    readonly TempSettings settings = new();
    readonly string folder = Directory.CreateTempSubdirectory("dy-clusters").FullName;
    readonly RecordingAgent agent = new();

    public void Dispose()
    {
        settings.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    DockyardSetup For(FakeHost host, IClusterAgent? clusterAgent) =>
        new(host, settings.Settings, new ShellProfiles((_, _) => { }, () => [])) { ClusterAgent = () => clusterAgent, ClusterFolder = folder };

    const string Yaml = "kind: Cluster\napiVersion: kind.x-k8s.io/v1alpha4\nnodes:\n- role: control-plane\n";

    [Fact]
    public async Task Every_cluster_change_through_the_facade_pushes_the_wants()
    {
        var setup = For(new FakeHost(), agent);
        ClusterDefinitions.Save("dev", new ClusterDefinition(Yaml, ClusterStorage.Nfs, [], LoadBalancer: true, Ports: [8080, 8443]), folder);
        ClusterDefinitions.Save("quiet", new ClusterDefinition(Yaml, ClusterStorage.HostFolder, [], LoadBalancer: false, Ports: [80]), folder);

        await setup.PushClusterWants();
        Assert.Equal([new ClusterWant("dev", true, [8080, 8443], true), new ClusterWant("quiet", false, [], false)], agent.Pushed[^1], WantComparer.Instance);

        ClusterDefinitions.Remove("dev", folder);
        await setup.PushClusterWants();
        Assert.Equal([new ClusterWant("quiet", false, [], false)], agent.Pushed[^1], WantComparer.Instance);

        Assert.Equal(DockyardSetup.Ok, await setup.Setup(new SetupRequest([], [], null, false, false, Silent: true, Resume: false) { Start = false }, new ProgressLines(), CancellationToken.None));
        Assert.Equal(3, agent.Pushed.Count);
    }

    [Fact]
    public async Task Without_an_agent_or_with_one_that_does_not_answer_the_push_does_nothing()
    {
        ClusterDefinitions.Save("dev", new ClusterDefinition(Yaml, ClusterStorage.None, []), folder);

        await For(new FakeHost(), null).PushClusterWants();
        await For(new FakeHost(), new RecordingAgent { Failure = new IOException("The agent next to the engine did not answer") }).PushClusterWants();
    }

    [Fact]
    public async Task An_agent_that_answers_with_an_error_says_so()
    {
        ClusterDefinitions.Save("dev", new ClusterDefinition(Yaml, ClusterStorage.None, []), folder);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            For(new FakeHost(), new RecordingAgent { Failure = new InvalidOperationException("unknown request") }).PushClusterWants());

        Assert.Equal("The engine's agent refused the cluster settings: unknown request", e.Message);
    }

    sealed class RecordingAgent : IClusterAgent
    {
        public List<IReadOnlyList<ClusterWant>> Pushed { get; } = [];
        public Exception? Failure { get; init; }

        public Task SetClusterWantsAsync(IReadOnlyList<ClusterWant> wants, CancellationToken ct = default)
        {
            if (Failure is not null) throw Failure;
            Pushed.Add(wants);
            return Task.CompletedTask;
        }

        public Task<ClusterServicesStatus> ClusterStatusAsync(CancellationToken ct = default) => Task.FromResult(new ClusterServicesStatus([], []));
    }

    // A record's own equality compares the list of ports by reference.
    sealed class WantComparer : IEqualityComparer<ClusterWant>
    {
        public static readonly WantComparer Instance = new();
        public bool Equals(ClusterWant? x, ClusterWant? y) => x is not null && y is not null && x.Cluster == y.Cluster && x.LoadBalancer == y.LoadBalancer && x.Nfs == y.Nfs && x.Ports.SequenceEqual(y.Ports);
        public int GetHashCode(ClusterWant obj) => obj.Cluster.GetHashCode();
    }
}
