# SIAP Print Tailscale 🖨️🔒

**SIAP Print Tailscale** adalah aplikasi desktop Windows modern dan ringan berbasis **Tauri v2 (Rust + Vanilla JS)** yang dirancang khusus untuk menjadi jembatan cetak dokumen resmi (PIB, Dokap, dan Berita Acara Pemeriksaan / BAP) dari aplikasi **SIAP NEO Mobile** (KPU Bea Cukai Tanjung Priok) ke printer kantor (seperti Kyocera TASKalfa, HP, Canon, Epson) melalui jaringan aman **Tailscale** atau WiFi lokal.

---

## Fitur Utama

- 🔒 **Deteksi Otomatis Tailscale IP**: Otomatis mendeteksi IP `100.x.y.z` jika Tailscale aktif di PC.
- 🌐 **Deteksi Otomatis Local LAN IP**: Otomatis mendeteksi IP jaringan kantor (`192.168.x.x`).
- ⚡ **Pengaturan Port Dinamis**: Konfigurasi port listening HTTP (default `1234`) langsung dari UI dengan restart server otomatis.
- 📄 **Deteksi Seluruh Printer Windows**: Menampilkan semua printer fisik dan virtual yang terpasang di Windows (Kyocera TASKalfa, Print to PDF, dll).
- 🖨️ **Uji Coba Cetak (Test Print)**: Menguji koneksi printer secara langsung dari aplikasi sebelum dipakai oleh petugas di lapangan.
- 📋 **Riwayat Cetak Real-Time**: Log visual yang mencatat setiap dokumen PIB atau BAP yang diterima dari HP petugas.
- 🚀 **Super Ringan & Mandiri**: Dibangun dengan Rust + Tauri, hanya berukuran belasan MB, hemat RAM (<30MB), dan dapat dijalankan langsung (`.exe`).

---

## File Eksekusi Siap Pakai

File eksekusi mandiri (*standalone executable*) sudah tersedia di root repositori:
```text
C:\Repo\SIAP-Print-Tailscale\SIAP-Print-Tailscale.exe
```
Tinggal *double-click* file tersebut untuk menjalankan server.

---

## API Endpoints untuk SIAP NEO

Aplikasi ini mendengarkan request HTTP di port yang ditentukan (default `1234`):

1. **`GET /ping`**
   - Health check sederhana.
   - Respon: `"🖨️ SIAP Print Server is running"`

2. **`GET /info`**
   - Informasi status server, IP Tailscale, IP Local, dan printer aktif.
   - Respon JSON:
     ```json
     {
       "status": "online",
       "port": 1234,
       "tailscaleIp": "100.85.20.10",
       "localIp": "192.168.38.15",
       "selectedPrinter": "Kyocera TASKalfa MZ3200i KX"
     }
     ```

3. **`GET /printers`**
   - Mengambil daftar printer terpasang di sistem operasi Windows.

4. **`POST /print-pdf`** (Endpoint Utama Cetak Dokumen)
   - Menerima file PDF dokumen resmi dalam format Base64 dan langsung mencetaknya secara senyap (*silent*) ke printer Windows.
   - Body Request JSON:
     ```json
     {
       "printerName": "Kyocera TASKalfa MZ3200i KX",
       "filename": "PIB_123456.pdf",
       "pdfBase64": "JVBERi0xLjQK..."
     }
     ```
   - Respon JSON:
     ```json
     {
       "success": true,
       "message": "Dicetak via SumatraPDF",
       "printer": "Kyocera TASKalfa MZ3200i KX",
       "filename": "PIB_123456.pdf"
     }
     ```

---

## Cara Kompilasi Ulang (Developer)

Jika ingin memodifikasi tampilan atau fungsi di PC lain:
1. Pastikan **Rust** dan **Node.js** terpasang.
2. Jalankan:
   ```powershell
   cd C:\Repo\SIAP-Print-Tailscale
   npm install
   npm run tauri dev      # Untuk mode pengembangan langsung
   npm run tauri build    # Untuk membuat installer / release .exe
   ```
