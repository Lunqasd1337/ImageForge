using Microsoft.UI.Xaml;

namespace ImageForge.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += async (_, _) => await ViewModel.DisposeAsync().ConfigureAwait(false);
    }

    public MainViewModel ViewModel { get; } = new();
}
