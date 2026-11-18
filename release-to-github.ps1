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
$version = "2.3.1"
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

### 1. 🔄 Sinkronisasi Riwayat Cetakan Real-Time (Live GUI Sync)
- Menambahkan **FileSystemWatcher** dan validasi timestamp berkala pada tabel riwayat cetak di aplikasi GUI.
- Setiap kali ada dokumen baru dicetak dari SIAP NEO, baris riwayat langsung muncul seketika di posisi paling atas tabel, baik saat server berjalan di jendela GUI maupun di latar belakang (Boot Service).
- Batas maksimal 100 entri riwayat (*sliding window*) diterapkan secara konsisten tanpa kebocoran memori.

### 2. 🛡️ Perlindungan Riwayat & Named Mutex Lintas-Proses
- Menggantikan lock in-process dengan **Windows Named Mutex** (``Global\SIAP_Print_Tailscale_Logs_Mutex``) dengan penanganan aman terhadap *abandoned mutex*.
- Mencegah *sharing violation IOException* saat proses background dan proses GUI mengakses file log secara bersamaan.
- Mekanisme **Atomic Write** menggunakan berkas sementara (``.tmp``) dan ``File.Move`` menjamin data log tidak akan pernah terhapus atau kosong saat tabel penuh.

### 3. 🔒 Isolasi Port Eksklusif (Exclusive Address Use)
- Menghapus opsi ``SO_REUSEADDR`` yang sebelumnya memicu pembajakan port 1234 di Windows.
- Jika server Boot Service telah berjalan di latar belakang, aplikasi GUI otomatis mendeteksinya sebagai **Boot Service Viewer** (status: ``● BOOT SERVICE``) tanpa bentrok socket.

### 4. ⚡ Installer Administrator & Graceful Shutdown
- Installer resmi Windows kini beroperasi dengan hak administrator penuh (``requireAdministrator``).
- Mampu mematikan service dan task lama di seluruh sesi Windows (termasuk Session 0) secara bersih sebelum mengekstrak berkas baru.
- Penutupan anggun (*graceful shutdown*) didukung via HTTP ``POST /shutdown``.

### 5. 🔍 Deteksi Status Printer Real-Time & Job Tracking
- Tetap menyertakan fitur v2.3.0: status printer fisik real-time (idle, aktif, offline, error) via Win32 Spooler API dan endpoint ``GET /printer-status`` serta ``GET /job-status``.

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
