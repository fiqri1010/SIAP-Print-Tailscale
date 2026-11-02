const invoke = window.__TAURI__ ? window.__TAURI__.core.invoke : async (cmd, args) => {
  console.log('Mock invoke:', cmd, args);
  return {};
};

// UI Elements
const serverStatusPill = document.getElementById('server-status-pill');
const serverStatusText = document.getElementById('server-status-text');
const btnToggleServer = document.getElementById('btn-toggle-server');

const funnelDomainDisplay = document.getElementById('funnel-domain-display');
const funnelUrlInput = document.getElementById('funnel-url-input');
const btnCopyFunnel = document.getElementById('btn-copy-funnel');

const tailscaleIpDisplay = document.getElementById('tailscale-ip-display');
const tailscaleUrlInput = document.getElementById('tailscale-url-input');
const btnCopyTs = document.getElementById('btn-copy-ts');
const tailscaleWarning = document.getElementById('tailscale-warning');
const btnOpenTs = document.getElementById('btn-open-ts');

const localIpDisplay = document.getElementById('local-ip-display');
const localUrlInput = document.getElementById('local-url-input');
const btnCopyLan = document.getElementById('btn-copy-lan');

const inputPort = document.getElementById('input-port');
const chkAutoStart = document.getElementById('chk-auto-start');
const chkStartBeforeLogin = document.getElementById('chk-start-before-login');
const chkMinimizeToTray = document.getElementById('chk-minimize-to-tray');
const chkMinimizeOnExit = document.getElementById('chk-minimize-on-exit');
const startupSubOptions = document.getElementById('startup-sub-options');
const btnSaveSettings = document.getElementById('btn-save-settings');

const selectPrinter = document.getElementById('select-printer');
const printerMetaInfo = document.getElementById('printer-meta-info');
const btnRefreshPrinters = document.getElementById('btn-refresh-printers');
const btnSavePrinter = document.getElementById('btn-save-printer');
const btnTestPrint = document.getElementById('btn-test-print');

const funnelWarning = document.getElementById('funnel-warning');
const funnelWarningText = document.getElementById('funnel-warning-text');
const btnEnableFunnel = document.getElementById('btn-enable-funnel');
const portValueEls = document.querySelectorAll('.port-value');
const firewallBanner = document.getElementById('firewall-banner');
const firewallText = document.getElementById('firewall-text');
const btnAllowFirewall = document.getElementById('btn-allow-firewall');
const serverErrorBanner = document.getElementById('server-error-banner');

const chkKeepFunnel = document.getElementById('chk-keep-funnel');
const peerList = document.getElementById('peer-list');
const lastRequestEl = document.getElementById('last-request');

const logsTbody = document.getElementById('logs-tbody');
const btnClearLogs = document.getElementById('btn-clear-logs');
const toastEl = document.getElementById('toast');

let currentStatus = null;
let isUserEditingPort = false;

const esc = (v) => String(v).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

// Toast Notification
function showToast(message, duration = 3000) {
  toastEl.textContent = message;
  toastEl.classList.remove('hidden');
  setTimeout(() => {
    toastEl.classList.add('hidden');
  }, duration);
}

