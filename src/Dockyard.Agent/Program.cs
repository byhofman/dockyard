using Dockyard.Agent;

var options = AgentOptions.FromCmdline(File.Exists("/proc/cmdline") ? File.ReadAllText("/proc/cmdline") : "");
ITransport transport = options.Tcp ? new TcpTransport() : new VsockTransport();
IHostDialer dialer = options.Tcp ? new TcpHostDialer(options.TestHost) : new VsockHostDialer();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

Action<string> log = line => Console.Error.WriteLine($"dockyard-agent: {line}");
var agent = new Agent(transport, log);
await NetworkRules.Apply(Agent.Exit, log);
var dns = new DnsServer(GuestAddresses.Dns, GuestAddresses.Resolve, () => File.Exists("/etc/resolv.conf") ? File.ReadAllText("/etc/resolv.conf") : "", log);
var hostProxy = new HostProxy(dialer, HostProxy.OriginalDestination, log);
try
{
    await Task.WhenAll(agent.Run(cts.Token), dns.Run(cts.Token), hostProxy.Run(cts.Token));
}
catch (System.Net.Sockets.SocketException)
{
    return 1;
}
catch (OperationCanceledException)
{
}
return 0;
