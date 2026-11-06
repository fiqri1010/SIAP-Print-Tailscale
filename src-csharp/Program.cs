using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SiapPrintServer;

static class Program
{
    private const string GlobalMutexName = @"Global\SIAP-Print-Tailscale-SingleInstance";
    private const string LocalMutexName = @"Local\SIAP_Print_Tailscale_SingleInstance_Mutex";
    private const string HeadlessMutexName = @"Global\SIAP-Print-Tailscale-Headless";
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
        string crashLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SIAP-Print-Tailscale",
            "crash.log"
        );

        try
        {
            File.AppendAllText(crashLog, $"[{DateTime.Now}] Starting Main(args: {string.Join(" ", args)})...\n");

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

            // 2. Mode Headless (Background Boot Service sebelum login Windows)
            bool isHeadless = args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase) ||
                                           a.Equals("--hidden", StringComparison.OrdinalIgnoreCase) ||
                                           a.Equals("--service", StringComparison.OrdinalIgnoreCase));

            if (isHeadless)
            {
                using var headlessMutex = new Mutex(true, HeadlessMutexName, out bool isNewHeadless);
                if (!isNewHeadless && !forceReplace)
                {
                    File.AppendAllText(crashLog, $"[{DateTime.Now}] Headless server already running, exiting.\n");
                    return;
                }

                File.AppendAllText(crashLog, $"[{DateTime.Now}] Starting Headless Print Server (Boot Service)...\n");
                var cfgHeadless = ConfigManager.LoadConfig();
                var server = new HttpPrintServer();
                _ = server.StartAsync(cfgHeadless.Port);

                using var cancelEvent = new ManualResetEventSlim(false);
                AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                {
                    _ = server.StopAsync();
                    cancelEvent.Set();
                };

                cancelEvent.Wait();
                return;
            }

            // 3. Mode GUI Interaktif (Desktop & System Tray)
            // Menggunakan Local Mutex untuk memastikan hanya ada 1 jendela GUI di sesi pengguna aktif
            using var localMutex = new Mutex(true, LocalMutexName, out bool isNewLocal);

            if (!forceReplace)
            {
                runningInstances = Process.GetProcesses()
                    .Where(p => p.Id != current.Id &&
                                (p.ProcessName.StartsWith("SIAP-Print-Tailscale", StringComparison.OrdinalIgnoreCase) ||
                                 p.ProcessName.Equals("SIAP-Print-Tailscale", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            var existingGuiInstance = runningInstances.FirstOrDefault(p =>
                p.SessionId == current.SessionId &&
                (p.MainWindowHandle != IntPtr.Zero || !isNewLocal));

            bool duplicateGuiDetected = (!isNewLocal || existingGuiInstance != null) && !forceReplace;

            if (duplicateGuiDetected)
            {
                File.AppendAllText(crashLog, $"[{DateTime.Now}] Another GUI instance detected in Session {current.SessionId}, restoring existing window.\n");

                // Broadcast pesan khusus ke seluruh jendela untuk memunculkan aplikasi yang ada di System Tray
                uint wmShow = RegisterWindowMessage("SIAP_PRINT_TAILSCALE_SHOW_WINDOW");
                PostMessage(HWND_BROADCAST, wmShow, IntPtr.Zero, IntPtr.Zero);

                IntPtr existingHwnd = FindWindow(null, WindowTitle);
                if (existingHwnd == IntPtr.Zero && existingGuiInstance != null)
                {
                    existingHwnd = existingGuiInstance.MainWindowHandle;
                }
                if (existingHwnd != IntPtr.Zero)
                {
                    ShowWindow(existingHwnd, SW_RESTORE);
                    SetForegroundWindow(existingHwnd);
                }

                // Keluar seketika agar tidak pernah ada 2 jendela aplikasi berjalan bersamaan di layar pengguna
                return;
            }

            ApplicationConfiguration.Initialize();

            bool startMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            var cfg = ConfigManager.LoadConfig();
            if (cfg.MinimizeToTrayOnStartup && startMinimized)
            {
                startMinimized = true;
            }

            File.AppendAllText(crashLog, $"[{DateTime.Now}] Creating MainForm...\n");
            var mainForm = new MainForm(startMinimized);
            File.AppendAllText(crashLog, $"[{DateTime.Now}] Running Application.Run()...\n");
            Application.Run(mainForm);
        }
        catch (Exception ex)
        {
            File.AppendAllText(crashLog, $"[{DateTime.Now}] FATAL CRASH: {ex}\n");
        }
    }
}
