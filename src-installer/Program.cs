using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Forms;

namespace SiapPrintInstaller;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool isSilent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        bool noElevate = args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));

        // Jika dibuka interaktif oleh pengguna dan belum berstatus Administrator, minta elevasi UAC
        if (!isSilent && !noElevate && !IsAdministrator())
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location,
                    Arguments = string.Join(" ", args),
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                return;
            }
            catch
            {
                // Jika pengguna menolak dialog UAC atau sistem tidak mendukung prompt, lanjutkan sebagai invoker
            }
        }

        ApplicationConfiguration.Initialize();

        if (isSilent)
        {
            InstallerLogic.ExecuteInstall(status => { });
            return;
        }

        Application.Run(new InstallForm());
    }

    private static bool IsAdministrator()
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
}
