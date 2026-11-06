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
            Log("Memulai eksekusi instalasi SIAP Print Tailscale...");

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

            // 5. Daftarkan di shell:startup dan HKCU Run
            Log("Mendaftarkan otomatisasi peluncuran Windows saat login...");
            try
            {
                string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string startupLnk = Path.Combine(startupDir, "SIAP-Print-Tailscale.lnk");
                CreateSingleShortcut(startupLnk, TargetExe, iconPath, "--minimized");

                using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (runKey != null)
                {
                    runKey.SetValue("SIAP-Print-Tailscale", $"\"{TargetExe}\" --minimized");
                }
                Log("Pintasan Startup login berhasil dikonfigurasi.");
            }
            catch (Exception ex)
            {
                Log($"Peringatan startup user: {ex.Message}");
            }

            // 6. Daftarkan Scheduled Task AtStartup (Mulai sebelum login Windows)
            Log("Mendaftarkan Scheduled Task Boot (hidup sebelum login)...");
            try
            {
                string safeExe = TargetExe.Replace("'", "''");
                string psScript = 
                    $"$action = New-ScheduledTaskAction -Execute '{safeExe}' -Argument '--headless'; " +
                    "$trigger = New-ScheduledTaskTrigger -AtStartup; " +
                    "$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest; " +
                    "$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Days 0) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1); " +
                    "Register-ScheduledTask -TaskName 'SIAP-Print-Tailscale-Boot' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{psScript}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(6000);
                    if (proc.HasExited)
                    {
                        Log($"Hasil registrasi Boot Task (ExitCode: {proc.ExitCode})");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Peringatan Boot Task: {ex.Message}");
            }

            // 7. Konfigurasi Windows Firewall untuk port 1234
            Log("Mendaftarkan aturan Windows Firewall...");
            try
            {
                var psiFw = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "advfirewall firewall add rule name=\"SIAP Print Server\" dir=in action=allow protocol=TCP localport=1234",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var procFw = Process.Start(psiFw);
                procFw?.WaitForExit(5000);
            }
            catch { }

            // 8. Tulis/Update config default agar start_before_login = true
            try
            {
                string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIAP-Print-Tailscale");
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                string cfgFile = Path.Combine(dataDir, "config.json");
                if (File.Exists(cfgFile))
                {
                    string json = File.ReadAllText(cfgFile);
                    json = json.Replace("\"auto_start_startup\": false", "\"auto_start_startup\": true");
                    json = json.Replace("\"start_before_login\": false", "\"start_before_login\": true");
                    File.WriteAllText(cfgFile, json);
                }
                else
                {
                    string initialJson = "{\n  \"port\": 1234,\n  \"selected_printer\": \"\",\n  \"auto_start_startup\": true,\n  \"start_before_login\": true,\n  \"minimize_to_tray_on_startup\": true,\n  \"minimize_on_exit\": true,\n  \"keep_funnel\": true\n}";
                    File.WriteAllText(cfgFile, initialJson);
                }
            }
            catch { }

            // 9. Jalankan aplikasi baru
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
            // Desktop Shortcut
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string desktopLnk = Path.Combine(desktopDir, "SIAP Print Tailscale.lnk");
            CreateSingleShortcut(desktopLnk, exePath, iconPath);

            // Start Menu Shortcut
            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            string startMenuLnk = Path.Combine(startMenuDir, "SIAP Print Tailscale.lnk");
            CreateSingleShortcut(startMenuLnk, exePath, iconPath);
        }
        catch { }
    }

    public static void CreateSingleShortcut(string lnkPath, string exePath, string iconPath, string arguments = "")
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic sc = shell.CreateShortcut(lnkPath);
            sc.TargetPath = exePath;
            sc.WorkingDirectory = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(arguments))
            {
                sc.Arguments = arguments;
            }
            sc.Description = "SIAP Print Tailscale Server";
            sc.IconLocation = File.Exists(iconPath) ? iconPath : $"{exePath},0";
            sc.Save();
        }
        catch { }
    }
}
