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
}

#[derive(Serialize, Deserialize, Clone, Debug, Default)]
pub struct CachedData {
    pub tailscale_ip: Option<String>,
    pub tailscale_funnel_domain: Option<String>,
    pub local_ip: String,
    pub hostname: String,
    pub printers: Vec<PrinterInfo>,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct AppStatus {
    pub is_running: bool,
    pub port: u16,
    pub tailscale_ip: Option<String>,
    pub tailscale_funnel_domain: Option<String>,
    pub local_ip: String,
    pub hostname: String,
    pub selected_printer: String,
    pub auto_start_startup: bool,
    pub printers: Vec<PrinterInfo>,
    pub logs: Vec<PrintLogItem>,
}

pub struct AppState {
    pub is_running: Arc<AtomicBool>,
    pub port: Arc<AtomicU16>,
    pub selected_printer: Arc<Mutex<String>>,
    pub auto_start_startup: Arc<AtomicBool>,
    pub logs: Arc<Mutex<Vec<PrintLogItem>>>,
    pub cache: Arc<Mutex<CachedData>>,
    pub server_handle: Arc<Mutex<Option<thread::JoinHandle<()>>>>,
    pub app_handle: Arc<Mutex<Option<tauri::AppHandle>>>,
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
fn query_system_info_silent() -> CachedData {
    let local_ip = detect_local_ip_native();
    let hostname = std::env::var("COMPUTERNAME").unwrap_or_else(|_| "Windows-PC".to_string());

    let mut tailscale_ip: Option<String> = None;
    let mut tailscale_funnel_domain: Option<String> = None;

    let ts_paths = [
        "C:\\Program Files\\Tailscale\\tailscale.exe",
        "tailscale.exe",
    ];

    for p in ts_paths {
        // 1. Get Tailscale IP
        if tailscale_ip.is_none() {
            if let Ok(output) = Command::new(p)
                .args(["ip", "-4"])
                .creation_flags(CREATE_NO_WINDOW)
                .output()
            {
                if output.status.success() {
                    let s = String::from_utf8_lossy(&output.stdout).trim().to_string();
                    if s.starts_with("100.") {
                        tailscale_ip = Some(s);
                    }
                }
            }
        }

        // 2. Get Tailscale Serve/Funnel domain
        if tailscale_funnel_domain.is_none() {
            if let Ok(output) = Command::new(p)
                .args(["serve", "status", "--json"])
                .creation_flags(CREATE_NO_WINDOW)
                .output()
            {
                if output.status.success() {
                    let s = String::from_utf8_lossy(&output.stdout);
                    if let Ok(val) = serde_json::from_str::<serde_json::Value>(&s) {
                        if let Some(fg) = val.get("Foreground").and_then(|f| f.as_object()) {
                            for (_k, v) in fg {
                                if let Some(web) = v.get("Web").and_then(|w| w.as_object()) {
                                    for domain_port in web.keys() {
                                        let domain = domain_port.split(':').next().unwrap_or(domain_port);
                                        if !domain.is_empty() {
                                            tailscale_funnel_domain = Some(domain.to_string());
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // 3. Fallback: query status --json for DNSName
        if tailscale_funnel_domain.is_none() {
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
                                tailscale_funnel_domain = Some(trimmed.to_string());
                            }
                        }
                    }
                }
            }
        }

        if tailscale_ip.is_some() && tailscale_funnel_domain.is_some() {
            break;
        }
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
        local_ip,
        hostname,
        printers,
    }
}

fn print_pdf_to_windows(file_path: &str, target_printer: &str) -> Result<String, String> {
    let local_app_data = std::env::var("LOCALAPPDATA").unwrap_or_default();
    let sumatra_paths = [
        format!("{}\\SumatraPDF\\SumatraPDF.exe", local_app_data),
        "C:\\Program Files\\SumatraPDF\\SumatraPDF.exe".to_string(),
        "C:\\Program Files (x86)\\SumatraPDF\\SumatraPDF.exe".to_string(),
    ];

    let found_sumatra = sumatra_paths.iter().find(|p| std::path::Path::new(p).exists());

    if let Some(sumatra_exe) = found_sumatra {
        let mut cmd = Command::new(sumatra_exe);
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
    is_running: Arc<AtomicBool>,
    logs: Arc<Mutex<Vec<PrintLogItem>>>,
    selected_printer: Arc<Mutex<String>>,
    cache: Arc<Mutex<CachedData>>,
    app_handle: Arc<Mutex<Option<tauri::AppHandle>>>,
) -> Result<thread::JoinHandle<()>, String> {
    let addr = format!("0.0.0.0:{}", port);
    let server = Server::http(&addr).map_err(|e| format!("Gagal bind port {}: {}", port, e))?;

    let handle = thread::spawn(move || {
        while is_running.load(Ordering::SeqCst) {
            if let Ok(Some(mut request)) = server.recv_timeout(std::time::Duration::from_millis(500)) {
                let url = request.url().to_string();
                let method = request.method().clone();

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
                        "port": port,
                        "tailscaleIp": c.tailscale_ip,
                        "tailscaleFunnelDomain": c.tailscale_funnel_domain,
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
                        let mut resp = Response::from_string(r#"{"success":false,"error":"Gagal membaca body request"}"#);
                        resp.add_header(cors_origin);
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
                            let req_printer = payload.printer_name.unwrap_or_default();
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
                                        let mut resp = Response::from_string(serde_json::json!({"success":false,"error": err_msg}).to_string());
                                        resp.add_header(cors_origin);
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
                                        "printer": final_printer,
                                        "filename": doc_name
                                    });
                                    let mut resp = Response::from_string(resp_json.to_string());
                                    resp.add_header(cors_origin);
                                    resp.add_header(cors_pna);
                                    resp.add_header(Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap());
                                    let _ = request.respond(resp);
                                }
                                Err(decode_err) => {
                                    let err_msg = format!("Format Base64 PDF tidak valid: {}", decode_err);
                                    let mut resp = Response::from_string(serde_json::json!({"success":false,"error": err_msg}).to_string());
                                    resp.add_header(cors_origin);
                                    let _ = request.respond(resp);
                                }
                            }
                        }
                        _ => {
                            let mut resp = Response::from_string(r#"{"success":false,"error":"Body harus berisi pdfBase64"}"#);
                            resp.add_header(cors_origin);
                            let _ = request.respond(resp);
                        }
                    }
                    continue;
                }

                let mut resp = Response::from_string("Not Found");
                resp.add_header(cors_origin);
                let _ = request.respond(resp);
            }
        }
    });

    Ok(handle)
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
        local_ip: cache.local_ip,
        hostname: cache.hostname,
        selected_printer,
        auto_start_startup,
        printers: cache.printers,
        logs,
    }
}

// Explicit refresh button: Only runs when user specifically clicks "Refresh" (with CREATE_NO_WINDOW)
#[tauri::command]
fn refresh_system_info(state: State<AppState>) -> AppStatus {
    let new_cache = query_system_info_silent();
    *state.cache.lock().unwrap() = new_cache;
    get_app_status(state)
}

#[tauri::command]
fn toggle_server(enable: bool, state: State<AppState>) -> Result<bool, String> {
    if enable {
        if !state.is_running.load(Ordering::SeqCst) {
            let port = state.port.load(Ordering::SeqCst);
            state.is_running.store(true, Ordering::SeqCst);
            let handle = start_http_listener(
                port,
                Arc::clone(&state.is_running),
                Arc::clone(&state.logs),
                Arc::clone(&state.selected_printer),
                Arc::clone(&state.cache),
                Arc::clone(&state.app_handle),
            )?;
            *state.server_handle.lock().unwrap() = Some(handle);
        }
    } else {
        state.is_running.store(false, Ordering::SeqCst);
        *state.server_handle.lock().unwrap() = None;
    }
    Ok(state.is_running.load(Ordering::SeqCst))
}

#[tauri::command]
fn save_printer_default(printer_name: String, state: State<AppState>) -> Result<String, String> {
    *state.selected_printer.lock().unwrap() = printer_name.clone();

    let cfg = AppConfig {
        port: state.port.load(Ordering::SeqCst),
        selected_printer: printer_name.clone(),
        auto_start_startup: state.auto_start_startup.load(Ordering::SeqCst),
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
    };
    save_config(&cfg);
    configure_startup_shortcut(auto_start);

    if state.is_running.load(Ordering::SeqCst) && old_port != new_port {
        state.is_running.store(false, Ordering::SeqCst);
        *state.server_handle.lock().unwrap() = None;
        thread::sleep(std::time::Duration::from_millis(300));
        state.is_running.store(true, Ordering::SeqCst);
        let handle = start_http_listener(
            new_port,
            Arc::clone(&state.is_running),
            Arc::clone(&state.logs),
            Arc::clone(&state.selected_printer),
            Arc::clone(&state.cache),
            Arc::clone(&state.app_handle),
        )?;
        *state.server_handle.lock().unwrap() = Some(handle);
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
    let is_running = Arc::new(AtomicBool::new(true));
    let port = Arc::new(AtomicU16::new(saved_cfg.port));
    let selected_printer = Arc::new(Mutex::new(saved_cfg.selected_printer.clone()));
    let auto_start_startup = Arc::new(AtomicBool::new(saved_cfg.auto_start_startup));
    let logs = Arc::new(Mutex::new(Vec::new()));
    let app_handle_holder = Arc::new(Mutex::new(None));

    // Read initial system info ONCE silently with CREATE_NO_WINDOW
    let initial_cache = query_system_info_silent();

    if saved_cfg.selected_printer.is_empty() {
        if let Some(def) = initial_cache.printers.iter().find(|p| p.is_default) {
            *selected_printer.lock().unwrap() = def.name.clone();
        } else if let Some(first) = initial_cache.printers.first() {
            *selected_printer.lock().unwrap() = first.name.clone();
        }
    }

    let cache = Arc::new(Mutex::new(initial_cache));

    let server_handle = match start_http_listener(
        saved_cfg.port,
        Arc::clone(&is_running),
        Arc::clone(&logs),
        Arc::clone(&selected_printer),
        Arc::clone(&cache),
        Arc::clone(&app_handle_holder),
    ) {
        Ok(h) => Arc::new(Mutex::new(Some(h))),
        Err(e) => {
            eprintln!("Warning: could not auto start server: {}", e);
            Arc::new(Mutex::new(None))
        }
    };

    let app_state = AppState {
        is_running,
        port,
        selected_printer,
        auto_start_startup,
        logs,
        cache,
        server_handle,
        app_handle: Arc::clone(&app_handle_holder),
    };

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
            open_tailscale
        ])
        .run(tauri::generate_context!())
        .expect("error while running SIAP Print Tailscale");
}
