using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dockyard.Agent;

interface IConnection : IDisposable
{
    Stream Stream { get; }
    void ShutdownSend();
}

interface IListener : IDisposable
{
    Task<IConnection> AcceptAsync(CancellationToken ct);
}

interface ITransport
{
    IListener Listen(int port);
}

sealed class SocketConnection(Socket socket) : IConnection
{
    readonly NetworkStream stream = new(socket, ownsSocket: true);
    public Stream Stream => stream;

    public void ShutdownSend()
    {
        try { socket.Shutdown(SocketShutdown.Send); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    public void Dispose() => stream.Dispose();
}

sealed class TcpTransport : ITransport
{
    public IListener Listen(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        socket.Listen(64);
        return new Listener(socket);
    }

    sealed class Listener(Socket socket) : IListener
    {
        public async Task<IConnection> AcceptAsync(CancellationToken ct) => new SocketConnection(await socket.AcceptAsync(ct));
        public void Dispose() => socket.Dispose();
    }
}

// .NET cannot build a SocketAddress for AF_VSOCK on Unix, so vsock sockets go straight through libc.
sealed partial class VsockTransport : ITransport
{
    const int AfVsock = 40;
    const int SockStream = 1;
    const int ShutWr = 1;
    const int ShutRdWr = 2;
    public const uint AnyCid = 0xFFFFFFFF;

    public IListener Listen(int port)
    {
        var fd = socket(AfVsock, SockStream, 0);
        if (fd < 0) throw new SocketException(Marshal.GetLastPInvokeError());
        var address = Address(AnyCid, (uint)port);
        if (bind(fd, address, address.Length) != 0 || listen(fd, 64) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            close(fd);
            throw new SocketException(error);
        }
        return new Listener(fd);
    }

    // struct sockaddr_vm (Linux): family(2) reserved(2) port(4) cid(4) flags(1) zero(3)
    internal static byte[] Address(uint cid, uint port)
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 2), (ushort)AfVsock);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), port);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 4), cid);
        return bytes;
    }

    sealed class Listener(int fd) : IListener
    {
        public async Task<IConnection> AcceptAsync(CancellationToken ct)
        {
            using var registration = ct.Register(() => shutdown(fd, ShutRdWr));
            var client = await Task.Run(() => accept(fd, 0, 0), CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if (client < 0) throw new SocketException(Marshal.GetLastPInvokeError());
            return new FdConnection(client);
        }

        public void Dispose()
        {
            shutdown(fd, ShutRdWr);
            close(fd);
        }
    }

    sealed class FdConnection(int fd) : IConnection
    {
        public Stream Stream { get; } = new FileStream(new SafeFileHandle(fd, ownsHandle: true), FileAccess.ReadWrite, bufferSize: 0);
        public void ShutdownSend() => shutdown(fd, ShutWr);
        public void Dispose() => Stream.Dispose();
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int socket(int domain, int type, int protocol);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int bind(int fd, byte[] address, int length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int listen(int fd, int backlog);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int accept(int fd, nint address, nint length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int shutdown(int fd, int how);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
}

static class Pump
{
    // Copies both ways. When one side stops sending, its end-of-stream is passed on (half-close) and the
    // other direction keeps flowing until it ends too: attach/exec and HTTP/1.0 clients rely on that.
    public static Task Both(IConnection a, IConnection b, CancellationToken ct) =>
        Task.WhenAll(Copy(a, b, ct), Copy(b, a, ct));

    static async Task Copy(IConnection from, IConnection to, CancellationToken ct)
    {
        try { await from.Stream.CopyToAsync(to.Stream, 64 * 1024, ct); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or OperationCanceledException) { }
        to.ShutdownSend();
    }
}
