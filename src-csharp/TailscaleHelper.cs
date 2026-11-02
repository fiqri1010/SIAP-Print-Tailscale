using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace SiapPrintServer;

public static class TailscaleHelper
{
    public static string? GetTailscaleIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ip = addr.Address.ToString();
                        if (ip.StartsWith("100."))
                        {
                            return ip;
                        }
                    }
                }
            }
        }
        catch { }

        // Fallback: jalankan `tailscale.exe ip -4`
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tailscale.exe",
                Arguments = "ip -4",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(3000);
                if (!string.IsNullOrWhiteSpace(output) && output.StartsWith("100."))
                {
                    return output.Split('\n')[0].Trim();
                }
            }
        }
        catch { }

        return null;
    }

    public static string GetLocalIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ip = addr.Address.ToString();
                        if (!ip.StartsWith("100.") && !ip.StartsWith("127."))
                        {
                            return ip;
                        }
                    }
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }

    public static (string? FunnelDomain, int? FunnelPort) GetFunnelStatus()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tailscale.exe",
                Arguments = "serve status --json",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string json = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(4000);

                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Cek Foreground atau root Web
                    if (root.TryGetProperty("Web", out var webProp))
                    {
                        foreach (var prop in webProp.EnumerateObject())
                        {
                            string hostPort = prop.Name; // mis. domain.ts.net:443
                            string domain = hostPort.Split(':')[0];
                            return (domain, 443);
                        }
                    }

                    if (root.TryGetProperty("Foreground", out var fgProp))
                    {
                        foreach (var fgItem in fgProp.EnumerateObject())
                        {
                            if (fgItem.Value.TryGetProperty("Web", out var fgWeb))
                            {
                                foreach (var prop in fgWeb.EnumerateObject())
                                {
                                    string hostPort = prop.Name;
                                    string domain = hostPort.Split(':')[0];
                                    return (domain, 443);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return (null, null);
    }
}
