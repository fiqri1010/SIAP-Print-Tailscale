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
                        (p.ProcessName.IndexOf("SIAP-Print", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         p.ProcessName.IndexOf("SIAP_Print", StringComparison.OrdinalIgnoreCase) >= 0))
            .ToList();

        foreach (var p in processes)
        {
            try
            {
                statusCallback?.Invoke($"Menghentikan proses {p.ProcessName} (PID: {p.Id})...");
                p.Kill();
                p.WaitForExit(4000);
            }
            catch { }
        }
        Thread.Sleep(500);
    }

    public static bool ExecuteInstall(Action<string>? statusCallback = null)
    {
        string logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SIAP-Print-Tailscale",
            "install.log"
        );

        void Log(string msg)
        {
            try { File.AppendAllText(logPath, $"[{DateTime.Now}] {msg}\n"); } catch { }
            statusCallback?.Invoke(msg);
        }

        try
        {
            Log("Memulai eksekusi instalasi...");

            // 1. Matikan semua instance lama agar tidak ada 2 aplikasi berjalan bersama
            KillRunningInstances(Log);

            // 2. Buat direktori target
            Log($"Menyiapkan folder instalasi: {TargetDir}");
            if (!Directory.Exists(TargetDir))
            {
                Directory.CreateDirectory(TargetDir);
            }

            // 3. Ekstrak file binary utama dari embedded resource
            Log("Memasang berkas SIAP-Print-Tailscale.exe...");
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();
            Log($"Resource yang tersedia: {string.Join(", ", resourceNames)}");

            string? exeResName = resourceNames.FirstOrDefault(n => n.EndsWith("SIAP-Print-Tailscale.exe", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(exeResName))
            {
                using var stream = assembly.GetManifestResourceStream(exeResName);
                if (stream != null)
                {
                    using var fileStream = new FileStream(TargetExe, FileMode.Create, FileAccess.Write);
                    stream.CopyTo(fileStream);
                    Log($"Berhasil mengekstrak binary ke {TargetExe}");
                }
                else
                {
                    Log($"ERROR: Resource stream {exeResName} bernilai null!");
                }
            }
            else
            {
                Log("ERROR: Resource SIAP-Print-Tailscale.exe tidak ditemukan dalam assembly!");
            }

            // Ekstrak app.ico jika ada
            string iconPath = Path.Combine(TargetDir, "app.ico");
            string? icoResName = resourceNames.FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(icoResName))
            {
                using var stream = assembly.GetManifestResourceStream(icoResName);
                if (stream != null)
                {
                    using var fileStream = new FileStream(iconPath, FileMode.Create, FileAccess.Write);
                    stream.CopyTo(fileStream);
                }
            }

            // 4. Buat Pintasan Desktop dan Start Menu
            Log("Membuat pintasan Desktop dan Start Menu...");
            CreateShortcuts(TargetExe, iconPath);

            // 5. Daftarkan di HKCU Run (Startup)
            Log("Mendaftarkan otomatisasi peluncuran Windows...");
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (runKey != null)
                {
                    runKey.SetValue("SIAP-Print-Tailscale", $"\"{TargetExe}\" --minimized");
                }
            }
            catch (Exception ex)
            {
                Log($"Peringatan startup registry: {ex.Message}");
            }

            // 6. Jalankan aplikasi baru
            Log("Meluncurkan aplikasi SIAP Print Tailscale...");
            Process.Start(new ProcessStartInfo
            {
                FileName = TargetExe,
                WorkingDirectory = TargetDir,
                UseShellExecute = true
            });

            Log("Instalasi berhasil selesai!");
            return true;
        }
        catch (Exception ex)
        {
            Log($"FATAL ERROR instalasi: {ex}");
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
