use std::fs;
use std::process::Command;
use std::sync::atomic::{AtomicBool, AtomicU16, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{SystemTime, UNIX_EPOCH};

use base64::prelude::*;
use serde::{Deserialize, Serialize};
use tauri::State;
use tiny_http::{Header, Method, Response, Server, StatusCode};

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
pub struct AppStatus {
    pub is_running: bool,
    pub port: u16,
    pub tailscale_ip: Option<String>,
    pub local_ip: String,
    pub hostname: String,
    pub selected_printer: String,
    pub printers: Vec<PrinterInfo>,
    pub logs: Vec<PrintLogItem>,
}

pub struct AppState {
    pub is_running: Arc<AtomicBool>,
    pub port: Arc<AtomicU16>,
    pub selected_printer: Arc<Mutex<String>>,
    pub logs: Arc<Mutex<Vec<PrintLogItem>>>,
    pub server_handle: Arc<Mutex<Option<thread::JoinHandle<()>>>>,
}

// Helper: Format current time as HH:MM:SS
fn current_time_str() -> String {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let hours = (now / 3600) % 24;
    let minutes = (now / 60) % 60;
    let seconds = now % 60;
    format!("{:02}:{:02}:{:02}", (hours + 7) % 24, minutes, seconds) // UTC+7 approx
}

// Helper: Detect Tailscale IPv4
fn detect_tailscale_ip() -> Option<String> {
    // 1. Try running tailscale.exe ip -4
    let paths = [
        "C:\\Program Files\\Tailscale\\tailscale.exe",
        "tailscale.exe",
    ];
    for p in paths {
        if let Ok(output) = Command::new(p).args(["ip", "-4"]).output() {
            if output.status.success() {
                let s = String::from_utf8_lossy(&output.stdout).trim().to_string();
                if s.starts_with("100.") {
                    return Some(s);
                }
            }
        }
    }

    // 2. Fallback: Parse ipconfig for 100.x.x.x
    if let Ok(output) = Command::new("powershell")
        .args(["-NoProfile", "-Command", "Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '100.*' } | Select-Object -ExpandProperty IPAddress -First 1"])
        .output()
    {
        if output.status.success() {
            let s = String::from_utf8_lossy(&output.stdout).trim().to_string();
            if s.starts_with("100.") {
                return Some(s);
            }
        }
    }

    None
}

// Helper: Detect Local LAN IPv4
fn detect_local_ip() -> String {
    if let Ok(output) = Command::new("powershell")
        .args(["-NoProfile", "-Command", "Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '100.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -ExpandProperty IPAddress -First 1"])
        .output()
    {
        if output.status.success() {
            let s = String::from_utf8_lossy(&output.stdout).trim().to_string();
            if !s.is_empty() {
                return s;
            }
        }
    }
    "127.0.0.1".to_string()
}

// Helper: Fetch installed Windows printers
fn detect_printers() -> Vec<PrinterInfo> {
    let script = r#"
        Get-Printer | Select-Object Name, DriverName, PortName, @{Name='Default';Expression={$_.Default}} | ConvertTo-Json -Compress
    "#;

    if let Ok(output) = Command::new("powershell")
        .args(["-NoProfile", "-Command", script])
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

                return items
                    .into_iter()
                    .filter_map(|item| {
                        let name = item["Name"].as_str()?.to_string();
                        let driver = item["DriverName"].as_str().unwrap_or("").to_string();
                        let port = item["PortName"].as_str().unwrap_or("").to_string();
                        let is_default = item["Default"].as_bool().unwrap_or(false);
                        Some(PrinterInfo {
                            name,
                            driver,
                            port,
                            is_default,
                        })
                    })
                    .collect();
            }
        }
    }
    vec![]
}

