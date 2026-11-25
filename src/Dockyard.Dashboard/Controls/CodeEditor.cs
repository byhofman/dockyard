using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using AvaloniaEdit;

namespace Dockyard.Dashboard.Controls;

// AvaloniaEdit's editor with a bindable Text and the dashboard's colours for JSON and YAML.
sealed class CodeEditor : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CodeEditor, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<CodeLanguage> LanguageProperty = AvaloniaProperty.Register<CodeEditor, CodeLanguage>(nameof(Language));

    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<CodeEditor, bool>(nameof(IsReadOnly));

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
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = editor, ClipToBounds = true };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Line"));
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("TextControlBackground"));
        Content = frame;
        UseColorizer();
    }

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public CodeLanguage Language { get => GetValue(LanguageProperty); set => SetValue(LanguageProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }

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
    }
}
