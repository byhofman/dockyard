using Dockyard.Core;

namespace Dockyard.Tests;

public sealed class AcceptLoopTests
{
    sealed class Instance(int id) : IDisposable
    {
        public int Id { get; } = id;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task A_client_that_left_early_costs_one_instance_and_the_next_one_still_serves()
    {
        using var cts = new CancellationTokenSource();
        var made = new List<Instance> { new(0) };
        var handled = new List<int>();
        var waits = 0;
        Task Wait(Instance i, CancellationToken ct)
        {
            waits++;
            if (waits == 1) throw new IOException("No process is on the other end of the pipe.");
            if (waits == 3) { cts.Cancel(); throw new OperationCanceledException(); }
            return Task.CompletedTask;
        }

        await AcceptLoop.Run(made[0], () => { var i = new Instance(made.Count); made.Add(i); return i; }, Wait, i => handled.Add(i.Id), cts.Token);

        Assert.True(made[0].Disposed);
        Assert.Equal([1], handled);
        Assert.False(made[1].Disposed);
        Assert.True(made[^1].Disposed);
    }
}
