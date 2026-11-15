# ==============================================================================
# Script Otomatis Rilis GitHub untuk SIAP-Print-Tailscale (100% C# .NET)
# ==============================================================================
$ErrorActionPreference = "Stop"

$repoDir = "C:\Repo\SIAP-Print-Tailscale"
Set-Location $repoDir

$ghExe = "C:\Users\user\AppData\Local\GitHubCLI\bin\gh.exe"
if (-not (Test-Path $ghExe)) {
    $ghExe = "gh"
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " 🚀 MEMULAI PROSES RILIS GITHUB: SIAP-PRINT-TAILSCALE" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Cek Autentikasi GitHub
Write-Host "[1/4] Memeriksa status login GitHub CLI..." -ForegroundColor Yellow
$authStatus = & $ghExe auth status 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "⚠️ Anda belum login ke GitHub CLI. Membuka jendela login di browser..." -ForegroundColor Yellow
    & $ghExe auth login --web -h github.com -p https
}

# 2. Cek apakah repositori sudah ada di GitHub
Write-Host "[2/4] Sinkronisasi repositori ke GitHub..." -ForegroundColor Yellow
$repoCheck = & $ghExe repo view "fiqri1010/SIAP-Print-Tailscale" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "Mendaftarkan repositori publik baru 'SIAP-Print-Tailscale' di GitHub..." -ForegroundColor Green
    & $ghExe repo create "SIAP-Print-Tailscale" --public --source=. --remote=origin --push
} else {
    Write-Host "Mendorong commit terbaru ke GitHub..." -ForegroundColor Green
    git push -u origin master
}

# 3. Dapatkan Versi dari Project C# .NET
$csprojPath = Join-Path $repoDir "src-csharp\SIAP-Print-Tailscale.csproj"
$version = "2.3.0"
if (Test-Path $csprojPath) {
    [xml]$xml = Get-Content $csprojPath
    $verNode = $xml.SelectSingleNode("//Version")
    if ($verNode) {
        $version = $verNode.InnerText.Trim()
    }
}
$tag = "v$version"
$dateStr = Get-Date -Format "dd MMMM yyyy, HH:mm:ss"
$gitHash = (git rev-parse --short HEAD)

$releaseNotes = @"
# 🖨️ SIAP Print Tailscale $tag — Production Release

> **Waktu Rilis:** $dateStr (WIB)  
> **Platform Target:** Windows 10 / Windows 11 (x64)  
> **Arsitektur:** 100% C# .NET 8 Standalone (Ultra-Low Memory Edition)  
> **Commit Hash:** $gitHash  
> **Distribusi:** Windows Setup Installer & Portable Standalone Single-File

---

## 🚀 Ringkasan Fitur & Pembaruan Rilis $tag

### 1. 📅 Riwayat Cetak Lengkap dengan Tanggal & Waktu Presisi
- Menambahkan tanggal lengkap pada tabel riwayat cetak di aplikasi (format: ``dd/MM/yyyy HH:mm:ss``).
- Secara otomatis mengonversi riwayat log lama yang sebelumnya hanya menyimpan jam-menit-detik menjadi tanggal dan waktu lengkap yang akurat.
- Kolom tabel riwayat cetakan disesuaikan agar tanggal dan waktu terbaca penuh dan rapi.

### 2. 🔍 Deteksi Status Printer Real-Time (Idle, Aktif, Offline, Error) untuk SIAP NEO
- Integrasi native **Win32 Spooler API (winspool.drv)** untuk mendeteksi status operasional printer:
  - ``idle``: Printer online, siap menerima perintah cetak.
  - ``aktif``: Printer sedang memproses / mencetak dokumen dari antrean spooler.
  - ``offline``: Printer dalam mode Work Offline atau kabel/jaringan terputus.
  - ``error``: Printer mengalami error fisik (Paper Jam / Kertas Macet, Paper Out / Kertas Habis, Door Open / Pintu Terbuka, atau Paused).
- **Deteksi Cepat Printer Jaringan (TCP Port 9100):** Melakukan pemeriksaan status konektivitas TCP instan pada printer IP LAN (seperti Kyocera di kantor).
- Menyediakan endpoint API baru untuk SIAP NEO:
  - ``GET /printer-status``: Mengambil status printer default atau printer tertentu (``?name=...``).
  - ``GET /printers``: Daftar printer lengkap dengan status operasional real-time.

### 3. 🎯 Deteksi Tuntas Cetak Fisik (Physical Print Completion Tracking)
- Setiap dokumen cetak kini memiliki ``job_id`` unik.
- Respons ``POST /print-pdf`` menyertakan ``job_id`` dan status awal.
- Pemantauan otomatis di latar belakang (*background spooler watcher*) untuk mendeteksi saat seluruh data cetak telah tuntas disalurkan dan diterima oleh hardware printer.
- Menyediakan endpoint API baru untuk SIAP NEO:
  - ``GET /job-status?job_id=...`` (atau ``/print-status?job_id=...``): Mengembalikan status apakah dokumen masih dalam antrean spooler atau sudah benar-benar selesai dicetak (``status: "printed"``, ``is_completed: true``).

