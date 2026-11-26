using Dockyard.Engine;
using Dockyard.Engine.Transport;
using Dockyard.Hosts;

namespace Dockyard.Core;

// Trusted HTTPS on or off. Off is the default and everything else works without it, over plain HTTP.
static class Https
{
    public static async Task Enable(IEngineHost host, Settings settings, Action<string> log, bool silent)
    {
        PemPair ca;
        try
        {
            ca = await LocalCa.LoadOrCreate(host.CaDirectory, host.CaKeys);
        }
        catch (InvalidOperationException e)
        {
            log($"HTTPS was not turned on: {e.Message} Plain HTTP works as before.");
            return;
        }
        if (!await host.TrustCa(log, LocalCa.CertificateFile(host.CaDirectory), silent))
            log("The certificate was not added to this computer's trust store, so browsers warn about HTTPS until it is. Plain HTTP works as before.");
        settings.HttpsEnabled = true;
        settings.Save();
        await Push(host, ca, log);
    }

    public static async Task Disable(IEngineHost host, Settings settings, Action<string> log, bool silent)
    {
        await host.UntrustCa(log, LocalCa.CertificateFile(host.CaDirectory), silent);
        await LocalCa.Remove(host.CaDirectory, host.CaKeys);
        settings.HttpsEnabled = false;
        settings.Save();
    }

    public static async Task Push(IEngineHost host, PemPair ca, Action<string>? log = null)
    {
        if (AgentClient.For(DockerEndpoint.Parse(host.Engines[0].Endpoint)) is not { } agent) return;
        var leaf = LocalCa.Leaf(ca, DateTimeOffset.UtcNow);
        // Setup has just restarted the agent when the engine runs, and it sets up its rules before it answers.
        var attempts = await host.IsRunning(host.Engines[0].Id) ? 15 : 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await agent.SetCertificateAsync(leaf.Certificate, leaf.Key);
                return;
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
            {
                if (attempt < attempts) { await Task.Delay(TimeSpan.FromSeconds(1)); continue; }
                // The tray hands the certificate over once the engine is back.
                log?.Invoke("HTTPS starts once the engine runs and the tray is there");
                return;
            }
        }
    }

    // Container domains on or off, told to the engine's agent, which keeps it.
    public static async Task SetNames(IEngineHost host, bool on, Action<string> log)
    {
        if (AgentClient.For(DockerEndpoint.Parse(host.Engines[0].Endpoint)) is not { } agent) return;
        try
        {
            await agent.SetNamesAsync(on);
            log(on ? "Container domains are on: names under dockyard.internal and the proxy" : "Container domains are off: dockyard-proxy and dockyard-etcd go, containers keep their DNS");
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
        {
            log("Container domains change once the engine runs");
        }
    }

    // The agent keeps its certificate in memory only, so it has none after each engine start, and one lasts 90 days.
    public static async Task KeepCertificateCurrent(IEngineHost host, CancellationToken ct)
    {
        if (AgentClient.For(DockerEndpoint.Parse(host.Engines[0].Endpoint)) is not { } agent) return;
        var given = DateTime.MinValue;
        var reachable = false;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            if (!Settings.Load().HttpsEnabled) continue;
            try
            {
                await agent.DomainsAsync(ct);
                // An agent that just started may have missed a change made while it was gone.
                if (!reachable) await agent.SetNamesAsync(Settings.Load().ContainerDomains, ct);
                // Only when a certificate is due: the key can be in the OS's store, which is not for asking every 10 seconds.
                if ((!reachable || DateTime.UtcNow - given > TimeSpan.FromDays(1)) && await LocalCa.Load(host.CaDirectory, host.CaKeys) is { } ca)
                {
                    var leaf = LocalCa.Leaf(ca, DateTimeOffset.UtcNow);
                    await agent.SetCertificateAsync(leaf.Certificate, leaf.Key, ct);
                    given = DateTime.UtcNow;
                }
                reachable = true;
            }
            catch (Exception e) when (e is not OperationCanceledException) { reachable = false; }
        }
    }
}
