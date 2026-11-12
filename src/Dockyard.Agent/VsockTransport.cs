using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dockyard.Agent;

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

    public static async Task<IConnection> Connect(uint cid, uint port, CancellationToken ct)
    {
        var fd = socket(AfVsock, SockStream, 0);
        if (fd < 0) throw new SocketException(Marshal.GetLastPInvokeError());
        var address = Address(cid, port);
        using var registration = ct.Register(() => shutdown(fd, ShutRdWr));
        var result = await Task.Run(() => connect(fd, address, address.Length), CancellationToken.None);
        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            close(fd);
            throw new SocketException(error);
        }
        return new FdConnection(fd);
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
    private static partial int connect(int fd, byte[] address, int length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int listen(int fd, int backlog);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int accept(int fd, nint address, nint length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int shutdown(int fd, int how);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
}
