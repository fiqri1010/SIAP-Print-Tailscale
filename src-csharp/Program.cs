using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SiapPrintServer;

static class Program
{
    private const string GlobalMutexName = @"Global\SIAP-Print-Tailscale-SingleInstance";
    private const string LocalMutexName = @"Local\SIAP_Print_Tailscale_SingleInstance_Mutex";
    private const string WindowTitle = "SIAP Print Tailscale Server — Native C#/.NET";

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    private const int SW_RESTORE = 9;
    private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;

    [STAThread]
    static void Main(string[] args)
    {
        string crashLog = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SIAP-Print-Tailscale",
            "crash.log"
        );

        try
        {
            System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now}] Starting Main(args: {string.Join(" ", args)})...\n");

            // 1. Tangani opsi --replace / --force-restart
            bool forceReplace = args.Any(a => a.Equals("--replace", StringComparison.OrdinalIgnoreCase) ||
                                              a.Equals("--force-restart", StringComparison.OrdinalIgnoreCase));

            var current = Process.GetCurrentProcess();
            var runningInstances = Process.GetProcesses()
                .Where(p => p.Id != current.Id &&
                            (p.ProcessName.StartsWith("SIAP-Print-Tailscale", StringComparison.OrdinalIgnoreCase) ||
                             p.ProcessName.Equals("SIAP-Print-Tailscale", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (forceReplace && runningInstances.Count > 0)
            {
                foreach (var p in runningInstances)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                    catch { }
                }
                Thread.Sleep(500);
            }

            // 2. Proteksi Single Instance Berlapis (Global Mutex, Local Mutex, dan Process Check)
            using var globalMutex = new Mutex(true, GlobalMutexName, out bool isNewGlobal);
            using var localMutex = new Mutex(true, LocalMutexName, out bool isNewLocal);

            if (!forceReplace)
            {
                runningInstances = Process.GetProcesses()
                    .Where(p => p.Id != current.Id &&
                                (p.ProcessName.StartsWith("SIAP-Print-Tailscale", StringComparison.OrdinalIgnoreCase) ||
                                 p.ProcessName.Equals("SIAP-Print-Tailscale", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            bool anotherInstanceExists = (!isNewGlobal || !isNewLocal || runningInstances.Count > 0) && !forceReplace;

            if (anotherInstanceExists)
            {
                System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now}] Another instance detected, restoring existing window.\n");

                // Broadcast pesan khusus ke seluruh jendela untuk memunculkan aplikasi yang ada di System Tray
                uint wmShow = RegisterWindowMessage("SIAP_PRINT_TAILSCALE_SHOW_WINDOW");
                PostMessage(HWND_BROADCAST, wmShow, IntPtr.Zero, IntPtr.Zero);

                IntPtr existingHwnd = FindWindow(null, WindowTitle);
                if (existingHwnd == IntPtr.Zero && runningInstances.Count > 0)
                {
                    existingHwnd = runningInstances[0].MainWindowHandle;
                }
                if (existingHwnd != IntPtr.Zero)
                {
                    ShowWindow(existingHwnd, SW_RESTORE);
                    SetForegroundWindow(existingHwnd);
                }

                MessageBox.Show(
                    "Aplikasi telah terbuka, tidak bisa buka 2 jendela sekaligus.",
                    "Peringatan",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            ApplicationConfiguration.Initialize();

            bool startMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            var cfg = ConfigManager.LoadConfig();
            if (cfg.MinimizeToTrayOnStartup && startMinimized)
            {
                startMinimized = true;
            }

            System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now}] Creating MainForm...\n");
            var mainForm = new MainForm(startMinimized);
            System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now}] Running Application.Run()...\n");
            Application.Run(mainForm);
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now}] FATAL CRASH: {ex}\n");
        }
    }
}
