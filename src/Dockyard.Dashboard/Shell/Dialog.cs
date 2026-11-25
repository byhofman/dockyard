using Avalonia.Threading;

namespace Dockyard.Dashboard.Shell;

sealed class DialogField : Observable
{
    string text;
    string? choice;
    bool isChecked;

    public DialogField(string label, string text = "", string placeholder = "", IReadOnlyList<string>? choices = null)
    {
        Label = label;
        this.text = text;
        Placeholder = placeholder;
        Choices = choices;
        choice = choices?.FirstOrDefault();
    }

    DialogField(string label, bool isChecked) : this(label)
    {
        IsCheck = true;
        this.isChecked = isChecked;
    }

    public static DialogField Check(string label, bool isChecked = false) => new(label, isChecked);

    // A YAML or JSON text in the code editor; problem says what is wrong with it as it is typed.
    public static DialogField Code(string label, string text, Shared.CodeLanguage language, Func<string, string?>? problem = null) =>
        new(label, text) { IsCode = true, Language = language, problemOf = problem };

    Func<string, string?>? problemOf;

    public string Label { get; }
    public string Placeholder { get; }
    public IReadOnlyList<string>? Choices { get; }
    public bool IsCheck { get; }
    public bool IsCode { get; private init; }
    public Shared.CodeLanguage Language { get; private init; }
    public bool IsChoice => Choices is not null;
    public bool IsText => Choices is null && !IsCheck && !IsCode;
    public string? Problem => problemOf?.Invoke(text);
    public bool HasProblem => Problem is not null;
    public bool HasLabel => !IsCheck;
    public event Action? Changed;

    public bool IsChecked
    {
        get => isChecked;
        set { if (Set(ref isChecked, value)) Changed?.Invoke(); }
    }

    public string Text
    {
        get => text;
        set
        {
            if (!Set(ref text, value ?? "")) return;
            Raise(nameof(Problem), nameof(HasProblem));
            Changed?.Invoke();
        }
    }

    public string? Choice
    {
        get => choice;
        set { if (Set(ref choice, value)) Changed?.Invoke(); }
    }

    public string Value => IsChoice ? Choice ?? "" : Text.Trim();
}

// One modal at a time, over the whole window. Confirming runs the action inside it, with its progress; a failure stays
// in the dialog so it can be read and retried, and Hide lets a long action go on with its result as a toast.
sealed class Dialog : Observable
{
    readonly Func<Dialog, Task> action;
    readonly Func<Dialog, bool>? canConfirm;
    string? progress;
    double? percent;
    string? error;
    bool working;
    bool hidden;
    bool option;

    public Dialog(string title, string message, string confirmText, Func<Dialog, Task> action,
        bool danger = false, IReadOnlyList<DialogField>? fields = null, string? optionText = null, Func<Dialog, bool>? canConfirm = null)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        IsDanger = danger;
        Fields = fields ?? [];
        OptionText = optionText;
        this.action = action;
        this.canConfirm = canConfirm;
        ConfirmCommand = new Command(Confirm, () => !IsWorking && CanConfirm);
        CancelCommand = new Command(() => { if (!IsWorking) Close(); return Task.CompletedTask; });
        HideCommand = new Command(() => { hidden = true; Close(); return Task.CompletedTask; });
        foreach (var field in Fields) field.Changed += ConfirmCommand.Refresh;
    }

    public string Title { get; }
    public string Message { get; }
    public bool HasMessage => Message.Length > 0;
    public string ConfirmText { get; }
    public bool IsDanger { get; }
    public bool IsPrimary => !IsDanger;
    public IReadOnlyList<DialogField> Fields { get; }
    public double Width => Fields.Any(f => f.IsCode) ? 720 : 460;
    public string? OptionText { get; }
    public bool HasOption => OptionText is not null;
    public Command ConfirmCommand { get; }
    public Command CancelCommand { get; }
    public Command HideCommand { get; }
    public event Action? Closed;
    public Action<string>? ShowError { get; set; }
    public bool IsClosed { get; private set; }

    public bool Option { get => option; set => Set(ref option, value); }
    public bool CanConfirm => canConfirm?.Invoke(this) ?? Fields.All(f => f.Value.Length > 0);

    public bool IsWorking
    {
        get => working;
        private set
        {
            if (!Set(ref working, value)) return;
            Raise(nameof(IsIdle));
            ConfirmCommand.Refresh();
        }
    }

    public bool IsIdle => !IsWorking;
    public string? Progress { get => progress; private set { if (Set(ref progress, value)) Raise(nameof(HasProgress)); } }
    public bool HasProgress => IsWorking;
    public double Percent => percent ?? 0;
    public bool IsIndeterminate => percent is null;
    public string? Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(HasError)); } }
    public bool HasError => error is not null;

    public DialogField Field(string label) => Fields.First(f => f.Label == label);

    // From any thread: kind and the CLI report from their own.
    public void Report(string text, double? value = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Report(text, value));
            return;
        }
        Progress = text;
        percent = value;
        Raise(nameof(Percent), nameof(IsIndeterminate));
    }

    public async Task Confirm()
    {
        if (IsWorking || !CanConfirm) return;
        Error = null;
        Report("");
        IsWorking = true;
        Raise(nameof(HasProgress));
        try
        {
            await action(this);
            Close();
        }
        catch (Exception e)
        {
            if (hidden) ShowError?.Invoke($"{Title.TrimEnd('?')}: {e.Message}");
            else Error = e.Message;
        }
        finally
        {
            IsWorking = false;
            Raise(nameof(HasProgress));
        }
    }

    void Close()
    {
        if (IsClosed) return;
        IsClosed = true;
        Closed?.Invoke();
    }

    // Runs the action for each item, saying which one it is at, and goes on past failures: they are reported together.
    public async Task ForEach<T>(IReadOnlyList<T> items, string verb, Func<T, string> name, Func<T, Task> act)
    {
        var failed = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            Report(items.Count == 1 ? $"{verb} {name(item)}…" : $"{verb} {i + 1} of {items.Count}: {name(item)}…", items.Count == 1 ? null : 100.0 * i / items.Count);
            try { await act(item); }
            catch (Exception e) { failed.Add($"{name(item)}: {e.Message}"); }
        }
        if (failed.Count == 0) return;
        var done = items.Count - failed.Count;
        throw new InvalidOperationException((done > 0 ? $"{done} of {items.Count} done. " : "") + string.Join("\n", failed));
    }
}
