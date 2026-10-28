using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace ImageForge.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += async (_, _) => await ViewModel.DisposeAsync().ConfigureAwait(false);
    }

    public MainViewModel ViewModel { get; } = new();

    private async void ChooseRegFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".reg");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFileAsync() is { } file)
            {
                ViewModel.RegFilePath = file.Path;
            }
        }
        catch (COMException ex)
        {
            ViewModel.Status = "Не удалось открыть выбор файла, введите путь вручную: " + ex.Message;
        }
    }
}
