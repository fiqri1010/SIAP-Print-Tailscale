using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SiapPrintServer;

public class MainForm : Form
{
    private readonly HttpPrintServer _server = new();
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private readonly System.Windows.Forms.Timer _statusTimer;

    // Controls
    private Label _lblStatusBadge = null!;
    private TextBox _txtPort = null!;
    private Button _btnRestartServer = null!;
    private TextBox _txtLanIp = null!;
    private TextBox _txtTailscaleIp = null!;
    private TextBox _txtFunnelUrl = null!;
    private ComboBox _cmbPrinters = null!;
    private Button _btnSavePrinter = null!;
    private Button _btnRefreshPrinters = null!;
    private DataGridView _gridLogs = null!;
    private CheckBox _chkAutoStart = null!;
    private CheckBox _chkMinStartup = null!;
    private CheckBox _chkMinExit = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private ToolStripStatusLabel _ramLabel = null!;

    private bool _reallyExit = false;

    public MainForm(bool startMinimized = false)
    {
        Text = "SIAP Print Tailscale Server — Native C#/.NET";
        Size = new Size(840, 720);
        MinimumSize = new Size(760, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(241, 245, 249); // Slate-100

        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
            }
        }
        catch { }

        // Setup System Tray
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("Buka Dashboard", null, (s, e) => ShowAndRestore());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Salin IP Tailscale", null, (s, e) => CopyToClipboard(_txtTailscaleIp.Text));
        _trayMenu.Items.Add("Salin URL Funnel", null, (s, e) => CopyToClipboard(_txtFunnelUrl.Text));
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Keluar Aplikasi", null, (s, e) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "SIAP Print Tailscale Server",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (s, e) => ShowAndRestore();

        InitializeComponents();
        LoadConfigAndData();

        // Event listener saat log baru masuk
        ConfigManager.LogAdded += OnLogAdded;

        // Timer untuk pembaruan status & RAM (tiap 3 detik)
        _statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _statusTimer.Tick += (s, e) => RefreshDiagnostics();
        _statusTimer.Start();

        // Mulai Server HTTP
        var cfg = ConfigManager.LoadConfig();
        _ = _server.StartAsync(cfg.Port);

