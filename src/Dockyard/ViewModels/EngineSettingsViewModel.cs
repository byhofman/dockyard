using Avalonia.Threading;
using Dockyard.Core;
using Dockyard.Hosts;
using Dockyard.Shared;

namespace Dockyard.ViewModels;

// One engine's daemon.json: checked as it is typed, by dockerd itself shortly after, and applied with the engine restarted.
sealed class EngineSettingsViewModel : Observable
{
    readonly IEngineHost host;
    readonly Action<string> log;
    readonly DispatcherTimer settle = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    string text = "";
    string original = "";
    string? busy;
    string? verdict;
    bool verdictOk;
    int checkRun;
    IReadOnlyList<DaemonJsonSchema.Issue> issues = [];

    public EngineSettingsViewModel(IEngineHost host, EngineInfo engine, Action<string> log)
    {
        this.host = host;
        this.log = log;
        Engine = engine;
        ApplyCommand = new Command(Apply, () => busy is null && IsDirty && !HasErrors);
        RevertCommand = new Command(() => { Text = original; return Task.CompletedTask; }, () => busy is null && IsDirty);
        Snippets = DaemonJsonSchema.Snippets(engine.Id).Select(s => new SnippetItem(s.Title, new Command(() => { AddSnippet(s); return Task.CompletedTask; }))).ToList();
        Completions = () => DaemonJsonSchema.For(engine.Id)
            .Where(k => !text.Contains($"\"{k.Name}\"", StringComparison.Ordinal))
            .Select(k => new CodeCompletion(k.Name, k.Description, $"\"{k.Name}\": {k.Example}"))
            .ToList();
        settle.Tick += async (_, _) =>
        {
            settle.Stop();
            await AskDockerd();
        };
    }

    public sealed record SnippetItem(string Title, Command Command);

    public EngineInfo Engine { get; }
    public string Title => $"{Engine.Name} engine · daemon.json";
    public string Explanation => Engine.Id == EngineSettings.Windows
        ? "C:\\ProgramData\\docker\\config\\daemon.json. Dockyard keeps \"hosts\" on its pipe and adds you and docker-users to \"group\". Applying asks for administrator approval and restarts the engine if it runs."
        : "Dockyard adds \"dns\", which container domains need, and this computer's proxy when the file names none. Applying restarts the engine if it runs, which stops containers without a restart policy; one that does not start with the new settings gets the previous ones back.";

    public IReadOnlyList<SnippetItem> Snippets { get; }
    public Func<IReadOnlyList<CodeCompletion>> Completions { get; }
    public Command ApplyCommand { get; }
    public Command RevertCommand { get; }

    public string Text
    {
        get => text;
        set
        {
            if (!Set(ref text, value)) return;
            Issues = DaemonJsonSchema.Check(Engine.Id, text);
            Verdict = null;
            settle.Stop();
            if (!HasErrors) settle.Start();
            Raise(nameof(IsDirty));
            Refresh();
        }
    }

    public bool IsDirty => text != original;

    public IReadOnlyList<DaemonJsonSchema.Issue> Issues
    {
        get => issues;
        private set
        {
            issues = value;
            Raise(nameof(Issues), nameof(IssueLines), nameof(HasErrors), nameof(IssueTexts), nameof(HasIssues));
        }
    }

    public bool HasErrors => issues.Any(i => i.Severity == DaemonJsonSchema.Severity.Error);
    public IReadOnlyList<int> IssueLines => issues.Where(i => i.Line > 0).Select(i => i.Line).Distinct().ToList();
    public IReadOnlyList<string> IssueTexts => issues.Select(i => (i.Line > 0 ? $"Line {i.Line}: " : "") + i.Message).ToList();
    public bool HasIssues => issues.Count > 0;

    public string? Verdict { get => verdict; private set { if (Set(ref verdict, value)) Raise(nameof(HasVerdict)); } }
    public bool HasVerdict => verdict is not null;
    public bool VerdictOk { get => verdictOk; private set => Set(ref verdictOk, value); }

    public string? Busy { get => busy; private set { if (Set(ref busy, value)) { Raise(nameof(IsBusy)); Refresh(); } } }
    public bool IsBusy => busy is not null;

    public async Task Load()
    {
        var current = await host.ReadEngineSettings(Engine.Id) ?? "{}";
        original = current.TrimEnd() + "\n";
        Text = original;
    }

    void AddSnippet(DaemonJsonSchema.Snippet snippet)
    {
        if (DaemonJsonSchema.Add(text, snippet) is { } merged) Text = merged;
    }

    // dockerd checks the file without starting; a check overtaken by more typing has nothing to say.
    async Task AskDockerd()
    {
        var run = ++checkRun;
        var json = text;
        try
        {
            var problem = await Task.Run(() => host.ValidateEngineSettings(Engine.Id, json));
            if (run != checkRun) return;
            VerdictOk = problem is null;
            Verdict = problem ?? "dockerd accepts these settings";
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            if (run != checkRun) return;
            VerdictOk = false;
            Verdict = $"dockerd could not check them: {e.Message}";
        }
    }

    async Task Apply()
    {
        var json = text;
        Busy = "Applying";
        try
        {
            if (await host.ValidateEngineSettings(Engine.Id, json) is { } problem)
            {
                VerdictOk = false;
                Verdict = problem;
                return;
            }
            await host.ApplyEngineSettings(line => { log(line); Dispatcher.UIThread.Post(() => Busy = line); }, Engine.Id, json);
            original = json;
            VerdictOk = true;
            Verdict = $"The {Engine.Name} engine has these settings";
            Raise(nameof(IsDirty));
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            VerdictOk = false;
            Verdict = e.Message;
            log(e.Message);
        }
        finally { Busy = null; }
    }

    void Refresh()
    {
        ApplyCommand.Refresh();
        RevertCommand.Refresh();
    }
}
