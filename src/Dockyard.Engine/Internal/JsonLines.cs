using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dockyard.Engine.Internal;

static class JsonLines
{
    public static async IAsyncEnumerable<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return JsonSerializer.Deserialize(line, type)!;
        }
    }
}