// Render Status to UI (Pure DOM update)
function renderStatus(status) {
  if (!status) return;
  currentStatus = status;

  // Server state
  if (status.is_running) {
    serverStatusPill.className = 'status-pill status-running';
    serverStatusText.textContent = `Server Aktif (Port ${status.port})`;
    btnToggleServer.textContent = 'Matikan Server';
    btnToggleServer.className = 'btn btn-secondary';
  } else {
    serverStatusPill.className = 'status-pill status-stopped';
    serverStatusText.textContent = 'Server Mati';
    btnToggleServer.textContent = 'Nyalakan Server';
    btnToggleServer.className = 'btn btn-primary';
  }

  // Tailscale Funnel (Public HTTPS) — hanya ditampilkan bila BENAR-BENAR aktif
  const funnelPortMismatch = status.tailscale_funnel_domain && status.funnel_target_port && status.funnel_target_port !== status.port;
  if (status.tailscale_funnel_domain && !funnelPortMismatch) {
    funnelDomainDisplay.textContent = status.tailscale_funnel_domain;
    funnelUrlInput.value = `https://${status.tailscale_funnel_domain}`;
    funnelWarning.classList.add('hidden');
  } else {
    funnelDomainDisplay.textContent = funnelPortMismatch ? 'Funnel mengarah ke port lain' : 'Funnel belum aktif';
    funnelUrlInput.value = `tailscale funnel --bg ${status.port}`;
    funnelWarningText.textContent = funnelPortMismatch
      ? `⚠️ Funnel aktif ke port ${status.funnel_target_port}, sedangkan server di port ${status.port}.`
      : (status.tailscale_dns_name
          ? `⚠️ Funnel belum aktif. Setelah aktif, alamatnya https://${status.tailscale_dns_name}`
          : '⚠️ Funnel belum aktif (Tailscale belum login).');
    funnelWarning.classList.remove('hidden');
  }

  // Tailscale Private IP — hanya IP (tanpa http:// dan port)
  if (status.tailscale_ip) {
    tailscaleIpDisplay.textContent = status.tailscale_ip;
    tailscaleUrlInput.value = status.tailscale_ip;
    tailscaleWarning.classList.add('hidden');
  } else {
    tailscaleIpDisplay.textContent = 'Tidak Terdeteksi';
    tailscaleUrlInput.value = '';
    tailscaleUrlInput.placeholder = 'Tailscale belum aktif';
    tailscaleWarning.classList.remove('hidden');
  }

  // Local LAN IP — hanya IP (tanpa http:// dan port)
  localIpDisplay.textContent = status.local_ip || '127.0.0.1';
  localUrlInput.value = status.local_ip || '127.0.0.1';
  portValueEls.forEach((el) => { el.textContent = status.port; });

  // Pulihkan Funnel otomatis, perangkat tailnet, permintaan terakhir
  chkKeepFunnel.checked = !!status.keep_funnel;
  const peers = status.peers || [];
  peerList.innerHTML = peers.length === 0
    ? '<span class="tag">Tidak ada perangkat lain di tailnet</span>'
    : peers.map((p) => `<span class="tag" style="color:${p.online ? '#14532d' : '#7f1d1d'};font-weight:600;">${p.online ? '🟢' : '🔴'} ${esc(p.name)} (${esc(p.os || '-')}) ${esc(p.ip || '')} • ${p.online ? 'online' : 'offline'}</span>`).join(' ');
  lastRequestEl.textContent = status.last_request ? `Permintaan terakhir: ${status.last_request}` : 'Belum ada permintaan dari HP';

  // Firewall, error server, SumatraPDF
  firewallText.textContent = `⚠️ Windows Firewall belum mengizinkan port ${status.port}. HP tidak bisa terhubung lewat IP Tailscale / IP LAN (jalur Funnel tetap bisa).`;
  firewallBanner.classList.toggle('hidden', !!status.firewall_ok);
  if (status.server_error) {
    serverErrorBanner.textContent = `⚠️ ${status.server_error}`;
    serverErrorBanner.classList.remove('hidden');
  } else {
    serverErrorBanner.classList.add('hidden');
  }

  // Port & Startup checkboxes
  if (!isUserEditingPort) {
    inputPort.value = status.port;
  }
  chkAutoStart.checked = !!status.auto_start_startup;
  chkStartBeforeLogin.checked = !!status.start_before_login;
  chkStartBeforeLogin.disabled = !status.auto_start_startup;
  if (startupSubOptions) {
    startupSubOptions.classList.toggle('disabled', !status.auto_start_startup);
  }
  chkMinimizeToTray.checked = status.minimize_to_tray_on_startup !== false;
  if (chkMinimizeOnExit) {
    chkMinimizeOnExit.checked = status.minimize_on_exit !== false;
  }

  // Printers dropdown (Virtual PDF filtered out)
  renderPrinters(status.printers, status.selected_printer);

  // Logs table
  renderLogs(status.logs);
}

function renderPrinters(printers, selectedPrinter) {
  if (!printers || printers.length === 0) {
    selectPrinter.innerHTML = '<option value="">Tidak ada printer fisik terdeteksi</option>';
    printerMetaInfo.innerHTML = '<span class="tag" style="color:#ef4444;">Printer virtual PDF telah disaring. Hubungkan printer fisik (seperti Kyocera TASKalfa).</span>';
    return;
  }

  const currentValue = selectPrinter.value || selectedPrinter;
  let html = '';
  printers.forEach((p) => {
    const isSelected = p.name === currentValue || (!currentValue && p.is_default);
    html += `<option value="${p.name}" ${isSelected ? 'selected' : ''}>${p.name} ${p.is_default ? '(Default)' : ''}</option>`;
  });
  selectPrinter.innerHTML = html;

  const active = printers.find((p) => p.name === selectPrinter.value) || printers[0];
  if (active) {
    printerMetaInfo.innerHTML = `
      <span class="tag">Driver: ${active.driver || '-'}</span>
      <span class="tag">Port: ${active.port || '-'}</span>
      ${active.is_default ? '<span class="tag" style="color:#00a896;font-weight:600;">Default Windows</span>' : ''}
    `;
  }
}

