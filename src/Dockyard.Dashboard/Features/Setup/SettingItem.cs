using Dockyard.Dashboard.Shell;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Features.Setup;

// One setting definition with its current value, and what changing it is doing or did.
sealed class SettingItem : Observable
{
    readonly Func<SettingItem, string, Task>? change;
    string value;
    string? status, error;

    public SettingItem(SettingDefinition definition, string value, Func<SettingItem, string, Task>? change = null, Func<SettingItem, Task>? edit = null)
    {
        Definition = definition;
        this.value = value;
        this.change = change;
        EditCommand = new Command(() => edit?.Invoke(this) ?? Task.CompletedTask);
    }

    public SettingDefinition Definition { get; }
    public Command EditCommand { get; }
    public string EditText => "Edit daemon.json";

    public string Value
    {
        get => value;
        set
        {
            // One change at a time: the editor is put back while the last one is still applying.
            if (IsWorking) { Raise(nameof(Value)); return; }
            if (value is null || !Set(ref this.value, value)) return;
            if (change is not null) _ = change(this, value);
        }
    }

    // Without running the change, for what the facade reports back.
    public void Show(string current) { value = current; Raise(nameof(Value)); }

    public string? Status { get => status; set { if (Set(ref status, value)) Raise(nameof(IsWorking)); } }
    public bool IsWorking => status is not null;
    public string? Error { get => error; set => Set(ref error, value); }
}
