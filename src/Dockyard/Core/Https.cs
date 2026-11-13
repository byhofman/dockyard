using Dockyard.Engine;
using Dockyard.Engine.Transport;
using Dockyard.Hosts;

namespace Dockyard.Core;

// Trusted HTTPS on or off. Off is the default and everything else works without it, over plain HTTP.
static class Https
{
    public static async Task Enable(IEngineHost host, Action<string> log, bool silent)
    {
        PemPair ca;
        try
        {
            // A CA from before the IP and usage limits is replaced, which asks for trust once more.
            if (await LocalCa.Load(host.CaDirectory, host.CaKeys) is { } earlier && !LocalCa.IsConstrainedRoot(earlier.Certificate))
            {
                log("Replacing Dockyard's certificate authority with one that can only vouch for web servers under dockyard.test");
                await Disable(host, log);
            }
            ca = await LocalCa.LoadOrCreate(host.CaDirectory, host.CaKeys);
        }
        catch (InvalidOperationException e)
        {
            log($"HTTPS was not turned on: {e.Message} Plain HTTP works as before.");
            return;
        }
        if (!await host.TrustCa(log, LocalCa.CertificateFile(host.CaDirectory), silent))
            log("The certificate was not added to this computer's trust store, so browsers warn about HTTPS until it is. Plain HTTP works as before.");
        var settings = Settings.Load();
        settings.HttpsEnabled = true;
        settings.Save();
        await Push(host, ca, log);
    }

    public static async Task Disable(IEngineHost host, Action<string> log)
    {
        await host.UntrustCa(log, LocalCa.CertificateFile(host.CaDirectory));
        await LocalCa.Remove(host.CaDirectory, host.CaKeys);
        var settings = Settings.Load();
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
                // Only when a certificate is due: the key can be in the OS's store, which is not for asking every 10 seconds.
                if ((!reachable || DateTime.UtcNow - given > TimeSpan.FromDays(1)) && await LocalCa.Load(host.CaDirectory, host.CaKeys) is { } ca)
                {
                    if (!LocalCa.IsConstrainedRoot(ca.Certificate))
                    {
                        await Enable(host, _ => { }, silent: false);
                        continue;
                    }
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
