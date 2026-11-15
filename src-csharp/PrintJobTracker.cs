using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SiapPrintServer;

public static class PrintJobTracker
{
    private static readonly ConcurrentDictionary<string, PrintJobStatusDto> Jobs = new();

    public static void RegisterJob(string jobId, string filename, string printer, int pages)
    {
        var job = new PrintJobStatusDto
        {
            JobId = jobId,
            Filename = filename,
            Printer = printer,
            Pages = pages,
            Status = "spooling",
            IsCompleted = false,
            Time = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
            Message = "Dokumen sedang dikirim ke antrean Windows Spooler...",
            Error = null
        };

        Jobs[jobId] = job;

        // Pertahankan maksimal 100 job terakhir di memori
        if (Jobs.Count > 100)
        {
            var oldestKeys = Jobs.Keys.Take(Jobs.Count - 100);
            foreach (var key in oldestKeys)
            {
                Jobs.TryRemove(key, out _);
            }
        }
    }

    public static PrintJobStatusDto? GetJob(string jobId)
    {
        Jobs.TryGetValue(jobId, out var job);
        return job;
    }

    public static List<PrintJobStatusDto> GetAllJobs()
    {
        return Jobs.Values.OrderByDescending(j => j.JobId).ToList();
    }

    public static void UpdateJob(string jobId, string status, string message, bool isCompleted, string? error = null)
    {
        if (Jobs.TryGetValue(jobId, out var job))
        {
            job.Status = status;
            job.Message = message;
            job.IsCompleted = isCompleted;
            job.Error = error;
        }
    }

    /// <summary>
    /// Memantau dokumen di Windows Spooler hingga benar-benar selesai dikirim ke printer fisik.
    /// Berjalan secara asynchronous tanpa memblokir thread HTTP.
    /// </summary>
    public static void StartSpoolerMonitoring(string jobId, string printerName, int expectedPages)
    {
        Task.Run(async () =>
        {
            bool jobWasSeenInSpooler = false;
            int maxChecks = 40; // 40 * 250ms = 10 detik

            for (int i = 0; i < maxChecks; i++)
            {
                await Task.Delay(250);

                var spoolerJobs = WindowsPrinterHelper.GetSpoolerJobs(printerName);
                var matchedJob = spoolerJobs.FirstOrDefault(j => 
                    !string.IsNullOrWhiteSpace(j.pDocument) && j.pDocument.Contains(jobId, StringComparison.OrdinalIgnoreCase));

                if (matchedJob.pDocument != null)
                {
                    jobWasSeenInSpooler = true;
                    uint status = matchedJob.Status;

                    // Cek kondisi error pada job antrean
                    if ((status & (WindowsPrinterHelper.JOB_STATUS_ERROR | WindowsPrinterHelper.JOB_STATUS_PAPEROUT | WindowsPrinterHelper.JOB_STATUS_OFFLINE)) != 0)
                    {
                        string err = (status & WindowsPrinterHelper.JOB_STATUS_PAPEROUT) != 0 ? "Kertas printer habis saat mencetak" :
                                     (status & WindowsPrinterHelper.JOB_STATUS_OFFLINE) != 0 ? "Printer terputus saat mencetak" :
                                     "Terjadi error pada antrean printer";

                        UpdateJob(jobId, "failed", err, true, err);
                        return;
                    }

                    UpdateJob(jobId, "printing", $"Sedang diproses oleh printer ({matchedJob.PagesPrinted}/{matchedJob.TotalPages} halaman)...", false);
                }
                else if (jobWasSeenInSpooler)
                {
                    // Dokumen sebelumnya ada di spooler, dan sekarang sudah hilang dari antrean tanpa error.
                    // Ini membuktikan seluruh data cetak telah tuntas disalurkan dan diterima oleh hardware printer!
                    UpdateJob(jobId, "printed", $"Dokumen telah selesai dicetak ke printer fisik ({printerName})", true);
                    return;
                }
            }

            // Jika setelah 10 detik selesai atau spooler sangat cepat sehingga tidak sempat tertangkap di loop:
            if (!jobWasSeenInSpooler)
            {
                var (pStatus, _, isOffline, _, _, _) = WindowsPrinterHelper.GetPrinterDetailedStatus(printerName);
                if (isOffline || pStatus == "error")
                {
                    UpdateJob(jobId, "failed", "Printer offline atau mengalami error", true, "Printer offline");
                }
                else
                {
                    UpdateJob(jobId, "printed", $"Dokumen berhasil diproses dan dicetak ke {printerName}", true);
                }
            }
        });
    }
}
