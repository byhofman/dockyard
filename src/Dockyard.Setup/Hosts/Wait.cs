namespace Dockyard.Hosts;

static class Wait
{
    public static async Task For(Func<bool> ready, TimeSpan timeout, string what, Func<bool>? failed = null)
    {
        var until = DateTime.UtcNow + timeout;
        while (!await Task.Run(ready))
        {
            if (failed?.Invoke() == true) throw new InvalidOperationException($"{what} failed to start");
            if (DateTime.UtcNow > until) throw new TimeoutException($"{what} not ready after {timeout.TotalSeconds:0}s");
            await Task.Delay(500);
        }
    }
}
