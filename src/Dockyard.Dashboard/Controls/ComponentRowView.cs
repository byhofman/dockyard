using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Dockyard.Dashboard.Controls;

// One component: its name, where it comes from, what it needs or why it is required, its version and an update, and what can be
// done with it. With ShowsPick it is a choice instead, for the setup; a required one is picked and cannot be unpicked.
sealed class ComponentRowView : Grid
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<ComponentRowView, string?>(nameof(Title));
    public static readonly StyledProperty<string?> NeedsProperty = AvaloniaProperty.Register<ComponentRowView, string?>(nameof(Needs));
    public static readonly StyledProperty<string?> RequiredBecauseProperty = AvaloniaProperty.Register<ComponentRowView, string?>(nameof(RequiredBecause));
    public static readonly StyledProperty<string?> InstalledProperty = AvaloniaProperty.Register<ComponentRowView, string?>(nameof(Installed));
    public static readonly StyledProperty<string?> NewestProperty = AvaloniaProperty.Register<ComponentRowView, string?>(nameof(Newest));
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<ComponentRowView, string?>(nameof(Source));
    public static readonly StyledProperty<bool> CanAddProperty = AvaloniaProperty.Register<ComponentRowView, bool>(nameof(CanAdd));
    public static readonly StyledProperty<bool> CanRemoveProperty = AvaloniaProperty.Register<ComponentRowView, bool>(nameof(CanRemove));
    public static readonly StyledProperty<bool> CanUpdateProperty = AvaloniaProperty.Register<ComponentRowView, bool>(nameof(CanUpdate));
    public static readonly StyledProperty<bool> CanUninstallProperty = AvaloniaProperty.Register<ComponentRowView, bool>(nameof(CanUninstall));
    public static readonly StyledProperty<string?> BusyTextProperty = AvaloniaProperty.Register<ComponentRowView, string?>(nameof(BusyText));
    public static readonly StyledProperty<bool> ShowsPickProperty = AvaloniaProperty.Register<ComponentRowView, bool>(nameof(ShowsPick));
    public static readonly StyledProperty<bool> IsPickedProperty =
        AvaloniaProperty.Register<ComponentRowView, bool>(nameof(IsPicked), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<ICommand?> AddCommandProperty = AvaloniaProperty.Register<ComponentRowView, ICommand?>(nameof(AddCommand));
    public static readonly StyledProperty<ICommand?> RemoveCommandProperty = AvaloniaProperty.Register<ComponentRowView, ICommand?>(nameof(RemoveCommand));
    public static readonly StyledProperty<ICommand?> UpdateCommandProperty = AvaloniaProperty.Register<ComponentRowView, ICommand?>(nameof(UpdateCommand));
    public static readonly StyledProperty<ICommand?> UninstallCommandProperty = AvaloniaProperty.Register<ComponentRowView, ICommand?>(nameof(UninstallCommand));
    public static readonly StyledProperty<object?> CommandParameterProperty = AvaloniaProperty.Register<ComponentRowView, object?>(nameof(CommandParameter));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Needs { get => GetValue(NeedsProperty); set => SetValue(NeedsProperty, value); }
    public string? RequiredBecause { get => GetValue(RequiredBecauseProperty); set => SetValue(RequiredBecauseProperty, value); }
    public string? Installed { get => GetValue(InstalledProperty); set => SetValue(InstalledProperty, value); }
    public string? Newest { get => GetValue(NewestProperty); set => SetValue(NewestProperty, value); }
    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool CanAdd { get => GetValue(CanAddProperty); set => SetValue(CanAddProperty, value); }
    public bool CanRemove { get => GetValue(CanRemoveProperty); set => SetValue(CanRemoveProperty, value); }
    public bool CanUpdate { get => GetValue(CanUpdateProperty); set => SetValue(CanUpdateProperty, value); }
    public bool CanUninstall { get => GetValue(CanUninstallProperty); set => SetValue(CanUninstallProperty, value); }
    public string? BusyText { get => GetValue(BusyTextProperty); set => SetValue(BusyTextProperty, value); }
    public bool ShowsPick { get => GetValue(ShowsPickProperty); set => SetValue(ShowsPickProperty, value); }
    public bool IsPicked { get => GetValue(IsPickedProperty); set => SetValue(IsPickedProperty, value); }
    public ICommand? AddCommand { get => GetValue(AddCommandProperty); set => SetValue(AddCommandProperty, value); }
    public ICommand? RemoveCommand { get => GetValue(RemoveCommandProperty); set => SetValue(RemoveCommandProperty, value); }
    public ICommand? UpdateCommand { get => GetValue(UpdateCommandProperty); set => SetValue(UpdateCommandProperty, value); }
    public ICommand? UninstallCommand { get => GetValue(UninstallCommandProperty); set => SetValue(UninstallCommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }

    protected override Type StyleKeyOverride => typeof(Grid);

    readonly CheckBox pick = new();
    readonly TextBlock title = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    readonly SourceBadge source = new() { Margin = new Thickness(0, 1) };
    readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap };
    readonly StackPanel version = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), MinWidth = 110, Spacing = 1 };
    readonly TextBlock installed = new() { HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock available = new() { HorizontalAlignment = HorizontalAlignment.Right };
    readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 0, 0) };
    readonly Button update = Action("Update", "update");
    readonly Button add = Action("Add", null);
    // Quiet, and always there rather than on hover, so a keyboard and a touch screen reach it as well.
    readonly Button remove = Action("Remove", "ghost");
    readonly Button uninstall = Action("Uninstall…", "ghost danger");
    readonly Button required = new() { Focusable = true };
    readonly StackPanel busy = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock busyText = new() { VerticalAlignment = VerticalAlignment.Center };

    static Button Action(string text, string? style)
    {
        var b = new Button { Content = text, MinHeight = 28, Padding = new Thickness(12, 3), MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (style is not null) b.Classes.AddRange(style.Split(' '));
        return b;
    }

    public ComponentRowView()
    {
        // Shared across the rows of a screen, so versions and buttons line up from one card to the next.
        ColumnDefinitions =
        [
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(1, GridUnitType.Star),
            new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "ComponentVersion" },
            new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "ComponentUpdate" },
            new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "ComponentAction" },
        ];
        pick.Classes.Add("pick");
        pick.IsCheckedChanged += (_, _) => IsPicked = pick.IsChecked == true;
        detail.Classes.Add("muted");
        detail.Classes.Add("small");
        installed.Classes.Add("mono");
        available.Classes.Add("small");
        available.Token(TextBlock.ForegroundProperty, "AccentText");
        busyText.Classes.Add("muted");
        busyText.Classes.Add("small");
        required.Classes.Add("badge");
        var lockIcon = new PathIcon { Width = 12, Height = 12 };
        required.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { lockIcon, new TextBlock { Text = "Required" } } };
        // A tooltip opens on hover only: focus and a tap open it as well.
        required.GotFocus += (_, _) => ToolTip.SetIsOpen(required, true);
        required.LostFocus += (_, _) => ToolTip.SetIsOpen(required, false);
        required.Click += (_, _) => ToolTip.SetIsOpen(required, !ToolTip.GetIsOpen(required));

        var heading = new WrapPanel { Children = { title, source } };
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { heading, detail, busy } };
        version.Children.Add(installed);
        version.Children.Add(available);
        actions.Children.Add(add);
        actions.Children.Add(remove);
        actions.Children.Add(uninstall);
        actions.Children.Add(required);
        busy.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 60, MinWidth = 60, VerticalAlignment = VerticalAlignment.Center });
        busy.Children.Add(busyText);

        Place(pick, 0);
        Place(label, 1);
        Place(version, 2);
        update.Margin = new Thickness(16, 0, 0, 0);
        update.VerticalAlignment = VerticalAlignment.Center;
        Place(update, 3);
        Place(actions, 4);

        update.Click += (_, _) => Run(UpdateCommand);
        add.Click += (_, _) => Run(AddCommand);
        remove.Click += (_, _) => Run(RemoveCommand);
        uninstall.Click += (_, _) => Run(UninstallCommand);
        AttachedToVisualTree += (_, _) => { if (this.FindResource("IconLock") is Geometry g) lockIcon.Data = g; };
        Show();
    }

    static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    void Place(Control control, int column)
    {
        SetColumn(control, column);
        Children.Add(control);
    }

    void Run(ICommand? command)
    {
        if (command?.CanExecute(CommandParameter) == true) command.Execute(CommandParameter);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.OwnerType == typeof(ComponentRowView)) Show();
    }

    void Show()
    {
        var isRequired = RequiredBecause is not null;
        title.Text = Title;
        detail.Text = isRequired ? Sentence(RequiredBecause!) : Needs;
        detail.IsVisible = !string.IsNullOrEmpty(detail.Text);
        Avalonia.Automation.AutomationProperties.SetName(this, Title);

        pick.IsVisible = ShowsPick;
        pick.IsEnabled = !isRequired;
        pick.IsChecked = IsPicked || isRequired;
        Avalonia.Automation.AutomationProperties.SetName(pick, Title);

        var isBusy = !string.IsNullOrEmpty(BusyText);
        version.IsVisible = !ShowsPick;
        installed.Text = Installed ?? "Not installed";
        installed.Classes.Set("mono", Installed is not null);
        installed.Classes.Set("muted", Installed is null);
        installed.Classes.Set("small", Installed is null);
        var hasUpdate = CanUpdate && Newest is not null;
        available.Text = hasUpdate ? $"{Newest} available" : null;
        available.IsVisible = hasUpdate;
        update.IsVisible = hasUpdate && !ShowsPick;
        update.Opacity = isBusy ? 0 : 1;
        update.IsHitTestVisible = !isBusy;
        ToolTip.SetTip(update, hasUpdate ? $"Update {Title} to {Newest}" : null);

        source.Source = ShowsPick ? null : Source;

        busy.IsVisible = isBusy && !ShowsPick;
        busyText.Text = BusyText;
        // Kept in layout while busy, so the columns of every row stay where they are.
        actions.Opacity = isBusy ? 0 : 1;
        actions.IsHitTestVisible = !isBusy;
        actions.IsVisible = !ShowsPick;
        add.IsVisible = CanAdd;
        remove.IsVisible = CanRemove;
        uninstall.IsVisible = CanUninstall;
        required.IsVisible = isRequired && !CanUninstall;
        ToolTip.SetTip(required, isRequired ? $"Required: {RequiredBecause}" : null);
        Avalonia.Automation.AutomationProperties.SetName(required, isRequired ? $"Required: {RequiredBecause}" : null);
        foreach (var (button, verb) in new[] { (update, "Update"), (add, "Add"), (remove, "Remove"), (uninstall, "Uninstall") })
            Avalonia.Automation.AutomationProperties.SetName(button, $"{verb} {Title}");
    }
}
