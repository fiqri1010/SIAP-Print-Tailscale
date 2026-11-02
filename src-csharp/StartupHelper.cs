using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SiapPrintServer;

public static class StartupHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "SIAP-Print-Tailscale";

    public static void ConfigureStartup(bool enable, bool minimizeToTray)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            if (enable)
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName 
                    ?? Path.Combine(AppContext.BaseDirectory, "SIAP-Print-Tailscale.exe");

                string command = minimizeToTray ? $"\"{exePath}\" --minimized" : $"\"{exePath}\"";
                key.SetValue(AppName, command);
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch { }
    }

    public static bool IsStartupConfigured()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

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

    public static void AddFirewallRule(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = $"advfirewall firewall add rule name=\"SIAP Print Server\" dir=in action=allow protocol=TCP localport={port}",
                UseShellExecute = true,
                Verb = "runas" // Request elevation
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }
}
