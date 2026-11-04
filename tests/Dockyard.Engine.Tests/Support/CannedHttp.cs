using System.Text;

namespace Dockyard.Engine.Tests.Support;

static class CannedHttp
{
    const string Response =
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nApi-Version: 1.47\r\nConnection: close\r\n\r\nOK";

    public static async Task ServeOnce(Stream stream)
    {
        var buffer = new byte[4096];
        var received = new StringBuilder();
        while (!received.ToString().Contains("\r\n\r\n"))
        {
            var n = await stream.ReadAsync(buffer);
            if (n == 0) return;
            received.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(Response));
        await stream.FlushAsync();
    }
}
