const invoke = window.__TAURI__ ? window.__TAURI__.core.invoke : async (cmd, args) => {
  console.log('Mock invoke:', cmd, args);
  return {};
};

// UI Elements
const serverStatusPill = document.getElementById('server-status-pill');
const serverStatusText = document.getElementById('server-status-text');
const btnToggleServer = document.getElementById('btn-toggle-server');

const tailscaleIpDisplay = document.getElementById('tailscale-ip-display');
const tailscaleUrlInput = document.getElementById('tailscale-url-input');
const btnCopyTs = document.getElementById('btn-copy-ts');
const tailscaleWarning = document.getElementById('tailscale-warning');
const btnOpenTs = document.getElementById('btn-open-ts');

const localIpDisplay = document.getElementById('local-ip-display');
const localUrlInput = document.getElementById('local-url-input');
const btnCopyLan = document.getElementById('btn-copy-lan');

const inputPort = document.getElementById('input-port');
const btnSavePort = document.getElementById('btn-save-port');

const selectPrinter = document.getElementById('select-printer');
const printerMetaInfo = document.getElementById('printer-meta-info');
const btnRefreshPrinters = document.getElementById('btn-refresh-printers');
const btnTestPrint = document.getElementById('btn-test-print');

const logsTbody = document.getElementById('logs-tbody');
const btnClearLogs = document.getElementById('btn-clear-logs');
const toastEl = document.getElementById('toast');

let currentStatus = null;
let isUserEditingPort = false;

// Toast Helper
function showToast(message, duration = 3000) {
  toastEl.textContent = message;
  toastEl.classList.remove('hidden');
  setTimeout(() => {
    toastEl.classList.add('hidden');
  }, duration);
}

// Render Status to UI
function renderStatus(status) {
  currentStatus = status;

  // Server state
  if (status.is_running) {
    serverStatusPill.className = 'status-pill status-running';
    serverStatusText.textContent = `Aktif (Port ${status.port})`;
    btnToggleServer.textContent = 'Matikan Server';
    btnToggleServer.className = 'btn btn-secondary';
  } else {
    serverStatusPill.className = 'status-pill status-stopped';
    serverStatusText.textContent = 'Server Mati';
    btnToggleServer.textContent = 'Nyalakan Server';
    btnToggleServer.className = 'btn btn-primary';
  }

  // Tailscale IP
  if (status.tailscale_ip) {
    tailscaleIpDisplay.textContent = status.tailscale_ip;
    const tsUrl = `http://${status.tailscale_ip}:${status.port}`;
    tailscaleUrlInput.value = tsUrl;
    tailscaleWarning.classList.add('hidden');
  } else {
    tailscaleIpDisplay.textContent = 'Tidak Terdeteksi';
    tailscaleUrlInput.value = 'Tailscale belum aktif';
    tailscaleWarning.classList.remove('hidden');
  }

  // Local IP
  localIpDisplay.textContent = status.local_ip;
  localUrlInput.value = `http://${status.local_ip}:${status.port}`;

  // Port
  if (!isUserEditingPort) {
    inputPort.value = status.port;
  }

  // Printers dropdown
  renderPrinters(status.printers, status.selected_printer);

  // Logs table
  renderLogs(status.logs);
}

function renderPrinters(printers, selectedPrinter) {
  if (!printers || printers.length === 0) {
    selectPrinter.innerHTML = '<option value="">Tidak ada printer terdeteksi</option>';
    printerMetaInfo.innerHTML = '';
    return;
  }

  const currentValue = selectPrinter.value || selectedPrinter;
  let html = '';
  printers.forEach((p) => {
    const isSelected = p.name === currentValue || (!currentValue && p.is_default);
    html += `<option value="${p.name}" ${isSelected ? 'selected' : ''}>${p.name} ${p.is_default ? '(Default)' : ''}</option>`;
  });
  selectPrinter.innerHTML = html;

  // Display metadata of currently selected printer
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

// Fetch status from Rust backend
async function refreshStatus() {
  try {
    const status = await invoke('get_app_status');
    renderStatus(status);
  } catch (err) {
    console.error('Error fetching status:', err);
  }
}

// Event Listeners
btnCopyTs.addEventListener('click', () => {
  if (tailscaleUrlInput.value && !tailscaleUrlInput.value.includes('belum')) {
    navigator.clipboard.writeText(tailscaleUrlInput.value);
    showToast('✅ URL Tailscale disalin ke clipboard!');
  }
});

btnCopyLan.addEventListener('click', () => {
  navigator.clipboard.writeText(localUrlInput.value);
  showToast('✅ URL Local LAN disalin ke clipboard!');
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
    await refreshStatus();
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

btnSavePort.addEventListener('click', async () => {
  const newPort = parseInt(inputPort.value, 10);
  if (isNaN(newPort) || newPort < 1024 || newPort > 65535) {
    showToast('Port harus berupa angka antara 1024 - 65535');
    return;
  }
  try {
    isUserEditingPort = false;
    const printerName = selectPrinter.value || '';
    const updated = await invoke('update_settings', { newPort, newPrinter: printerName });
    renderStatus(updated);
    showToast(`✅ Port diperbarui ke ${newPort} dan server di-restart!`);
  } catch (err) {
    showToast('Gagal memperbarui port: ' + err);
  }
});

selectPrinter.addEventListener('change', async () => {
  const newPort = parseInt(inputPort.value, 10) || 1234;
  const printerName = selectPrinter.value || '';
  try {
    const updated = await invoke('update_settings', { newPort, newPrinter: printerName });
    renderStatus(updated);
    showToast(`✅ Printer target diubah ke: ${printerName}`);
  } catch (err) {
    console.error(err);
  }
});

btnRefreshPrinters.addEventListener('click', async () => {
  await refreshStatus();
  showToast('🔄 Daftar printer diperbarui!');
});

btnTestPrint.addEventListener('click', async () => {
  const printerName = selectPrinter.value || '';
  btnTestPrint.disabled = true;
  btnTestPrint.textContent = 'Mencetak...';
  try {
    const res = await invoke('test_print', { printerName });
    showToast(`🖨️ ${res}`);
    await refreshStatus();
  } catch (err) {
    showToast('❌ Gagal cetak uji coba: ' + err);
  } finally {
    btnTestPrint.disabled = false;
    btnTestPrint.textContent = '🖨️ Uji Cetak (Test)';
  }
});

btnClearLogs.addEventListener('click', async () => {
  try {
    await invoke('clear_logs');
    await refreshStatus();
    showToast('Riwayat cetak dibersihkan.');
  } catch (err) {
    console.error(err);
  }
});

// Initial boot & Polling interval
refreshStatus();
setInterval(refreshStatus, 2500);
