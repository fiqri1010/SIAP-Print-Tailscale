using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SiapPrintServer;

public class AppConfig
{
    [JsonPropertyName("port")]
    public int Port { get; set; } = 1234;

    [JsonPropertyName("selected_printer")]
    public string SelectedPrinter { get; set; } = "";

    [JsonPropertyName("auto_start_startup")]
    public bool AutoStartStartup { get; set; } = false;

    [JsonPropertyName("start_before_login")]
    public bool StartBeforeLogin { get; set; } = false;

    [JsonPropertyName("minimize_to_tray_on_startup")]
    public bool MinimizeToTrayOnStartup { get; set; } = true;

    [JsonPropertyName("minimize_on_exit")]
    public bool MinimizeOnExit { get; set; } = true;

    [JsonPropertyName("keep_funnel")]
    public bool KeepFunnel { get; set; } = false;
}

public class PrintLogItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [JsonPropertyName("time")]
    public string Time { get; set; } = DateTime.Now.ToString("HH:mm:ss");

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("printer")]
    public string Printer { get; set; } = "";

    [JsonPropertyName("pages")]
    public int Pages { get; set; } = 1;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "SUKSES"; // "SUKSES" atau "GAGAL"

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public class PrinterInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("driver")]
    public string Driver { get; set; } = "";

    [JsonPropertyName("port")]
    public string Port { get; set; } = "";

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; } = false;
}

public class PrintRequestDto
{
    [JsonPropertyName("printerName")]
    public string? PrinterName { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("pdfBase64")]
    public string? PdfBase64 { get; set; }

    /// <summary>Ukuran kertas: "A4", "F4", atau kosong = kertas bawaan printer. (v2.1)</summary>
    [JsonPropertyName("paperSize")]
    public string? PaperSize { get; set; }

    /// <summary>Margin dalam milimeter; null = margin bawaan driver. (v2.1)</summary>
    [JsonPropertyName("marginsMm")]
    public MarginsMmDto? MarginsMm { get; set; }

    /// <summary>Halaman yang dicetak, 1-based, mis. "1-3,5"; kosong = semua. (v2.1)</summary>
    [JsonPropertyName("pages")]
    public string? Pages { get; set; }
}

public class MarginsMmDto
{
    [JsonPropertyName("top")]
    public double Top { get; set; }

    [JsonPropertyName("right")]
    public double Right { get; set; }

    [JsonPropertyName("bottom")]
    public double Bottom { get; set; }

    [JsonPropertyName("left")]
    public double Left { get; set; }
}

public class PrintResponseDto
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("printer")]
    public string Printer { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("pages")]
    public int Pages { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class ServerInfoResponseDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "online";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 1234;

    [JsonPropertyName("tailscaleIp")]
    public string? TailscaleIp { get; set; }

    [JsonPropertyName("tailscaleFunnelDomain")]
    public string? TailscaleFunnelDomain { get; set; }

    [JsonPropertyName("localIp")]
    public string LocalIp { get; set; } = "127.0.0.1";

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = Environment.MachineName;

    [JsonPropertyName("selectedPrinter")]
    public string SelectedPrinter { get; set; } = "";

    [JsonPropertyName("uptimeSeconds")]
    public long UptimeSeconds { get; set; }

    /// <summary>Versi SIAP Print, dipakai SIAP NEO untuk mengetahui fitur yang didukung.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>Fitur cetak yang didukung: "paperSize", "margins", "pages".</summary>
    [JsonPropertyName("features")]
    public string[] Features { get; set; } = Array.Empty<string>();
}
