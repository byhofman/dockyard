using Avalonia.Controls;
using Dockyard.ViewModels;

namespace Dockyard.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    internal bool HideOnClose { get; init; }

    MainViewModel? ViewModel => DataContext as MainViewModel;
    MainViewModel? subscribed;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (subscribed is not null) subscribed.EngineSettingsRequested -= OpenEngineSettings;
        subscribed = ViewModel;
        if (subscribed is not null) subscribed.EngineSettingsRequested += OpenEngineSettings;
    }

    async void OpenEngineSettings(Hosts.EngineInfo engine)
    {
        if (ViewModel is not { } vm) return;
        var settings = new EngineSettingsViewModel(vm.Host, engine, vm.Note);
        var window = new EngineSettingsWindow { DataContext = settings };
        window.Show(this);
        try { await settings.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { vm.Note($"{engine.Name} engine settings: {ex.Message}"); }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ViewModel is not { } vm) return;
        if (vm.IsSetupMode) await vm.LoadSetup();
        else await vm.OnWindowShown();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (HideOnClose && !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
