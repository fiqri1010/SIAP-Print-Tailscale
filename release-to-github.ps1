# ==============================================================================
# Script Otomatis Rilis GitHub untuk SIAP-Print-Tailscale
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

# 3. Buat Catatan Rilis Dinamis & Unik
$tag = "v1.4.3"
$dateStr = Get-Date -Format "dd MMMM yyyy, HH:mm:ss"
$gitHash = (git rev-parse --short HEAD)

$releaseNotes = @"
# 🖨️ SIAP Print Tailscale $tag — Production Release

> **Waktu Rilis:** $dateStr (WIB)  
> **Platform Target:** Windows 10 / Windows 11 (x64)  
> **Commit Hash:** \`$gitHash\`  
> **Distribusi:** Single Executable Portable (Standalone)

---

## 🚀 Ringkasan Pembaruan & Fitur Rilis v1.4.3

### 1. 🛡️ Proteksi Single-Instance Mutex & Dialog Peringatan Native Win32
- Menggunakan kernel mutex terisolasi tingkat sesi (\Local\SIAP_Print_Tailscale_SingleInstance_Mutex\).
- Mencegah pengguna atau sistem membuka aplikasi berulang kali secara tidak sengaja.
- Jika instance kedua dijalankan, kotak pesan native Windows langsung ditampilkan:
  > **"Aplikasi telah terbuka, tidak bisa buka 2 jendela sekaligus"**
- Jendela aplikasi utama yang sedang berjalan (meskipun sedang diminimalkan ke System Tray) akan otomatis dipulihkan ke layar kerja dan difokuskan ke depan (\SetForegroundWindow\).

### 2. ⚙️ Manajemen Fleksibel Peluncuran Startup Windows
- **Open when Startup:** Dapat diaktifkan/dinonaktifkan langsung dari dashboard aplikasi.
- **Boot Service Sebelum Login Layar Kunci:** Terintegrasi dengan Windows Task Scheduler (\ONSTART\ mode SYSTEM) sehingga server cetak Tailscale langsung aktif saat PC dinyalakan tanpa perlu login interaktif terlebih dahulu.
- **Minimize to Tray when Startup:** Opsi peluncuran di latar belakang System Tray (\--hidden\) saat komputer menyala agar tidak mengganggu antarmuka desktop kantor.

### 3. 🧹 Manajemen Workspace & Kebersihan Rilis
- Pembersihan berkas biner versi sebelumnya dari direktori distribusi, menyisakan biner rilis terkini (\SIAP-Print-Tailscale-v1.4.3-windows-x64.exe\) dan berkas eksekusi utama portabel (\SIAP-Print-Tailscale.exe\).
- Pengoptimalan ukuran biner dan penghapusan duplikasi berkas build lama.

### 4. 🖨️ Penanganan Pencetakan Dokumen SIAP NEO Terpadu
- Penyaringan printer virtual otomatis (hanya menampilkan printer fisik aktif seperti Kyocera TASKalfa).
- Deteksi dinamis endpoint Tailscale Funnel (HTTPS publik), Tailscale IP, dan IP LAN lokal.
- Penyimpanan konfigurasi bersama dan log riwayat cetak di \%ProgramData%\SIAP-Print-Tailscale\.

### 5. ✍️ Tanda Tangan Digital Authenticode
- File binary eksekusi portabel telah ditandatangani secara digital dengan sertifikat resmi dan stempel waktu DigiCert Timestamping.

---

## 📦 Berkas Rilis
- **SIAP-Print-Tailscale-v1.4.3-windows-x64.exe** (Single Portable Executable, siap pakai tanpa instalasi)

---
*KPU Bea dan Cukai Tipe A Tanjung Priok • SIAP NEO Ecosystem*
"@

$notesFile = "$env:TEMP\siap_release_notes.md"
$releaseNotes | Out-File -FilePath $notesFile -Encoding utf8

# 4. Buat Rilis di GitHub beserta File Executable
Write-Host "[3/4] Menerbitkan Rilis $tag ke GitHub Releases..." -ForegroundColor Yellow
$releaseExe = Join-Path $repoDir "SIAP-Print-Tailscale-v1.4.3-windows-x64.exe"

& $ghExe release create $tag $releaseExe `
    --repo "fiqri1010/SIAP-Print-Tailscale" `
    --title "SIAP Print Tailscale $tag - Production Release" `
    --notes-file $notesFile

Remove-Item $notesFile -Force -ErrorAction SilentlyContinue

Write-Host "==========================================================" -ForegroundColor Green
Write-Host " ✅ RILIS GITHUB BERHASIL DITERBITKAN!" -ForegroundColor Green
Write-Host " Link: https://github.com/fiqri1010/SIAP-Print-Tailscale/releases/tag/$tag" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
