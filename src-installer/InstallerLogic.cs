using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace SiapPrintInstaller;

public static class InstallerLogic
{
    public static readonly string TargetDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "SIAP-Print-Tailscale"
    );

    public static readonly string TargetExe = Path.Combine(TargetDir, "SIAP-Print-Tailscale.exe");

    public static void KillRunningInstances(Action<string>? statusCallback = null)
    {
        statusCallback?.Invoke("Memeriksa proses aplikasi yang sedang berjalan...");
        var currentPid = Process.GetCurrentProcess().Id;
        var processes = Process.GetProcesses()
            .Where(p => p.Id != currentPid &&
                        (p.ProcessName.StartsWith("SIAP-Print-Tailscale", StringComparison.OrdinalIgnoreCase) ||
                         p.ProcessName.Equals("SIAP-Print-Tailscale", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var p in processes)
        {
            try
            {
                statusCallback?.Invoke($"Menghentikan proses {p.ProcessName} (PID: {p.Id})...");
                p.Kill();
                p.WaitForExit(3000);
            }
            catch { }
        }
        Thread.Sleep(500);
    }

    public static bool ExecuteInstall(Action<string>? statusCallback = null)
    {
        try
        {
            // 1. Matikan semua instance lama agar tidak ada 2 aplikasi berjalan bersama
            KillRunningInstances(statusCallback);

            // 2. Buat direktori target
            statusCallback?.Invoke("Menyiapkan folder instalasi...");
            if (!Directory.Exists(TargetDir))
            {
                Directory.CreateDirectory(TargetDir);
            }

            // 3. Ekstrak file binary utama dari embedded resource
            statusCallback?.Invoke("Memasang berkas SIAP-Print-Tailscale.exe...");
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream("SIAP-Print-Tailscale.exe"))
            {
                if (stream != null)
                {
                    using var fileStream = new FileStream(TargetExe, FileMode.Create, FileAccess.Write);
                    stream.CopyTo(fileStream);
                }
            }

            // Ekstrak app.ico jika ada
            string iconPath = Path.Combine(TargetDir, "app.ico");
            using (var stream = assembly.GetManifestResourceStream("app.ico"))
            {
                if (stream != null)
                {
                    using var fileStream = new FileStream(iconPath, FileMode.Create, FileAccess.Write);
                    stream.CopyTo(fileStream);
                }
            }

            // 4. Buat Pintasan Desktop dan Start Menu
            statusCallback?.Invoke("Membuat pintasan Desktop dan Start Menu...");
            CreateShortcuts(TargetExe, iconPath);

            // 5. Daftarkan di HKCU Run (Startup)
            statusCallback?.Invoke("Mendaftarkan otomatisasi peluncuran Windows...");
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (runKey != null)
                {
                    runKey.SetValue("SIAP-Print-Tailscale", $"\"{TargetExe}\" --minimized");
                }
            }
            catch { }

            // 6. Jalankan aplikasi baru
            statusCallback?.Invoke("Meluncurkan aplikasi SIAP Print Tailscale...");
            Process.Start(new ProcessStartInfo
            {
                FileName = TargetExe,
                WorkingDirectory = TargetDir,
                UseShellExecute = true
            });

            statusCallback?.Invoke("Instalasi berhasil selesai!");
            return true;
        }
        catch (Exception ex)
        {
            statusCallback?.Invoke($"Gagal instalasi: {ex.Message}");
            return false;
        }
    }

    public static void CreateShortcuts(string exePath, string iconPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;

            // Desktop Shortcut
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string desktopLnk = Path.Combine(desktopDir, "SIAP Print Tailscale.lnk");
            dynamic scDesktop = shell.CreateShortcut(desktopLnk);
            scDesktop.TargetPath = exePath;
            scDesktop.WorkingDirectory = Path.GetDirectoryName(exePath);
            scDesktop.Description = "SIAP Print Tailscale Server";
            scDesktop.IconLocation = File.Exists(iconPath) ? iconPath : $"{exePath},0";
            scDesktop.Save();

            // Start Menu Shortcut
            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            string startMenuLnk = Path.Combine(startMenuDir, "SIAP Print Tailscale.lnk");
            dynamic scStart = shell.CreateShortcut(startMenuLnk);
            scStart.TargetPath = exePath;
            scStart.WorkingDirectory = Path.GetDirectoryName(exePath);
            scStart.Description = "SIAP Print Tailscale Server";
            scStart.IconLocation = File.Exists(iconPath) ? iconPath : $"{exePath},0";
            scStart.Save();
        }
        catch { }
    }
}