function renderLogs(logs) {
  if (!logs || logs.length === 0) {
    logsTbody.innerHTML = `
      <tr>
        <td colspan="5" class="text-center text-muted empty-state">Belum ada dokumen yang dicetak dari SIAP NEO.</td>
      </tr>
    `;
    return;
  }

  let html = '';
  logs.forEach((item) => {
    const isSuccess = item.status === 'SUKSES';
    const badgeClass = isSuccess ? 'badge-success' : 'badge-error';
    html += `
      <tr>
        <td><strong>${item.time}</strong></td>
        <td>📄 ${item.filename}</td>
        <td>${item.printer}</td>
        <td><span class="badge-status ${badgeClass}">${item.status}</span></td>
        <td class="text-muted">${item.message}</td>
      </tr>
    `;
  });
  logsTbody.innerHTML = html;
}

// Fetch in-memory status instantly (Zero process execution)
async function fetchStatusFast() {
  try {
    const status = await invoke('get_app_status');
    renderStatus(status);
  } catch (err) {
    console.error('Error fetching fast status:', err);
  }
}

// Explicit refresh (Runs silent background scan only on demand)
async function refreshExplicitly() {
  btnRefreshPrinters.disabled = true;
  btnRefreshPrinters.textContent = '🔄 Memindai...';
  try {
    const status = await invoke('refresh_system_info');
    renderStatus(status);
    showToast('🔄 Informasi printer & jaringan diperbarui!');
  } catch (err) {
    showToast('Gagal memindai ulang: ' + err);
  } finally {
    btnRefreshPrinters.disabled = false;
    btnRefreshPrinters.textContent = '🔄 Refresh';
  }
}

// Event Listeners
btnCopyFunnel.addEventListener('click', () => {
  const v = funnelUrlInput.value;
  if (v.startsWith('https://')) {
    navigator.clipboard.writeText(v);
    showToast('🚀 URL Funnel HTTPS disalin! Bisa dipakai langsung di HP tanpa install Tailscale.');
  } else if (v) {
    navigator.clipboard.writeText(v);
    showToast('Perintah Funnel disalin. Jalankan di Command Prompt, atau klik "Aktifkan Funnel".');
  }
});

btnCopyTs.addEventListener('click', () => {
  if (tailscaleUrlInput.value) {
    navigator.clipboard.writeText(tailscaleUrlInput.value);
    showToast('🔒 IP Tailscale disalin! (Untuk HP dengan aplikasi Tailscale). Port diisi terpisah.');
  }
});

btnCopyLan.addEventListener('click', () => {
  navigator.clipboard.writeText(localUrlInput.value);
  showToast('🌐 IP LAN disalin! (Untuk HP di satu WiFi kantor). Port diisi terpisah.');
});

btnEnableFunnel.addEventListener('click', async () => {
  btnEnableFunnel.disabled = true;
  btnEnableFunnel.textContent = 'Mengaktifkan...';
  try {
    const status = await invoke('enable_funnel');
    renderStatus(status);
    showToast('🚀 Funnel aktif! URL publik siap dipakai di SIAP NEO.');
  } catch (err) {
    showToast('Gagal mengaktifkan Funnel: ' + err, 7000);
  } finally {
    btnEnableFunnel.disabled = false;
    btnEnableFunnel.textContent = 'Aktifkan Funnel';
  }
});

chkKeepFunnel.addEventListener('change', async () => {
  try {
    const status = await invoke('set_keep_funnel', { enable: chkKeepFunnel.checked });
    renderStatus(status);
    showToast(chkKeepFunnel.checked ? 'Funnel akan dipulihkan otomatis bila hilang.' : 'Pemulihan Funnel otomatis dimatikan.');
  } catch (err) {
    showToast('Gagal menyimpan: ' + err);
  }
});

