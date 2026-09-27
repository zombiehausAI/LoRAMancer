using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LoRAMancer.App.Services;

public sealed class NativeFileDialogService {
    public async Task<string?> PickSafeTensorsFileAsync() {
        try {
            FilePickerFileType customFileType = new(new Dictionary<DevicePlatform, IEnumerable<string>> {
                { DevicePlatform.WinUI, new[] { ".safetensors", ".ckpt", ".pt" } }
            });

            PickOptions options = new() {
                PickerTitle = "Select Donor LoRA (.safetensors)",
                FileTypes = customFileType
            };

            FileResult? result = await FilePicker.Default.PickAsync(options);
            return result?.FullPath;
        } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"[NativeFileDialogService] PickSafeTensorsFileAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> PickMultipleSafeTensorsFilesAsync() {
        try {
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
        } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"[NativeFileDialogService] PickMultipleSafeTensorsFilesAsync error: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public async Task<string?> PickFolderAsync(string title = "Select Directory") {
        try {
            FolderPicker folderPicker = new();
            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*");

            IntPtr hwnd = GetActiveWindowHandle();
            if (hwnd != IntPtr.Zero) {
                InitializeWithWindow.Initialize(folderPicker, hwnd);
            }

            Windows.Storage.StorageFolder? folder = await folderPicker.PickSingleFolderAsync();
            return folder?.Path;
        } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"[NativeFileDialogService] PickFolderAsync error (remote/server session): {ex.Message}");
            return null;
        }
    }

    public async Task<string?> PickScriptFileAsync() {
        try {
            FilePickerFileType customFileType = new(new Dictionary<DevicePlatform, IEnumerable<string>> {
                { DevicePlatform.WinUI, new[] { ".ps1", ".bat", ".cmd", ".py" } }
            });

            PickOptions options = new() {
                PickerTitle = "Select ComfyUI Startup Script (.ps1/.bat)",
                FileTypes = customFileType
            };

            FileResult? result = await FilePicker.Default.PickAsync(options);
            return result?.FullPath;
        } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"[NativeFileDialogService] PickScriptFileAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<string?> PickZipFileAsync(string title = "Select Dataset ZIP Archive") {
        try {
            FilePickerFileType customFileType = new(new Dictionary<DevicePlatform, IEnumerable<string>> {
                { DevicePlatform.WinUI, new[] { ".zip" } }
            });

            PickOptions options = new() {
                PickerTitle = title,
                FileTypes = customFileType
            };

            FileResult? result = await FilePicker.Default.PickAsync(options);
            return result?.FullPath;
        } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"[NativeFileDialogService] PickZipFileAsync error: {ex.Message}");
            return null;
        }
    }

    private static IntPtr GetActiveWindowHandle() {
        try {
            var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUIWindow) {
                IntPtr hwnd = WindowNative.GetWindowHandle(winUIWindow);
                if (hwnd != IntPtr.Zero) {
                    return hwnd;
                }
            }
        } catch { }

        try {
            return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        } catch {
            return IntPtr.Zero;
        }
    }
}
