using Dockyard.Agent;

var options = AgentOptions.FromCmdline(File.Exists("/proc/cmdline") ? File.ReadAllText("/proc/cmdline") : "");
ITransport transport = options.Tcp ? new TcpTransport() : new VsockTransport();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var agent = new Agent(transport, line => Console.Error.WriteLine($"dockyard-agent: {line}"));
try
{
    await agent.Run(cts.Token);
}
catch (System.Net.Sockets.SocketException)
{
    return 1;
}
catch (OperationCanceledException)
{
}
return 0;
