using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Dockyard.Agent;

// struct sockaddr_vm: family(2) reserved(2) port(4) cid(4) flags(1) zero(3)
sealed class VsockEndPoint(uint cid, uint port) : EndPoint
{
    public const AddressFamily VsockFamily = (AddressFamily)40;
    public const uint AnyCid = 0xFFFFFFFF;
    const int Size = 16;

    public uint Cid { get; } = cid;
    public uint Port { get; } = port;
    public override AddressFamily AddressFamily => VsockFamily;

    public override SocketAddress Serialize()
    {
        var address = new SocketAddress(VsockFamily, Size);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Port);
        for (var i = 0; i < 4; i++) address[4 + i] = bytes[i];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Cid);
        for (var i = 0; i < 4; i++) address[8 + i] = bytes[i];
        return address;
    }

    public override EndPoint Create(SocketAddress socketAddress)
    {
        Span<byte> bytes = stackalloc byte[8];
        for (var i = 0; i < 8; i++) bytes[i] = socketAddress[4 + i];
        return new VsockEndPoint(BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes));
    }

    public override string ToString() => $"vsock:{Cid}:{Port}";
}

interface ITransport
{
    Socket Listen(int port);
}

sealed class VsockTransport : ITransport
{
    public Socket Listen(int port)
    {
        var socket = new Socket(VsockEndPoint.VsockFamily, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new VsockEndPoint(VsockEndPoint.AnyCid, (uint)port));
        socket.Listen(64);
        return socket;
    }
}

sealed class TcpTransport : ITransport
{
    public Socket Listen(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        socket.Listen(64);
        return socket;
    }
}

static class Pump
{
    // Copies both ways. When one side stops sending, its end-of-stream is passed on (half-close) and the
    // other direction keeps flowing until it ends too: attach/exec and HTTP/1.0 clients rely on that.
    public static async Task Both(Socket a, Socket b, CancellationToken ct)
    {
        using var aStream = new NetworkStream(a, ownsSocket: false);
        using var bStream = new NetworkStream(b, ownsSocket: false);
        await Task.WhenAll(Copy(aStream, bStream, b, ct), Copy(bStream, aStream, a, ct));
    }

    static async Task Copy(Stream from, Stream to, Socket toSocket, CancellationToken ct)
    {
        try { await from.CopyToAsync(to, 64 * 1024, ct); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or OperationCanceledException) { }
        try { toSocket.Shutdown(SocketShutdown.Send); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }
}