        if (startMinimized)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Opacity = 0;
        }
    }

    private void InitializeComponents()
    {
        // 1. Header Bar
        var panelHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 65,
            BackColor = Color.FromArgb(15, 23, 42) // Slate-900
        };

        var lblTitle = new Label
        {
            Text = "SIAP PRINT TAILSCALE",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(20, 12),
            AutoSize = true
        };

        var lblSubtitle = new Label
        {
            Text = "Native Windows C#/.NET 8 Print Server & Headless PDF Engine",
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(21, 37),
            AutoSize = true
        };

        _lblStatusBadge = new Label
        {
            Text = "● ONLINE",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(16, 185, 129), // Emerald-500
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(100, 30),
            Location = new Point(Width - 140, 17),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        panelHeader.Controls.Add(lblTitle);
        panelHeader.Controls.Add(lblSubtitle);
        panelHeader.Controls.Add(_lblStatusBadge);
        Controls.Add(panelHeader);

        // 2. Main Scrollable Container
        var panelContent = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(15)
        };
        Controls.Add(panelContent);

        int currentY = 15;

        // CARD 1: Status & Jaringan
        var cardNetwork = CreateCard("KONEKSI & JARINGAN", currentY, 155, panelContent);
        currentY += 165;

        var lblPort = new Label { Text = "Port HTTP:", Location = new Point(15, 35), AutoSize = true };
        _txtPort = new TextBox { Text = "1234", Location = new Point(90, 32), Width = 70 };
        _btnRestartServer = new Button { Text = "Terapkan Port", Location = new Point(170, 30), Width = 110, Height = 28 };
        _btnRestartServer.Click += async (s, e) =>
        {
            if (int.TryParse(_txtPort.Text.Trim(), out int newPort) && newPort > 0 && newPort < 65536)
            {
                var cfg = ConfigManager.LoadConfig();
                cfg.Port = newPort;
                ConfigManager.SaveConfig(cfg);
                await _server.StartAsync(newPort);
                MessageBox.Show(this, $"Port berhasil diubah ke {newPort} dan server telah dimuat ulang.", "Informasi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshNetworkInfo();
            }
        };

        var lblLan = new Label { Text = "IP Lokal / LAN:", Location = new Point(15, 70), AutoSize = true };
        _txtLanIp = new TextBox { ReadOnly = true, Location = new Point(120, 67), Width = 230, BackColor = Color.White };
        var btnCopyLan = CreateCopyButton(360, 66, _txtLanIp);

        var lblTs = new Label { Text = "IP Tailscale:", Location = new Point(15, 102), AutoSize = true };
        _txtTailscaleIp = new TextBox { ReadOnly = true, Location = new Point(120, 99), Width = 230, BackColor = Color.White };
        var btnCopyTs = CreateCopyButton(360, 98, _txtTailscaleIp);

        var lblFunnel = new Label { Text = "URL Funnel:", Location = new Point(440, 70), AutoSize = true };
        _txtFunnelUrl = new TextBox { ReadOnly = true, Location = new Point(530, 67), Width = 200, BackColor = Color.White };
        var btnCopyFunnel = CreateCopyButton(740, 66, _txtFunnelUrl);

        var btnFirewall = new Button { Text = "Periksa Firewall Windows", Location = new Point(530, 100), Width = 200, Height = 28 };
        btnFirewall.Click += (s, e) =>
        {
            if (int.TryParse(_txtPort.Text.Trim(), out int port))
            {
                if (StartupHelper.CheckFirewallRule(port))
                {
                    MessageBox.Show(this, $"Port {port} sudah diizinkan di Windows Firewall.", "Status Firewall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    if (MessageBox.Show(this, $"Port {port} belum terdaftar di Windows Firewall. Apakah Anda ingin menambahkannya sekarang? (Akan meminta izin Administrator)", "Firewall Windows", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        StartupHelper.AddFirewallRule(port);
                    }
                }
            }
        };

        cardNetwork.Controls.AddRange(new Control[] {
            lblPort, _txtPort, _btnRestartServer,
            lblLan, _txtLanIp, btnCopyLan,
            lblTs, _txtTailscaleIp, btnCopyTs,
            lblFunnel, _txtFunnelUrl, btnCopyFunnel,
            btnFirewall
        });

        // CARD 2: Pemilihan Printer SIAP NEO
        var cardPrinter = CreateCard("PRINTER AKTIF (SIAP NEO)", currentY, 75, panelContent);
        currentY += 85;

        var lblPrn = new Label { Text = "Pilih Printer:", Location = new Point(15, 34), AutoSize = true };
        _cmbPrinters = new ComboBox { Location = new Point(110, 30), Width = 380, DropDownStyle = ComboBoxStyle.DropDownList };
        _btnSavePrinter = new Button { Text = "Simpan Default", Location = new Point(500, 29), Width = 130, Height = 28 };
        _btnSavePrinter.Click += (s, e) =>
        {
            if (_cmbPrinters.SelectedItem != null)
            {
                string sel = _cmbPrinters.SelectedItem.ToString() ?? "";
                var cfg = ConfigManager.LoadConfig();
                cfg.SelectedPrinter = sel;
                ConfigManager.SaveConfig(cfg);
                MessageBox.Show(this, $"Printer default berhasil disimpan: {sel}", "Informasi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        _btnRefreshPrinters = new Button { Text = "Segarkan", Location = new Point(640, 29), Width = 90, Height = 28 };
        _btnRefreshPrinters.Click += (s, e) => RefreshPrintersList();

        cardPrinter.Controls.AddRange(new Control[] { lblPrn, _cmbPrinters, _btnSavePrinter, _btnRefreshPrinters });

        // CARD 3: Pengaturan Startup & Tray
        var cardSettings = CreateCard("PENGATURAN APLIKASI", currentY, 65, panelContent);
        currentY += 75;

        _chkAutoStart = new CheckBox
        {
            Text = "Buka Otomatis saat Startup Windows",
            Location = new Point(15, 30),
            AutoSize = true
        };
        _chkAutoStart.CheckedChanged += (s, e) => SaveSettings();

        _chkMinStartup = new CheckBox
        {
            Text = "Minimize ke Tray saat Startup",
            Location = new Point(275, 30),
            AutoSize = true
        };
        _chkMinStartup.CheckedChanged += (s, e) => SaveSettings();

        _chkMinExit = new CheckBox
        {
            Text = "Minimize ke Tray on Exit (Tombol X)",
            Location = new Point(510, 30),
            AutoSize = true
        };
        _chkMinExit.CheckedChanged += (s, e) => SaveSettings();

        cardSettings.Controls.AddRange(new Control[] { _chkAutoStart, _chkMinStartup, _chkMinExit });

        // CARD 4: Riwayat Pencetakan
        var cardLogs = CreateCard("RIWAYAT PENCETAKAN (LIVE LOGS)", currentY, 230, panelContent);
        cardLogs.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

        var btnClearLogs = new Button { Text = "Bersihkan Riwayat", Location = new Point(cardLogs.Width - 145, 12), Width = 130, Height = 25, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnClearLogs.Click += (s, e) =>
        {
            ConfigManager.ClearLogs();
            _gridLogs.Rows.Clear();
        };
        cardLogs.Controls.Add(btnClearLogs);

        _gridLogs = new DataGridView
        {
            Location = new Point(15, 42),
            Size = new Size(cardLogs.Width - 30, 175),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            AllowUserToAddRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };

        _gridLogs.Columns.Add("Time", "Waktu");
        _gridLogs.Columns.Add("Filename", "Dokumen");
        _gridLogs.Columns.Add("Pages", "Hal");
        _gridLogs.Columns.Add("Printer", "Printer");
        _gridLogs.Columns.Add("Status", "Status");
        _gridLogs.Columns.Add("Message", "Pesan");

        _gridLogs.Columns["Time"].Width = 70;
        _gridLogs.Columns["Filename"].Width = 170;
        _gridLogs.Columns["Pages"].Width = 45;
        _gridLogs.Columns["Printer"].Width = 150;
        _gridLogs.Columns["Status"].Width = 75;

        cardLogs.Controls.Add(_gridLogs);

        // 3. Status Bar Bawah
        var statusStrip = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel { Text = "Server siap menerima dokumen dari SIAP NEO", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _ramLabel = new ToolStripStatusLabel { Text = "RAM: -- MB", ForeColor = Color.DarkSlateGray };
        statusStrip.Items.AddRange(new ToolStripItem[] { _statusLabel, _ramLabel });
        Controls.Add(statusStrip);

        // Penanganan Minimize on Close
        FormClosing += (s, e) =>
        {
            if (!_reallyExit && _chkMinExit.Checked && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _trayIcon.ShowBalloonTip(2000, "SIAP Print Tailscale", "Aplikasi disembunyikan ke System Tray dan tetap aktif menerima dokumen cetak.", ToolTipIcon.Info);
            }
        };
    }

    private Panel CreateCard(string title, int y, int height, Panel parent)
    {
        var panel = new Panel
        {
            Location = new Point(15, y),
            Size = new Size(parent.ClientSize.Width - 30, height),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.White
        };

        var lblHeader = new Label
        {
            Text = title,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(100, 116, 139), // Slate-500
            Location = new Point(15, 12),
            AutoSize = true
        };

        panel.Controls.Add(lblHeader);
        parent.Controls.Add(panel);
        return panel;
    }

    private Button CreateCopyButton(int x, int y, TextBox target)
    {
        var btn = new Button
        {
            Text = "Salin",
            Location = new Point(x, y),
            Size = new Size(55, 23),
            BackColor = Color.FromArgb(248, 250, 252)
        };
        btn.Click += (s, e) => CopyToClipboard(target.Text);
        return btn;
    }

    private void CopyToClipboard(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            Clipboard.SetText(text);
            _statusLabel.Text = $"Tersalin ke clipboard: {text}";
        }
    }

    private void LoadConfigAndData()
    {
        var cfg = ConfigManager.LoadConfig();
        _txtPort.Text = cfg.Port.ToString();
        _chkAutoStart.Checked = cfg.AutoStartStartup;
        _chkMinStartup.Checked = cfg.MinimizeToTrayOnStartup;
        _chkMinExit.Checked = cfg.MinimizeOnExit;

        RefreshNetworkInfo();
        RefreshPrintersList();
        LoadLogsData();
    }

    private void SaveSettings()
    {
        var cfg = ConfigManager.LoadConfig();
        cfg.AutoStartStartup = _chkAutoStart.Checked;
        cfg.MinimizeToTrayOnStartup = _chkMinStartup.Checked;
        cfg.MinimizeOnExit = _chkMinExit.Checked;
        ConfigManager.SaveConfig(cfg);

        StartupHelper.ConfigureStartup(cfg.AutoStartStartup, cfg.MinimizeToTrayOnStartup);
    }

    private void RefreshNetworkInfo()
    {
        _txtLanIp.Text = $"{TailscaleHelper.GetLocalIp()}:{_server.ActivePort}";

        Task.Run(() =>
        {
            string? tsIp = TailscaleHelper.GetTailscaleIp();
            var (funnelDomain, _) = TailscaleHelper.GetFunnelStatus();

            try
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        _txtTailscaleIp.Text = tsIp != null ? $"{tsIp}:{_server.ActivePort}" : "(Tailscale tidak aktif)";
                        _txtFunnelUrl.Text = funnelDomain != null ? $"https://{funnelDomain}" : "(Funnel belum diaktifkan)";
                    }));
                }
            }
            catch { }
        });
    }

    private void RefreshPrintersList()
    {
        _cmbPrinters.Items.Clear();
        var printers = WindowsPrinterHelper.GetPrinters();
        var cfg = ConfigManager.LoadConfig();

        int selectIndex = -1;
        for (int i = 0; i < printers.Count; i++)
        {
            string name = printers[i].Name;
            _cmbPrinters.Items.Add(name);

            if (!string.IsNullOrEmpty(cfg.SelectedPrinter) && string.Equals(name, cfg.SelectedPrinter, StringComparison.OrdinalIgnoreCase))
            {
                selectIndex = i;
            }
            else if (selectIndex == -1 && printers[i].IsDefault)
            {
                selectIndex = i;
            }
        }

        if (selectIndex != -1 && selectIndex < _cmbPrinters.Items.Count)
        {
            _cmbPrinters.SelectedIndex = selectIndex;
        }
        else if (_cmbPrinters.Items.Count > 0)
        {
            _cmbPrinters.SelectedIndex = 0;
        }
    }

    private void LoadLogsData()
    {
        _gridLogs.Rows.Clear();
        var logs = ConfigManager.LoadLogs();
        foreach (var item in logs)
        {
            AddGridRow(item);
        }
    }

    private void OnLogAdded(PrintLogItem item)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnLogAdded(item)));
            return;
        }

        AddGridRow(item, insertAtTop: true);
        _statusLabel.Text = $"Dokumen terakhir: {item.Filename} - {item.Status}";
    }

    private void AddGridRow(PrintLogItem item, bool insertAtTop = false)
    {
        DataGridViewRow row;
        if (insertAtTop)
        {
            _gridLogs.Rows.Insert(0, 1);
            row = _gridLogs.Rows[0];
        }
        else
        {
            int rowIdx = _gridLogs.Rows.Add();
            row = _gridLogs.Rows[rowIdx];
        }

        row.Cells["Time"].Value = item.Time;
        row.Cells["Filename"].Value = item.Filename;
        row.Cells["Pages"].Value = $"{item.Pages} hal";
        row.Cells["Printer"].Value = item.Printer;
        row.Cells["Status"].Value = item.Status;
        row.Cells["Message"].Value = item.Message;

        if (item.Status == "SUKSES")
        {
            row.Cells["Status"].Style.ForeColor = Color.ForestGreen;
            row.Cells["Status"].Style.Font = new Font(_gridLogs.Font, FontStyle.Bold);
        }
        else
        {
            row.Cells["Status"].Style.ForeColor = Color.Crimson;
            row.Cells["Status"].Style.Font = new Font(_gridLogs.Font, FontStyle.Bold);
        }
    }

    private void RefreshDiagnostics()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            long memoryMb = proc.WorkingSet64 / (1024 * 1024);
            _ramLabel.Text = $"RAM: {memoryMb} MB";

            if (_server.IsRunning)
            {
                _lblStatusBadge.Text = "● ONLINE";
                _lblStatusBadge.BackColor = Color.FromArgb(16, 185, 129);
            }
            else
            {
                _lblStatusBadge.Text = "● OFFLINE";
                _lblStatusBadge.BackColor = Color.FromArgb(239, 68, 68);
            }
        }
        catch { }
    }

    public void ShowAndRestore()
    {
        Show();
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        Opacity = 1;
        BringToFront();
        Activate();
    }

    public void ExitApplication()
    {
        _reallyExit = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _statusTimer.Stop();
        _ = _server.StopAsync();
        Application.Exit();
    }
}
