using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dockyard.Agent;
using Dockyard.Engine;
using Dockyard.Engine.Transport;

var options = AgentOptions.From(args, File.Exists("/proc/cmdline") ? File.ReadAllText("/proc/cmdline") : "");
Action<string> log = line => Console.Error.WriteLine($"dockyard-agent: {line}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

// dockyard-vm-init has eth0 up with its lease before the agent starts.
var vmHost = options.Mode is AgentMode.Vm or AgentMode.TcpTest
    ? WslHost.DefaultGateway(File.ReadAllText("/proc/net/route")) ?? throw new InvalidOperationException("the VM has no gateway to the Mac yet")
    : null;
await NetworkRules.Apply(Agent.Exit, log, vmHost, linuxHost: options.Mode == AgentMode.Host);

var table = new DomainTable(() => new DockerClient(DockerEndpoint.Parse("unix:///var/run/docker.sock")), "/var/lib/docker/dockyard/domain-ports.json", log);
var proxy = new DomainProxy(table, log);
var api = new AgentApi(table, proxy);

string ReadResolvConf() => File.Exists("/etc/resolv.conf") ? File.ReadAllText("/etc/resolv.conf") : "";

// Each place a name is looked up from gets the address that works from there.
IPAddress? ContainerView(string name) => GuestAddresses.Resolve(name) ?? (table.Contains(name) ? GuestAddresses.Dns : null);
Func<string, IPAddress?> HostView(Func<IPAddress?> address) => name => table.Contains(name) ? address() : null;

List<Task> services =
[
    // On a Linux host port 53 may belong to dnsmasq or the like, so a rule brings containers' queries to a port of its own.
    new DnsServer(GuestAddresses.Dns, ContainerView, ReadResolvConf, log, options.Mode == AgentMode.Host ? GuestAddresses.ContainerDnsPort : 53, DomainNames.InZone).Run(cts.Token),
    table.Run(cts.Token),
    Optional("HTTP on domains", () => proxy.Serve(DomainProxy.Listen(IPAddress.Any, GuestAddresses.DomainHttpPort), tls: false, guarded: true, cts.Token)),
    Optional("HTTPS on domains", () => proxy.Serve(DomainProxy.Listen(IPAddress.Any, GuestAddresses.DomainHttpsPort), tls: true, guarded: true, cts.Token)),
];

IHostDialer dialer;
switch (options.Mode)
{
    case AgentMode.Vm:
    case AgentMode.TcpTest:
        // QEMU's user networking puts the machine that started the VM at this address.
        dialer = options.Mode == AgentMode.Vm ? new VsockHostDialer() : new TcpHostDialer(IPAddress.Parse("10.0.2.2"));
        ITransport transport = options.Mode == AgentMode.Vm ? new VsockTransport() : new TcpTransport();
        services.Add(Optional("domains on the Mac", () => new DnsServer(IPAddress.Loopback, HostView(() => InterfaceAddress.Of("eth0")), ReadResolvConf, log, ownedZone: DomainNames.InZone).Run(cts.Token)));
        services.Add(new Agent(transport, log, api).Run(cts.Token));
        break;
    case AgentMode.Wsl:
        dialer = new TcpHostDialer(WslHost.Address(Directory.Exists("/sys/class/net/loopback0"), File.ReadAllText("/proc/net/route")), GuestAddresses.RefusedOnHost);
        // The tray on Windows relays to these: only loopback is shared with it, also in mirrored networking.
        services.Add(Optional("domains on this machine", () => new DnsServer(IPAddress.Loopback, HostView(() => GuestAddresses.HostLoopback), ReadResolvConf, log, GuestAddresses.RelayDnsPort, DomainNames.InZone).Run(cts.Token)));
        services.Add(Optional("HTTP on domains from Windows", () => proxy.Serve(DomainProxy.Listen(IPAddress.Loopback, GuestAddresses.RelayHttpPort), tls: false, guarded: false, cts.Token)));
        services.Add(Optional("HTTPS on domains from Windows", () => proxy.Serve(DomainProxy.Listen(IPAddress.Loopback, GuestAddresses.RelayHttpsPort), tls: true, guarded: false, cts.Token)));
        services.Add(ServeWindows());
        services.Add(ExitWithEngine());
        break;
    default:
        dialer = new TcpHostDialer(IPAddress.Loopback, GuestAddresses.RefusedOnHost);
        // systemd-resolved takes a port: 53 on any loopback address could clash with a DNS server already on this machine.
        services.Add(Optional("domains on this machine", () => new DnsServer(IPAddress.Loopback, HostView(() => GuestAddresses.HostLoopback), ReadResolvConf, log, GuestAddresses.RelayDnsPort, DomainNames.InZone).Run(cts.Token)));
        services.Add(AgentApiServer.Unix(api, "/run/dockyard/agent.sock", cts.Token));
        break;
}
services.Add(new HostProxy(dialer, HostProxy.OriginalDestination, log).Run(cts.Token));

// One service failing must end the process, so that init or the loop around it brings all of them back and the reason
// shows. Waiting for all of them would leave a half-working agent running with its failure unobserved.
try
{
    await await Task.WhenAny(services);
}
catch (OperationCanceledException)
{
}
catch (Exception e)
{
    log($"{e.GetType().Name}: {e.Message}");
    return 1;
}
return 0;

// Domains are an extra: a port someone else holds turns that feature off, and containers, DNS and the engine keep working.
async Task Optional(string what, Func<Task> service)
{
    try
    {
        await service();
    }
    catch (SocketException e)
    {
        log($"{what} is off: {e.Message}");
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
}

// Without the engine reachable from Windows nothing works there, so this is not optional: failing ends the agent and the
// loop in dockyard-init starts it again.
async Task ServeWindows()
{
    if (Directory.Exists("/sys/class/net/loopback0"))
    {
        log($"the engine for Windows on 127.0.0.1:{Ports.Docker}");
        await WindowsEngine.Serve(WindowsEngine.Listen(IPAddress.Loopback), IPAddress.IsLoopback, WindowsEngine.ReadToken, Agent.DockerSocket, api, cts.Token);
        return;
    }
    var address = InterfaceAddress.Of("eth0") ?? throw new InvalidOperationException("eth0 has no IPv4 address");
    var windows = WslHost.DefaultGateway(File.ReadAllText("/proc/net/route")) ?? throw new InvalidOperationException("no default gateway");
    log($"the engine for Windows on {address}:{Ports.Docker}, for {windows} only");
    await WindowsEngine.Serve(WindowsEngine.Listen(address), windows.Equals, WindowsEngine.ReadToken, Agent.DockerSocket, api, cts.Token);
}

async Task ExitWithEngine()
{
    // dockerd starts right after this process; give it the time a slow disk needs.
    await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
    while (Process.GetProcessesByName("dockerd").Length > 0) await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
    log("the engine stopped");
    cts.Cancel();
}
