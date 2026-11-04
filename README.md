# SIAP Print Tailscale 🖨️🔒

**SIAP Print Tailscale** adalah aplikasi desktop Windows mandiri berbasis **C# dan .NET 8 Native** dengan antarmuka ringkas. Aplikasi ini menjadi jembatan cetak dokumen resmi (PIB, Dokap, dan Berita Acara Pemeriksaan / BAP) dari aplikasi **SIAP NEO** (KPU Bea Cukai Tanjung Priok) ke printer kantor (mis. Kyocera TASKalfa MZ3200i). Jalur koneksi yang didukung: **Tailscale Funnel**, **IP Tailscale**, atau **Wi-Fi kantor (LAN)**.

Versi saat ini: **v2.2.0 (Native C# / .NET 8 Ultra-Low Memory Edition)**

---

## Fitur Utama

- ⚡ **Native C# / .NET 8 Standalone (Ultra-Low Memory)**: Beroperasi tanpa dependensi Node.js, V8 engine, maupun runtime eksternal. Konsumsi memori sangat minim (hanya **~8–14 MB RAM**, dan **~3–6 MB saat berada di System Tray**) berkat engine HTTP asynchronous berbasis `TcpListener` dan optimasi Windows Working Set.
- 🛡️ **Proteksi Single-Instance Anti-Duplikasi Mutlak**: Terintegrasi langsung pada kode kernel level. Mencegah duplikasi 2 aplikasi berjalan bersamaan di sistem. Jika instance kedua dijalankan, instance aktif di System Tray otomatis dipulihkan ke depan layar (`WM_SHOW_WINDOW`) dan dialog native Windows ditampilkan:
  > *"Aplikasi telah terbuka, tidak bisa buka 2 jendela sekaligus"*
- 🖨️ **PDF Engine Mandiri Presisi**: Menggunakan mesin render internal Windows 10/11 (`Windows.Data.Pdf`) yang merender dokumen per halaman menjadi grafis resolusi tinggi 300 DPI dan menyalurkannya via Windows Print Spooler GDI+ (`PrintDocument`).
  - **Akurasi Halaman 100%**: Dokumen 1 lembar dicetak tepat 1 lembar (mencegah pencetakan kertas berlebih).
  - **Bebas Kerusakan PDF**: Format vektor, barcode, dan font formulir kepabeanan tetap tajam tanpa distorsi.
  - **100% Silent**: Tidak membuka jendela PDF viewer atau pop-up eksternal apa pun saat mencetak.
- 📄 **Pengaturan cetak dari SIAP NEO** (v2.1+): ukuran kertas A4/F4, margin (mm), dan rentang halaman dipilih di HP lalu diterapkan ke printer.
- 🚀 **Tailscale Funnel (HTTPS publik)**: Mendeteksi domain Funnel aktif secara dinamis (`https://<nama-pc>.<tailnet>.ts.net`).
- 🔒 **IP Tailscale** dan 🌐 **IP LAN**: Dilengkapi tombol salin cepat untuk mempermudah konfigurasi di SIAP NEO.
- 🛡️ **Windows Firewall Integration**: Pemeriksaan port aktif dan pembuatan aturan firewall otomatis dengan satu klik.
- 🗕 **Manajemen System Tray & Startup**:
  - Pilihan peluncuran otomatis saat Windows menyala.
  - *Minimize to Tray saat Startup*.
  - *Minimize to Tray on Exit* (menekan tombol silang X tidak mematikan server cetak).

---

## Menghubungkan SIAP NEO

Di HP buka **Akun › Printer kantor**, lalu isi:

| Kolom di SIAP NEO | Isi dengan | Syarat |
|---|---|---|
| URL Funnel | `https://<nama-pc>.<tailnet>.ts.net` | Funnel aktif |
| IP Tailscale | contoh `100.113.7.91` | Aplikasi Tailscale aktif di HP + firewall diizinkan |
| IP LAN | contoh `192.168.38.145` | HP satu Wi-Fi dengan PC + firewall diizinkan |
| Port | `1234` | Sama dengan port di aplikasi ini |

Pilih mode **Otomatis** agar SIAP NEO mencoba Funnel → IP Tailscale → IP LAN, lalu tekan **Tes Koneksi**.

---

## Berkas Rilis & Distribusi

Seluruh berkas binary telah ditandatangani secara digital dengan sertifikat resmi Authenticode (`CN=Jadwal Priok Developer`) dan stempel waktu DigiCert:

1. **`SIAP-Print-Tailscale-v2.2.0-Setup.exe`**: File installer resmi Windows, otomatis menutup proses lama, memasang ke `%LocalAppData%\Programs\`, dan membuat pintasan Desktop serta Start Menu.
2. **`SIAP-Print-Tailscale-v2.2.0-windows-x64.exe`**: Executable portabel mandiri tanpa instalasi (Single Portable Executable).
3. **`SIAP-Print-Tailscale.exe`**: Binary mandiri utama di root repositori.

---

## API Endpoints untuk SIAP NEO

Server HTTP ringan asynchronous mendengarkan di `0.0.0.0:<port>` (default `1234`):

1. **`GET /ping`**: Health check. Respon: `🖨️ SIAP Print Server is running`.
2. **`GET /info`**: Status JSON server, IP Tailscale, Funnel Domain, LAN IP, printer aktif, dan waktu aktif.
3. **`GET /printers`**: Mengembalikan daftar printer fisik yang terpasang di sistem Windows.
4. **`POST /print-pdf`**: Menerima JSON payload `{ printerName, filename, pdfBase64, paperSize, marginsMm, pages }` dan mencetaknya secara senyap melalui Native PDF Engine.
