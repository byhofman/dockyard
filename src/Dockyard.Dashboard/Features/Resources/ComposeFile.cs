using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dockyard.Dashboard.Features.Resources;

sealed class ComposeFile
{
    ComposeFile(string path, string text, bool crlf, bool bom)
    {
        Path = path;
        Text = text;
        this.crlf = crlf;
        this.bom = bom;
    }

    readonly bool crlf;
    readonly bool bom;

    public string Path { get; }
    public string Text { get; private set; }

    public static ComposeFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = Encoding.UTF8.GetString(bom ? bytes[3..] : bytes);
        return new ComposeFile(path, text.Replace("\r\n", "\n"), text.Contains("\r\n"), bom);
    }

    public bool IsChangedBy(string text) => Normalize(text) != Text;

    public void Save(string text)
    {
        var lf = Normalize(text);
        File.WriteAllText(Path, crlf ? lf.Replace("\n", "\r\n") : lf, new UTF8Encoding(bom));
        Text = lf;
    }

    static string Normalize(string text) => text.Replace("\r\n", "\n");

    public static (int Line, string Message)? Problem(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode)
                return (1, "A compose file is a mapping, with services: at the top level");
            return null;
        }
        catch (YamlException e)
        {
            return ((int)e.Start.Line, e.Message.Split(". ")[0].TrimEnd('.'));
        }
    }

    public static int? LineOf(string composeError)
    {
        var match = System.Text.RegularExpressions.Regex.Match(composeError, @"line (\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var line) ? line : null;
    }

    public static string[] ConfigArgs(string context, ComposeProject project, string replaced)
    {
        List<string> args = ["--context", context, "compose", "-p", project.Name];
        foreach (var f in project.ConfigFiles) args.AddRange(["-f", string.Equals(f, replaced, StringComparison.OrdinalIgnoreCase) ? "-" : f]);
        var dir = project.WorkingDir is { Length: > 0 } wd ? wd : System.IO.Path.GetDirectoryName(replaced);
        if (dir is { Length: > 0 }) args.AddRange(["--project-directory", dir]);
        args.AddRange(["config", "-q"]);
        return [.. args];
    }
}
