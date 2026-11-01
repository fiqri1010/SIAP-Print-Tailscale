use std::fs;
use std::os::windows::process::CommandExt;
use std::path::PathBuf;
use std::process::Command;
use std::sync::atomic::{AtomicBool, AtomicU16, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{SystemTime, UNIX_EPOCH};

use base64::prelude::*;
use serde::{Deserialize, Serialize};
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, TrayIconBuilder, TrayIconEvent};
use tauri::{Emitter, Manager, State};
use tiny_http::{Header, Method, Response, Server, StatusCode};

const CREATE_NO_WINDOW: u32 = 0x08000000;

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct PrinterInfo {
    pub name: String,
    pub driver: String,
    pub port: String,
    pub is_default: bool,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct PrintLogItem {
    pub id: u64,
    pub time: String,
    pub filename: String,
    pub printer: String,
    pub status: String,
    pub message: String,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct AppConfig {
    pub port: u16,
    pub selected_printer: String,
    pub auto_start_startup: bool,
    /// Aktifkan ulang Funnel otomatis bila hilang (mis. setelah `tailscale reset`). Diatur pengguna.
    #[serde(default)]
    pub keep_funnel: bool,
}

#[derive(Serialize, Deserialize, Clone, Debug, Default)]
pub struct PeerInfo {
    pub name: String,
    pub os: String,
    pub ip: String,
    pub online: bool,
}

#[derive(Serialize, Deserialize, Clone, Debug, Default)]
pub struct CachedData {
    pub tailscale_ip: Option<String>,
    /// Domain Funnel yang BENAR-BENAR aktif (AllowFunnel = true). None bila Funnel mati.
    pub tailscale_funnel_domain: Option<String>,
    /// Nama MagicDNS PC (selalu ada bila Tailscale login), dipakai untuk saran perintah Funnel.
    pub tailscale_dns_name: Option<String>,
    /// Port lokal yang diteruskan Funnel (mis. 1234). None bila tidak diketahui.
    pub funnel_target_port: Option<u16>,
    pub local_ip: String,
    pub hostname: String,
    pub printers: Vec<PrinterInfo>,
    /// Aturan Windows Firewall untuk port server sudah ada (jalur IP Tailscale & LAN).
    pub firewall_ok: bool,
    pub firewall_port: u16,
    pub sumatra_path: Option<String>,
    /// Perangkat lain di tailnet (untuk melihat apakah HP online di Tailscale).
    pub peers: Vec<PeerInfo>,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct AppStatus {
    pub is_running: bool,
    pub port: u16,
    pub tailscale_ip: Option<String>,
    pub tailscale_funnel_domain: Option<String>,
    pub tailscale_dns_name: Option<String>,
    pub funnel_target_port: Option<u16>,
    pub local_ip: String,
    pub hostname: String,
    pub selected_printer: String,
    pub auto_start_startup: bool,
    pub printers: Vec<PrinterInfo>,
    pub logs: Vec<PrintLogItem>,
    pub firewall_ok: bool,
    pub sumatra_path: Option<String>,
    pub server_error: Option<String>,
    pub keep_funnel: bool,
    pub last_request: Option<String>,
    pub peers: Vec<PeerInfo>,
}

/// Server HTTP yang sedang berjalan: flag berhenti miliknya sendiri + handle thread,
/// sehingga mematikan/menyalakan ulang tidak pernah menyisakan listener lama di port yang sama.
pub struct RunningServer {
    stop: Arc<AtomicBool>,
    handle: thread::JoinHandle<()>,
}

pub struct AppState {
    pub is_running: Arc<AtomicBool>,
    pub port: Arc<AtomicU16>,
    pub selected_printer: Arc<Mutex<String>>,
    pub auto_start_startup: Arc<AtomicBool>,
    pub logs: Arc<Mutex<Vec<PrintLogItem>>>,
    pub cache: Arc<Mutex<CachedData>>,
    pub server: Arc<Mutex<Option<RunningServer>>>,
    pub server_error: Arc<Mutex<Option<String>>>,
    pub app_handle: Arc<Mutex<Option<tauri::AppHandle>>>,
    pub keep_funnel: Arc<AtomicBool>,
    /// Permintaan terakhir yang diterima server (untuk diagnosis koneksi dari HP).
    pub last_request: Arc<Mutex<Option<String>>>,
}

const TAILSCALE_PATHS: [&str; 2] = ["C:\\Program Files\\Tailscale\\tailscale.exe", "tailscale.exe"];

fn firewall_rule_name(port: u16) -> String {
    format!("SIAP Print Tailscale (TCP {})", port)
}

/// Mengecek aturan inbound Windows Firewall untuk port server (tanpa jendela, tanpa admin).
fn firewall_rule_exists(port: u16) -> bool {
    let name = firewall_rule_name(port);
    match Command::new("netsh")
        .args(["advfirewall", "firewall", "show", "rule", &format!("name={}", name)])
        .creation_flags(CREATE_NO_WINDOW)
        .output()
    {
        Ok(out) => out.status.success() && String::from_utf8_lossy(&out.stdout).contains(&name),
        Err(_) => false,
    }
}

fn find_sumatra() -> Option<String> {
    let mut candidates = Vec::new();
    for var in ["LOCALAPPDATA", "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)"] {
        if let Ok(base) = std::env::var(var) {
            candidates.push(format!("{}\\SumatraPDF\\SumatraPDF.exe", base));
        }
    }
    candidates.push("C:\\Program Files\\SumatraPDF\\SumatraPDF.exe".to_string());
    candidates.push("C:\\Program Files (x86)\\SumatraPDF\\SumatraPDF.exe".to_string());
    candidates.into_iter().find(|p| std::path::Path::new(p).exists())
}

/// Membaca `tailscale serve status --json`. Mendukung mode latar (`--bg`, kunci di level atas)
/// maupun mode foreground. Mengembalikan (domain Funnel aktif, port lokal tujuan proxy).
fn parse_funnel_status(val: &serde_json::Value) -> (Option<String>, Option<u16>) {
    let mut configs: Vec<&serde_json::Value> = vec![val];
    if let Some(fg) = val.get("Foreground").and_then(|f| f.as_object()) {
        configs.extend(fg.values());
    }
    for cfg in configs {
        let Some(allow) = cfg.get("AllowFunnel").and_then(|a| a.as_object()) else { continue };
        for (host_port, enabled) in allow {
            if !enabled.as_bool().unwrap_or(false) {
                continue;
            }
            let domain = host_port.split(':').next().unwrap_or(host_port).to_string();
            if domain.is_empty() {
                continue;
            }
            let target = cfg
                .get("Web")
                .and_then(|w| w.get(host_port))
                .and_then(|w| w.get("Handlers"))
                .and_then(|h| h.as_object())
                .and_then(|h| h.values().find_map(|v| v.get("Proxy").and_then(|p| p.as_str()).map(|s| s.to_string())))
                .and_then(|proxy| proxy.rsplit(':').next().and_then(|p| p.trim_end_matches('/').parse::<u16>().ok()));
            return (Some(domain), target);
        }
    }
    (None, None)
}

/// Nama printer yang pernah tersimpan sebagai teks JSON objek dikembalikan ke nama saja.
fn bare_printer_name(raw: &str) -> String {
    let t = raw.trim();
    if t.starts_with('{') {
        if let Ok(v) = serde_json::from_str::<serde_json::Value>(t) {
            if let Some(n) = v.get("name").and_then(|n| n.as_str()) {
                return n.trim().to_string();
            }
        }
    }
    t.to_string()
}

/// Skrip PowerShell → argumen `-EncodedCommand` (UTF-16LE Base64), bebas masalah tanda kutip.
fn ps_encoded(script: &str) -> String {
    let bytes: Vec<u8> = script.encode_utf16().flat_map(|u| u.to_le_bytes()).collect();
    BASE64_STANDARD.encode(bytes)
}

fn get_config_path() -> PathBuf {
    let app_data = std::env::var("APPDATA").unwrap_or_else(|_| ".".to_string());
    let dir = PathBuf::from(app_data).join("SIAP-Print-Tailscale");
    let _ = fs::create_dir_all(&dir);
    dir.join("config.json")
}

fn load_config() -> AppConfig {
    let path = get_config_path();
    if let Ok(data) = fs::read_to_string(path) {
        if let Ok(cfg) = serde_json::from_str(&data) {
            return cfg;
        }
    }
    AppConfig {
        port: 1234,
        selected_printer: String::new(),
        auto_start_startup: false,
        keep_funnel: false,
    }
}

fn save_config(cfg: &AppConfig) {
    let path = get_config_path();
    let _ = fs::write(path, serde_json::to_string_pretty(cfg).unwrap_or_default());
}

fn current_time_str() -> String {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let hours = (now / 3600) % 24;
    let minutes = (now / 60) % 60;
    let seconds = now % 60;
    format!("{:02}:{:02}:{:02}", (hours + 7) % 24, minutes, seconds)
}

// Ultra-fast Native Local IP detection without spawning any process
fn detect_local_ip_native() -> String {
    if let Ok(socket) = std::net::UdpSocket::bind("0.0.0.0:0") {
        if socket.connect("8.8.8.8:80").is_ok() {
            if let Ok(addr) = socket.local_addr() {
                let ip = addr.ip().to_string();
                if !ip.starts_with("127.") && !ip.starts_with("169.254.") {
                    return ip;
                }
            }
        }
    }
    "127.0.0.1".to_string()
}

// Background detection: Called ONCE at startup or when user clicks Refresh (Silent with CREATE_NO_WINDOW)
fn query_system_info_silent(port: u16) -> CachedData {
    let local_ip = detect_local_ip_native();
    let hostname = std::env::var("COMPUTERNAME").unwrap_or_else(|_| "Windows-PC".to_string());

    let mut tailscale_ip: Option<String> = None;
    let mut tailscale_funnel_domain: Option<String> = None;
    let mut tailscale_dns_name: Option<String> = None;
    let mut funnel_target_port: Option<u16> = None;
    let mut peers: Vec<PeerInfo> = Vec::new();

    for p in TAILSCALE_PATHS {
        // 1. IP Tailscale (100.x.x.x)
        let Ok(output) = Command::new(p)
            .args(["ip", "-4"])
            .creation_flags(CREATE_NO_WINDOW)
            .output()
        else {
            continue;
        };
        if output.status.success() {
            let s = String::from_utf8_lossy(&output.stdout).trim().to_string();
            if s.starts_with("100.") {
                tailscale_ip = Some(s);
            }
        }

        // 2. Funnel yang benar-benar aktif (AllowFunnel), termasuk mode --bg
        if let Ok(output) = Command::new(p)
            .args(["serve", "status", "--json"])
            .creation_flags(CREATE_NO_WINDOW)
            .output()
        {
            if output.status.success() {
                let s = String::from_utf8_lossy(&output.stdout);
                if let Ok(val) = serde_json::from_str::<serde_json::Value>(&s) {
                    let (domain, target) = parse_funnel_status(&val);
                    tailscale_funnel_domain = domain;
                    funnel_target_port = target;
                }
            }
        }

        // 3. Nama MagicDNS PC (bukan bukti Funnel aktif, hanya untuk saran)
        if let Ok(output) = Command::new(p)
            .args(["status", "--json"])
            .creation_flags(CREATE_NO_WINDOW)
            .output()
        {
            if output.status.success() {
                let s = String::from_utf8_lossy(&output.stdout);
                if let Ok(val) = serde_json::from_str::<serde_json::Value>(&s) {
                    if let Some(dns) = val.get("Self").and_then(|s| s.get("DNSName")).and_then(|d| d.as_str()) {
                        let trimmed = dns.trim_end_matches('.');
                        if !trimmed.is_empty() {
                            tailscale_dns_name = Some(trimmed.to_string());
                        }
                    }
                    if let Some(peer_map) = val.get("Peer").and_then(|p| p.as_object()) {
                        for pv in peer_map.values() {
                            let name = pv.get("HostName").and_then(|v| v.as_str()).unwrap_or("").to_string();
                            if name.is_empty() {
                                continue;
                            }
                            peers.push(PeerInfo {
                                name,
                                os: pv.get("OS").and_then(|v| v.as_str()).unwrap_or("").to_string(),
                                ip: pv.get("TailscaleIPs").and_then(|v| v.as_array()).and_then(|a| a.first())
                                    .and_then(|v| v.as_str()).unwrap_or("").to_string(),
                                online: pv.get("Online").and_then(|v| v.as_bool()).unwrap_or(false),
                            });
                        }
                    }
                }
            }
        }
        break;
    }

    // 4. Detect Printers (PowerShell with CREATE_NO_WINDOW, completely hidden)
    let script = r#"
        Get-Printer | Select-Object Name, DriverName, PortName, @{Name='Default';Expression={$_.Default}} | ConvertTo-Json -Compress
    "#;

    let mut printers = Vec::new();
    if let Ok(output) = Command::new("powershell")
        .args(["-NoProfile", "-Command", script])
        .creation_flags(CREATE_NO_WINDOW)
        .output()
    {
        if output.status.success() {
            let json_str = String::from_utf8_lossy(&output.stdout);
            if let Ok(val) = serde_json::from_str::<serde_json::Value>(&json_str) {
                let items = if let Some(arr) = val.as_array() {
                    arr.clone()
                } else if val.is_object() {
                    vec![val]
                } else {
                    vec![]
                };

                for item in items {
                    if let Some(name) = item["Name"].as_str() {
                        let name = name.to_string();
                        let driver = item["DriverName"].as_str().unwrap_or("").to_string();
                        let port = item["PortName"].as_str().unwrap_or("").to_string();
                        let is_default = item["Default"].as_bool().unwrap_or(false);

                        let name_lower = name.to_lowercase();
                        let driver_lower = driver.to_lowercase();
                        let is_virtual = name_lower.contains("pdf")
                            || name_lower.contains("onenote")
                            || name_lower.contains("fax")
                            || name_lower.contains("xps")
                            || name_lower.contains("document writer")
                            || driver_lower.contains("pdf")
                            || driver_lower.contains("onenote")
                            || driver_lower.contains("fax")
                            || driver_lower.contains("xps");

                        if !is_virtual {
                            printers.push(PrinterInfo {
                                name,
                                driver,
                                port,
                                is_default,
                            });
                        }
                    }
                }
            }
        }
    }

    CachedData {
        tailscale_ip,
        tailscale_funnel_domain,
        tailscale_dns_name,
        funnel_target_port,
        local_ip,
        hostname,
        printers,
        firewall_ok: firewall_rule_exists(port),
        firewall_port: port,
        sumatra_path: find_sumatra(),
        peers,
    }
}

fn print_pdf_to_windows(file_path: &str, target_printer: &str) -> Result<String, String> {
    if let Some(sumatra_exe) = find_sumatra() {
        let mut cmd = Command::new(&sumatra_exe);
        cmd.creation_flags(CREATE_NO_WINDOW);
        if target_printer.is_empty() {
            cmd.args(["-print-to-default", "-silent", file_path]);
        } else {
            cmd.args(["-print-to", target_printer, "-silent", file_path]);
        }

        match cmd.output() {
            Ok(output) if output.status.success() => Ok("Dicetak via SumatraPDF".to_string()),
            Ok(output) => Err(format!("SumatraPDF error: {}", String::from_utf8_lossy(&output.stderr))),
            Err(e) => Err(format!("Gagal mengeksekusi SumatraPDF: {}", e)),
        }
    } else {
        let ps_cmd = if target_printer.is_empty() {
            format!("Start-Process -FilePath \"{}\" -Verb Print -PassThru | Out-Null", file_path)
        } else {
            format!("Start-Process -FilePath \"{}\" -Verb PrintTo -ArgumentList '\"{}\"' -PassThru | Out-Null", file_path, target_printer)
        };

        match Command::new("powershell")
            .args(["-NoProfile", "-Command", &ps_cmd])
            .creation_flags(CREATE_NO_WINDOW)
            .output()
        {
            Ok(output) if output.status.success() => Ok("Dicetak via Windows Shell".to_string()),
            Ok(output) => Err(format!("Print command error: {}", String::from_utf8_lossy(&output.stderr))),
            Err(e) => Err(format!("Gagal mengeksekusi print: {}", e)),
        }
    }
}

fn configure_startup_shortcut(enable: bool) {
    let script = if enable {
        r#"
            $exePath = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
            $startup = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup"
            $shortcutPath = Join-Path $startup "SIAP-Print-Tailscale.lnk"
            $wsh = New-Object -ComObject WScript.Shell
            $sc = $wsh.CreateShortcut($shortcutPath)
            $sc.TargetPath = $exePath
            $sc.Arguments = "--hidden"
            $sc.WorkingDirectory = Split-Path $exePath
            $sc.Description = "SIAP Print Tailscale Background Service"
            $sc.Save()
        "#
    } else {
        r#"
            $startup = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup"
            $shortcutPath = Join-Path $startup "SIAP-Print-Tailscale.lnk"
            if (Test-Path $shortcutPath) { Remove-Item $shortcutPath -Force }
        "#
    };
    let _ = Command::new("powershell")
        .args(["-NoProfile", "-Command", script])
        .creation_flags(CREATE_NO_WINDOW)
        .output();
}

fn start_http_listener(
    port: u16,
    last_request: Arc<Mutex<Option<String>>>,
    logs: Arc<Mutex<Vec<PrintLogItem>>>,
    selected_printer: Arc<Mutex<String>>,
    cache: Arc<Mutex<CachedData>>,
    app_handle: Arc<Mutex<Option<tauri::AppHandle>>>,
) -> Result<RunningServer, String> {
    let addr = format!("0.0.0.0:{}", port);
    let server = Server::http(&addr).map_err(|e| {
        format!("Port {} sedang dipakai aplikasi lain (atau SIAP Print lain masih berjalan): {}", port, e)
    })?;
    let stop = Arc::new(AtomicBool::new(false));
    let stop_flag = Arc::clone(&stop);

    let handle = thread::spawn(move || {
        while !stop_flag.load(Ordering::SeqCst) {
            if let Ok(Some(mut request)) = server.recv_timeout(std::time::Duration::from_millis(500)) {
                // Abaikan query string & garis miring akhir: "/ping/", "/ping?x=1" tetap dikenali
                let raw_url = request.url().to_string();
                let path = raw_url.split('?').next().unwrap_or("/").trim_end_matches('/');
                let url = if path.is_empty() { "/".to_string() } else { path.to_string() };
                let method = request.method().clone();
                if method != Method::Options {
                    let from = match request.remote_addr() {
                        Some(a) if a.ip().is_loopback() => "Funnel/PC ini".to_string(),
                        Some(a) => a.ip().to_string(),
                        None => "-".to_string(),
                    };
                    if let Ok(mut lr) = last_request.lock() {
                        *lr = Some(format!("{} {} {} dari {}", current_time_str(), method, url, from));
                    }
                }
                let json_ct = || Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap();

                let cors_origin = Header::from_bytes(&b"Access-Control-Allow-Origin"[..], &b"*"[..]).unwrap();
                let cors_headers = Header::from_bytes(&b"Access-Control-Allow-Headers"[..], &b"Origin, Content-Type, Accept, Authorization"[..]).unwrap();
                let cors_methods = Header::from_bytes(&b"Access-Control-Allow-Methods"[..], &b"GET, POST, OPTIONS"[..]).unwrap();
                let cors_pna = Header::from_bytes(&b"Access-Control-Allow-Private-Network"[..], &b"true"[..]).unwrap();

                if method == Method::Options {
                    let mut resp = Response::empty(StatusCode(204));
                    resp.add_header(cors_origin);
                    resp.add_header(cors_headers);
                    resp.add_header(cors_methods);
                    resp.add_header(cors_pna);
                    let _ = request.respond(resp);
                    continue;
                }

                if url == "/ping" && method == Method::Get {
                    let mut resp = Response::from_string("🖨️ SIAP Print Server is running");
                    resp.add_header(cors_origin);
                    resp.add_header(cors_pna);
                    let _ = request.respond(resp);
                    continue;
                }

                if url == "/info" && method == Method::Get {
                    let c = cache.lock().unwrap().clone();
                    let curr_printer = selected_printer.lock().unwrap().clone();
                    let json = serde_json::json!({
                        "status": "online",
                        "version": env!("CARGO_PKG_VERSION"),
                        "port": port,
                        "tailscaleIp": c.tailscale_ip,
                        "tailscaleFunnelDomain": c.tailscale_funnel_domain,
                        "funnelActive": c.tailscale_funnel_domain.is_some(),
                        "localIp": c.local_ip,
                        "selectedPrinter": curr_printer
                    });
                    let mut resp = Response::from_string(json.to_string());
                    resp.add_header(cors_origin);
                    resp.add_header(cors_pna);
                    resp.add_header(Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap());
                    let _ = request.respond(resp);
                    continue;
                }

                if url == "/printers" && method == Method::Get {
                    let list = cache.lock().unwrap().printers.clone();
                    let json = serde_json::to_string(&list).unwrap_or_else(|_| "[]".to_string());
                    let mut resp = Response::from_string(json);
                    resp.add_header(cors_origin);
                    resp.add_header(cors_pna);
                    resp.add_header(Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap());
                    let _ = request.respond(resp);
                    continue;
                }

                if url == "/print-pdf" && method == Method::Post {
                    let mut body_str = String::new();
                    if let Err(_) = request.as_reader().read_to_string(&mut body_str) {
                        let mut resp = Response::from_string(r#"{"success":false,"error":"Gagal membaca body request"}"#)
                            .with_status_code(StatusCode(400));
                        resp.add_header(cors_origin);
                        resp.add_header(json_ct());
                        let _ = request.respond(resp);
                        continue;
                    }

                    #[derive(Deserialize)]
                    #[serde(rename_all = "camelCase")]
                    struct PrintPdfPayload {
                        printer_name: Option<String>,
                        filename: Option<String>,
                        pdf_base64: Option<String>,
                    }

                    let parsed: Result<PrintPdfPayload, _> = serde_json::from_str(&body_str);
                    match parsed {
                        Ok(payload) if payload.pdf_base64.is_some() => {
                            let base64_data = payload.pdf_base64.unwrap();
                            let req_printer = bare_printer_name(&payload.printer_name.unwrap_or_default());
                            let default_printer = selected_printer.lock().unwrap().clone();
                            let final_printer = if !req_printer.is_empty() { req_printer } else { default_printer };
                            let doc_name = payload.filename.unwrap_or_else(|| "Dokumen.pdf".to_string());

                            match BASE64_STANDARD.decode(base64_data.trim()) {
                                Ok(bytes) => {
                                    let temp_dir = std::env::temp_dir();
                                    let timestamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_millis();
                                    let safe_name = format!("SIAP_{}_{}", timestamp, doc_name.replace(['/', '\\', ':', '*', '?', '"', '<', '>', '|'], "_"));
                                    let temp_file_path = temp_dir.join(&safe_name);

                                    if let Err(write_err) = fs::write(&temp_file_path, bytes) {
                                        let err_msg = format!("Gagal menyimpan PDF sementara: {}", write_err);
                                        let mut resp = Response::from_string(serde_json::json!({"success":false,"error": err_msg}).to_string())
                                            .with_status_code(StatusCode(500));
                                        resp.add_header(cors_origin);
                                        resp.add_header(json_ct());
                                        let _ = request.respond(resp);
                                        continue;
                                    }

                                    let path_str = temp_file_path.to_string_lossy().to_string();
                                    let print_res = print_pdf_to_windows(&path_str, &final_printer);

                                    let (success, log_msg) = match print_res {
                                        Ok(msg) => (true, msg),
                                        Err(err) => (false, err),
                                    };

                                    let log_item = PrintLogItem {
                                        id: timestamp as u64,
                                        time: current_time_str(),
                                        filename: doc_name.clone(),
                                        printer: if final_printer.is_empty() { "Default Windows".to_string() } else { final_printer.clone() },
                                        status: if success { "SUKSES".to_string() } else { "GAGAL".to_string() },
                                        message: log_msg.clone(),
                                    };

                                    if let Ok(mut log_lock) = logs.lock() {
                                        log_lock.insert(0, log_item.clone());
                                        if log_lock.len() > 100 {
                                            log_lock.pop();
                                        }
                                    }

                                    // Emit event to frontend if app is available
                                    if let Ok(handle_guard) = app_handle.lock() {
                                        if let Some(h) = handle_guard.as_ref() {
                                            let _ = h.emit("new_print_log", log_item);
                                        }
                                    }

                                    let p_to_clean = temp_file_path.clone();
                                    thread::spawn(move || {
                                        thread::sleep(std::time::Duration::from_secs(60));
                                        let _ = fs::remove_file(p_to_clean);
                                    });

                                    let resp_json = serde_json::json!({
                                        "success": success,
                                        "message": log_msg,
                                        "error": if success { serde_json::Value::Null } else { serde_json::Value::String(log_msg.clone()) },
                                        "printer": final_printer,
                                        "filename": doc_name
                                    });
                                    let mut resp = Response::from_string(resp_json.to_string())
                                        .with_status_code(StatusCode(if success { 200 } else { 500 }));
                                    resp.add_header(cors_origin);
                                    resp.add_header(cors_pna);
                                    resp.add_header(json_ct());
                                    let _ = request.respond(resp);
                                }
                                Err(decode_err) => {
                                    let err_msg = format!("Format Base64 PDF tidak valid: {}", decode_err);
                                    let mut resp = Response::from_string(serde_json::json!({"success":false,"error": err_msg}).to_string())
                                        .with_status_code(StatusCode(400));
                                    resp.add_header(cors_origin);
                                    resp.add_header(json_ct());
                                    let _ = request.respond(resp);
                                }
                            }
                        }
                        _ => {
                            let mut resp = Response::from_string(r#"{"success":false,"error":"Body harus berisi pdfBase64"}"#)
                                .with_status_code(StatusCode(400));
                            resp.add_header(cors_origin);
                            resp.add_header(json_ct());
                            let _ = request.respond(resp);
                        }
                    }
                    continue;
                }

                let mut resp = Response::from_string("Not Found").with_status_code(StatusCode(404));
                resp.add_header(cors_origin);
                let _ = request.respond(resp);
            }
        }
        // `server` di-drop di sini sehingga port langsung dilepas
    });

    Ok(RunningServer { stop, handle })
}

/// Menghentikan server yang berjalan dan MENUNGGU thread-nya selesai (≤ 0,5 detik)
/// agar port benar-benar bebas sebelum dinyalakan ulang.
fn stop_server(slot: &Mutex<Option<RunningServer>>) {
    let running = slot.lock().unwrap().take();
    if let Some(srv) = running {
        srv.stop.store(true, Ordering::SeqCst);
        let _ = srv.handle.join();
    }
}

fn start_server(state: &AppState, port: u16) -> Result<(), String> {
    stop_server(&state.server);
    match start_http_listener(
        port,
        Arc::clone(&state.last_request),
        Arc::clone(&state.logs),
        Arc::clone(&state.selected_printer),
        Arc::clone(&state.cache),
        Arc::clone(&state.app_handle),
    ) {
        Ok(srv) => {
            *state.server.lock().unwrap() = Some(srv);
            state.is_running.store(true, Ordering::SeqCst);
            *state.server_error.lock().unwrap() = None;
            Ok(())
        }
        Err(e) => {
            state.is_running.store(false, Ordering::SeqCst);
            *state.server_error.lock().unwrap() = Some(e.clone());
            Err(e)
        }
    }
}

/// Menjalankan perintah dengan batas waktu (tanpa jendela). Mengembalikan (berhasil, keluaran gabungan).
fn run_with_timeout(program: &str, args: &[&str], secs: u64) -> Result<(bool, String), String> {
    let child = Command::new(program)
        .args(args)
        .creation_flags(CREATE_NO_WINDOW)
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .spawn()
        .map_err(|e| format!("Gagal menjalankan {}: {}", program, e))?;
    let (tx, rx) = std::sync::mpsc::channel();
    thread::spawn(move || {
        let _ = tx.send(child.wait_with_output());
    });
    match rx.recv_timeout(std::time::Duration::from_secs(secs)) {
        Ok(Ok(out)) => {
            let text = format!(
                "{}{}",
                String::from_utf8_lossy(&out.stdout),
                String::from_utf8_lossy(&out.stderr)
            );
            Ok((out.status.success(), text.trim().to_string()))
        }
        Ok(Err(e)) => Err(e.to_string()),
        Err(_) => Err("timeout".to_string()),
    }
}

// INSTANT in-memory state retrieval (0 processes, 0 cmd, 0 powershell)
#[tauri::command]
fn get_app_status(state: State<AppState>) -> AppStatus {
    let is_running = state.is_running.load(Ordering::SeqCst);
    let port = state.port.load(Ordering::SeqCst);
    let selected_printer = state.selected_printer.lock().unwrap().clone();
    let auto_start_startup = state.auto_start_startup.load(Ordering::SeqCst);
    let logs = state.logs.lock().unwrap().clone();
    let cache = state.cache.lock().unwrap().clone();

    AppStatus {
        is_running,
        port,
        tailscale_ip: cache.tailscale_ip,
        tailscale_funnel_domain: cache.tailscale_funnel_domain,
        tailscale_dns_name: cache.tailscale_dns_name,
        funnel_target_port: cache.funnel_target_port,
        local_ip: cache.local_ip,
        hostname: cache.hostname,
        selected_printer,
        auto_start_startup,
        printers: cache.printers,
        logs,
        firewall_ok: cache.firewall_ok && cache.firewall_port == port,
        sumatra_path: cache.sumatra_path,
        server_error: state.server_error.lock().unwrap().clone(),
        keep_funnel: state.keep_funnel.load(Ordering::SeqCst),
        last_request: state.last_request.lock().unwrap().clone(),
        peers: cache.peers,
    }
}

// Explicit refresh button: Only runs when user specifically clicks "Refresh" (with CREATE_NO_WINDOW)
#[tauri::command]
fn refresh_system_info(state: State<AppState>) -> AppStatus {
    let new_cache = query_system_info_silent(state.port.load(Ordering::SeqCst));
    *state.cache.lock().unwrap() = new_cache;
    get_app_status(state)
}

#[tauri::command]
fn toggle_server(enable: bool, state: State<AppState>) -> Result<bool, String> {
    if enable {
        if !state.is_running.load(Ordering::SeqCst) {
            start_server(&state, state.port.load(Ordering::SeqCst))?;
        }
    } else {
        state.is_running.store(false, Ordering::SeqCst);
        stop_server(&state.server);
    }
    Ok(state.is_running.load(Ordering::SeqCst))
}

/// Menambahkan aturan inbound Windows Firewall untuk port server (meminta izin Administrator/UAC).
/// Dibutuhkan agar HP bisa terhubung lewat IP Tailscale dan IP LAN. Jalur Funnel tidak membutuhkannya.
#[tauri::command]
fn allow_firewall(state: State<AppState>) -> Result<AppStatus, String> {
    let port = state.port.load(Ordering::SeqCst);
    let name = firewall_rule_name(port);
    let inner = format!(
        "netsh advfirewall firewall delete rule name=\"{name}\" | Out-Null; netsh advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={port} profile=any | Out-Null",
    );
    let outer = format!(
        "Start-Process -FilePath powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile','-EncodedCommand','{}'",
        ps_encoded(&inner)
    );
    let res = run_with_timeout("powershell", &["-NoProfile", "-Command", &outer], 120);
    let ok = firewall_rule_exists(port);
    {
        let mut c = state.cache.lock().unwrap();
        c.firewall_ok = ok;
        c.firewall_port = port;
    }
    if ok {
        Ok(get_app_status(state))
    } else {
        match res {
            Ok((_, out)) if !out.is_empty() => Err(format!("Aturan firewall belum dibuat: {}", out)),
            Err(e) => Err(format!("Aturan firewall belum dibuat: {}", e)),
            _ => Err("Aturan firewall belum dibuat (izin Administrator ditolak?)".to_string()),
        }
    }
}

fn persist_config(state: &AppState) {
    save_config(&AppConfig {
        port: state.port.load(Ordering::SeqCst),
        selected_printer: state.selected_printer.lock().unwrap().clone(),
        auto_start_startup: state.auto_start_startup.load(Ordering::SeqCst),
        keep_funnel: state.keep_funnel.load(Ordering::SeqCst),
    });
}

#[tauri::command]
fn set_keep_funnel(enable: bool, state: State<AppState>) -> AppStatus {
    state.keep_funnel.store(enable, Ordering::SeqCst);
    persist_config(&state);
    get_app_status(state)
}

/// Cek ringan (`tailscale serve status --json`) lalu aktifkan ulang Funnel bila hilang.
/// Dipanggil watcher latar tiap 90 detik HANYA bila pengguna mengaktifkan "Pulihkan Funnel otomatis".
fn ensure_funnel(port: u16, cache: &Arc<Mutex<CachedData>>) {
    for p in TAILSCALE_PATHS {
        let Ok((ok, out)) = run_with_timeout(p, &["serve", "status", "--json"], 15) else { continue };
        if !ok {
            return;
        }
        let active_port = serde_json::from_str::<serde_json::Value>(&out)
            .ok()
            .map(|v| parse_funnel_status(&v));
        let needs = match active_port {
            Some((Some(_), Some(target))) => target != port,
            Some((Some(_), None)) => false,
            _ => true,
        };
        if needs {
            let _ = run_with_timeout(p, &["funnel", "--bg", &port.to_string()], 25);
            let fresh = query_system_info_silent(port);
            if let Ok(mut c) = cache.lock() {
                *c = fresh;
            }
        }
        return;
    }
}

/// Menjalankan `tailscale funnel --bg <port>` lalu memperbarui status Funnel.
#[tauri::command]
fn enable_funnel(state: State<AppState>) -> Result<AppStatus, String> {
    let port = state.port.load(Ordering::SeqCst);
    let port_str = port.to_string();
    let mut last_err = String::from("Tailscale tidak ditemukan di PC ini");
    for p in TAILSCALE_PATHS {
        match run_with_timeout(p, &["funnel", "--bg", &port_str], 25) {
            Ok((true, _)) => {
                let new_cache = query_system_info_silent(port);
                let active = new_cache.tailscale_funnel_domain.is_some();
                *state.cache.lock().unwrap() = new_cache;
                if active {
                    // Pengguna sengaja mengaktifkan Funnel: pulihkan otomatis bila nanti hilang
                    state.keep_funnel.store(true, Ordering::SeqCst);
                    persist_config(&state);
                    return Ok(get_app_status(state));
                }
                last_err = "Perintah selesai tetapi Funnel belum aktif. Pastikan Funnel diizinkan di admin console Tailscale.".to_string();
                break;
            }
            Ok((false, out)) => {
                last_err = if out.is_empty() { "Tailscale menolak perintah funnel".to_string() } else { out };
                break;
            }
            Err(e) if e == "timeout" => {
                last_err = "Tailscale meminta persetujuan Funnel. Jalankan 'tailscale funnel --bg ".to_string()
                    + &port_str
                    + "' di Command Prompt untuk melihat tautan persetujuannya.";
                break;
            }
            Err(e) => last_err = e,
        }
    }
    Err(last_err)
}

#[tauri::command]
fn save_printer_default(printer_name: String, state: State<AppState>) -> Result<String, String> {
    let printer_name = bare_printer_name(&printer_name);
    *state.selected_printer.lock().unwrap() = printer_name.clone();

    let cfg = AppConfig {
        port: state.port.load(Ordering::SeqCst),
        selected_printer: printer_name.clone(),
        auto_start_startup: state.auto_start_startup.load(Ordering::SeqCst),
        keep_funnel: state.keep_funnel.load(Ordering::SeqCst),
    };
    save_config(&cfg);

    Ok(format!("Printer default berhasil disimpan: {}", printer_name))
}

#[tauri::command]
fn update_settings(
    new_port: u16,
    new_printer: String,
    auto_start: bool,
    state: State<AppState>,
) -> Result<AppStatus, String> {
    let old_port = state.port.load(Ordering::SeqCst);
    state.port.store(new_port, Ordering::SeqCst);
    *state.selected_printer.lock().unwrap() = new_printer.clone();
    state.auto_start_startup.store(auto_start, Ordering::SeqCst);

    let cfg = AppConfig {
        port: new_port,
        selected_printer: new_printer,
        auto_start_startup: auto_start,
        keep_funnel: state.keep_funnel.load(Ordering::SeqCst),
    };
    save_config(&cfg);
    configure_startup_shortcut(auto_start);

    if old_port != new_port {
        {
            let mut c = state.cache.lock().unwrap();
            c.firewall_ok = firewall_rule_exists(new_port);
            c.firewall_port = new_port;
        }
        if state.is_running.load(Ordering::SeqCst) {
            start_server(&state, new_port)?;
        }
    }

    Ok(get_app_status(state))
}

#[tauri::command]
fn test_print(printer_name: String, state: State<AppState>) -> Result<String, String> {
    let printers = state.cache.lock().unwrap().printers.clone();
    let final_printer = if !printer_name.is_empty() {
        printer_name
    } else {
        printers.iter().find(|p| p.is_default).map(|p| p.name.clone()).unwrap_or_default()
    };

    if final_printer.is_empty() {
        return Err("Tidak ada printer fisik yang dipilih atau terpasang di sistem.".to_string());
    }

    let temp_dir = std::env::temp_dir();
    let test_file = temp_dir.join("SIAP_Test_Print.txt");
    let content = format!(
        "=========================================\r\n\
         UJI COBA CETAK DOKUMEN RESMI SIAP NEO\r\n\
         KPU BEA CUKAI TANJUNG PRIOK\r\n\
         =========================================\r\n\
         Printer : {}\r\n\
         Waktu   : {}\r\n\
         Status  : Berhasil terhubung ke PC Server!\r\n\
         =========================================\r\n",
        final_printer,
        current_time_str()
    );
    let _ = fs::write(&test_file, content);

    let ps_cmd = format!(
        "Start-Process -FilePath notepad.exe -ArgumentList '/p \"{}\"' -WindowStyle Hidden",
        test_file.to_string_lossy()
    );
    let _ = Command::new("powershell")
        .args(["-NoProfile", "-Command", &ps_cmd])
        .creation_flags(CREATE_NO_WINDOW)
        .output();

    let timestamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_millis();
    let log_item = PrintLogItem {
        id: timestamp as u64,
        time: current_time_str(),
        filename: "Uji_Cetak_SIAP.txt".to_string(),
        printer: final_printer.clone(),
        status: "SUKSES".to_string(),
        message: "Halaman uji coba dikirim ke printer".to_string(),
    };
    if let Ok(mut log_lock) = state.logs.lock() {
        log_lock.insert(0, log_item);
    }

    Ok(format!("Uji coba cetak berhasil dikirim ke {}", final_printer))
}

#[tauri::command]
fn clear_logs(state: State<AppState>) -> Result<bool, String> {
    if let Ok(mut log_lock) = state.logs.lock() {
        log_lock.clear();
    }
    Ok(true)
}

#[tauri::command]
fn open_tailscale() -> Result<String, String> {
    let paths = [
        "C:\\Program Files\\Tailscale\\tailscale.exe",
        "tailscale.exe",
    ];
    for p in paths {
        if Command::new(p)
            .arg("up")
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .is_ok()
        {
            return Ok("Membuka Tailscale...".to_string());
        }
    }
    Ok("Silakan buka aplikasi Tailscale dari menu Start".to_string())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let saved_cfg = load_config();
    let is_running = Arc::new(AtomicBool::new(false));
    let port = Arc::new(AtomicU16::new(saved_cfg.port));
    let selected_printer = Arc::new(Mutex::new(bare_printer_name(&saved_cfg.selected_printer)));
    let auto_start_startup = Arc::new(AtomicBool::new(saved_cfg.auto_start_startup));
    let logs = Arc::new(Mutex::new(Vec::new()));
    let app_handle_holder = Arc::new(Mutex::new(None));

    // Read initial system info ONCE silently with CREATE_NO_WINDOW
    let initial_cache = query_system_info_silent(saved_cfg.port);

    if saved_cfg.selected_printer.is_empty() {
        if let Some(def) = initial_cache.printers.iter().find(|p| p.is_default) {
            *selected_printer.lock().unwrap() = def.name.clone();
        } else if let Some(first) = initial_cache.printers.first() {
            *selected_printer.lock().unwrap() = first.name.clone();
        }
    }

    // Funnel sudah aktif saat aplikasi dibuka = pilihan pengguna; jaga tetap aktif
    let keep_funnel = Arc::new(AtomicBool::new(
        saved_cfg.keep_funnel || initial_cache.tailscale_funnel_domain.is_some(),
    ));
    let cache = Arc::new(Mutex::new(initial_cache));

    let app_state = AppState {
        is_running,
        port,
        selected_printer,
        auto_start_startup,
        logs,
        cache,
        server: Arc::new(Mutex::new(None)),
        server_error: Arc::new(Mutex::new(None)),
        app_handle: Arc::clone(&app_handle_holder),
        keep_funnel: Arc::clone(&keep_funnel),
        last_request: Arc::new(Mutex::new(None)),
    };
    persist_config(&app_state);

    // Watcher Funnel (ringan, 1 proses tiap 90 detik, hanya bila diaktifkan pengguna)
    {
        let keep = Arc::clone(&app_state.keep_funnel);
        let running = Arc::clone(&app_state.is_running);
        let port_ref = Arc::clone(&app_state.port);
        let cache_ref = Arc::clone(&app_state.cache);
        thread::spawn(move || loop {
            thread::sleep(std::time::Duration::from_secs(90));
            if keep.load(Ordering::SeqCst) && running.load(Ordering::SeqCst) {
                ensure_funnel(port_ref.load(Ordering::SeqCst), &cache_ref);
            }
        });
    }
    if let Err(e) = start_server(&app_state, saved_cfg.port) {
        eprintln!("Warning: could not auto start server: {}", e);
    }

    tauri::Builder::default()
        .manage(app_state)
        .setup(move |app| {
            *app_handle_holder.lock().unwrap() = Some(app.handle().clone());

            let show_i = MenuItem::with_id(app, "show", "Buka SIAP Print", true, None::<&str>)?;
            let hide_i = MenuItem::with_id(app, "hide", "Sembunyikan ke Tray", true, None::<&str>)?;
            let quit_i = MenuItem::with_id(app, "quit", "Keluar", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&show_i, &hide_i, &quit_i])?;

            if let Some(icon) = app.default_window_icon() {
                let _tray = TrayIconBuilder::new()
                    .icon(icon.clone())
                    .tooltip("SIAP Print Tailscale - KPU Bea Cukai Tanjung Priok")
                    .menu(&menu)
                    .show_menu_on_left_click(false)
                    .on_menu_event(|app, event| {
                        match event.id.as_ref() {
                            "show" => {
                                if let Some(window) = app.get_webview_window("main") {
                                    let _ = window.show();
                                    let _ = window.set_focus();
                                }
                            }
                            "hide" => {
                                if let Some(window) = app.get_webview_window("main") {
                                    let _ = window.hide();
                                }
                            }
                            "quit" => {
                                app.exit(0);
                            }
                            _ => {}
                        }
                    })
                    .on_tray_icon_event(|tray, event| {
                        if let TrayIconEvent::Click { button: MouseButton::Left, .. } = event {
                            let app = tray.app_handle();
                            if let Some(window) = app.get_webview_window("main") {
                                if window.is_visible().unwrap_or(false) {
                                    let _ = window.hide();
                                } else {
                                    let _ = window.show();
                                    let _ = window.set_focus();
                                }
                            }
                        }
                    })
                    .build(app)?;
            }

            let args: Vec<String> = std::env::args().collect();
            let start_hidden = args.iter().any(|a| a == "--hidden" || a == "--minimized" || a == "--startup");
            if start_hidden {
                if let Some(window) = app.get_webview_window("main") {
                    let _ = window.hide();
                }
            }

            Ok(())
        })
        .on_window_event(|window, event| {
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                let _ = window.hide();
                api.prevent_close();
            }
        })
        .invoke_handler(tauri::generate_handler![
            get_app_status,
            refresh_system_info,
            toggle_server,
            update_settings,
            save_printer_default,
            test_print,
            clear_logs,
            open_tailscale,
            allow_firewall,
            enable_funnel,
            set_keep_funnel
        ])
        .run(tauri::generate_context!())
        .expect("error while running SIAP Print Tailscale");
}
