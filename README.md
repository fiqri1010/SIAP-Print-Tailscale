# SIAP Print Tailscale 🖨️🔒

**SIAP Print Tailscale** adalah aplikasi desktop Windows ringan berbasis **Tauri v2 (Rust + Vanilla JS)**. Aplikasi ini menjadi jembatan cetak dokumen resmi (PIB, Dokap, dan Berita Acara Pemeriksaan / BAP) dari aplikasi **SIAP NEO** (KPU Bea Cukai Tanjung Priok) ke printer kantor (mis. Kyocera TASKalfa). Jalur yang didukung: **Tailscale Funnel**, **IP Tailscale**, atau **Wi-Fi kantor**.

Versi saat ini: **1.3.0**

---

## Fitur Utama

- 🚀 **Tailscale Funnel (HTTPS publik)**: hanya ditampilkan bila Funnel **benar-benar aktif** (termasuk mode `--bg`). Tombol **Aktifkan Funnel** menjalankan `tailscale funnel --bg <port>`.
- 🔒 **IP Tailscale** dan 🌐 **IP LAN**: ditampilkan **hanya IP-nya** (tanpa `http://` dan `:port`), sama dengan kolom isian di SIAP NEO. Port ditampilkan terpisah.
- 🛡️ **Windows Firewall**: peringatan bila port belum diizinkan, beserta tombol **Izinkan di Firewall** (meminta izin Administrator). Tanpa aturan ini HP tidak bisa masuk lewat IP Tailscale / IP LAN. Jalur Funnel tidak membutuhkannya.
- ⚡ **Port dinamis**: default `1234`. Server lama dihentikan sepenuhnya sebelum dinyalakan ulang sehingga tidak bentrok port.
- 🖨️ **Printer fisik saja**: printer virtual PDF/XPS/OneNote/Fax disaring. Printer default disimpan permanen.
- 📄 **Cetak senyap via SumatraPDF**, dengan fallback Windows Shell. Muncul peringatan bila SumatraPDF tidak ditemukan.
- 📋 **Riwayat cetak real-time** untuk setiap dokumen dari SIAP NEO.
- 🔁 **Pulihkan Funnel otomatis** (opsional): bila Funnel hilang (mis. setelah `tailscale reset`), diaktifkan ulang tiap 90 detik.
- 🟢 **Perangkat tailnet & permintaan terakhir**: daftar HP online/offline di Tailscale dan permintaan terakhir yang diterima server, untuk diagnosis koneksi dari HP.
- 🧩 **System tray** dan mulai otomatis saat Windows menyala (`--hidden`).

---

## Menghubungkan SIAP NEO

Di HP buka **Akun › Printer kantor**, lalu isi:

| Kolom di SIAP NEO | Isi dengan | Syarat |
|---|---|---|
| URL Funnel | `https://<nama-pc>.<tailnet>.ts.net` | Funnel aktif (kartu Funnel tidak menampilkan peringatan) |
| IP Tailscale | contoh `100.113.7.91` | Aplikasi Tailscale aktif di HP + firewall diizinkan |
| IP LAN | contoh `192.168.38.145` | HP satu Wi-Fi dengan PC + firewall diizinkan |
| Port | `1234` | Sama dengan port di aplikasi ini |

Pilih mode **Otomatis** agar SIAP NEO mencoba Funnel → IP Tailscale → IP LAN, lalu tekan **Tes Koneksi**.

---

## File Eksekusi

- `SIAP-Print-Tailscale.exe` (root repositori): versi terbaru, ditandatangani.
- Berkas berversi (`SIAP-Print-Tailscale-v1.3.0-windows-x64.exe`) tersedia di halaman GitHub Releases.

---

## API Endpoints untuk SIAP NEO

Server mendengarkan di `0.0.0.0:<port>` (default `1234`). Query string dan garis miring akhir diabaikan (`/ping/` = `/ping`).

1. **`GET /ping`**: health check. Respon: `🖨️ SIAP Print Server is running`.
2. **`GET /info`**:
   ```json
   {
     "status": "online",
     "version": "1.3.0",
     "port": 1234,
     "tailscaleIp": "100.113.7.91",
     "tailscaleFunnelDomain": "pc.tailnet.ts.net",
     "funnelActive": true,
     "localIp": "192.168.38.145",
     "selectedPrinter": "Kyocera TASKalfa MZ3200i KX"
   }
   ```
   `tailscaleFunnelDomain` bernilai `null` bila Funnel tidak aktif.
3. **`GET /printers`**: array `{ "name", "driver", "port", "is_default" }`.
4. **`POST /print-pdf`**:
   ```json
   { "printerName": "Kyocera TASKalfa MZ3200i KX", "filename": "BAP_123456.pdf", "pdfBase64": "JVBERi0xLjQK..." }
   ```
   `printerName` kosong berarti memakai printer default yang dipilih di aplikasi ini.

   | Kondisi | HTTP | Body |
   |---|---|---|
   | Berhasil | 200 | `{"success":true,"message":"Dicetak via SumatraPDF",...}` |
   | Body/Base64 salah | 400 | `{"success":false,"error":"..."}` |
   | Gagal mencetak | 500 | `{"success":false,"error":"...",...}` |
   | Path tidak dikenal | 404 | `Not Found` |

---

## Kompilasi Ulang (Developer)

Prasyarat: Rust dan Node.js.

```powershell
cd C:\Repo\SIAP-Print-Tailscale
npm install
npx tauri dev                 # mode pengembangan
npx tauri build --no-bundle   # src-tauri\target\release\siap-print-tailscale.exe
```

Penandatanganan (sertifikat Code Signing "Jadwal Priok Developer"):

```powershell
signtool sign /sha1 E8692153EDD5AED0C4CE9FE300D0991757F0467B /fd sha256 /tr http://timestamp.digicert.com /td sha256 SIAP-Print-Tailscale.exe
```
