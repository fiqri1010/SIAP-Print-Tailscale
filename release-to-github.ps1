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
$tag = "v1.4.1"
$dateStr = Get-Date -Format "dd MMMM yyyy, HH:mm:ss"
$gitHash = (git rev-parse --short HEAD)

$releaseNotes = @"
## SIAP Print Tailscale $tag (Official Release)

Dipublikasikan secara resmi pada: **$dateStr**
Commit Ref: \`$gitHash\`

### 🌟 Fitur Baru & Pembaruan (v1.4.0)
1. **Opsi Pengaturan 'Open when Startup' (Buka saat Startup Windows)**
   - Tersedia opsi untuk mengaktifkan peluncuran otomatis saat Windows pertama kali menyala.
2. **Mulai Sebelum Login Layar Kunci Windows (System Boot Service)**
   - **Fitur Khusus Kantor:** Aplikasi dapat didaftarkan sebagai Windows Scheduled Task (`ONSTART` level SYSTEM).
   - Server HTTP dan Tailscale Funnel langsung aktif saat PC dihidupkan, sehingga petugas Android (SIAP NEO) di lapangan dapat langsung mencetak dokumen kapan saja **tanpa perlu menunggu ada petugas yang membuka kunci/login ke Windows**.
3. **Aturan 'Minimize to Tray when Startup'**
   - Pengguna dapat memilih apakah aplikasi saat startup langsung bersembunyi di System Tray pojok kanan bawah (`--hidden`), atau langsung membuka jendela utama di layar desktop.
4. **Shared Configuration & Persistent Logs di ProgramData**
   - Konfigurasi printer default dan riwayat cetak disimpan terpusat di `%ProgramData%\SIAP-Print-Tailscale\`, sehingga dapat diakses bersama secara konsisten oleh layanan sistem boot maupun pengguna interaktif.
5. **Multi-Instance & Boot Server Auto-Detection**
   - Aplikasi desktop mendeteksi otomatis jika server cetak sudah berjalan di latar belakang sebagai layanan boot (`127.0.0.1:port/ping`), menampilkan status aktif tanpa konflik port.
6. **Deteksi Otomatis Alamat Tailscale Funnel (HTTPS) & VPN Mesh**
   - Mendeteksi domain publik `https://fqrbc.taile81f15.ts.net`, IP Tailscale, dan IP LAN lokal.
7. **Penyaringan Printer Fisik Kantor**
   - Printer virtual PDF otomatis disaring; mendeteksi dan siap mencetak ke **Kyocera TASKalfa MZ3200i KX**.
8. **Tanda Tangan Digital Authenticode**
   - Binary eksekusi ditandatangani secara digital dengan sertifikat resmi dan stempel waktu DigiCert.

---
*KPU Bea Cukai Tanjung Priok • SIAP NEO Ecosystem*
"@

$notesFile = "$env:TEMP\siap_release_notes.md"
$releaseNotes | Out-File -FilePath $notesFile -Encoding utf8

# 4. Buat Rilis di GitHub beserta File Executable
Write-Host "[3/4] Menerbitkan Rilis $tag ke GitHub Releases..." -ForegroundColor Yellow
$releaseExe = Join-Path $repoDir "SIAP-Print-Tailscale-v1.4.1-windows-x64.exe"

& $ghExe release create $tag $releaseExe `
    --repo "fiqri1010/SIAP-Print-Tailscale" `
    --title "SIAP Print Tailscale $tag - Production Release" `
    --notes-file $notesFile

Remove-Item $notesFile -Force -ErrorAction SilentlyContinue

Write-Host "==========================================================" -ForegroundColor Green
Write-Host " ✅ RILIS GITHUB BERHASIL DITERBITKAN!" -ForegroundColor Green
Write-Host " Link: https://github.com/fiqri1010/SIAP-Print-Tailscale/releases/tag/$tag" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
