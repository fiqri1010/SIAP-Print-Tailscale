using System;
using System.Collections.Generic;
using System.Drawing.Printing;

namespace SiapPrintServer;

public static class WindowsPrinterHelper
{
    public static List<PrinterInfo> GetPrinters()
    {
        var list = new List<PrinterInfo>();
        string defaultPrinter = "";
        try
        {
            defaultPrinter = new PrinterSettings().PrinterName ?? "";
        }
        catch { }

        try
        {
            foreach (string name in PrinterSettings.InstalledPrinters)
            {
                string lower = name.ToLowerInvariant();
                if (!lower.Contains("pdf") && !lower.Contains("onenote") && !lower.Contains("fax") && !lower.Contains("xps"))
                {
                    list.Add(new PrinterInfo
                    {
                        Name = name,
                        Driver = "Windows Print Spooler Driver",
                        Port = "",
                        IsDefault = string.Equals(name, defaultPrinter, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
        }
        catch { }

        return list;
    }
}
