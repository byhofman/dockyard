using System.Diagnostics;

namespace Dockyard.Cli;

// dockyard config edit: the engine's settings in the user's editor, checked by the Toolbox, until they are valid or left
// unchanged, then applied by the Toolbox.
static class ConfigEdit
{
    public static int Run(IReadOnlyList<string> args)
    {
        var engine = Arguments.Option(args, "--engine");
        string[] Engine() => engine is null ? [] : ["--engine", engine];
        var lines = new List<string>();
        var errors = new List<string>();
        if (Toolbox.Run(["config", "show", .. Engine()], lines.Add, errors.Add) != 0)
        {
            foreach (var e in errors) Console.Error.WriteLine(e);
            return 1;
        }
        var current = string.Join('\n', lines) + "\n";
        var file = Path.Combine(Path.GetTempPath(), $"dockyard-{engine ?? "engine"}-daemon.json");
        File.WriteAllText(file, current);
        try
        {
            while (true)
            {
                OpenEditor(file);
                var edited = File.ReadAllText(file);
                if (edited.Trim() == current.Trim())
                {
                    Console.WriteLine("No changes");
                    return 0;
                }
                var problems = new List<string>();
                if (Toolbox.Run(["config", "validate", .. Engine(), "--file", file], _ => { }, problems.Add) == 0)
                    return Toolbox.Run(["config", "apply", .. Engine(), "--file", file], Console.WriteLine, Console.Error.WriteLine);
                foreach (var p in problems) Console.Error.WriteLine(p);
                Console.Error.Write("Edit it again? [Y/n] ");
                if (Console.ReadLine()?.Trim().ToLowerInvariant() is "n" or "no") return 1;
            }
        }
        finally { File.Delete(file); }
    }

    static void OpenEditor(string file)
    {
        var editor = Environment.GetEnvironmentVariable("VISUAL") is { Length: > 0 } visual ? visual
            : Environment.GetEnvironmentVariable("EDITOR") is { Length: > 0 } set ? set
            : null;
        (string exe, string[] args) = editor is not null ? (editor, new[] { file })
            : OperatingSystem.IsWindows() ? ("notepad.exe", [file])
            : OperatingSystem.IsMacOS() ? ("/usr/bin/open", ["-W", "-t", file])
            : ("nano", [file]);
        // An editor given with options, as "code --wait", is run by the shell.
        if (editor is not null && editor.Contains(' '))
            (exe, args) = OperatingSystem.IsWindows() ? ("cmd.exe", new[] { "/c", $"{editor} \"{file}\"" }) : ("/bin/sh", new[] { "-c", $"{editor} '{file.Replace("'", "'\\''")}'" });
        using var process = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false }) ?? throw new InvalidOperationException($"Could not start {exe}");
        process.WaitForExit();
    }
}
