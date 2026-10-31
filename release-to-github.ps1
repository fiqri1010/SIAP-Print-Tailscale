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
$tag = "v1.1.0"
$dateStr = Get-Date -Format "dd MMMM yyyy, HH:mm:ss"
$gitHash = (git rev-parse --short HEAD)

$releaseNotes = @"
## SIAP Print Tailscale $tag (Official Release)

Dipublikasikan secara resmi pada: **$dateStr**
Commit Ref: \`$gitHash\`

### 🌟 Fitur Baru & Pembaruan
1. **Deteksi Otomatis Alamat Tailscale Funnel (HTTPS)**
   - Mendeteksi secara langsung domain publik \`https://fqrbc.taile81f15.ts.net\`
   - Petugas dengan smartphone Android (SIAP NEO) **TIDAK PERLU** mengunduh atau mengaktifkan aplikasi Tailscale di HP.
2. **System Tray & Berjalan di Background (Auto-Hide Startup)**
   - Aplikasi dapat diminimalkan ke System Tray pojok kanan bawah.
   - Menutup jendela (klik tombol X) tidak mematikan server, melainkan bersembunyi di System Tray.
   - Mendukung opsi auto-start saat Windows menyala dengan argumen \`--hidden\`.
3. **Penyaringan Printer Fisik Kantor**
   - Printer virtual PDF (seperti Microsoft Print to PDF, OneNote, Fax, XPS) otomatis disaring.
   - Hanya menampilkan printer fisik resmi (seperti **Kyocera TASKalfa MZ3200i KX**).
4. **Penyimpanan Permanen Printer Default**
   - Dilengkapi tombol \`Simpan Printer Default\` yang disimpan ke konfigurasi permanen (\`config.json\`).
5. **Arsitektur In-Memory Cache & Silent Execution (Bebas Kedipan Window)**
   - Menghilangkan bug CMD/PowerShell berulang-ulang yang mengganggu interaksi aplikasi.
   - Status sistem, IP Tailscale, dan printer di-cache langsung di memori Rust backend (0ms latency response).
   - Semua proses background menggunakan flag \`CREATE_NO_WINDOW (0x08000000)\` agar berjalan 100% senyap.
6. **Ikon Vektor Resmi Printer**
   - Menggunakan ikon vektor printer modern dengan indikator status jaringan.
7. **Tanda Tangan Digital Authenticode**
   - File eksekusi ditandatangani secara digital dengan stempel waktu resmi (DigiCert Timestamping).

---
*KPU Bea Cukai Tanjung Priok • SIAP NEO Ecosystem*
"@

$notesFile = "$env:TEMP\siap_release_notes.md"
$releaseNotes | Out-File -FilePath $notesFile -Encoding utf8

# 4. Buat Rilis di GitHub beserta File Executable
Write-Host "[3/4] Menerbitkan Rilis $tag ke GitHub Releases..." -ForegroundColor Yellow
$releaseExe = Join-Path $repoDir "SIAP-Print-Tailscale-v1.1.0-windows-x64.exe"

& $ghExe release create $tag $releaseExe `
    --title "SIAP Print Tailscale $tag - Production Release" `
    --notes-file $notesFile

Remove-Item $notesFile -Force -ErrorAction SilentlyContinue

Write-Host "==========================================================" -ForegroundColor Green
Write-Host " ✅ RILIS GITHUB BERHASIL DITERBITKAN!" -ForegroundColor Green
Write-Host " Link: https://github.com/fiqri1010/SIAP-Print-Tailscale/releases/tag/$tag" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
