using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Metadata;

namespace Dockyard.Dashboard.Controls;

// One setting: its title with what applies to it, what it does, and its editor on the right. Under it, what changing it is
// doing or why it failed.
sealed class SettingRow : Grid
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Title));
    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));
    public static readonly StyledProperty<bool> IsLinuxEngineOnlyProperty = AvaloniaProperty.Register<SettingRow, bool>(nameof(IsLinuxEngineOnly));
    public static readonly StyledProperty<bool> NeedsAdminProperty = AvaloniaProperty.Register<SettingRow, bool>(nameof(NeedsAdmin));
    public static readonly StyledProperty<string?> StatusProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Status));
    public static readonly StyledProperty<string?> ErrorProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Error));
    public static readonly StyledProperty<Control?> EditorProperty = AvaloniaProperty.Register<SettingRow, Control?>(nameof(Editor));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public bool IsLinuxEngineOnly { get => GetValue(IsLinuxEngineOnlyProperty); set => SetValue(IsLinuxEngineOnlyProperty, value); }
    public bool NeedsAdmin { get => GetValue(NeedsAdminProperty); set => SetValue(NeedsAdminProperty, value); }
    public string? Status { get => GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public string? Error { get => GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }
    [Content]
    public Control? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }

    protected override Type StyleKeyOverride => typeof(Grid);

    readonly TextBlock title = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    readonly Border linuxOnly = Badge("Linux engine only", "Applies to the Linux engine's containers");
    readonly Border admin = Badge("Asks for approval", "Changing it asks for an administrator's approval, where the system asks for one");
    readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap };
    readonly StackPanel status = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock statusText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly ContentControl editor = new() { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24, 0, 0, 0) };

    static Border Badge(string text, string tip)
    {
        var b = new Border { Child = new TextBlock { Text = text }, Margin = new Thickness(0, 1, 6, 1) };
        b.Classes.Add("pill");
        ToolTip.SetTip(b, tip);
        return b;
    }

    public SettingRow()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        description.Classes.Add("muted");
        description.Classes.Add("small");
        statusText.Classes.Add("muted");
        statusText.Classes.Add("small");
        error.Classes.Add("small");
        error.Token(TextBlock.ForegroundProperty, "Bad");
        status.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 60, MinWidth = 60, VerticalAlignment = VerticalAlignment.Center });
        status.Children.Add(statusText);

        var heading = new WrapPanel { Children = { title, linuxOnly, admin } };
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { heading, description, status, error } };
        Children.Add(text);
        SetColumn(editor, 1);
        Children.Add(editor);
        Show();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty) editor.Content = Editor;
        else if (change.Property.OwnerType == typeof(SettingRow)) Show();
    }

    void Show()
    {
        title.Text = Title;
        Avalonia.Automation.AutomationProperties.SetName(this, Title);
        description.Text = Description;
        description.IsVisible = !string.IsNullOrEmpty(Description);
        linuxOnly.IsVisible = IsLinuxEngineOnly;
        admin.IsVisible = NeedsAdmin;
        statusText.Text = Status;
        status.IsVisible = !string.IsNullOrEmpty(Status);
        error.Text = Error;
        error.IsVisible = !string.IsNullOrEmpty(Error);
    }
}
