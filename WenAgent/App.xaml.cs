using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace WenAgent;

public partial class App : Application
{
    private const string MutexName = @"Local\WenAgent.SingleInstance.4B3E7D2A";

    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            ActivateExistingWindow();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void ActivateExistingWindow()
    {
        try
        {
            string name = Process.GetCurrentProcess().ProcessName;

            for (int attempt = 0; attempt < 10; attempt++)
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        if (process.Id == Environment.ProcessId) continue;

                        IntPtr handle = process.MainWindowHandle;
                        if (handle == IntPtr.Zero) continue;

                        ShowWindow(handle, 9); // SW_RESTORE
                        SetForegroundWindow(handle);
                        return;
                    }
                }

                Thread.Sleep(100);
            }
        }
        catch
        {
            // 聚焦已有窗口失败时直接退出即可
        }
    }
}
