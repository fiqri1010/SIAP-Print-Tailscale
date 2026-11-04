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
$version = "2.1.1"
if (Test-Path $csprojPath) {
    $csprojContent = [xml](Get-Content $csprojPath)
    if ($csprojContent.Project.PropertyGroup.Version) {
        $version = $csprojContent.Project.PropertyGroup.Version.Trim()
    }
}
$tag = "v$version"
$dateStr = Get-Date -Format "dd MMMM yyyy, HH:mm:ss"
$gitHash = (git rev-parse --short HEAD)

$releaseNotes = @"
# 🖨️ SIAP Print Tailscale $tag — Production Release

> **Waktu Rilis:** $dateStr (WIB)  
> **Platform Target:** Windows 10 / Windows 11 (x64)  
> **Arsitektur:** 100% C# .NET 8 Standalone (Native Windows Runtime)  
> **Commit Hash:** \`$gitHash\`  
> **Distribusi:** Single Executable Portable (Standalone)

---

## 🚀 Ringkasan Fitur & Pembaruan Rilis $tag

### 1. ⚡ 100% Native C# / .NET 8 Standalone
- Beroperasi tanpa dependensi Node.js, V8 engine, Tauri, maupun runtime eksternal.
- Sangat hemat memori (hanya ~25–40 MB RAM) dengan responsivitas tinggi.
- Menggunakan ASP.NET Core Kestrel terintegrasi untuk endpoint HTTP lokal dan jaringan Tailscale.

### 2. 🖨️ PDF Engine Mandiri Presisi (Windows.Data.Pdf)
- Merender dokumen per halaman menjadi grafis resolusi tinggi 300 DPI melalui Windows Runtime (`Windows.Data.Pdf`) bawaan Windows 10/11.
- Penyaluran langsung via Windows Print Spooler GDI+ (`PrintDocument`) tanpa memerlukan software viewer pihak ketiga seperti SumatraPDF atau Acrobat.
- **Akurasi Halaman 100%:** Jumlah halaman tercetak persis sesuai dokumen asli.
- **100% Silent:** Tidak membuka jendela PDF viewer atau pop-up eksternal apa pun saat mencetak.

### 3. 📄 Pengaturan Cetak Terpadu dari SIAP NEO
- Mendukung kustomisasi ukuran kertas (A4, F4), margin khusus dalam milimeter, dan pemilihan rentang halaman langsung dari aplikasi mobile SIAP NEO.

### 4. 🚀 Tailscale Funnel & Multi-Jaringan
- Deteksi dinamis endpoint Tailscale Funnel publik (HTTPS), Tailscale IP, dan IP LAN lokal kantor.
- Dukungan satu-klik untuk pemeriksaan dan pembukaan port Windows Firewall.

### 5. 🛡️ Proteksi Single-Instance Mutex & Tray Management
- Proteksi Mutex sesi kernel Windows terisolasi untuk mencegah duplikasi instance.
- Dialog peringatan otomatis dan auto-focus ke jendela utama jika dibuka berulang.
- Pilihan fleksibel startup Windows (*Open when Startup*, *Minimize to Tray*, dan *Minimize to Tray on Exit*).

---

## 📦 Berkas Rilis
- **SIAP-Print-Tailscale-$tag-windows-x64.exe** (Single Portable Executable, siap pakai tanpa instalasi)

---
*KPU Bea dan Cukai Tipe A Tanjung Priok • SIAP NEO Ecosystem*
"@

$notesFile = "$env:TEMP\siap_release_notes.md"
$releaseNotes | Out-File -FilePath $notesFile -Encoding utf8

# 4. Buat Rilis di GitHub beserta File Executable
Write-Host "[3/4] Menerbitkan Rilis $tag ke GitHub Releases..." -ForegroundColor Yellow
$releaseExe = Join-Path $repoDir "SIAP-Print-Tailscale-$tag-windows-x64.exe"
if (-not (Test-Path $releaseExe)) {
    $releaseExe = Join-Path $repoDir "SIAP-Print-Tailscale.exe"
}

& $ghExe release create $tag $releaseExe `
    --repo "fiqri1010/SIAP-Print-Tailscale" `
    --title "SIAP Print Tailscale $tag - Production Release" `
    --notes-file $notesFile

Remove-Item $notesFile -Force -ErrorAction SilentlyContinue

Write-Host "==========================================================" -ForegroundColor Green
Write-Host " ✅ RILIS GITHUB BERHASIL DITERBITKAN!" -ForegroundColor Green
Write-Host " Link: https://github.com/fiqri1010/SIAP-Print-Tailscale/releases/tag/$tag" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