// Helper: Print a PDF file directly using SumatraPDF or PowerShell PrintTo
fn print_pdf_to_windows(file_path: &str, target_printer: &str) -> Result<String, String> {
    // Check for SumatraPDF in common paths
    let local_app_data = std::env::var("LOCALAPPDATA").unwrap_or_default();
    let sumatra_paths = [
        format!("{}\\SumatraPDF\\SumatraPDF.exe", local_app_data),
        "C:\\Program Files\\SumatraPDF\\SumatraPDF.exe".to_string(),
        "C:\\Program Files (x86)\\SumatraPDF\\SumatraPDF.exe".to_string(),
    ];

    let found_sumatra = sumatra_paths.iter().find(|p| std::path::Path::new(p).exists());

    if let Some(sumatra_exe) = found_sumatra {
        let mut cmd = Command::new(sumatra_exe);
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
        // Fallback: PowerShell PrintTo
        let ps_cmd = if target_printer.is_empty() {
            format!("Start-Process -FilePath \"{}\" -Verb Print -PassThru | Out-Null", file_path)
        } else {
            format!("Start-Process -FilePath \"{}\" -Verb PrintTo -ArgumentList '\"{}\"' -PassThru | Out-Null", file_path, target_printer)
        };

        match Command::new("powershell")
            .args(["-NoProfile", "-Command", &ps_cmd])
            .output()
        {
            Ok(output) if output.status.success() => Ok("Dicetak via Windows Shell".to_string()),
            Ok(output) => Err(format!("Print command error: {}", String::from_utf8_lossy(&output.stderr))),
            Err(e) => Err(format!("Gagal mengeksekusi print: {}", e)),
        }
    }
}

// Background HTTP Server implementation using tiny_http
fn start_http_listener(
    port: u16,
    is_running: Arc<AtomicBool>,
    logs: Arc<Mutex<Vec<PrintLogItem>>>,
    selected_printer: Arc<Mutex<String>>,
) -> Result<thread::JoinHandle<()>, String> {
    let addr = format!("0.0.0.0:{}", port);
    let server = Server::http(&addr).map_err(|e| format!("Gagal bind port {}: {}", port, e))?;

    let handle = thread::spawn(move || {
        while is_running.load(Ordering::SeqCst) {
            // Non-blocking or timed accept so we can exit cleanly
            if let Ok(Some(mut request)) = server.recv_timeout(std::time::Duration::from_millis(500)) {
                let url = request.url().to_string();
                let method = request.method().clone();

                // Common CORS headers
                let cors_origin = Header::from_bytes(&b"Access-Control-Allow-Origin"[..], &b"*"[..]).unwrap();
                let cors_headers = Header::from_bytes(&b"Access-Control-Allow-Headers"[..], &b"Origin, Content-Type, Accept, Authorization"[..]).unwrap();
                let cors_methods = Header::from_bytes(&b"Access-Control-Allow-Methods"[..], &b"GET, POST, OPTIONS"[..]).unwrap();
                let cors_pna = Header::from_bytes(&b"Access-Control-Allow-Private-Network"[..], &b"true"[..]).unwrap();

                // Handle preflight OPTIONS
                if method == Method::Options {
                    let mut resp = Response::empty(StatusCode(204));
                    resp.add_header(cors_origin);
                    resp.add_header(cors_headers);
                    resp.add_header(cors_methods);
                    resp.add_header(cors_pna);
                    let _ = request.respond(resp);
                    continue;
                }

                // Handle /ping
                if url == "/ping" && method == Method::Get {
                    let mut resp = Response::from_string("🖨️ SIAP Print Server is running");
                    resp.add_header(cors_origin);
                    resp.add_header(cors_pna);
                    let _ = request.respond(resp);
                    continue;
                }

                // Handle /info
                if url == "/info" && method == Method::Get {
                    let ts_ip = detect_tailscale_ip();
                    let local_ip = detect_local_ip();
                    let curr_printer = selected_printer.lock().unwrap().clone();
                    let json = serde_json::json!({
                        "status": "online",
                        "port": port,
                        "tailscaleIp": ts_ip,
                        "localIp": local_ip,
                        "selectedPrinter": curr_printer
                    });
                    let mut resp = Response::from_string(json.to_string());
                    resp.add_header(cors_origin);
                    resp.add_header(cors_pna);
                    resp.add_header(Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap());
                    let _ = request.respond(resp);
                    continue;
                }

                // Handle /printers
                if url == "/printers" && method == Method::Get {
                    let list = detect_printers();
                    let json = serde_json::to_string(&list).unwrap_or_else(|_| "[]".to_string());
                    let mut resp = Response::from_string(json);
                    resp.add_header(cors_origin);
                    resp.add_header(cors_pna);
                    resp.add_header(Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap());
                    let _ = request.respond(resp);
                    continue;
                }

                // Handle /print-pdf (POST)
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

                                    // Record log
                                    let log_item = PrintLogItem {
                                        id: timestamp as u64,
                                        time: current_time_str(),
                                        filename: doc_name.clone(),
                                        printer: if final_printer.is_empty() { "Default Windows".to_string() } else { final_printer.clone() },
                                        status: if success { "SUKSES".to_string() } else { "GAGAL".to_string() },
                                        message: log_msg.clone(),
                                    };
                                    if let Ok(mut log_lock) = logs.lock() {
                                        log_lock.insert(0, log_item);
                                        if log_lock.len() > 100 {
                                            log_lock.pop();
                                        }
                                    }

                                    // Clean up temp file after delay
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

                // Default 404
                let mut resp = Response::from_string("Not Found");
                resp.add_header(cors_origin);
                let _ = request.respond(resp);
            }
        }
    });

    Ok(handle)
}

