using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dockyard.Agent;

// A Windows docker CLI asks for bind mounts by Windows path (docker run -v C:\src:/src, Compose without
// COMPOSE_CONVERT_WINDOWS_PATHS), which the Linux engine cannot use. As Docker Desktop does, such a path becomes /c/src,
// which the engine links to /mnt/c. Only the requests from Windows pass through here, and only a container's creation is
// changed: everything else goes on byte for byte, and whatever is not plain request after request (an upgrade for attach
// or exec, a chunked body) ends the reading and is copied as it comes.
static partial class WindowsPaths
{
    const int MaxHead = 64 * 1024;
    const int MaxBody = 4 * 1024 * 1024;

    public static string? ToLinux(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
            ? $"/{char.ToLowerInvariant(path[0])}/{path[3..].Replace('\\', '/')}".TrimEnd('/') is { Length: > 0 } p && p != $"/{char.ToLowerInvariant(path[0])}" ? p : $"/{char.ToLowerInvariant(path[0])}"
            : null;

    // C:\src:/src:ro: the Windows path is everything up to the first colon after the drive's.
    public static string RewriteBind(string bind)
    {
        if (bind.Length < 3 || !char.IsAsciiLetter(bind[0]) || bind[1] != ':' || bind[2] is not ('\\' or '/')) return bind;
        var end = bind.IndexOf(':', 2);
        return end < 0 ? bind : ToLinux(bind[..end]) + bind[end..];
    }

    public static byte[]? RewriteCreate(byte[] body)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (System.Text.Json.JsonException) { return null; }
        if (root?["HostConfig"] is not JsonObject host) return null;
        var changed = false;
        if (host["Binds"] is JsonArray binds)
            for (var i = 0; i < binds.Count; i++)
                if (binds[i]?.GetValueKind() == System.Text.Json.JsonValueKind.String && binds[i]!.GetValue<string>() is var bind && RewriteBind(bind) is var rewritten && rewritten != bind)
                {
                    binds[i] = rewritten;
                    changed = true;
                }
        if (host["Mounts"] is JsonArray mounts)
            foreach (var mount in mounts.OfType<JsonObject>())
                if (mount["Type"]?.GetValue<string>() == "bind" && mount["Source"]?.GetValue<string>() is { } source && ToLinux(source) is { } linux)
                {
                    mount["Source"] = linux;
                    changed = true;
                }
        return changed ? Encoding.UTF8.GetBytes(root.ToJsonString()) : null;
    }

    public static async Task CopyRequests(Stream from, Stream to, CancellationToken ct)
    {
        while (true)
        {
            var (head, complete) = await ReadHead(from, ct);
            if (head.Length == 0) return;
            if (!complete)
            {
                await to.WriteAsync(head, ct);
                await from.CopyToAsync(to, 64 * 1024, ct);
                return;
            }
            var text = Encoding.Latin1.GetString(head);
            var length = Header(text, "Content-Length") is { } l && long.TryParse(l, out var n) ? n : 0;
            var raw = Header(text, "Upgrade") is not null || Header(text, "Transfer-Encoding") is not null;
            if (!raw && length is > 0 and <= MaxBody && CreateRequest().IsMatch(text))
            {
                var body = new byte[length];
                await from.ReadExactlyAsync(body, ct);
                if (RewriteCreate(body) is { } rewritten)
                {
                    head = Encoding.Latin1.GetBytes(ContentLength().Replace(text, $"Content-Length: {rewritten.Length}\r\n", 1));
                    body = rewritten;
                }
                await to.WriteAsync(head, ct);
                await to.WriteAsync(body, ct);
                continue;
            }
            await to.WriteAsync(head, ct);
            if (raw)
            {
                await from.CopyToAsync(to, 64 * 1024, ct);
                return;
            }
            if (length > 0) await CopyExactly(from, to, length, ct);
        }
    }

    static async Task CopyExactly(Stream from, Stream to, long length, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (length > 0)
        {
            var n = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), ct);
            if (n == 0) throw new EndOfStreamException();
            await to.WriteAsync(buffer.AsMemory(0, n), ct);
            length -= n;
        }
    }

    // Up to and with the empty line; complete is false when the stream ended or the head is not one a client sends.
    static async Task<(byte[] Head, bool Complete)> ReadHead(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHead];
        var length = 0;
        while (length < buffer.Length)
        {
            if (await stream.ReadAsync(buffer.AsMemory(length, 1), ct) == 0) return (buffer[..length], false);
            length++;
            if (length >= 4 && buffer[length - 1] == '\n' && buffer[length - 2] == '\r' && buffer[length - 3] == '\n' && buffer[length - 4] == '\r')
                return (buffer[..length], true);
        }
        return (buffer[..length], false);
    }

    static string? Header(string head, string name)
    {
        foreach (var line in head.Split("\r\n").Skip(1))
            if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) return line[(name.Length + 1)..].Trim();
        return null;
    }

    [GeneratedRegex(@"^POST (/v[\d.]+)?/containers/create(\?[^ ]*)? HTTP/1\.[01]\r\n")]
    private static partial Regex CreateRequest();

    [GeneratedRegex(@"(?im)^Content-Length:[^\r\n]*\r\n")]
    private static partial Regex ContentLength();
}
