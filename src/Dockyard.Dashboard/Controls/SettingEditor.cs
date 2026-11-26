using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Controls;

// The editor a setting's kind asks for: a switch for on and off, a list of its choices, a text box, or a button that opens
// an engine's daemon.json. Value is the setting's value as the facade takes it.
sealed class SettingEditor : ContentControl
{
    public static readonly StyledProperty<SettingKind> KindProperty = AvaloniaProperty.Register<SettingEditor, SettingKind>(nameof(Kind));
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<SettingEditor, string?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IReadOnlyList<string>?> ChoicesProperty = AvaloniaProperty.Register<SettingEditor, IReadOnlyList<string>?>(nameof(Choices));
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<SettingEditor, string?>(nameof(Label));
    public static readonly StyledProperty<string?> EditTextProperty = AvaloniaProperty.Register<SettingEditor, string?>(nameof(EditText), "Edit…");
    public static readonly StyledProperty<ICommand?> EditCommandProperty = AvaloniaProperty.Register<SettingEditor, ICommand?>(nameof(EditCommand));

    public SettingKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IReadOnlyList<string>? Choices { get => GetValue(ChoicesProperty); set => SetValue(ChoicesProperty, value); }
    // What a screen reader calls the editor: the setting's title.
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? EditText { get => GetValue(EditTextProperty); set => SetValue(EditTextProperty, value); }
    public ICommand? EditCommand { get => GetValue(EditCommandProperty); set => SetValue(EditCommandProperty, value); }

    protected override Type StyleKeyOverride => typeof(ContentControl);

    bool showing;

    public SettingEditor()
    {
        VerticalAlignment = VerticalAlignment.Top;
        Build();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == ChoicesProperty || change.Property == EditTextProperty) Build();
        else if (change.Property == ValueProperty) ShowValue();
        else if (change.Property == LabelProperty && Content is Control c) Avalonia.Automation.AutomationProperties.SetName(c, Label);
    }

    void Build()
    {
        Content = Kind switch
        {
            SettingKind.Toggle => Toggle(),
            SettingKind.Choice => Choice(),
            SettingKind.Text => Text(),
            _ => EngineFile(),
        };
        if (Content is Control c) Avalonia.Automation.AutomationProperties.SetName(c, Label);
        ShowValue();
    }

    ToggleSwitch Toggle()
    {
        // Without words beside it: the row's title says what it switches.
        var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0, Margin = new Thickness(0, -6, -12, 0) };
        toggle.IsCheckedChanged += (_, _) => Take(toggle.IsChecked == true ? SettingDefinitions.On : SettingDefinitions.Off);
        return toggle;
    }

    ComboBox Choice()
    {
        var box = new ComboBox { ItemsSource = Choices, MinWidth = 160 };
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is string s) Take(s); };
        return box;
    }

    TextBox Text()
    {
        var box = new TextBox { Width = 240 };
        box.LostFocus += (_, _) => Take(box.Text ?? "");
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Take(box.Text ?? ""); };
        return box;
    }

    Button EngineFile()
    {
        var icon = new PathIcon();
        var label = new StackPanel { Children = { icon, new TextBlock { Text = EditText } } };
        label.Classes.Add("label");
        var button = new Button { Content = label };
        button.AttachedToVisualTree += (_, _) => { if (button.FindResource("IconOpenFile") is Geometry g) icon.Data = g; };
        button.Click += (_, _) => { if (EditCommand?.CanExecute(null) == true) EditCommand.Execute(null); };
        return button;
    }

    void Take(string value)
    {
        if (!showing && value != Value) Value = value;
    }

    void ShowValue()
    {
        showing = true;
        try
        {
            switch (Content)
            {
                case ToggleSwitch toggle: toggle.IsChecked = Value == SettingDefinitions.On; break;
                case ComboBox box: box.SelectedItem = Value; break;
                case TextBox box: box.Text = Value; break;
            }
        }
        finally { showing = false; }
    }
}
