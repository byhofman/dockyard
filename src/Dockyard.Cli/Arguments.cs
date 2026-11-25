namespace Dockyard.Cli;

sealed class UsageException(string message) : Exception(message);

static class Arguments
{
    public const string InlinePrefix = "inline:";

    public static string? Option(IReadOnlyList<string> args, string name)
    {
        var at = args.ToList().IndexOf(name);
        if (at < 0) return null;
        return at + 1 < args.Count ? args[at + 1] : throw new UsageException($"{name} needs a value");
    }

    // The Toolbox may run elsewhere than this terminal, with another working directory and no stdin: the file goes along.
    public static IReadOnlyList<string> InlineFile(IReadOnlyList<string> args, Func<string, string>? read = null)
    {
        var list = args.ToList();
        var at = list.IndexOf("--file");
        if (at < 0 || at + 1 >= list.Count || list[at + 1].StartsWith(InlinePrefix, StringComparison.Ordinal)) return list;
        var file = list[at + 1];
        var text = read?.Invoke(file) ?? (file == "-" ? Console.In.ReadToEnd() : File.ReadAllText(file));
        list[at + 1] = InlinePrefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        return list;
    }
}
