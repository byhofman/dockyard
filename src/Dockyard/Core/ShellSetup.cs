namespace Dockyard.Core;

// kubectl as the CKAD exams have people set it up: k, its completion, and $do and $now for dry runs and forced deletes. A
// marked block in the shell's profile, which Dockyard replaces when it changes and takes out when the setting is off.
static class ShellSetup
{
    public const string Begin = "# >>> dockyard kubectl >>>";
    public const string End = "# <<< dockyard kubectl <<<";

    public enum Kind { Bash, Zsh, PowerShell }

    public static string Block(Kind kind) => string.Join('\n', kind switch
    {
        Kind.Bash =>
        [
            Begin,
            "alias k=kubectl",
            "if command -v kubectl >/dev/null 2>&1; then",
            "  source <(kubectl completion bash)",
            "  complete -o default -F __start_kubectl k",
            "fi",
            "export do=\"--dry-run=client -o yaml\"",
            "export now=\"--force --grace-period 0\"",
            End,
        ],
        // zsh does not split a variable into words, so do and now are arrays; do is a reserved word, hence typeset.
        Kind.Zsh =>
        [
            Begin,
            "alias k=kubectl",
            "if command -v kubectl >/dev/null 2>&1; then",
            "  (( $+functions[compdef] )) || { autoload -Uz compinit && compinit -C; }",
            "  source <(kubectl completion zsh)",
            "  compdef __start_kubectl k",
            "fi",
            "typeset -ga do=(--dry-run=client -o yaml)",
            "typeset -ga now=(--force --grace-period 0)",
            End,
        ],
        // A PowerShell array passed to a program becomes separate arguments.
        _ =>
        [
            Begin,
            "Set-Alias -Name k -Value kubectl",
            "if (Get-Command kubectl -ErrorAction SilentlyContinue) {",
            "    kubectl completion powershell | Out-String | Invoke-Expression",
            "    Register-ArgumentCompleter -Native -CommandName k -ScriptBlock $__kubectlCompleterBlock",
            "}",
            "$do = @('--dry-run=client', '-o', 'yaml')",
            "$now = @('--force', '--grace-period', '0')",
            End,
        ],
    }) + "\n";

    // The block in place of the one there was, or at the end; the rest of the file as it was, line endings too.
    public static string With(string? content, Kind kind)
    {
        var without = Without(content ?? "");
        var newline = without.Contains("\r\n") ? "\r\n" : "\n";
        var block = Block(kind).Replace("\n", newline);
        return without.Length == 0 ? block : without.TrimEnd('\r', '\n') + newline + newline + block;
    }

    public static bool HasBrokenBlock(string content) =>
        content.IndexOf(Begin, StringComparison.Ordinal) is var start and >= 0 && content.IndexOf(End, start, StringComparison.Ordinal) < 0;

    public static string Without(string content)
    {
        var start = content.IndexOf(Begin, StringComparison.Ordinal);
        if (start < 0) return content;
        var end = content.IndexOf(End, start, StringComparison.Ordinal);
        if (end < 0) return content;
        end += End.Length;
        while (end < content.Length && content[end] is '\r' or '\n') end++;
        var before = content[..start].TrimEnd('\r', '\n');
        var after = content[end..];
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        return before.Length == 0 ? after : after.Length == 0 ? before + newline : before + newline + newline + after;
    }

    // The profiles each system's shells read: those that exist, and the one its default shell needs.
    public static IEnumerable<(string File, Kind Kind)> Profiles(string home, string documents)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return (Path.Combine(documents, "PowerShell", "Microsoft.PowerShell_profile.ps1"), Kind.PowerShell);
            yield return (Path.Combine(documents, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"), Kind.PowerShell);
            if (File.Exists(Path.Combine(home, ".bashrc"))) yield return (Path.Combine(home, ".bashrc"), Kind.Bash);
            yield break;
        }
        var zshrc = Path.Combine(home, ".zshrc");
        var bashrc = Path.Combine(home, ".bashrc");
        if (OperatingSystem.IsMacOS() || File.Exists(zshrc)) yield return (zshrc, Kind.Zsh);
        if (!OperatingSystem.IsMacOS() || File.Exists(bashrc)) yield return (bashrc, Kind.Bash);
    }

    // As the settings ask for it: on with kubectl, and never into root's profile from a setup run with sudo.
    public static void Apply(Settings settings, Action<string> log)
    {
        if (Environment.UserName == "root") return;
        TryApply(settings.KubectlShell && !settings.RemovedComponents.Contains(nameof(Component.Kubectl)), log);
    }

    public static void TryApply(bool enabled, Action<string> log)
    {
        try { Apply(enabled, log); }
        catch (UnauthorizedAccessException e) when (OperatingSystem.IsWindows() && enabled)
        {
            log($"The shell setup for kubectl was not written ({e.Message}). When Controlled Folder Access is on, allow {Path.GetFileName(Environment.ProcessPath)} under Windows Security > Ransomware protection and switch the setting again, or paste this into your profile yourself:\n{Block(Kind.PowerShell)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"The shell setup for kubectl was left as it is ({e.Message})"); }
    }

    public static void Apply(bool enabled, Action<string> log, string? home = null, string? documents = null)
    {
        home ??= Paths.UserProfile;
        documents ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var (file, kind) in Profiles(home, documents))
        {
            var existing = File.Exists(file) ? File.ReadAllText(file) : null;
            if (existing is not null && HasBrokenBlock(existing))
            {
                log($"{file} has the start of Dockyard's kubectl block without its end ({End}); left as it is");
                continue;
            }
            var updated = enabled ? With(existing, kind) : existing is null ? null : Without(existing);
            if (updated is null || updated == existing) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, updated);
            log(enabled ? $"kubectl set up in {file}: k, completion, $do and $now" : $"Took Dockyard's kubectl setup out of {file}");
        }
    }
}
