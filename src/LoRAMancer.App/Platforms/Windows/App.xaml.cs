using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;

namespace LoRAMancer.App.WinUI;

public partial class App : MauiWinUIApplication {
    private static Mutex? _singleInstanceMutex;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

    public App() {
        const string mutexName = @"Global\LoRAMancer_Studio_SingleInstance_Mutex";
        try {
            _singleInstanceMutex = new Mutex(true, mutexName, out bool isNewInstance);
            if (!isNewInstance) {
                ActivateExistingInstance();
                Process.GetCurrentProcess().Kill();
                return;
            }
        } catch {
            // Non-critical fallback
        }

        this.InitializeComponent();
    }

    private static void ActivateExistingInstance() {
        try {
            Process current = Process.GetCurrentProcess();
            Process? existing = Process.GetProcessesByName(current.ProcessName)
                .FirstOrDefault(p => p.Id != current.Id);
            if (existing != null && existing.MainWindowHandle != IntPtr.Zero) {
                ShowWindow(existing.MainWindowHandle, SwRestore);
                SetForegroundWindow(existing.MainWindowHandle);
            }
        } catch { }
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

