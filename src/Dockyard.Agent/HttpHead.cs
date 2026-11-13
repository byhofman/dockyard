using System.Text;

namespace Dockyard.Agent;

static class HttpHead
{
    public static string? Host(byte[] head)
    {
        foreach (var line in Encoding.ASCII.GetString(head).Split("\r\n"))
        {
            if (!line.StartsWith("host:", StringComparison.OrdinalIgnoreCase)) continue;
            var host = line[5..].Trim().ToLowerInvariant();
            var colon = host.LastIndexOf(':');
            return colon > 0 ? host[..colon] : host;
        }
        return null;
    }

    public static byte[] WithForwarded(byte[] head, string scheme, string host)
    {
        var text = Encoding.Latin1.GetString(head);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return Encoding.Latin1.GetBytes(text[..end] + $"\r\nX-Forwarded-Proto: {scheme}\r\nX-Forwarded-Host: {host}" + text[end..]);
    }
}
