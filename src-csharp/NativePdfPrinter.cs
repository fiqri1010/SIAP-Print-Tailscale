using System;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Text;
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
public static class NativePdfPrinter
{
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

    public static async Task<(bool Success, string Message, int PagesPrinted, string FinalPrinter)> PrintAsync(
        byte[] pdfBytes,
        string? requestedPrinter,
        string docTitle)
    {
        if (pdfBytes == null || pdfBytes.Length == 0)
        {
            return (false, "Data berkas PDF kosong (0 byte)", 0, "");
        }

        // Validasi header PDF (%PDF-)
        if (pdfBytes.Length < 5 ||
            pdfBytes[0] != 0x25 || pdfBytes[1] != 0x50 || pdfBytes[2] != 0x44 || pdfBytes[3] != 0x46)
        {
            return (false, "Format berkas tidak valid: bukan berkas PDF yang sah", 0, "");
        }

        await PrintLock.WaitAsync();
        string tempFilePath = Path.Combine(TempDir, $"{Guid.NewGuid():N}.pdf");

        try
        {
            await File.WriteAllBytesAsync(tempFilePath, pdfBytes);

            // 1. Muat dokumen melalui Windows.Data.Pdf
            var storageFile = await StorageFile.GetFileFromPathAsync(tempFilePath);
            var pdfDoc = await PdfDocument.LoadFromFileAsync(storageFile);

            int totalPages = (int)pdfDoc.PageCount;
            if (totalPages <= 0)
            {
                return (false, "Berkas PDF tidak memiliki halaman yang dapat dicetak", 0, "");
            }

            // 2. Tentukan printer tujuan
            string finalPrinter = ResolvePrinter(requestedPrinter);
            if (string.IsNullOrWhiteSpace(finalPrinter))
            {
                return (false, "Tidak ada printer aktif yang terdeteksi di sistem Windows", 0, "");
            }

            // 3. Siapkan PrintDocument GDI+
            using var printDoc = new PrintDocument();
            printDoc.PrinterSettings.PrinterName = finalPrinter;
            printDoc.DocumentName = string.IsNullOrWhiteSpace(docTitle) ? "SIAP_Document.pdf" : docTitle;

            // Gunakan StandardPrintController agar cetak berjalan 100% silent tanpa pop-up dialog
            printDoc.PrintController = new StandardPrintController();

            int currentPageIndex = 0;
            Exception? pageException = null;

            printDoc.PrintPage += (sender, e) =>
            {
                try
                {
                    if (currentPageIndex >= totalPages)
                    {
                        e.HasMorePages = false;
                        return;
                    }

                    using var pdfPage = pdfDoc.GetPage((uint)currentPageIndex);

                    // Render halaman ke bitmap 300 DPI untuk ketajaman teks formulir dan barcode
                    using var memStream = new InMemoryRandomAccessStream();
                    var renderOptions = new PdfPageRenderOptions();
                    uint targetWidth = (uint)Math.Max(300, pdfPage.Size.Width * 300 / 72);
                    renderOptions.DestinationWidth = targetWidth;

                    pdfPage.RenderToStreamAsync(memStream, renderOptions).AsTask().GetAwaiter().GetResult();

                    using var netStream = memStream.AsStream();
                    using var pageBmp = new Bitmap(netStream);

                    // Deteksi orientasi otomatis (Landscape / Portrait)
                    bool isLandscape = pdfPage.Size.Width > pdfPage.Size.Height;
                    e.PageSettings.Landscape = isLandscape;

                    // Hitung batas cetak
                    var printArea = e.MarginBounds;
                    if (printArea.Width <= 0 || printArea.Height <= 0)
                    {
                        printArea = e.PageBounds;
                    }

                    // Skala gambar agar pas dan proporsional di tengah halaman
                    float scale = Math.Min(
                        (float)printArea.Width / pageBmp.Width,
                        (float)printArea.Height / pageBmp.Height
                    );

                    int destWidth = (int)(pageBmp.Width * scale);
                    int destHeight = (int)(pageBmp.Height * scale);
                    int destX = printArea.Left + (printArea.Width - destWidth) / 2;
                    int destY = printArea.Top + (printArea.Height - destHeight) / 2;

                    if (e.Graphics != null)
                    {
                        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                        e.Graphics.DrawImage(pageBmp, new Rectangle(destX, destY, destWidth, destHeight));
                    }

                    currentPageIndex++;
                    e.HasMorePages = (currentPageIndex < totalPages);
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
                return (false, $"Kesalahan saat mencetak: {pageException.Message}", currentPageIndex, finalPrinter);
            }

            return (true, $"Dicetak via Native Windows PDF Engine ({totalPages} halaman)", totalPages, finalPrinter);
        }
        catch (Exception ex)
        {
            return (false, $"Gagal mencetak PDF: {ex.Message}", 0, requestedPrinter ?? "");
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
