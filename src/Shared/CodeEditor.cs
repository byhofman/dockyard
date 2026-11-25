using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using AvaloniaEdit;

namespace Dockyard.Shared;

// AvaloniaEdit's editor with a bindable Text, the app's colours for JSON and YAML, the lines with problems marked, and
// completion on Ctrl+Space.
sealed class CodeEditor : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CodeEditor, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<CodeLanguage> LanguageProperty = AvaloniaProperty.Register<CodeEditor, CodeLanguage>(nameof(Language));

    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<CodeEditor, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<IReadOnlyList<int>?> ProblemLinesProperty = AvaloniaProperty.Register<CodeEditor, IReadOnlyList<int>?>(nameof(ProblemLines));

    public static readonly StyledProperty<Func<IReadOnlyList<CodeCompletion>>?> CompletionsProperty =
        AvaloniaProperty.Register<CodeEditor, Func<IReadOnlyList<CodeCompletion>>?>(nameof(Completions));

    readonly TextEditor editor;
    CodeColorizer? colorizer;
    bool syncing;

    public CodeEditor()
    {
        editor = new TextEditor
        {
            ShowLineNumbers = true,
            WordWrap = false,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 13,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        editor.Options.IndentationSize = 2;
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.EnableHyperlinks = false;
        editor.TextChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            try { SetCurrentValue(TextProperty, editor.Text); }
            finally { syncing = false; }
        };
        ActualThemeVariantChanged += (_, _) => editor.TextArea.TextView.Redraw();
        editor.TextArea.TextView.BackgroundRenderers.Add(new ProblemMarker(this));
        editor.TextArea.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Space || !e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) || Completions is not { } completions) return;
            e.Handled = true;
            ShowCompletions(completions());
        };
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = editor, ClipToBounds = true };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Line"));
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("TextControlBackground"));
        Content = frame;
        UseColorizer();
    }

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public CodeLanguage Language { get => GetValue(LanguageProperty); set => SetValue(LanguageProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public IReadOnlyList<int>? ProblemLines { get => GetValue(ProblemLinesProperty); set => SetValue(ProblemLinesProperty, value); }
    public Func<IReadOnlyList<CodeCompletion>>? Completions { get => GetValue(CompletionsProperty); set => SetValue(CompletionsProperty, value); }

    void ShowCompletions(IReadOnlyList<CodeCompletion> items)
    {
        if (items.Count == 0) return;
        var window = new AvaloniaEdit.CodeCompletion.CompletionWindow(editor.TextArea);
        foreach (var item in items) window.CompletionList.CompletionData.Add(new CompletionData(item));
        window.Show();
    }

    sealed class CompletionData(CodeCompletion item) : AvaloniaEdit.CodeCompletion.ICompletionData
    {
        public IImage? Image => null;
        public string Text => item.Text;
        public object Content => item.Text;
        public object Description => item.Description;
        public double Priority => 0;

        public void Complete(AvaloniaEdit.Editing.TextArea textArea, AvaloniaEdit.Document.ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, item.Insert);
    }

    // A faint band behind each line the checks found something on.
    sealed class ProblemMarker(CodeEditor owner) : AvaloniaEdit.Rendering.IBackgroundRenderer
    {
        public AvaloniaEdit.Rendering.KnownLayer Layer => AvaloniaEdit.Rendering.KnownLayer.Background;

        public void Draw(AvaloniaEdit.Rendering.TextView textView, DrawingContext drawingContext)
        {
            if (owner.ProblemLines is not { Count: > 0 } lines || owner.Brush("BadBg") is not { } brush) return;
            foreach (var line in textView.VisualLines)
                if (lines.Contains(line.FirstDocumentLine.LineNumber))
                    drawingContext.FillRectangle(brush, new Rect(0, line.VisualTop - textView.ScrollOffset.Y, textView.Bounds.Width, line.Height));
        }
    }

    IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    void UseColorizer()
    {
        if (colorizer is not null) editor.TextArea.TextView.LineTransformers.Remove(colorizer);
        colorizer = new CodeColorizer(Language, Brush);
        editor.TextArea.TextView.LineTransformers.Add(colorizer);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && !syncing && editor.Text != Text)
        {
            syncing = true;
            try { editor.Text = Text ?? ""; }
            finally { syncing = false; }
        }
        else if (change.Property == LanguageProperty) UseColorizer();
        else if (change.Property == IsReadOnlyProperty) editor.IsReadOnly = IsReadOnly;
        else if (change.Property == ProblemLinesProperty) editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
    }
}

// What Ctrl+Space offers: shown as Text with its Description, and Insert put where the caret is.
sealed record CodeCompletion(string Text, string Description, string Insert);
