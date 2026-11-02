using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SiapPrintServer;

public class HttpPrintServer
{
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _uptime = new();

    public bool IsRunning { get; private set; }
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
            File.AppendAllText(logPath, $"[{DateTime.Now}] Starting server on port {port}...\n");
            if (IsRunning)
            {
                await StopAsync();
            }

            ActivePort = port;
            _cts = new CancellationTokenSource();

            var builder = WebApplication.CreateBuilder();

            // Konfigurasi Kestrel URL & Limit
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100 MB payload support
            });

            builder.Logging.ClearProviders(); // Silent console logging

            // Tambahkan CORS
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(p =>
                {
                    p.AllowAnyOrigin()
                     .AllowAnyMethod()
                     .AllowAnyHeader();
                });
            });

            _app = builder.Build();

            _app.UseCors();

            // Middleware CORS Headers khusus & Private Network Access
            _app.Use(async (context, next) =>
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Origin, X-Requested-With, Content-Type, Accept, Authorization";
                context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";

                if (context.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 204;
                    return;
                }

                await next();
            });

            // 1. GET /ping
            _app.MapGet("/ping", () => Results.Text("🖨️ SIAP Print Server is running", "text/plain; charset=utf-8"));

            // 2. GET /info
            _app.MapGet("/info", () =>
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
                    Version = typeof(HttpPrintServer).Assembly.GetName().Version?.ToString(3) ?? "",
                    Features = new[] { "paperSize", "margins", "pages" }
                };

                return Results.Json(info);
            });

            // 3. GET /printers
            _app.MapGet("/printers", () =>
            {
                var printers = WindowsPrinterHelper.GetPrinters();
                return Results.Json(printers);
            });

            // 4. POST /print-pdf
            _app.MapPost("/print-pdf", async (HttpContext ctx) =>
            {
                try
                {
                    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                    string body = await reader.ReadToEndAsync();

                    if (string.IsNullOrWhiteSpace(body))
                    {
                        return Results.BadRequest(new PrintResponseDto
                        {
                            Success = false,
                            Error = "Bad Request: Body permintaan kosong"
                        });
                    }

                    var req = JsonSerializer.Deserialize<PrintRequestDto>(body);
                    if (req == null || string.IsNullOrWhiteSpace(req.PdfBase64))
                    {
                        return Results.BadRequest(new PrintResponseDto
                        {
                            Success = false,
                            Error = "Data berkas PDF (pdfBase64) tidak ditemukan dalam permintaan"
                        });
                    }

                    byte[] pdfBytes;
                    try
                    {
                        pdfBytes = Convert.FromBase64String(req.PdfBase64.Trim());
                    }
                    catch (Exception ex)
                    {
                        return Results.BadRequest(new PrintResponseDto
                        {
                            Success = false,
                            Error = $"Format Base64 tidak valid: {ex.Message}"
                        });
                    }

                    string docTitle = req.Filename ?? "SIAP_Document.pdf";
                    var options = new PrintOptions(req.PaperSize, req.MarginsMm, req.Pages);
                    var (success, message, pages, finalPrinter) = await NativePdfPrinter.PrintAsync(
                        pdfBytes,
                        req.PrinterName,
                        docTitle,
                        options
                    );

                    // Simpan ke riwayat log
                    var logItem = new PrintLogItem
                    {
                        Filename = docTitle,
                        Printer = finalPrinter,
                        Pages = pages,
                        Status = success ? "SUKSES" : "GAGAL",
                        Message = message
                    };
                    ConfigManager.AddLog(logItem);

                    return Results.Json(new PrintResponseDto
                    {
                        Success = success,
                        Printer = finalPrinter,
                        Pages = pages,
                        Message = message,
                        Error = success ? null : message
                    });
                }
                catch (Exception ex)
                {
                    return Results.Json(new PrintResponseDto
                    {
                        Success = false,
                        Error = ex.Message,
                        Message = $"Gagal memproses pencetakan: {ex.Message}"
                    });
                }
            });

            File.AppendAllText(logPath, $"[{DateTime.Now}] Calling _app.StartAsync()...\n");
            await _app.StartAsync(_cts.Token);
            _uptime.Restart();
            IsRunning = true;
            ServerStatusChanged?.Invoke($"Berjalan pada port {port}");
            File.AppendAllText(logPath, $"[{DateTime.Now}] Server running successfully on port {port}!\n");
        }
        catch (Exception ex)
        {
            IsRunning = false;
            ServerStatusChanged?.Invoke($"Terhenti: {ex.Message}");
            File.AppendAllText(logPath, $"[{DateTime.Now}] ERROR starting server: {ex}\n");
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning || _app == null) return;

        try
        {
            _cts?.Cancel();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        catch { }
        finally
        {
            IsRunning = false;
            _uptime.Stop();
            ServerStatusChanged?.Invoke("Terhenti");
        }
    }
}