btnAllowFirewall.addEventListener('click', async () => {
  btnAllowFirewall.disabled = true;
  btnAllowFirewall.textContent = 'Menunggu izin Administrator...';
  try {
    const status = await invoke('allow_firewall');
    renderStatus(status);
    showToast('✅ Port diizinkan di Windows Firewall.');
  } catch (err) {
    showToast(String(err), 7000);
  } finally {
    btnAllowFirewall.disabled = false;
    btnAllowFirewall.textContent = 'Izinkan di Firewall';
  }
});

btnOpenTs.addEventListener('click', async () => {
  try {
    const res = await invoke('open_tailscale');
    showToast(res);
  } catch (e) {
    showToast('Gagal membuka Tailscale: ' + e);
  }
});

btnToggleServer.addEventListener('click', async () => {
  if (!currentStatus) return;
  try {
    const nextState = !currentStatus.is_running;
    btnToggleServer.disabled = true;
    await invoke('toggle_server', { enable: nextState });
    await fetchStatusFast();
    showToast(nextState ? '🟢 Server berhasil dinyalakan!' : '🔴 Server dimatikan.');
  } catch (err) {
    showToast('Gagal mengubah status server: ' + err);
  } finally {
    btnToggleServer.disabled = false;
  }
});

inputPort.addEventListener('focus', () => {
  isUserEditingPort = true;
});

chkAutoStart.addEventListener('change', () => {
  const enabled = chkAutoStart.checked;
  chkStartBeforeLogin.disabled = !enabled;
  if (startupSubOptions) {
    startupSubOptions.classList.toggle('disabled', !enabled);
  }
});

btnSaveSettings.addEventListener('click', async () => {
  const newPort = parseInt(inputPort.value, 10);
  if (isNaN(newPort) || newPort < 1024 || newPort > 65535) {
    showToast('Port harus berupa angka antara 1024 - 65535');
    return;
  }
  btnSaveSettings.disabled = true;
  btnSaveSettings.textContent = 'Menyimpan...';
  try {
    isUserEditingPort = false;
    const printerName = selectPrinter.value || '';
    const autoStart = chkAutoStart.checked;
    const startBeforeLogin = chkStartBeforeLogin.checked;
    const minimizeToTray = chkMinimizeToTray.checked;
    const minimizeOnExit = chkMinimizeOnExit ? chkMinimizeOnExit.checked : true;

    const updated = await invoke('update_settings', {
      newPort,
      newPrinter: printerName,
      autoStart,
      startBeforeLogin,
      minimizeToTray,
      minimizeOnExit,
    });
    renderStatus(updated);
    showToast('✅ Pengaturan Port & Startup Windows berhasil disimpan!');
  } catch (err) {
    showToast('⚠️ Gagal menyimpan pengaturan: ' + err, 7000);
  } finally {
    btnSaveSettings.disabled = false;
    btnSaveSettings.textContent = '💾 Terapkan Pengaturan';
  }
});

btnSavePrinter.addEventListener('click', async () => {
  const printerName = selectPrinter.value || '';
  if (!printerName) {
    showToast('Pilih printer terlebih dahulu');
    return;
  }
  try {
    const res = await invoke('save_printer_default', { printerName });
    showToast(`💾 ${res}`);
    await fetchStatusFast();
  } catch (err) {
    showToast('Gagal menyimpan printer default: ' + err);
  }
});

btnRefreshPrinters.addEventListener('click', refreshExplicitly);

btnTestPrint.addEventListener('click', async () => {
  const printerName = selectPrinter.value || '';
  btnTestPrint.disabled = true;
  btnTestPrint.textContent = 'Mencetak...';
  try {
    const res = await invoke('test_print', { printerName });
    showToast(`🖨️ ${res}`);
    await fetchStatusFast();
  } catch (err) {
    showToast('❌ Gagal cetak uji coba: ' + err);
  } finally {
    btnTestPrint.disabled = false;
    btnTestPrint.textContent = '📄 Uji Cetak (Test)';
  }
});

btnClearLogs.addEventListener('click', async () => {
  try {
    await invoke('clear_logs');
    await fetchStatusFast();
    showToast('Riwayat cetak dibersihkan.');
  } catch (err) {
    console.error(err);
  }
});

// Real-time Event Listener from Tauri (Instant reactive update without polling)
if (window.__TAURI__ && window.__TAURI__.event) {
  window.__TAURI__.event.listen('new_print_log', (event) => {
    fetchStatusFast();
  });
}

// Initial Boot: Read memory status once immediately
fetchStatusFast();

// Gentle in-memory sync every 5 seconds (Cost: 0ms, Zero process spawns)
setInterval(fetchStatusFast, 5000);