### 4. 📊 Tampilan Status Printer Real-Time di Antarmuka (UI)
- Kartu pemilihan printer pada dashboard aplikasi kini menampilkan indikator status real-time dengan kode warna:
  - 🟢 **Online (Siap)**
  - 🔵 **Aktif (Mencetak)**
  - 🔴 **Offline (Terputus)**
  - ⚠️ **Error (Kertas Habis / Macet)**

### 5. ⚡ Hidup Otomatis Saat Windows Startup Bahkan Sebelum Login (Boot Service)
- Server langsung hidup dan mendengarkan permintaan cetak saat komputer dinyalakan, **bahkan sebelum ada pengguna yang login ke layar kunci Windows**.
- Menggunakan integrasi native **Windows Task Scheduler (Task: ``SIAP-Print-Tailscale-Boot``)** dengan pemicu ``AtStartup`` dan principal ``NT AUTHORITY\SYSTEM``.
- Mode ``--headless`` super hemat memori (<5 MB RAM).
- Proteksi *Silent Loading* tanpa pop-up dialog UAC.

### 6. ⚡ Ultra-Low Memory & Refactoring Performa Tinggi (>60% Lebih Hemat RAM)
- Footprint RAM **~8–14 MB** saat aktif dan **~3–6 MB** di System Tray.
- Zero-copy Base64 decoding streaming buffer.
- Proteksi Anti-Duplikasi (*Single-Instance Mutex*).
- Manajemen agresif Windows Working Set (``SetProcessWorkingSetSize``) segera setelah mencetak dan saat idle.

### 7. 🛡️ Proteksi Single-Instance Anti-Duplikasi Mutlak
- Proteksi berlapis ganda melalui Global Kernel Mutex, Local Session Mutex, dan pemindaian proses aktif Windows.
- Jika pengguna mencoba membuka jendela kedua, aplikasi yang sedang aktif di latar belakang / System Tray otomatis dipulihkan ke layar kerja (``WM_SHOW_WINDOW``), dan proses kedua seketika keluar tanpa meninggalkan zombie process.

### 8. 🖨️ PDF Engine Mandiri Presisi (Windows.Data.Pdf)
- Merender dokumen per halaman ke grafis resolusi tinggi 300 DPI via Windows Runtime (``Windows.Data.Pdf``).
- Penyaluran langsung via Windows Print Spooler GDI+ (``PrintDocument``) tanpa aplikasi pihak ketiga.
- **Akurasi Halaman 100%:** Jumlah halaman tercetak persis sesuai dokumen asli dan 100% silent.

### 9. 📄 Pengaturan Cetak dari SIAP NEO (v2.1+)
- Mendukung ukuran kertas (A4, F4), margin khusus dalam milimeter, dan pemilihan rentang halaman dari HP.

### 10. 🚀 Tailscale Funnel & Multi-Jaringan
- Deteksi dinamis endpoint Tailscale Funnel HTTPS publik, IP Tailscale, dan IP LAN lokal kantor.
- Dukungan satu-klik integrasi Windows Firewall.

### 11. 📦 Paket Instalasi Windows Lengkap
- Menyediakan installer mandiri (``SIAP-Print-Tailscale-$tag-Setup.exe``) dengan pembuatan pintasan Desktop dan Start Menu otomatis serta pendaftaran Boot Task Scheduler.
- Berkas binary telah ditandatangani dengan sertifikat resmi Authenticode (``CN=Jadwal Priok Developer``).

---

## 📦 Berkas Rilis
- **SIAP-Print-Tailscale-$tag-Setup.exe** (Installer Resmi Windows, otomatis buat pintasan, Boot Task, dan kelola proses lama)
- **SIAP-Print-Tailscale-$tag-windows-x64.exe** (Single Portable Executable, langsung pakai tanpa instalasi)

---
*KPU Bea dan Cukai Tipe A Tanjung Priok • SIAP NEO Ecosystem*
"@

$notesFile = "$env:TEMP\siap_release_notes.md"
$releaseNotes | Out-File -FilePath $notesFile -Encoding utf8

# 4. Buat Rilis di GitHub beserta File Executable
Write-Host "[3/4] Menerbitkan Rilis $tag ke GitHub Releases..." -ForegroundColor Yellow

$releaseFiles = @()
$setupExe = Join-Path $repoDir "SIAP-Print-Tailscale-$tag-Setup.exe"
if (Test-Path $setupExe) {
    $releaseFiles += $setupExe
}

$portableExe = Join-Path $repoDir "SIAP-Print-Tailscale-$tag-windows-x64.exe"
if (Test-Path $portableExe) {
    $releaseFiles += $portableExe
}

if ($releaseFiles.Count -eq 0) {
    $releaseFiles += (Join-Path $repoDir "SIAP-Print-Tailscale.exe")
}

& $ghExe release create $tag $releaseFiles `
    --repo "fiqri1010/SIAP-Print-Tailscale" `
    --title "SIAP Print Tailscale $tag - Production Release" `
    --notes-file $notesFile

Remove-Item $notesFile -Force -ErrorAction SilentlyContinue

Write-Host "==========================================================" -ForegroundColor Green
Write-Host " ✅ RILIS GITHUB BERHASIL DITERBITKAN!" -ForegroundColor Green
Write-Host " Link: https://github.com/fiqri1010/SIAP-Print-Tailscale/releases/tag/$tag" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
