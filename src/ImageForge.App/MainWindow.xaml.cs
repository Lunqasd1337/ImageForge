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

    private async void ChooseFodFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                ViewModel.FodPath = folder.Path;
            }
        }
        catch (COMException ex)
        {
            ViewModel.Status = (Ui.FolderPickerFailed + " ") + ex.Message;
        }
    }

    private async void ChooseDriverFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                ViewModel.DriverPath = folder.Path;
            }
        }
        catch (COMException ex)
        {
            ViewModel.Status = (Ui.FolderPickerFailed + " ") + ex.Message;
        }
    }

    private async void ChooseUpdateFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                ViewModel.UpdatePath = folder.Path;
            }
        }
        catch (COMException ex)
        {
            ViewModel.Status = (Ui.FolderPickerFailed + " ") + ex.Message;
        }
    }

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
            ViewModel.Status = (Ui.FilePickerFailed + " ") + ex.Message;
        }
    }
}
