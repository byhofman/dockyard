using System.Text.Json;
using Dockyard.Dashboard.Shell;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Features.Setup;

// A row per setting definition. A change applies at once; an engine's daemon.json is edited in a dialog and applied there.
sealed class SettingsViewModel : Observable
{
    readonly ISetupSource source;
    readonly ShellViewModel? shell;

    public SettingsViewModel(ISetupSource source, ShellViewModel? shell = null)
    {
        this.source = source;
        this.shell = shell;
        Items = source.SettingDefinitions
            .Select(d => new SettingItem(d, d.Kind == SettingKind.EngineFile ? "" : source.GetSetting(d.Key), Change, Edit))
            .ToList();
    }

    public IReadOnlyList<SettingItem> Items { get; }

    async Task Change(SettingItem item, string value)
    {
        item.Error = null;
        item.Status = item.Definition.NeedsAdmin ? "Waiting for your approval…" : "Applying…";
        using var working = shell?.Activity.Working(item.Definition.Title);
        try
        {
            await source.SetSetting(item.Definition.Key, value, new Progress(item, shell));
        }
        catch (Exception ex)
        {
            item.Error = ex.Message;
            item.Show(source.GetSetting(item.Definition.Key));
        }
        finally { item.Status = null; }
    }

    Task Edit(SettingItem item)
    {
        string text;
        try { text = source.GetSetting(item.Definition.Key); }
        catch (InvalidOperationException ex) { shell?.ShowError(ex.Message); return Task.CompletedTask; }
        var field = DialogField.Code("daemon.json", text, Dockyard.Shared.CodeLanguage.Json, JsonProblem);
        shell?.Open(new Dialog(item.Definition.Title, item.Definition.Description, "Apply",
            d => source.SetSetting(item.Definition.Key, d.Fields[0].Text, new DialogProgress(d)), fields: [field]));
        return Task.CompletedTask;
    }

    static string? JsonProblem(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object ? null : "daemon.json holds one JSON object, in { }.";
        }
        catch (JsonException ex) { return ex.Message; }
    }

    sealed class Progress(SettingItem item, ShellViewModel? shell) : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value)
        {
            shell?.Activity.Note(value.Line, value.Kind == SetupProgressKind.Error ? ActivityKind.Error : ActivityKind.Detail);
            if (value.Kind == SetupProgressKind.Info) item.Status = value.Line;
        }
    }

    sealed class DialogProgress(Dialog dialog) : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value) => dialog.Report(value.Line);
    }
}
