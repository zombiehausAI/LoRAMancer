using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LoRAMancer.App.Services;

public sealed class NativeFileDialogService {
    public async Task<string?> PickSafeTensorsFileAsync() {
        FilePickerFileType customFileType = new(new Dictionary<DevicePlatform, IEnumerable<string>> {
            { DevicePlatform.WinUI, new[] { ".safetensors", ".ckpt", ".pt" } }
        });

        PickOptions options = new() {
            PickerTitle = "Select Donor LoRA (.safetensors)",
            FileTypes = customFileType
        };

        FileResult? result = await FilePicker.Default.PickAsync(options);
        return result?.FullPath;
    }

    public async Task<IReadOnlyList<string>> PickMultipleSafeTensorsFilesAsync() {
        FilePickerFileType customFileType = new(new Dictionary<DevicePlatform, IEnumerable<string>> {
            { DevicePlatform.WinUI, new[] { ".safetensors" } }
        });

        PickOptions options = new() {
            PickerTitle = "Select LoRA Files (.safetensors)",
            FileTypes = customFileType
        };

        var results = await FilePicker.Default.PickMultipleAsync(options);
        if (results == null) {
            return Array.Empty<string>();
        }

        return results.Where(r => r != null).Select(r => r!.FullPath).ToList();
    }

    public async Task<string?> PickFolderAsync(string title = "Select Directory") {
        FolderPicker folderPicker = new();
        folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        folderPicker.FileTypeFilter.Add("*");

        IntPtr hwnd = GetActiveWindowHandle();
        InitializeWithWindow.Initialize(folderPicker, hwnd);

        Windows.Storage.StorageFolder? folder = await folderPicker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public async Task<string?> PickScriptFileAsync() {
        FilePickerFileType customFileType = new(new Dictionary<DevicePlatform, IEnumerable<string>> {
            { DevicePlatform.WinUI, new[] { ".ps1", ".bat", ".cmd", ".py" } }
        });

        PickOptions options = new() {
            PickerTitle = "Select ComfyUI Startup Script (.ps1/.bat)",
            FileTypes = customFileType
        };

        FileResult? result = await FilePicker.Default.PickAsync(options);
        return result?.FullPath;
    }

    private static IntPtr GetActiveWindowHandle() {
        var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
        if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUIWindow) {
            return WindowNative.GetWindowHandle(winUIWindow);
        }
        return IntPtr.Zero;
    }
}
