using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Dockyard.Dashboard.Controls;

// Where a component comes from and what updates it: Dockyard's own downloads in the brand's colours, a package manager plain.
sealed class SourceBadge : Border
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<SourceBadge, string?>(nameof(Source));

    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    protected override Type StyleKeyOverride => typeof(Border);

    readonly TextBlock text = new() { TextTrimming = TextTrimming.CharacterEllipsis };

    public SourceBadge()
    {
        Classes.Add("pill");
        Child = text;
        IsVisible = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SourceProperty) return;
        text.Text = Source;
        IsVisible = !string.IsNullOrEmpty(Source);
        var own = Source == Dockyard.Setup.SettingDefinitions.DockyardSource;
        Classes.Set("brand", own);
        ToolTip.SetTip(this, own ? "Dockyard downloads and updates this itself" : $"{Source} installs and updates this");
    }
}
