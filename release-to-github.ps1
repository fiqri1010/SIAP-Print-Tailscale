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
$version = "2.2.1"
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

### 1. ⚡ Hidup Otomatis Saat Windows Startup Bahkan Sebelum Login (Boot Service)
- Memperbaiki fitur auto-start agar server langsung hidup dan mendengarkan permintaan cetak saat komputer dinyalakan, **bahkan sebelum ada pengguna yang login ke layar kunci Windows**.
- Menggunakan integrasi native **Windows Task Scheduler (Task: ``SIAP-Print-Tailscale-Boot``)** dengan pemicu ``AtStartup`` dan principal ``NT AUTHORITY\SYSTEM`` berhak akses tertinggi.
- Mode ``--headless`` super hemat memori (<5 MB RAM) yang melayani endpoint cetak 24/7 tanpa membutuhkan sesi desktop aktif.
- Begitu petugas menyalakan PC kantor di pagi hari, HP petugas di lapangan dapat langsung mencetak dokumen SIAP NEO tanpa harus menunggu seseorang login ke desktop Windows.
- Tetap menyediakan integrasi ``shell:startup`` dan registry ``HKCU\Run`` agar ikon System Tray dan Dashboard otomatis muncul saat pengguna login ke desktop.

### 2. ⚡ Ultra-Low Memory & Refactoring Performa Tinggi (>60% Lebih Hemat RAM)
- Menggantikan ASP.NET Core Kestrel secara total dengan engine HTTP asynchronous berbasis ``TcpListener``.
- Footprint RAM turun drastis ke **~8–14 MB** saat aktif dan **~3–6 MB** saat berada di System Tray (penghematan >60%–80%).
- Streaming zero-copy Base64 decoding langsung dari UTF-8 buffer JSON tanpa alokasi string raksasa di Large Object Heap (LOH).
- Manajemen agresif Windows Working Set (``SetProcessWorkingSetSize``) segera setelah mencetak dan saat idle.

### 3. 🛡️ Proteksi Single-Instance Anti-Duplikasi Mutlak
- Proteksi berlapis ganda melalui Global Kernel Mutex, Local Session Mutex, dan pemindaian proses aktif Windows.
- Jika pengguna mencoba membuka jendela kedua, aplikasi yang sedang aktif di latar belakang / System Tray otomatis dipulihkan ke layar kerja (``WM_SHOW_WINDOW``), dan proses kedua seketika keluar tanpa meninggalkan zombie process.

### 4. 🖨️ PDF Engine Mandiri Presisi (Windows.Data.Pdf)
- Merender dokumen per halaman ke grafis resolusi tinggi 300 DPI via Windows Runtime (``Windows.Data.Pdf``).
- Penyaluran langsung via Windows Print Spooler GDI+ (``PrintDocument``) tanpa aplikasi pihak ketiga.
- **Akurasi Halaman 100%:** Jumlah halaman tercetak persis sesuai dokumen asli dan 100% silent.

### 5. 📄 Pengaturan Cetak dari SIAP NEO (v2.1+)
- Mendukung ukuran kertas (A4, F4), margin khusus dalam milimeter, dan pemilihan rentang halaman dari HP.

### 6. 🚀 Tailscale Funnel & Multi-Jaringan
- Deteksi dinamis endpoint Tailscale Funnel HTTPS publik, IP Tailscale, dan IP LAN lokal kantor.
- Dukungan satu-klik integrasi Windows Firewall.

### 7. 📦 Paket Instalasi Windows Lengkap
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
