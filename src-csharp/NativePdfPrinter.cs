using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SiapPrintServer;

/// <summary>
/// PDF Engine Mandiri Native Windows 10/11.
/// Menggunakan Windows.Data.Pdf internal untuk merender halaman secara presisi ke bitmap 300 DPI,
/// kemudian mengirimkannya melalui Windows Print Spooler (GDI+ PrintDocument) ke driver printer resmi.
/// Mencegah kerusakan PDF dan menjamin jumlah halaman tercetak persis sesuai dokumen asli.
/// </summary>
public record PrintOptions(string? PaperSize, MarginsMmDto? MarginsMm, string? Pages)
{
    public static readonly PrintOptions Default = new(null, null, null);
}

public static class NativePdfPrinter
{
    /// <summary>
    /// "1-3,5" -> [0,1,2,4] (0-based, urut, unik, dalam batas). Kosong / tidak valid seluruhnya = semua halaman.
    /// </summary>
    public static List<int> ParsePageRange(string? spec, int totalPages)
    {
        var result = new SortedSet<int>();
        if (!string.IsNullOrWhiteSpace(spec))
        {
            foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = raw.Split('-', StringSplitOptions.TrimEntries);
                if (parts.Length == 1 && int.TryParse(parts[0], out int single))
                {
                    if (single >= 1 && single <= totalPages) result.Add(single - 1);
                }
                else if (parts.Length == 2 && int.TryParse(parts[0], out int a) && int.TryParse(parts[1], out int b))
                {
                    if (a > b) (a, b) = (b, a);
                    for (int p = Math.Max(1, a); p <= Math.Min(totalPages, b); p++) result.Add(p - 1);
                }
            }
        }
        if (result.Count == 0)
        {
            for (int i = 0; i < totalPages; i++) result.Add(i);
        }
        return result.ToList();
    }

    /// <summary>
    /// Ukuran kertas yang diminta dari daftar kertas printer. F4 (Folio) = 215 x 330 mm (8,5 x 13 inci).
    /// Bila driver tidak punya F4, dibuat ukuran kustom; null = pakai kertas bawaan printer.
    /// </summary>
    private static PaperSize? FindPaper(PrinterSettings ps, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        string want = requested.Trim().ToUpperInvariant();
        // satuan PaperSize: seperseratus inci
        (int w, int h) target = want == "F4" ? (850, 1300) : want == "A4" ? (827, 1169) : (0, 0);
        if (target.w == 0) return null;
        foreach (PaperSize p in ps.PaperSizes)
        {
            string name = (p.PaperName ?? "").ToUpperInvariant();
            if (want == "A4" && (p.Kind == PaperKind.A4 || name.Contains("A4"))) return p;
            if (want == "F4" && (name.Contains("F4") || name.Contains("FOLIO"))) return p;
        }
        foreach (PaperSize p in ps.PaperSizes)
        {
            if (Math.Abs(p.Width - target.w) <= 6 && Math.Abs(p.Height - target.h) <= 6) return p;
        }
        return new PaperSize(want == "F4" ? "F4 (215 x 330 mm)" : "A4", target.w, target.h);
    }

    private static int MmToHundredthsInch(double mm) => (int)Math.Round(Math.Clamp(mm, 0, 60) / 25.4 * 100);

    private static readonly SemaphoreSlim PrintLock = new(1, 1);

    private static readonly string TempDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SIAP-Print-Tailscale",
        "temp"
    );

    static NativePdfPrinter()
    {
        try
        {
            if (!Directory.Exists(TempDir))
            {
                Directory.CreateDirectory(TempDir);
            }
        }
        catch { }
    }

    public static async Task<(bool Success, string Message, int PagesPrinted, string FinalPrinter, string JobId, string Status)> PrintAsync(
        byte[] pdfBytes,
        string? requestedPrinter,
        string docTitle,
        PrintOptions? options = null,
        string? requestedJobId = null,
        bool forcePrint = false)
    {
        options ??= PrintOptions.Default;
        string jobId = string.IsNullOrWhiteSpace(requestedJobId)
            ? $"job_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..6]}"
            : requestedJobId;

        if (pdfBytes == null || pdfBytes.Length == 0)
        {
            PrintJobTracker.RegisterJob(jobId, docTitle, requestedPrinter ?? "", 0);
            PrintJobTracker.UpdateJob(jobId, "failed", "Data berkas PDF kosong (0 byte)", true, "Data berkas PDF kosong (0 byte)");
            return (false, "Data berkas PDF kosong (0 byte)", 0, "", jobId, "failed");
        }

        // Validasi header PDF (%PDF-)
        if (pdfBytes.Length < 5 ||
            pdfBytes[0] != 0x25 || pdfBytes[1] != 0x50 || pdfBytes[2] != 0x44 || pdfBytes[3] != 0x46)
        {
            PrintJobTracker.RegisterJob(jobId, docTitle, requestedPrinter ?? "", 0);
            PrintJobTracker.UpdateJob(jobId, "failed", "Format berkas tidak valid: bukan berkas PDF yang sah", true, "Format berkas tidak valid");
            return (false, "Format berkas tidak valid: bukan berkas PDF yang sah", 0, "", jobId, "failed");
        }

        await PrintLock.WaitAsync();
        string tempFilePath = Path.Combine(TempDir, $"{Guid.NewGuid():N}.pdf");

        try
        {
            await File.WriteAllBytesAsync(tempFilePath, pdfBytes);
            pdfBytes = null!; // Bebaskan referensi memori buffer besar

            // 1. Muat dokumen melalui Windows.Data.Pdf
            var storageFile = await StorageFile.GetFileFromPathAsync(tempFilePath);
            var pdfDoc = await PdfDocument.LoadFromFileAsync(storageFile);

            int totalPages = (int)pdfDoc.PageCount;
            if (totalPages <= 0)
            {
                PrintJobTracker.RegisterJob(jobId, docTitle, requestedPrinter ?? "", 0);
                PrintJobTracker.UpdateJob(jobId, "failed", "Berkas PDF tidak memiliki halaman yang dapat dicetak", true, "No pages");
                return (false, "Berkas PDF tidak memiliki halaman yang dapat dicetak", 0, "", jobId, "failed");
            }

            // 2. Tentukan printer tujuan
            string finalPrinter = ResolvePrinter(requestedPrinter);
            if (string.IsNullOrWhiteSpace(finalPrinter))
            {
                PrintJobTracker.RegisterJob(jobId, docTitle, requestedPrinter ?? "", totalPages);
                PrintJobTracker.UpdateJob(jobId, "failed", "Tidak ada printer aktif yang terdeteksi di sistem Windows", true, "No active printer");
                return (false, "Tidak ada printer aktif yang terdeteksi di sistem Windows", 0, "", jobId, "failed");
            }

            // Pre-check status printer: Jangan kirim bila printer offline atau error fisik (kecuali forcePrint=true)
            if (!forcePrint)
            {
                var (pStatus, pDesc, isOffline, _, _, _) = WindowsPrinterHelper.GetPrinterDetailedStatus(finalPrinter);
                if (isOffline || pStatus == "error")
                {
                    PrintJobTracker.RegisterJob(jobId, docTitle, finalPrinter, totalPages);
                    PrintJobTracker.UpdateJob(jobId, "failed", $"Printer '{finalPrinter}' tidak siap: {pDesc}", true, pDesc);
                    return (false, $"Printer '{finalPrinter}' tidak siap: {pDesc}", 0, finalPrinter, jobId, "failed");
                }
            }

            // Daftarkan print job ke tracker
            PrintJobTracker.RegisterJob(jobId, docTitle, finalPrinter, totalPages);

            // 3. Siapkan PrintDocument GDI+
            using var printDoc = new PrintDocument();
            printDoc.PrinterSettings.PrinterName = finalPrinter;
            printDoc.DocumentName = $"{docTitle} [{jobId}]";

            // Gunakan StandardPrintController agar cetak berjalan 100% silent tanpa pop-up dialog
            printDoc.PrintController = new StandardPrintController();

            // v2.1: ukuran kertas & margin dari SIAP NEO
            var paper = FindPaper(printDoc.PrinterSettings, options.PaperSize);
            if (paper != null)
            {
                printDoc.DefaultPageSettings.PaperSize = paper;
            }
            if (options.MarginsMm != null)
            {
                var m = options.MarginsMm;
                printDoc.DefaultPageSettings.Margins = new Margins(
                    MmToHundredthsInch(m.Left), MmToHundredthsInch(m.Right),
                    MmToHundredthsInch(m.Top), MmToHundredthsInch(m.Bottom));
            }
            else
            {
                // v2.3.3: Margin bawaan = 0 mm tambahan (cetak 1:1 sesuai ukuran & margin asli dokumen PDF).
                // Dokumen PDF sudah memiliki margin halamannya sendiri; menumpuk margin GDI+ tambahan
                // justru memperkecil isi halaman dan memperlebar margin putih secara tidak wajar.
                printDoc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            }

            // v2.1: hanya halaman terpilih
            var pageList = ParsePageRange(options.Pages, totalPages);
            int listPos = 0;
            int currentPageIndex = 0;
            Exception? pageException = null;

            // v2.3.3: Orientasi Landscape/Portrait WAJIB disetel di QueryPageSettings sebelum
            // Windows membuat Device Context (HDC) halaman; menyetelnya di dalam PrintPage
            // diabaikan oleh GDI+ sehingga halaman Landscape menyusut ke kertas Portrait.
            printDoc.QueryPageSettings += (sender, e) =>
            {
                try
                {
                    if (listPos < pageList.Count)
                    {
                        using var pdfPage = pdfDoc.GetPage((uint)pageList[listPos]);
                        e.PageSettings.Landscape = pdfPage.Size.Width > pdfPage.Size.Height;
                    }
                }
                catch { }
            };

            printDoc.PrintPage += (sender, e) =>
            {
                try
                {
                    if (listPos >= pageList.Count)
                    {
                        e.HasMorePages = false;
                        return;
                    }
                    currentPageIndex = pageList[listPos];

                    using var pdfPage = pdfDoc.GetPage((uint)currentPageIndex);

                    // Render halaman ke bitmap 300 DPI presisi (Windows.Data.Pdf Size memakai satuan 96 DIP/inci)
                    using var memStream = new InMemoryRandomAccessStream();
                    var renderOptions = new PdfPageRenderOptions();
                    uint targetWidth = (uint)Math.Max(300, Math.Round(pdfPage.Size.Width * 300.0 / 96.0));
                    renderOptions.DestinationWidth = targetWidth;

                    pdfPage.RenderToStreamAsync(memStream, renderOptions).AsTask().GetAwaiter().GetResult();

                    using var netStream = memStream.AsStream();
                    using var pageBmp = new Bitmap(netStream);

                    bool isLandscape = e.PageSettings.Landscape;

                    // Area target fisik (dalam 1/100 inci relatif terhadap tepi kertas fisik 0,0):
                    // - Margin bawaan (null): penuh seluruh kertas (e.PageBounds) agar 1:1 asli dokumen PDF.
                    // - Margin manual: tepi kertas dikurangi tepat margin mm yang diminta pengguna.
                    RectangleF targetPhys;
                    if (options.MarginsMm != null)
                    {
                        var m = options.MarginsMm;
                        float mLeft = MmToHundredthsInch(m.Left);
                        float mRight = MmToHundredthsInch(m.Right);
                        float mTop = MmToHundredthsInch(m.Top);
                        float mBottom = MmToHundredthsInch(m.Bottom);
                        float w = e.PageBounds.Width - mLeft - mRight;
                        float h = e.PageBounds.Height - mTop - mBottom;
                        targetPhys = (w > 10f && h > 10f)
                            ? new RectangleF(mLeft, mTop, w, h)
                            : new RectangleF(0f, 0f, e.PageBounds.Width, e.PageBounds.Height);
                    }
                    else
                    {
                        targetPhys = new RectangleF(0f, 0f, e.PageBounds.Width, e.PageBounds.Height);
                    }

                    // Skala proporsional agar pas dan terpusat pada area target fisik
                    float scale = Math.Min(
                        targetPhys.Width / pageBmp.Width,
                        targetPhys.Height / pageBmp.Height
                    );

                    float destWidth = pageBmp.Width * scale;
                    float destHeight = pageBmp.Height * scale;
                    float physX = targetPhys.Left + (targetPhys.Width - destWidth) / 2f;
                    float physY = targetPhys.Top + (targetPhys.Height - destHeight) / 2f;

                    // Kompensasi HardMarginX/Y (origin e.Graphics saat OriginAtMargins=false berada pada
                    // sudut kiri-atas PrintableArea, bukan tepi fisik 0,0; pada Landscape X & Y ditukar).
                    float hardX = isLandscape ? e.PageSettings.HardMarginY : e.PageSettings.HardMarginX;
                    float hardY = isLandscape ? e.PageSettings.HardMarginX : e.PageSettings.HardMarginY;
                    float drawX = physX - hardX;
                    float drawY = physY - hardY;

                    if (e.Graphics != null)
                    {
                        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                        e.Graphics.DrawImage(pageBmp, new RectangleF(drawX, drawY, destWidth, destHeight));
                    }

                    listPos++;
                    e.HasMorePages = (listPos < pageList.Count);
                }
                catch (Exception ex)
                {
                    pageException = ex;
                    e.HasMorePages = false;
                }
            };

            printDoc.Print();

            if (pageException != null)
            {
                PrintJobTracker.UpdateJob(jobId, "failed", $"Kesalahan saat mencetak: {pageException.Message}", true, pageException.Message);
                return (false, $"Kesalahan saat mencetak: {pageException.Message}", listPos, finalPrinter, jobId, "failed");
            }

            // Mulai pantau status pelepasan antrean spooler ke hardware printer secara async
            PrintJobTracker.StartSpoolerMonitoring(jobId, finalPrinter, pageList.Count);

            string paperInfo = paper != null ? $", kertas {paper.PaperName}" : "";
            return (true, $"Dicetak via Native Windows PDF Engine ({pageList.Count} dari {totalPages} halaman{paperInfo})", pageList.Count, finalPrinter, jobId, "printed");
        }
        catch (Exception ex)
        {
            PrintJobTracker.UpdateJob(jobId, "failed", $"Gagal mencetak PDF: {ex.Message}", true, ex.Message);
            return (false, $"Gagal mencetak PDF: {ex.Message}", 0, requestedPrinter ?? "", jobId, "failed");
        }
        finally
        {
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch { }

            PrintLock.Release();
            MemoryHelper.TrimWorkingSet();
        }
    }

    private static string ResolvePrinter(string? requested)
    {
        var installed = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in PrinterSettings.InstalledPrinters)
        {
            installed.Add(name);
        }

        if (!string.IsNullOrWhiteSpace(requested) && installed.Contains(requested))
        {
            return requested;
        }

        var cfg = ConfigManager.LoadConfig();
        if (!string.IsNullOrWhiteSpace(cfg.SelectedPrinter) && installed.Contains(cfg.SelectedPrinter))
        {
            return cfg.SelectedPrinter;
        }

        var defaultPrinter = new PrinterSettings().PrinterName;
        if (!string.IsNullOrWhiteSpace(defaultPrinter) && installed.Contains(defaultPrinter))
        {
            return defaultPrinter;
        }

        foreach (string name in installed)
        {
            string lower = name.ToLowerInvariant();
            if (!lower.Contains("pdf") && !lower.Contains("xps") && !lower.Contains("onenote") && !lower.Contains("fax"))
            {
                return name;
            }
        }

        return defaultPrinter ?? "";
    }
}
