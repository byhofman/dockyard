namespace Dockyard.Core;

// Marked blocks in the shell's profiles, which Dockyard replaces when they change and takes out when their setting is off:
// kubectl as the CKAD exams have people set it up (k, its completion, and $do and $now for dry runs and forced deletes),
// which comes with kubectl; and dockctx, a shell pinned to one kube context, which comes with the dockyard command.
static class ShellSetup
{
    public enum Part { Kubectl, Dockctx }

    public static string Begin(Part part) => $"# >>> dockyard {Name(part)} >>>";
    public static string End(Part part) => $"# <<< dockyard {Name(part)} <<<";
    public static string Name(Part part) => part == Part.Kubectl ? "kubectl" : "dockctx";

    public enum Kind { Bash, Zsh, PowerShell }

    // How each shell uses them: PowerShell splats the array (kubectl run web --image nginx @do), bash and zsh expand it.
    public static string DoAndNow(Kind kind) => kind == Kind.PowerShell ? "@do and @now" : "$do and $now";
    public static string DoAndNowHere => DoAndNow(OperatingSystem.IsWindows() ? Kind.PowerShell : Kind.Bash);

    public static string Block(Part part, Kind kind) => part == Part.Kubectl ? KubectlBlock(kind) : string.Join('\n',
        Begin(part), kind == Kind.PowerShell ? "function dockctx { dockyard ctx @args }" : "alias dockctx='dockyard ctx'", End(part)) + "\n";

    static string KubectlBlock(Kind kind) => string.Join('\n', kind switch
    {
        Kind.Bash =>
        [
            Begin(Part.Kubectl),
            "alias k=kubectl",
            "if command -v kubectl >/dev/null 2>&1; then",
            "  source <(kubectl completion bash)",
            "  complete -o default -F __start_kubectl k",
            "fi",
            "export do=\"--dry-run=client -o yaml\"",
            "export now=\"--force --grace-period 0\"",
            End(Part.Kubectl),
        ],
        // zsh does not split a variable into words, so do and now are arrays; do is a reserved word, hence typeset.
        Kind.Zsh =>
        [
            Begin(Part.Kubectl),
            "alias k=kubectl",
            "if command -v kubectl >/dev/null 2>&1; then",
            "  (( $+functions[compdef] )) || { autoload -Uz compinit && compinit -C; }",
            "  source <(kubectl completion zsh)",
            "  compdef __start_kubectl k",
            "fi",
            "typeset -ga do=(--dry-run=client -o yaml)",
            "typeset -ga now=(--force --grace-period 0)",
            End(Part.Kubectl),
        ],
        // A PowerShell array passed to a program becomes separate arguments.
        _ =>
        [
            Begin(Part.Kubectl),
            "Set-Alias -Name k -Value kubectl",
            "if (Get-Command kubectl -ErrorAction SilentlyContinue) {",
            "    kubectl completion powershell | Out-String | Invoke-Expression",
            "    Register-ArgumentCompleter -Native -CommandName k -ScriptBlock $__kubectlCompleterBlock",
            "}",
            "$do = @('--dry-run=client', '-o', 'yaml')",
            "$now = @('--force', '--grace-period', '0')",
            End(Part.Kubectl),
        ],
    }) + "\n";

    // The block in place of the one there was, or at the end; the rest of the file as it was, line endings too.
    public static string With(string? content, Part part, Kind kind)
    {
        var without = Without(content ?? "", part);
        var newline = without.Contains("\r\n") ? "\r\n" : "\n";
        var block = Block(part, kind).Replace("\n", newline);
        return without.Length == 0 ? block : without.TrimEnd('\r', '\n') + newline + newline + block;
    }

    // The profiles that should have the block and do not.
    public static IEnumerable<string> Missing(Part part, string? home = null, string? documents = null)
    {
        home ??= Paths.UserProfile;
        documents ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var (file, _) in Profiles(home, documents))
        {
            string? content = null;
            try { content = File.Exists(file) ? File.ReadAllText(file) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (!HasBlock(content, part)) yield return file;
        }
    }

    static bool HasBlock(string? content, Part part) =>
        content is not null && content.IndexOf(Begin(part), StringComparison.Ordinal) is var start and >= 0 && content.IndexOf(End(part), start, StringComparison.Ordinal) >= 0;

    public static bool HasBrokenBlock(string content, Part part) =>
        content.IndexOf(Begin(part), StringComparison.Ordinal) is var start and >= 0 && content.IndexOf(End(part), start, StringComparison.Ordinal) < 0;

    public static string Without(string content, Part part)
    {
        var start = content.IndexOf(Begin(part), StringComparison.Ordinal);
        if (start < 0) return content;
        var end = content.IndexOf(End(part), start, StringComparison.Ordinal);
        if (end < 0) return content;
        end += End(part).Length;
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

    public static void TryApply(Part part, bool enabled, Action<string> log)
    {
        try { Apply(part, enabled, log); }
        catch (UnauthorizedAccessException e) when (OperatingSystem.IsWindows() && enabled)
        {
            log($"The shell setup for {Name(part)} was not written ({e.Message}). When Controlled Folder Access is on, allow {Path.GetFileName(Environment.ProcessPath)} under Windows Security > Ransomware protection and switch the setting again, or paste this into your profile yourself:\n{Block(part, Kind.PowerShell)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"The shell setup for {Name(part)} was left as it is ({e.Message})"); }
    }

    public static void Apply(Part part, bool enabled, Action<string> log, string? home = null, string? documents = null)
    {
        home ??= Paths.UserProfile;
        documents ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var (file, kind) in Profiles(home, documents))
        {
            var existing = File.Exists(file) ? File.ReadAllText(file) : null;
            if (existing is not null && HasBrokenBlock(existing, part))
            {
                log($"{file} has the start of Dockyard's {Name(part)} block without its end ({End(part)}); left as it is");
                continue;
            }
            var updated = enabled ? With(existing, part, kind) : existing is null ? null : Without(existing, part);
            if (updated is null || updated == existing) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, updated);
            var what = part == Part.Kubectl ? $"kubectl set up in {file}: k, completion, {DoAndNow(kind)}" : $"dockctx set up in {file}";
            log(enabled ? what : $"Took Dockyard's {Name(part)} setup out of {file}");
        }
    }
}
