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
/// <summary>Pilihan cetak dari SIAP NEO (v2.1). Semua opsional; null = perilaku lama.</summary>
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

    public static async Task<(bool Success, string Message, int PagesPrinted, string FinalPrinter)> PrintAsync(
        byte[] pdfBytes,
        string? requestedPrinter,
        string docTitle,
        PrintOptions? options = null)
    {
        options ??= PrintOptions.Default;
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
                // Margin bawaan: gunakan batas wajar cetak (4 mm / ~16 hundredths inch)
                // agar halaman otomatis pas (auto-fit) kiri-kanan-atas-bawah dan tidak
                // terpotong, menggantikan margin default GDI+ 1 inci (25.4 mm) yang terlalu besar.
                int defaultMargin = MmToHundredthsInch(4.0);
                printDoc.DefaultPageSettings.Margins = new Margins(
                    defaultMargin, defaultMargin, defaultMargin, defaultMargin);
            }

            // v2.1: hanya halaman terpilih
            var pageList = ParsePageRange(options.Pages, totalPages);
            int listPos = 0;
            int currentPageIndex = 0;
            Exception? pageException = null;

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
                    try
                    {
                        var printable = e.PageSettings.PrintableArea;
                        if (printable.Width > 0 && printable.Height > 0)
                        {
                            int pLeft = (int)Math.Ceiling(printable.Left);
                            int pTop = (int)Math.Ceiling(printable.Top);
                            int pRight = (int)Math.Floor(printable.Right);
                            int pBottom = (int)Math.Floor(printable.Bottom);

                            int left = Math.Max(printArea.Left, pLeft);
                            int top = Math.Max(printArea.Top, pTop);
                            int right = Math.Min(printArea.Right, pRight);
                            int bottom = Math.Min(printArea.Bottom, pBottom);

                            if (right > left && bottom > top)
                            {
                                printArea = new Rectangle(left, top, right - left, bottom - top);
                            }
                        }
                    }
                    catch { }

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
                return (false, $"Kesalahan saat mencetak: {pageException.Message}", listPos, finalPrinter);
            }

            string paperInfo = paper != null ? $", kertas {paper.PaperName}" : "";
            return (true, $"Dicetak via Native Windows PDF Engine ({pageList.Count} dari {totalPages} halaman{paperInfo})", pageList.Count, finalPrinter);
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