// Tauri Commands
#[tauri::command]
fn get_app_status(state: State<AppState>) -> AppStatus {
    let is_running = state.is_running.load(Ordering::SeqCst);
    let port = state.port.load(Ordering::SeqCst);
    let tailscale_ip = detect_tailscale_ip();
    let local_ip = detect_local_ip();
    let hostname = std::env::var("COMPUTERNAME").unwrap_or_else(|_| "Windows-PC".to_string());
    let selected_printer = state.selected_printer.lock().unwrap().clone();
    let printers = detect_printers();
    let logs = state.logs.lock().unwrap().clone();

    AppStatus {
        is_running,
        port,
        tailscale_ip,
        local_ip,
        hostname,
        selected_printer,
        printers,
        logs,
    }
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
fn update_settings(
    new_port: u16,
    new_printer: String,
    state: State<AppState>,
) -> Result<AppStatus, String> {
    let old_port = state.port.load(Ordering::SeqCst);
    state.port.store(new_port, Ordering::SeqCst);
    *state.selected_printer.lock().unwrap() = new_printer;

    // Restart server if running and port changed
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
        )?;
        *state.server_handle.lock().unwrap() = Some(handle);
    }

    Ok(get_app_status(state))
}

#[tauri::command]
fn test_print(printer_name: String, state: State<AppState>) -> Result<String, String> {
    let printers = detect_printers();
    let final_printer = if !printer_name.is_empty() {
        printer_name
    } else {
        printers.iter().find(|p| p.is_default).map(|p| p.name.clone()).unwrap_or_default()
    };

    if final_printer.is_empty() {
        return Err("Tidak ada printer yang dipilih atau terpasang di sistem.".to_string());
    }

    // Generate small test text file or test page
    let temp_dir = std::env::temp_dir();
    let test_file = temp_dir.join("SIAP_Test_Print.txt");
    let content = format!(
        "=========================================\r\n\
         UJI COBA CETAK DOKUMEN SIAP NEO\r\n\
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

    // Print test file using Notepad /p or PowerShell
    let ps_cmd = format!(
        "Start-Process -FilePath notepad.exe -ArgumentList '/p \"{}\"' -WindowStyle Hidden",
        test_file.to_string_lossy()
    );
    let _ = Command::new("powershell").args(["-NoProfile", "-Command", &ps_cmd]).output();

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
        if Command::new(p).arg("up").spawn().is_ok() {
            return Ok("Membuka Tailscale...".to_string());
        }
    }
    Ok("Silakan buka aplikasi Tailscale dari menu Start".to_string())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let is_running = Arc::new(AtomicBool::new(true));
    let port = Arc::new(AtomicU16::new(1234));
    let selected_printer = Arc::new(Mutex::new(String::new()));
    let logs = Arc::new(Mutex::new(Vec::new()));

    // Automatically set default printer from Windows
    let printers = detect_printers();
    if let Some(def) = printers.iter().find(|p| p.is_default) {
        *selected_printer.lock().unwrap() = def.name.clone();
    } else if let Some(first) = printers.first() {
        *selected_printer.lock().unwrap() = first.name.clone();
    }

    // Auto start server on port 1234
    let server_handle = match start_http_listener(
        1234,
        Arc::clone(&is_running),
        Arc::clone(&logs),
        Arc::clone(&selected_printer),
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
        logs,
        server_handle,
    };

    tauri::Builder::default()
        .manage(app_state)
        .invoke_handler(tauri::generate_handler![
            get_app_status,
            toggle_server,
            update_settings,
            test_print,
            clear_logs,
            open_tailscale
        ])
        .run(tauri::generate_context!())
        .expect("error while running SIAP Print Tailscale");
}
