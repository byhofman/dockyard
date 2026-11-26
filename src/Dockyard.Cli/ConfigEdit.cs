using System.Diagnostics;
using Dockyard.Setup;
using Spectre.Console;

namespace Dockyard.Cli;

// An engine's settings in the user's editor, checked by the library until they are valid or left unchanged, then applied.
static class ConfigEdit
{
    public static async Task<int> Run(DockyardSetup setup, string engine, CliEnvironment env)
    {
        var key = SettingDefinitions.EngineFilePrefix + engine;
        var current = setup.GetSetting(key).TrimEnd() + "\n";
        var file = Path.Combine(Path.GetTempPath(), $"dockyard-{engine}-daemon.json");
        File.WriteAllText(file, current);
        try
        {
            while (true)
            {
                OpenEditor(file);
                var edited = File.ReadAllText(file);
                if (edited.Trim() == current.Trim())
                {
                    env.Console.WriteLine("No changes");
                    return 0;
                }
                if (await setup.CheckSetting(key, edited) is not { } problem)
                {
                    await setup.SetSetting(key, edited, new ProgressRenderer(env.Console, env.Error));
                    return 0;
                }
                env.Error.WriteLine(problem);
                if (!env.HasTerminal) return 1;
                env.Error.Write("Edit it again? [Y/n] ");
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
