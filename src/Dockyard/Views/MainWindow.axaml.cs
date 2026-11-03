using Avalonia.Controls;
using Dockyard.ViewModels;

namespace Dockyard.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    internal bool HideOnClose { get; init; }

    MainViewModel? ViewModel => DataContext as MainViewModel;

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
