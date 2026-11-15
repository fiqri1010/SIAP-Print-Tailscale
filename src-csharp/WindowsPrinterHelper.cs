using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SiapPrintServer;

public static class WindowsPrinterHelper
{
    // =========================================================================
    // Win32 Spooler Native P/Invoke
    // =========================================================================
    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GetPrinter(IntPtr hPrinter, int dwLevel, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool EnumJobs(IntPtr hPrinter, uint FirstJob, uint NoJobs, uint Level, IntPtr pJob, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PRINTER_INFO_2
    {
        public string pServerName;
        public string pPrinterName;
        public string pShareName;
        public string pPortName;
        public string pDriverName;
        public string pComment;
        public string pLocation;
        public IntPtr pDevMode;
        public string pSepFile;
        public string pPrintProcessor;
        public string pDatatype;
        public string pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct JOB_INFO_1
    {
        public uint JobId;
        public string pPrinterName;
        public string pMachineName;
        public string pUserName;
        public string pDocument;
        public string pDatatype;
        public string pStatus;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint TotalPages;
        public uint PagesPrinted;
    }

    // Win32 Printer Attributes
    private const uint PRINTER_ATTRIBUTE_WORK_OFFLINE = 0x00000400;

    // Win32 Printer Status Flags
    private const uint PRINTER_STATUS_PAUSED = 0x00000001;
    private const uint PRINTER_STATUS_ERROR = 0x00000002;
    private const uint PRINTER_STATUS_PENDING_DELETION = 0x00000004;
    private const uint PRINTER_STATUS_PAPER_JAM = 0x00000008;
    private const uint PRINTER_STATUS_PAPER_OUT = 0x00000010;
    private const uint PRINTER_STATUS_MANUAL_FEED = 0x00000020;
    private const uint PRINTER_STATUS_PAPER_PROBLEM = 0x00000040;
    private const uint PRINTER_STATUS_OFFLINE = 0x00000080;
    private const uint PRINTER_STATUS_IO_ACTIVE = 0x00000100;
    private const uint PRINTER_STATUS_BUSY = 0x00000200;
    private const uint PRINTER_STATUS_PRINTING = 0x00000400;
    private const uint PRINTER_STATUS_OUTPUT_BIN_FULL = 0x00000800;
    private const uint PRINTER_STATUS_NOT_AVAILABLE = 0x00001000;
    private const uint PRINTER_STATUS_WAITING = 0x00002000;
    private const uint PRINTER_STATUS_PROCESSING = 0x00004000;
    private const uint PRINTER_STATUS_INITIALIZING = 0x00008000;
    private const uint PRINTER_STATUS_WARMING_UP = 0x00010000;
    private const uint PRINTER_STATUS_TONER_LOW = 0x00020000;
    private const uint PRINTER_STATUS_NO_TONER = 0x00040000;
    private const uint PRINTER_STATUS_PAGE_PUNT = 0x00080000;
    private const uint PRINTER_STATUS_USER_INTERVENTION = 0x00100000;
    private const uint PRINTER_STATUS_OUT_OF_MEMORY = 0x00200000;
    private const uint PRINTER_STATUS_DOOR_OPEN = 0x00400000;
    private const uint PRINTER_STATUS_SERVER_UNKNOWN = 0x00800000;
    private const uint PRINTER_STATUS_POWER_SAVE = 0x01000000;

    // Win32 Job Status Flags
    public const uint JOB_STATUS_PAUSED = 0x00000001;
    public const uint JOB_STATUS_ERROR = 0x00000002;
    public const uint JOB_STATUS_DELETING = 0x00000004;
    public const uint JOB_STATUS_SPOOLING = 0x00000008;
    public const uint JOB_STATUS_PRINTING = 0x00000010;
    public const uint JOB_STATUS_OFFLINE = 0x00000020;
    public const uint JOB_STATUS_PAPEROUT = 0x00000040;
    public const uint JOB_STATUS_PRINTED = 0x00000080;
    public const uint JOB_STATUS_DELETED = 0x00000100;
    public const uint JOB_STATUS_BLOCKED_DEVQ = 0x00000200;
    public const uint JOB_STATUS_USER_INTERVENTION = 0x00000400;
    public const uint JOB_STATUS_RESTART = 0x00000800;
    public const uint JOB_STATUS_COMPLETE = 0x00001000;

    private static readonly Regex IpRegex = new(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled);

    /// <summary>
    /// Mengambil status mendalam dari printer tertentu (Idle, Aktif, Offline, Error).
    /// </summary>
    public static (string status, string description, bool isOffline, int jobCount, string port, string driver) GetPrinterDetailedStatus(string printerName)
    {
        string portName = "";
        string driverName = "";
        uint attributes = 0;
        uint statusFlags = 0;
        uint cJobs = 0;
        bool queried = false;

        if (OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
        {
            try
            {
                GetPrinter(hPrinter, 2, IntPtr.Zero, 0, out int needed);
                if (needed > 0)
                {
                    IntPtr pBuf = Marshal.AllocHGlobal(needed);
                    try
                    {
                        if (GetPrinter(hPrinter, 2, pBuf, needed, out needed))
                        {
                            var info = Marshal.PtrToStructure<PRINTER_INFO_2>(pBuf);
                            portName = info.pPortName ?? "";
                            driverName = info.pDriverName ?? "";
                            attributes = info.Attributes;
                            statusFlags = info.Status;
                            cJobs = info.cJobs;
                            queried = true;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pBuf);
                    }
                }
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }

        if (!queried)
        {
            return ("offline", "Printer tidak dapat dibuka melalui Spooler Windows", true, 0, "", "");
        }

        // 1. Cek mode Offline Spooler
        if ((attributes & PRINTER_ATTRIBUTE_WORK_OFFLINE) != 0 || (statusFlags & PRINTER_STATUS_OFFLINE) != 0)
        {
            return ("offline", "Printer dalam mode Offline (Work Offline / Terputus)", true, (int)cJobs, portName, driverName);
        }

        // 2. Cek status Error fisik printer
        if ((statusFlags & PRINTER_STATUS_PAPER_JAM) != 0)
        {
            return ("error", "Kertas macet di dalam printer (Paper Jam)", false, (int)cJobs, portName, driverName);
        }
        if ((statusFlags & PRINTER_STATUS_PAPER_OUT) != 0)
        {
            return ("error", "Kertas habis pada baki printer (Paper Out)", false, (int)cJobs, portName, driverName);
        }
        if ((statusFlags & PRINTER_STATUS_DOOR_OPEN) != 0)
        {
            return ("error", "Pintu cover printer terbuka (Door Open)", false, (int)cJobs, portName, driverName);
        }
        if ((statusFlags & PRINTER_STATUS_PAUSED) != 0)
        {
            return ("error", "Antrean printer sedang dijeda (Paused)", false, (int)cJobs, portName, driverName);
        }
        if ((statusFlags & PRINTER_STATUS_ERROR) != 0)
        {
            return ("error", "Printer mengalami error hardware atau driver", false, (int)cJobs, portName, driverName);
        }

        // 3. Cek apakah ini printer jaringan berbasis IP (mis. Kyocera di LAN)
        var match = IpRegex.Match(portName);
        if (match.Success)
        {
            string ipAddress = match.Value;
            bool isReachable = IsPortReachable(ipAddress, 9100, 500);
            if (!isReachable)
            {
                return ("offline", $"Printer fisik di IP {ipAddress} tidak merespons (Offline/Mati/Kabel Terputus)", true, (int)cJobs, portName, driverName);
            }
        }

        // 4. Cek apakah printer sedang sibuk / mencetak
        if ((statusFlags & (PRINTER_STATUS_PRINTING | PRINTER_STATUS_BUSY | PRINTER_STATUS_PROCESSING | PRINTER_STATUS_IO_ACTIVE)) != 0 || cJobs > 0)
        {
            return ("aktif", $"Sedang mencetak ({cJobs} antrean dokumen)", false, (int)cJobs, portName, driverName);
        }

        // 5. Normal / Siap (Idle)
        string readyMsg = match.Success 
            ? $"Printer online & siap mencetak ({match.Value})" 
            : "Printer siap mencetak (Idle)";

        return ("idle", readyMsg, false, 0, portName, driverName);
    }

    /// <summary>
    /// Menguji cepat konektivitas TCP ke port RAW printer jaringan (biasanya port 9100).
    /// </summary>
    private static bool IsPortReachable(string hostOrIp, int port, int timeoutMs)
    {
        try
        {
            using var tcp = new TcpClient();
            var ar = tcp.BeginConnect(hostOrIp, port, null, null);
            using var wh = ar.AsyncWaitHandle;
            if (!wh.WaitOne(TimeSpan.FromMilliseconds(timeoutMs), false))
            {
                tcp.Close();
                return false;
            }
            tcp.EndConnect(ar);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Mengambil daftar semua printer fisik yang terpasang beserta status real-time.
    /// </summary>
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
                    var (status, desc, isOffline, jobCount, port, driver) = GetPrinterDetailedStatus(name);

                    list.Add(new PrinterInfo
                    {
                        Name = name,
                        Driver = string.IsNullOrWhiteSpace(driver) ? "Windows Print Spooler Driver" : driver,
                        Port = port,
                        IsDefault = string.Equals(name, defaultPrinter, StringComparison.OrdinalIgnoreCase),
                        Status = status,
                        StatusDescription = desc,
                        IsOffline = isOffline,
                        JobCount = jobCount
                    });
                }
            }
        }
        catch { }

        return list;
    }

    /// <summary>
    /// Mengambil daftar dokumen / antrean cetak yang sedang aktif di Windows Spooler untuk printer tertentu.
    /// </summary>
    public static List<JOB_INFO_1> GetSpoolerJobs(string printerName)
    {
        var result = new List<JOB_INFO_1>();
        if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
        {
            return result;
        }

        try
        {
            EnumJobs(hPrinter, 0, 100, 1, IntPtr.Zero, 0, out uint needed, out uint returned);
            if (needed == 0) return result;

            IntPtr pBuf = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (EnumJobs(hPrinter, 0, 100, 1, pBuf, needed, out needed, out returned))
                {
                    int structSize = Marshal.SizeOf(typeof(JOB_INFO_1));
                    for (int i = 0; i < returned; i++)
                    {
                        IntPtr jobPtr = new(pBuf.ToInt64() + (i * structSize));
                        var job = Marshal.PtrToStructure<JOB_INFO_1>(jobPtr);
                        result.Add(job);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pBuf);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }

        return result;
    }
}
