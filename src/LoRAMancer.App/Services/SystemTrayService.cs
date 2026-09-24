using System.Diagnostics;
using System.Runtime.InteropServices;
using LoRAMancer.App.Models;
using Microsoft.Extensions.Logging;

namespace LoRAMancer.App.Services;

public sealed class SystemTrayService : IDisposable {
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;

    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYICON = WM_USER + 1024;

    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint NIN_BALLOONUSERCLICK = 0x0405;

    private const uint SW_RESTORE = 9;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_SEPARATOR = 0x00000800;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint CMD_OPEN = 1001;
    private const uint CMD_STATUS = 1002;
    private const uint CMD_EXIT = 1003;

    private const nuint SUBCLASS_ID = 4242;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    private readonly SettingsService _settingsService;
    private readonly TrainingRunnerService _trainingRunner;
    private readonly ILogger<SystemTrayService> _logger;

    private IntPtr _hwnd = IntPtr.Zero;
#if WINDOWS
    private Microsoft.UI.Windowing.AppWindow? _appWindow;
#endif
    private IntPtr _hIcon = IntPtr.Zero;
    private NOTIFYICONDATAW _notifyIconData;
    private bool _isInitialized;
    private bool _isExiting;
    private bool _hasShownBalloonOnce;
    private readonly SubclassProc _subclassProc;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, uint nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lpTPMParams);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    public SystemTrayService(SettingsService settingsService, TrainingRunnerService trainingRunner, ILogger<SystemTrayService> logger) {
        _settingsService = settingsService;
        _trainingRunner = trainingRunner;
        _logger = logger;
        _subclassProc = WindowSubclassCallback;
    }

#if WINDOWS
    public void Initialize(Microsoft.UI.Xaml.Window window) {
        if (_isInitialized) {
            return;
        }

        try {
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (_hwnd == IntPtr.Zero) {
                _logger.LogWarning("SystemTrayService: Unable to obtain native HWND from Window.");
                return;
            }

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            string iconPath = ResolveIconPath();
            if (File.Exists(iconPath)) {
                _appWindow.SetIcon(iconPath);
                _hIcon = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            }

            // Hook window close event to implement run in system tray on close
            _appWindow.Closing += OnWindowClosing;

            // Subclass the HWND to handle system tray notifications
            SetWindowSubclass(_hwnd, _subclassProc, SUBCLASS_ID, 0);

            // Add the icon to the Windows notification tray
            CreateTrayIcon();
            _isInitialized = true;
            _logger.LogInformation("SystemTrayService: Successfully initialized with system tray integration.");
        } catch (Exception ex) {
            _logger.LogError(ex, "SystemTrayService: Failed to initialize system tray integration.");
        }
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args) {
        if (_isExiting) {
            return;
        }

        if (_settingsService.Current.MinimizeToTrayOnClose) {
            args.Cancel = true;
            _appWindow?.Hide();

            if (!_hasShownBalloonOnce) {
                _hasShownBalloonOnce = true;
                ShowBalloonNotification(
                    "LoRAMancer Running in Tray",
                    "LoRAMancer is minimized to the system tray. Double-click or right-click the tray icon to restore.");
            }
        }
    }
#endif

    public void RestoreWindow() {
#if WINDOWS
        if (_appWindow != null && _hwnd != IntPtr.Zero) {
            _appWindow.Show();
            ShowWindow(_hwnd, SW_RESTORE);
            SetForegroundWindow(_hwnd);
        }
#endif
    }

    public void ShowBalloonNotification(string title, string message) {
        if (!_isInitialized || _hwnd == IntPtr.Zero) {
            return;
        }

        try {
            _notifyIconData.uFlags = NIF_INFO;
            _notifyIconData.szInfoTitle = title.Length > 63 ? title.Substring(0, 63) : title;
            _notifyIconData.szInfo = message.Length > 255 ? message.Substring(0, 255) : message;
            _notifyIconData.dwInfoFlags = NIIF_INFO;

            Shell_NotifyIconW(NIM_MODIFY, ref _notifyIconData);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "SystemTrayService: Failed to show balloon notification.");
        }
    }

    public void ExitApplication() {
        if (_isExiting) {
            return;
        }

        _isExiting = true;
        _logger.LogInformation("SystemTrayService: Exiting application cleanly.");

        Dispose();

        Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(() => {
            Microsoft.Maui.Controls.Application.Current?.Quit();
        });

        // Backup safety exit if MAUI quit is blocked
        Task.Delay(600).ContinueWith(_ => {
            Environment.Exit(0);
        });
    }

    private void CreateTrayIcon() {
        _notifyIconData = new NOTIFYICONDATAW {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "LoRAMancer - LoRA Manager & Trainer"
        };

        Shell_NotifyIconW(NIM_ADD, ref _notifyIconData);
    }

    private IntPtr WindowSubclassCallback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData) {
        if (uMsg == WM_TRAYICON) {
            uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (mouseMsg) {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                case NIN_BALLOONUSERCLICK:
                    RestoreWindow();
                    break;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowContextMenu();
                    break;
            }
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ShowContextMenu() {
        if (_hwnd == IntPtr.Zero) {
            return;
        }

        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) {
            return;
        }

        try {
            AppendMenuW(hMenu, MF_STRING, CMD_OPEN, "Open LoRAMancer");

            string statusText = _trainingRunner.IsRunning
                ? $"Status: Training ({_trainingRunner.CurrentProgress.CurrentStep}/{_trainingRunner.CurrentProgress.TotalSteps})"
                : "Status: Idle / Ready";
            AppendMenuW(hMenu, MF_STRING | MF_GRAYED, CMD_STATUS, statusText);

            AppendMenuW(hMenu, MF_SEPARATOR, 0, string.Empty);
            AppendMenuW(hMenu, MF_STRING, CMD_EXIT, "Exit LoRAMancer");

            SetForegroundWindow(_hwnd);

            if (GetCursorPos(out POINT pt)) {
                uint selectedCmd = TrackPopupMenuEx(hMenu, TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, _hwnd, IntPtr.Zero);
                if (selectedCmd == CMD_OPEN) {
                    RestoreWindow();
                } else if (selectedCmd == CMD_EXIT) {
                    ExitApplication();
                }
            }
        } finally {
            DestroyMenu(hMenu);
        }
    }

    private static string ResolveIconPath() {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates = [
            Path.Combine(baseDir, "Resources", "AppIcon", "appicon.ico"),
            Path.Combine(baseDir, "appicon.ico"),
            Path.Combine(baseDir, "wwwroot", "favicon.ico")
        ];

        foreach (string candidate in candidates) {
            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        return string.Empty;
    }

    public void Dispose() {
        if (_hwnd != IntPtr.Zero && _isInitialized) {
            try {
                Shell_NotifyIconW(NIM_DELETE, ref _notifyIconData);
                RemoveWindowSubclass(_hwnd, _subclassProc, SUBCLASS_ID);
            } catch {
                // Ignore during shutdown
            }
        }

        if (_hIcon != IntPtr.Zero) {
            try {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            } catch {
                // Ignore during shutdown
            }
        }

        _isInitialized = false;
    }
}
