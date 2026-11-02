using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SiapPrintServer;

static class Program
{
    private const string MutexName = @"Global\SIAP-Print-Tailscale-SingleInstance";
    private const string WindowTitle = "SIAP Print Tailscale Server — Native C#/.NET";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    private const int SW_RESTORE = 9;

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
            System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now}] Starting Main()...\n");

            // 1. Proteksi Single Instance
            using var mutex = new Mutex(true, MutexName, out bool isNewInstance);
            if (!isNewInstance)
            {
                System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now}] Mutex exists, bringing existing to front.\n");
                IntPtr existingHwnd = FindWindow(null, WindowTitle);
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
