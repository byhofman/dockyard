using System.Diagnostics;
using Dockyard.Agent;

var options = AgentOptions.From(args, File.Exists("/proc/cmdline") ? File.ReadAllText("/proc/cmdline") : "");
Action<string> log = line => Console.Error.WriteLine($"dockyard-agent: {line}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

await NetworkRules.Apply(Agent.Exit, log);
var dns = new DnsServer(GuestAddresses.Dns, GuestAddresses.Resolve, () => File.Exists("/etc/resolv.conf") ? File.ReadAllText("/etc/resolv.conf") : "", log);
IHostDialer dialer = options.Mode switch
{
    AgentMode.Vm => new VsockHostDialer(),
    // QEMU's user networking puts the machine that started the VM at this address.
    AgentMode.TcpTest => new TcpHostDialer(System.Net.IPAddress.Parse("10.0.2.2")),
    _ => new TcpHostDialer(WslHost.Address(Directory.Exists("/sys/class/net/loopback0"), File.ReadAllText("/proc/net/route")), WslHost.Refused),
};
var hostProxy = new HostProxy(dialer, HostProxy.OriginalDestination, log);
List<Task> services = [dns.Run(cts.Token), hostProxy.Run(cts.Token)];
if (options.Mode == AgentMode.Wsl) services.Add(ExitWithEngine());
else services.Add(new Agent(options.Mode == AgentMode.Vm ? new VsockTransport() : new TcpTransport(), log).Run(cts.Token));

try
{
    await Task.WhenAll(services);
}
catch (System.Net.Sockets.SocketException e)
{
    log(e.Message);
    return 1;
}
catch (OperationCanceledException)
{
}
return 0;

async Task ExitWithEngine()
{
    // dockerd starts right after this process; give it the time a slow disk needs.
    await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
    while (Process.GetProcessesByName("dockerd").Length > 0) await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
    log("the engine stopped");
    cts.Cancel();
}
