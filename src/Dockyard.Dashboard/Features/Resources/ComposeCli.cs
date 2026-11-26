namespace Dockyard.Dashboard.Features.Resources;

static class ComposeCli
{
    public static string[] Args(string context, ComposeProject project, params string[] verb)
    {
        List<string> args = ["--context", context, "compose", "-p", project.Name];
        // A project whose files were moved or deleted is still the engine's: compose finds its containers by their labels.
        if (project.ConfigFiles.Count > 0 && project.ConfigFiles.All(File.Exists))
        {
            foreach (var f in project.ConfigFiles) args.AddRange(["-f", f]);
            if (project.WorkingDir is { Length: > 0 } dir && Directory.Exists(dir)) args.AddRange(["--project-directory", dir]);
        }
        args.AddRange(verb);
        return [.. args];
    }

    public static string DefaultProjectName(string composeFile)
    {
        var dir = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(composeFile))) ?? "project";
        var name = new string(dir.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray());
        return name.Length > 0 ? name : "project";
    }
}
