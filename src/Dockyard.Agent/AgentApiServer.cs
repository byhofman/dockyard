using System.Net.Sockets;

namespace Dockyard.Agent;

static class AgentApiServer
{
    public static async Task Unix(AgentApi api, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(16);
        // The same people who may use the engine, whose socket gives them far more than this one does.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        await Agent.Exit("chgrp", ["docker", path]);
        await Accept(listener, api, ct);
    }

    static async Task Accept(Socket listener, AgentApi api, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = await listener.AcceptAsync(ct);
            _ = Task.Run(async () =>
            {
                using var connection = new SocketConnection(client);
                try { await api.Handle(connection.Stream, ct); }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
            }, ct);
        }
    }
}
