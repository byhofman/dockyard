using System.Text.Json;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;

namespace Dockyard.Dashboard.Features.Engine;

// The connected engine's daemon.json, edited here and checked and applied by the dockyard command, which hands the
// restart to the tray.
sealed class EngineSettingsViewModel : SectionViewModel
{
    string text = "";
    string original = "";
    string? loadedFor;
    string? checkResult;
    bool checkFailed;

    public EngineSettingsViewModel(ShellViewModel shell, EngineSession session) : base(shell, session)
    {
        ApplyCommand = new Command(Apply, () => IsDirty && Problem is null && !IsBusy && HasCli);
        CheckCommand = new Command(Check, () => Problem is null && !IsBusy && HasCli);
        RevertCommand = new Command(() => { Text = original; return Task.CompletedTask; }, () => IsDirty && !IsBusy);
        ReloadCommand = new Command(() => Load(force: true));
        session.Connected += () => { if (loadedFor is not null && loadedFor != Engine) _ = Load(force: false); };
    }

    public Command ApplyCommand { get; }
    public Command CheckCommand { get; }
    public Command RevertCommand { get; }
    public Command ReloadCommand { get; }

    public static bool HasCli => DockyardCli.IsThere;
    public string Engine => Session.IsWindowsEngine ? "win" : "linux";
    public string EngineTitle => Session.IsWindowsEngine ? "Windows engine" : "Linux engine";

    public string Text
    {
        get => text;
        set
        {
            if (!Set(ref text, value)) return;
            CheckResult = null;
            Raise(nameof(IsDirty), nameof(Problem), nameof(HasProblem), nameof(Notes), nameof(HasNotes));
            Refresh();
        }
    }

    public bool IsDirty => text != original;

    // Straight away, as the text changes; what dockerd itself says comes with Check.
    public string? Problem
    {
        get
        {
            try
            {
                using var document = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
                return document.RootElement.ValueKind == JsonValueKind.Object ? null : "daemon.json has to be one JSON object, in { }";
            }
            catch (JsonException e) { return $"Line {(e.LineNumber ?? 0) + 1}, column {(e.BytePositionInLine ?? 0) + 1}: {e.Message.Split(". ")[0].TrimEnd('.')}"; }
        }
    }

    public bool HasProblem => Problem is not null;

    public string Notes => string.Join(" ", OwnKeys().Select(k => $"Dockyard sets \"{k}\" itself, so yours is replaced."));
    public bool HasNotes => Notes.Length > 0;

    IEnumerable<string> OwnKeys()
    {
        if (Problem is not null) return [];
        using var document = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        string[] own = Session.IsWindowsEngine ? ["hosts"] : ["dns"];
        return own.Where(k => document.RootElement.TryGetProperty(k, out _)).ToList();
    }

    public string Explanation => Session.IsWindowsEngine
        ? "C:\\ProgramData\\docker\\config\\daemon.json. Dockyard keeps \"hosts\" on its pipe and adds you and docker-users to \"group\". Applying asks for administrator approval and restarts the engine if it runs."
        : "Dockyard adds \"dns\", which container domains need, and this computer's proxy when the file names none. Applying restarts the engine if it runs, which stops containers without a restart policy; one that does not start with the new settings gets the previous ones back.";

    public string? CheckResult { get => checkResult; private set { if (Set(ref checkResult, value)) Raise(nameof(HasCheckResult)); } }
    public bool HasCheckResult => checkResult is not null;
    public bool CheckFailed { get => checkFailed; private set => Set(ref checkFailed, value); }

    public override void OnShown() => _ = Load(force: false);

    protected override void ApplyFilter() { }

    public override Task Reload() => Load(force: false);

    async Task Load(bool force)
    {
        if (!HasCli || (!force && loadedFor == Engine && IsDirty)) return;
        var engine = Engine;
        var result = await DockyardCli.Run(["config", "show", "--engine", engine]);
        if (!result.Ok)
        {
            Shell.ShowError($"The {EngineTitle.ToLowerInvariant()}'s settings: {result.Message}");
            return;
        }
        loadedFor = engine;
        original = result.Output.TrimEnd() + "\n";
        Text = original;
        Raise(nameof(Engine), nameof(EngineTitle), nameof(Explanation), nameof(IsDirty));
        Refresh();
    }

    Task Check() => Run("Checking", async () =>
    {
        var result = await DockyardCli.Run(["config", "validate", "--engine", Engine, "--file", "-"], text);
        CheckFailed = !result.Ok;
        CheckResult = result.Message;
    });

    Task Apply()
    {
        var engine = Engine;
        var json = text;
        Shell.Open(new Dialog($"Apply to the {EngineTitle.ToLowerInvariant()}?", Explanation, "Apply", async d =>
        {
            d.Report("Checking and applying");
            var result = await DockyardCli.Run(["config", "apply", "--engine", engine, "--file", "-"], json, line => Avalonia.Threading.Dispatcher.UIThread.Post(() => d.Report(line)));
            if (!result.Ok) throw new InvalidOperationException(result.Message);
            original = json;
            Raise(nameof(IsDirty));
            Refresh();
            Shell.ShowInfo($"The {EngineTitle.ToLowerInvariant()} has its new settings");
        }));
        return Task.CompletedTask;
    }

    void Refresh()
    {
        ApplyCommand.Refresh();
        CheckCommand.Refresh();
        RevertCommand.Refresh();
    }
}
