using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SiapPrintInstaller;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        bool isSilent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

        if (isSilent)
        {
            InstallerLogic.ExecuteInstall(status => { });
            return;
        }

        Application.Run(new InstallForm());
    }
}

