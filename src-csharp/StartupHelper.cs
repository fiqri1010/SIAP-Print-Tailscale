using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace SiapPrintServer;

/// <summary>
/// Pembantu konfigurasi Windows Startup dan Windows Task Scheduler (Boot Task).
/// Memastikan server dapat langsung hidup saat Windows dinyalakan bahkan sebelum pengguna login.
/// </summary>
public static class StartupHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "SIAP-Print-Tailscale";
    public const string BootTaskName = "SIAP-Print-Tailscale-Boot";

    /// <summary>
    /// Memeriksa apakah proses saat ini berjalan dengan hak akses Administrator (Elevated).
    /// </summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Mengonfigurasi Task Scheduler agar server hidup otomatis saat komputer boot (sebelum login Windows).
    /// Menggunakan akun NT AUTHORITY\SYSTEM dengan hak akses tertinggi.
    /// </summary>
    public static bool ConfigureBootTask(bool enable, string? customExePath = null)
    {
        try
        {
            string exePath = customExePath 
                ?? Process.GetCurrentProcess().MainModule?.FileName 
                ?? Path.Combine(AppContext.BaseDirectory, "SIAP-Print-Tailscale.exe");

            if (enable)
            {
                // Script PowerShell untuk mendaftarkan Scheduled Task AtStartup (sebelum login)
                string safeExe = exePath.Replace("'", "''");
                string psScript = 
                    $"$action = New-ScheduledTaskAction -Execute '{safeExe}' -Argument '--headless'; " +
                    "$trigger = New-ScheduledTaskTrigger -AtStartup; " +
                    "$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest; " +
                    "$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Days 0) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1); " +
                    $"Register-ScheduledTask -TaskName '{BootTaskName}' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force";

                return ExecutePowerShellElevated(psScript);
            }
            else
            {
                string psScript = $"Unregister-ScheduledTask -TaskName '{BootTaskName}' -Confirm:$false -ErrorAction SilentlyContinue";
                return ExecutePowerShellElevated(psScript);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Gagal konfigurasi Boot Task: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Memeriksa apakah Scheduled Task 'SIAP-Print-Tailscale-Boot' sudah terdaftar di sistem.
    /// </summary>
    public static bool IsBootTaskInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"[bool](Get-ScheduledTask -TaskName '{BootTaskName}' -ErrorAction SilentlyContinue)\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(4000);
                return bool.TryParse(output, out bool res) && res;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Mengonfigurasi peluncuran otomatis saat pengguna login ke Windows Desktop (shell:startup & HKCU Run).
    /// </summary>
    public static void ConfigureUserStartup(bool enable, bool minimizeToTray, string? customExePath = null)
    {
        try
        {
            string exePath = customExePath 
                ?? Process.GetCurrentProcess().MainModule?.FileName 
                ?? Path.Combine(AppContext.BaseDirectory, "SIAP-Print-Tailscale.exe");

            string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupDir, "SIAP-Print-Tailscale.lnk");

            if (enable)
            {
                // 1. Buat shortcut di shell:startup (paling andal di Windows)
                try
                {
                    Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        dynamic shell = Activator.CreateInstance(shellType)!;
                        dynamic shortcut = shell.CreateShortcut(shortcutPath);
                        shortcut.TargetPath = exePath;
                        shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                        shortcut.Arguments = minimizeToTray ? "--minimized" : "";
                        shortcut.Description = "SIAP Print Tailscale Server";
                        shortcut.Save();
                    }
                }
                catch { }

                // 2. Daftarkan di HKCU Run sebagai fallback
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                    if (key != null)
                    {
                        string command = minimizeToTray ? $"\"{exePath}\" --minimized" : $"\"{exePath}\"";
                        key.SetValue(AppName, command);
                    }
                }
                catch { }
            }
            else
            {
                // Hapus shortcut Startup
                if (File.Exists(shortcutPath))
                {
                    try { File.Delete(shortcutPath); } catch { }
                }

                // Hapus registry HKCU Run
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                    key?.DeleteValue(AppName, false);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Memeriksa apakah startup user (shortcut atau HKCU) aktif.
    /// </summary>
    public static bool IsUserStartupConfigured()
    {
        try
        {
            string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupDir, "SIAP-Print-Tailscale.lnk");
            if (File.Exists(shortcutPath)) return true;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Memeriksa apakah aturan firewall untuk port server sudah terpasang.
    /// </summary>
    public static bool CheckFirewallRule(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = "advfirewall firewall show rule name=\"SIAP Print Server\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                return output.Contains("SIAP Print Server") && output.Contains(port.ToString());
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Menambahkan aturan Windows Firewall untuk port server.
    /// </summary>
    public static void AddFirewallRule(int port)
    {
        try
        {
            if (CheckFirewallRule(port)) return;

            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = $"advfirewall firewall add rule name=\"SIAP Print Server\" dir=in action=allow protocol=TCP localport={port}",
                UseShellExecute = true,
                Verb = IsAdministrator() ? "" : "runas"
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }

    private static bool ExecutePowerShellElevated(string script)
    {
        try
        {
            if (IsAdministrator())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{script}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(10000);
                    return proc.ExitCode == 0;
                }
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{script}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(15000);
                    return proc.ExitCode == 0;
                }
            }
        }
        catch { }

        return false;
    }
}
