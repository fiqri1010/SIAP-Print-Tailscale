using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SiapPrintServer;

/// <summary>
/// Server HTTP ringan asynchronous berbasis System.Net.Sockets.TcpListener.
/// Menggantikan ASP.NET Core Kestrel secara total untuk memangkas footprint RAM
/// hingga 70% lebih kecil (~5 - 12 MB) dan mengeliminasi ketergantungan framework besar.
/// </summary>
public class HttpPrintServer
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _uptime = new();

    public bool IsRunning { get; private set; }
    public bool IsExternalService { get; private set; }
    public int ActivePort { get; private set; } = 1234;

    public event Action<string>? ServerStatusChanged;

    public async Task StartAsync(int port)
    {
        string logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SIAP-Print-Tailscale",
            "server.log"
        );

        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now}] Memulai Lightweight HTTP Server pada port {port}...\n");
            if (IsRunning)
            {
                await StopAsync();
            }

            ActivePort = port;
            _cts = new CancellationTokenSource();

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(100);

            _uptime.Restart();
            IsRunning = true;
            IsExternalService = false;
            ServerStatusChanged?.Invoke($"Berjalan pada port {port}");

            File.AppendAllText(logPath, $"[{DateTime.Now}] Server berjalan sukses pada 0.0.0.0:{port}\n");

            _ = AcceptConnectionsAsync(_cts.Token, logPath);
        }
        catch (Exception ex)
        {
            // Cek apakah server kita sendiri yang sedang berjalan (mis. Boot Task di background)
            bool isSelfActive = await CheckIfSelfServiceActiveAsync(port);
            if (isSelfActive)
            {
                IsRunning = true;
                IsExternalService = true;
                ActivePort = port;
                ServerStatusChanged?.Invoke($"Berjalan via Background Boot Service (Port {port})");
                File.AppendAllText(logPath, $"[{DateTime.Now}] Server sudah aktif di latar belakang (Boot Service) pada port {port}.\n");
                return;
            }

            IsRunning = false;
            IsExternalService = false;
            ServerStatusChanged?.Invoke($"Terhenti: {ex.Message}");
            File.AppendAllText(logPath, $"[{DateTime.Now}] ERROR memulai server: {ex}\n");
        }
    }

    public static async Task<bool> CheckIfSelfServiceActiveAsync(int port)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var response = await client.GetAsync($"http://127.0.0.1:{port}/ping");
            if (response.IsSuccessStatusCode)
            {
                string text = await response.Content.ReadAsStringAsync();
                return text.Contains("SIAP Print Server");
            }
        }
        catch { }
        return false;
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct, string logPath)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct, logPath);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    File.AppendAllText(logPath, $"[{DateTime.Now}] Accept error: {ex.Message}\n");
                }
                catch { }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct, string logPath)
    {
        using (client)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 30000;
            client.SendTimeout = 30000;

            using var stream = client.GetStream();

            try
            {
                // 1. Baca header HTTP sampai \r\n\r\n
                var headerBuffer = new byte[8192];
                int totalRead = 0;
                int headerEndIndex = -1;

                while (totalRead < headerBuffer.Length)
                {
                    int bytesRead = await stream.ReadAsync(headerBuffer.AsMemory(totalRead, headerBuffer.Length - totalRead), ct);
                    if (bytesRead == 0) return;
                    totalRead += bytesRead;

                    headerEndIndex = FindHeaderEnd(headerBuffer, totalRead);
                    if (headerEndIndex != -1) break;
                }

                if (headerEndIndex == -1)
                {
                    await SendResponseAsync(stream, 400, "Bad Request", "text/plain", "Header terlalu panjang atau format tidak valid");
                    return;
                }

                string headerText = Encoding.ASCII.GetString(headerBuffer, 0, headerEndIndex);
                var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;

                var reqTokens = lines[0].Split(' ');
                if (reqTokens.Length < 2) return;

                string method = reqTokens[0].ToUpperInvariant();
                string rawUrl = reqTokens[1];
                string path = rawUrl.Split('?')[0];

                long contentLength = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    int colonIdx = line.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        string headerName = line.Substring(0, colonIdx).Trim();
                        string headerVal = line.Substring(colonIdx + 1).Trim();
                        if (string.Equals(headerName, "Content-Length", StringComparison.OrdinalIgnoreCase))
                        {
                            long.TryParse(headerVal, out contentLength);
                        }
                    }
                }

                // 2. Tangani Preflight CORS (OPTIONS)
                if (method == "OPTIONS")
                {
                    await SendResponseAsync(stream, 204, "No Content", "text/plain", "");
                    return;
                }

                // 3. Routing Endpoint GET
                if (method == "GET")
                {
                    if (path == "/ping")
                    {
                        await SendResponseAsync(stream, 200, "OK", "text/plain; charset=utf-8", "🖨️ SIAP Print Server is running");
                        return;
                    }

                    if (path == "/info")
                    {
                        var cfg = ConfigManager.LoadConfig();
                        var (funnelDomain, _) = TailscaleHelper.GetFunnelStatus();

                        var info = new ServerInfoResponseDto
                        {
                            Status = "online",
                            Port = ActivePort,
                            TailscaleIp = TailscaleHelper.GetTailscaleIp(),
                            TailscaleFunnelDomain = funnelDomain,
                            LocalIp = TailscaleHelper.GetLocalIp(),
                            Hostname = Environment.MachineName,
                            SelectedPrinter = cfg.SelectedPrinter,
                            UptimeSeconds = _uptime.ElapsedMilliseconds / 1000,
                            Version = typeof(HttpPrintServer).Assembly.GetName().Version?.ToString(3) ?? "2.1.1",
                            Features = new[] { "paperSize", "margins", "pages" }
                        };

                        string json = JsonSerializer.Serialize(info);
                        await SendResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json);
                        return;
                    }

                    if (path == "/printers")
                    {
                        var printers = WindowsPrinterHelper.GetPrinters();
                        string json = JsonSerializer.Serialize(printers);
                        await SendResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json);
                        return;
                    }

                    await SendResponseAsync(stream, 404, "Not Found", "text/plain", "Endpoint tidak ditemukan");
                    return;
                }

                // 4. Routing Endpoint POST /print-pdf
                if (method == "POST")
                {
                    if (path != "/print-pdf")
                    {
                        await SendResponseAsync(stream, 404, "Not Found", "text/plain", "Endpoint tidak ditemukan");
                        return;
                    }

                    if (contentLength <= 0 || contentLength > 100 * 1024 * 1024)
                    {
                        var errResp = new PrintResponseDto
                        {
                            Success = false,
                            Error = "Bad Request: Ukuran body permintaan tidak valid atau kosong"
                        };
                        await SendResponseAsync(stream, 400, "Bad Request", "application/json; charset=utf-8", JsonSerializer.Serialize(errResp));
                        return;
                    }

                    // Ambil sisa body yang sudah terbaca di header buffer
                    int bodyStartInHeader = headerEndIndex + 4;
                    int bodyBytesAlreadyRead = totalRead - bodyStartInHeader;

                    using var bodyMs = new MemoryStream((int)Math.Min(contentLength, 1024 * 1024));
                    if (bodyBytesAlreadyRead > 0)
                    {
                        bodyMs.Write(headerBuffer, bodyStartInHeader, bodyBytesAlreadyRead);
                    }

                    long remainingToRead = contentLength - bodyBytesAlreadyRead;
                    var readChunk = new byte[16384];
                    while (remainingToRead > 0)
                    {
                        int toRead = (int)Math.Min(readChunk.Length, remainingToRead);
                        int bytes = await stream.ReadAsync(readChunk.AsMemory(0, toRead), ct);
                        if (bytes == 0) break;
                        bodyMs.Write(readChunk, 0, bytes);
                        remainingToRead -= bytes;
                    }

                    bodyMs.Position = 0;

                    // Deserialisasi JSON stream langsung tanpa alokasi string raksasa di heap
                    PrintRequestDto? req = null;
                    byte[]? pdfBytes = null;

                    try
                    {
                        using var doc = await JsonDocument.ParseAsync(bodyMs, cancellationToken: ct);
                        var root = doc.RootElement;

                        req = new PrintRequestDto();
                        if (root.TryGetProperty("printerName", out var pProp) && pProp.ValueKind == JsonValueKind.String)
                            req.PrinterName = pProp.GetString();

                        if (root.TryGetProperty("filename", out var fProp) && fProp.ValueKind == JsonValueKind.String)
                            req.Filename = fProp.GetString();

                        if (root.TryGetProperty("paperSize", out var psProp) && psProp.ValueKind == JsonValueKind.String)
                            req.PaperSize = psProp.GetString();

                        if (root.TryGetProperty("pages", out var pgProp) && pgProp.ValueKind == JsonValueKind.String)
                            req.Pages = pgProp.GetString();

                        if (root.TryGetProperty("marginsMm", out var mgProp) && mgProp.ValueKind == JsonValueKind.Object)
                        {
                            var margins = new MarginsMmDto();
                            if (mgProp.TryGetProperty("top", out var tProp) && tProp.TryGetDouble(out var top)) margins.Top = top;
                            if (mgProp.TryGetProperty("right", out var rProp) && rProp.TryGetDouble(out var right)) margins.Right = right;
                            if (mgProp.TryGetProperty("bottom", out var bProp) && bProp.TryGetDouble(out var bottom)) margins.Bottom = bottom;
                            if (mgProp.TryGetProperty("left", out var lProp) && lProp.TryGetDouble(out var left)) margins.Left = left;
                            req.MarginsMm = margins;
                        }

                        if (root.TryGetProperty("pdfBase64", out var b64Prop) && b64Prop.ValueKind == JsonValueKind.String)
                        {
                            // Konversi langsung dari UTF-8 buffer JSON tanpa intermediate C# UTF-16 string!
                            pdfBytes = b64Prop.GetBytesFromBase64();
                        }
                    }
                    catch (Exception ex)
                    {
                        var errResp = new PrintResponseDto
                        {
                            Success = false,
                            Error = $"JSON atau Base64 tidak valid: {ex.Message}"
                        };
                        await SendResponseAsync(stream, 400, "Bad Request", "application/json; charset=utf-8", JsonSerializer.Serialize(errResp));
                        return;
                    }

                    if (pdfBytes == null || pdfBytes.Length == 0)
                    {
                        var errResp = new PrintResponseDto
                        {
                            Success = false,
                            Error = "Data berkas PDF (pdfBase64) kosong atau tidak ditemukan"
                        };
                        await SendResponseAsync(stream, 400, "Bad Request", "application/json; charset=utf-8", JsonSerializer.Serialize(errResp));
                        return;
                    }

                    string docTitle = req.Filename ?? "SIAP_Document.pdf";
                    var options = new PrintOptions(req.PaperSize, req.MarginsMm, req.Pages);

                    var (success, message, pagesPrinted, finalPrinter) = await NativePdfPrinter.PrintAsync(
                        pdfBytes,
                        req.PrinterName,
                        docTitle,
                        options
                    );

                    // Bersihkan referensi data PDF untuk membantu Garbage Collector
                    pdfBytes = null;

                    var logItem = new PrintLogItem
                    {
                        Filename = docTitle,
                        Printer = finalPrinter,
                        Pages = pagesPrinted,
                        Status = success ? "SUKSES" : "GAGAL",
                        Message = message
                    };
                    ConfigManager.AddLog(logItem);

                    var responseDto = new PrintResponseDto
                    {
                        Success = success,
                        Printer = finalPrinter,
                        Pages = pagesPrinted,
                        Message = message,
                        Error = success ? null : message
                    };

                    await SendResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", JsonSerializer.Serialize(responseDto));

                    // Pangkas working set segera setelah print job selesai
                    MemoryHelper.TrimWorkingSet();
                    return;
                }

                await SendResponseAsync(stream, 405, "Method Not Allowed", "text/plain", "Metode HTTP tidak didukung");
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(logPath, $"[{DateTime.Now}] Client processing error: {ex.Message}\n");
                }
                catch { }
            }
        }
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i < length - 3; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static async Task SendResponseAsync(NetworkStream stream, int statusCode, string statusText, string contentType, string body)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

        var headerSb = new StringBuilder(256);
        headerSb.Append($"HTTP/1.1 {statusCode} {statusText}\r\n");
        headerSb.Append($"Content-Type: {contentType}\r\n");
        headerSb.Append($"Content-Length: {bodyBytes.Length}\r\n");
        headerSb.Append("Access-Control-Allow-Origin: *\r\n");
        headerSb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        headerSb.Append("Access-Control-Allow-Headers: Origin, X-Requested-With, Content-Type, Accept, Authorization\r\n");
        headerSb.Append("Access-Control-Allow-Private-Network: true\r\n");
        headerSb.Append("Connection: close\r\n");
        headerSb.Append("\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(headerSb.ToString());
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
        }
        await stream.FlushAsync();
    }

    public Task StopAsync()
    {
        if (!IsRunning) return Task.CompletedTask;

        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch { }
        finally
        {
            _listener = null;
            IsRunning = false;
            _uptime.Stop();
            ServerStatusChanged?.Invoke("Terhenti");
            MemoryHelper.TrimWorkingSet();
        }

        return Task.CompletedTask;
    }
}
