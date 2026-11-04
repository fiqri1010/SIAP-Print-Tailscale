using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SiapPrintServer;

/// <summary>
/// Pembantu optimasi memori untuk runtime .NET dan Windows Working Set.
/// Memastikan alokasi heap yang tidak terpakai dikembalikan ke OS sehingga aplikasi
/// dapat berjalan dengan footprint RAM seminimal mungkin (~5 - 15 MB).
/// </summary>
public static class MemoryHelper
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    /// <summary>
    /// Melakukan pembersihan Garbage Collection agresif dan memangkas Windows Working Set
    /// proses ke batas aktif minimal. Sangat cocok dipanggil setelah cetak selesai,
    /// saat aplikasi diminimalkan ke tray, atau saat idle.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);

            using var proc = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(proc.Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { }
    }
}
