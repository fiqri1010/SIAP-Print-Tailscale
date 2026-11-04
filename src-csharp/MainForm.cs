using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
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
    private int _diagTickCount = 0;

    private bool _reallyExit = false;

    public MainForm(bool startMinimized = false)
    {
        Text = "SIAP Print Tailscale Server — Native C#/.NET";
        Size = new Size(880, 760);
        MinimumSize = new Size(800, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(241, 245, 249); // Slate-100
        AutoScaleMode = AutoScaleMode.Dpi;

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

        Shown += (s, e) =>
        {
            if (startMinimized)
            {
                Hide();
            }
            MemoryHelper.TrimWorkingSet();
        };

        Resize += (s, e) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                MemoryHelper.TrimWorkingSet();
            }
        };
    }

    private void InitializeComponents()
    {
        // 1. Header Bar (Dock = Top)
        var panelHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 68,
            BackColor = Color.FromArgb(15, 23, 42) // Slate-900
        };
        panelHeader.Width = ClientSize.Width;

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
            Location = new Point(21, 38),
            AutoSize = true
        };

        _lblStatusBadge = new Label
        {
            Text = "● ONLINE",
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(16, 185, 129), // Emerald-500
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(100, 32),
            Location = new Point(ClientSize.Width - 125, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        panelHeader.Controls.Add(lblTitle);
        panelHeader.Controls.Add(lblSubtitle);
        panelHeader.Controls.Add(_lblStatusBadge);

        // 2. Status Bar Bawah (Dock = Bottom)
        var statusStrip = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel { Text = "Server siap menerima dokumen dari SIAP NEO", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _ramLabel = new ToolStripStatusLabel { Text = "RAM: -- MB", ForeColor = Color.DarkSlateGray };
        statusStrip.Items.AddRange(new ToolStripItem[] { _statusLabel, _ramLabel });

        // 3. Main Content Container (Dock = Fill)
        var panelContent = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(16)
        };

        // SUSUNAN Z-ORDER AGAR HEADER TIDAK MENUTUPI KONTEN:
        // Di WinForms, kontrol yang Fill harus ditambahkan dan dibawa ke depan (BringToFront)
        // sedangkan panel Top & Bottom harus SendToBack agar docking mengecilkan area Fill dengan benar.
        Controls.Add(panelContent);
        Controls.Add(statusStrip);
        Controls.Add(panelHeader);

        panelHeader.SendToBack();
        statusStrip.SendToBack();
        panelContent.BringToFront();

        int currentY = 16;

        // CARD 1: Status & Jaringan (Tinggi 185px)
        var cardNetwork = CreateCard("KONEKSI & JARINGAN (SIAP NEO)", currentY, 185, panelContent);
        currentY += 200;

        // Row 1: Port & Firewall
        var lblPort = new Label { Text = "Port HTTP:", Location = new Point(15, 34), AutoSize = true };
        _txtPort = new TextBox { Text = "1234", Location = new Point(95, 31), Width = 70 };
        _btnRestartServer = new Button { Text = "Terapkan Port", Location = new Point(175, 29), Width = 110, Height = 28 };
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

        var btnFirewall = new Button { Text = "Periksa Firewall Windows", Location = new Point(295, 29), Width = 185, Height = 28 };
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

        // Row 2: LAN IP
        var lblLan = new Label { Text = "IP Lokal / LAN:", Location = new Point(15, 71), Width = 110, AutoSize = false };
        _txtLanIp = new TextBox { 
            ReadOnly = true, 
            Location = new Point(130, 68), 
            Width = cardNetwork.ClientSize.Width - 130 - 75, 
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, 
            BackColor = Color.White 
        };
        var btnCopyLan = CreateCopyButton(cardNetwork.ClientSize.Width - 65, 67, _txtLanIp, AnchorStyles.Top | AnchorStyles.Right);

        // Row 3: Tailscale IP
        var lblTs = new Label { Text = "IP Tailscale:", Location = new Point(15, 107), Width = 110, AutoSize = false };
        _txtTailscaleIp = new TextBox { 
            ReadOnly = true, 
            Location = new Point(130, 104), 
            Width = cardNetwork.ClientSize.Width - 130 - 75, 
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, 
            BackColor = Color.White 
        };
        var btnCopyTs = CreateCopyButton(cardNetwork.ClientSize.Width - 65, 103, _txtTailscaleIp, AnchorStyles.Top | AnchorStyles.Right);

        // Row 4: Funnel URL
        var lblFunnel = new Label { Text = "URL Funnel:", Location = new Point(15, 143), Width = 110, AutoSize = false };
        _txtFunnelUrl = new TextBox { 
            ReadOnly = true, 
            Location = new Point(130, 140), 
            Width = cardNetwork.ClientSize.Width - 130 - 75, 
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, 
            BackColor = Color.White 
        };
        var btnCopyFunnel = CreateCopyButton(cardNetwork.ClientSize.Width - 65, 139, _txtFunnelUrl, AnchorStyles.Top | AnchorStyles.Right);

        cardNetwork.Controls.AddRange(new Control[] {
            lblPort, _txtPort, _btnRestartServer, btnFirewall,
            lblLan, _txtLanIp, btnCopyLan,
            lblTs, _txtTailscaleIp, btnCopyTs,
            lblFunnel, _txtFunnelUrl, btnCopyFunnel
        });

        // CARD 2: Pemilihan Printer SIAP NEO (Tinggi 80px)
        var cardPrinter = CreateCard("PRINTER AKTIF (SIAP NEO)", currentY, 80, panelContent);
        currentY += 95;

        var lblPrn = new Label { Text = "Pilih Printer:", Location = new Point(15, 34), Width = 100, AutoSize = false };
        _cmbPrinters = new ComboBox { 
            Location = new Point(120, 30), 
            Width = cardPrinter.ClientSize.Width - 120 - 140 - 95 - 20, 
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, 
            DropDownStyle = ComboBoxStyle.DropDownList 
        };
        _btnSavePrinter = new Button { 
            Text = "Simpan Default", 
            Location = new Point(cardPrinter.ClientSize.Width - 245, 29), 
            Width = 135, 
            Height = 28,
            Anchor = AnchorStyles.Top | AnchorStyles.Right 
        };
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

        _btnRefreshPrinters = new Button { 
            Text = "Segarkan", 
            Location = new Point(cardPrinter.ClientSize.Width - 95, 29), 
            Width = 85, 
            Height = 28,
            Anchor = AnchorStyles.Top | AnchorStyles.Right 
        };
        _btnRefreshPrinters.Click += (s, e) => RefreshPrintersList();

        cardPrinter.Controls.AddRange(new Control[] { lblPrn, _cmbPrinters, _btnSavePrinter, _btnRefreshPrinters });

        // CARD 3: Pengaturan Startup & Tray (Tinggi 85px)
        var cardSettings = CreateCard("PENGATURAN APLIKASI", currentY, 85, panelContent);
        currentY += 100;

        var flowSettings = new FlowLayoutPanel
        {
            Location = new Point(15, 32),
            Size = new Size(cardSettings.ClientSize.Width - 30, 45),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = false
        };

        _chkAutoStart = new CheckBox
        {
            Text = "Buka Otomatis saat Startup Windows",
            AutoSize = true,
            Margin = new Padding(0, 0, 25, 5)
        };
        _chkAutoStart.CheckedChanged += (s, e) => SaveSettings();

        _chkMinStartup = new CheckBox
        {
            Text = "Minimize ke Tray saat Startup",
            AutoSize = true,
            Margin = new Padding(0, 0, 25, 5)
        };
        _chkMinStartup.CheckedChanged += (s, e) => SaveSettings();

        _chkMinExit = new CheckBox
        {
            Text = "Minimize ke Tray on Exit (Tombol X)",
            AutoSize = true,
            Margin = new Padding(0, 0, 10, 5)
        };
        _chkMinExit.CheckedChanged += (s, e) => SaveSettings();

        flowSettings.Controls.Add(_chkAutoStart);
        flowSettings.Controls.Add(_chkMinStartup);
        flowSettings.Controls.Add(_chkMinExit);
        cardSettings.Controls.Add(flowSettings);

        // CARD 4: Riwayat Pencetakan
        int initialLogsH = panelContent.ClientSize.Height - currentY - 20;
        if (initialLogsH < 220) initialLogsH = 220;

        var cardLogs = CreateCard("RIWAYAT PENCETAKAN (LIVE LOGS)", currentY, initialLogsH, panelContent);
        cardLogs.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        // Dynamic height adjustment without AutoScroll / Bottom anchor conflict
        panelContent.ClientSizeChanged += (s, e) =>
        {
            int h = panelContent.ClientSize.Height - cardLogs.Top - 20;
            if (h < 220) h = 220;
            cardLogs.Height = h;
        };

        var btnClearLogs = new Button { 
            Text = "Bersihkan Riwayat", 
            Location = new Point(cardLogs.Width - 145, 12), 
            Width = 130, 
            Height = 26, 
            Anchor = AnchorStyles.Top | AnchorStyles.Right 
        };
        btnClearLogs.Click += (s, e) =>
        {
            ConfigManager.ClearLogs();
            _gridLogs.Rows.Clear();
        };
        cardLogs.Controls.Add(btnClearLogs);

        _gridLogs = new DataGridView
        {
            Location = new Point(15, 45),
            Size = new Size(cardLogs.Width - 30, cardLogs.Height - 60),
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

        _gridLogs.Columns["Time"].Width = 75;
        _gridLogs.Columns["Filename"].Width = 180;
        _gridLogs.Columns["Pages"].Width = 45;
        _gridLogs.Columns["Printer"].Width = 150;
        _gridLogs.Columns["Status"].Width = 80;

        cardLogs.Controls.Add(_gridLogs);

        // Penanganan Minimize on Close
        FormClosing += (s, e) =>
        {
            if (!_reallyExit && _chkMinExit.Checked && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                MemoryHelper.TrimWorkingSet();
                _trayIcon.ShowBalloonTip(2000, "SIAP Print Tailscale", "Aplikasi disembunyikan ke System Tray dan tetap aktif menerima dokumen cetak.", ToolTipIcon.Info);
            }
        };
    }

    private Panel CreateCard(string title, int y, int height, Panel parent)
    {
        var panel = new Panel
        {
            Location = new Point(16, y),
            Size = new Size(parent.ClientSize.Width - 32, height),
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

    private Button CreateCopyButton(int x, int y, TextBox target, AnchorStyles anchor = AnchorStyles.Top | AnchorStyles.Left)
    {
        var btn = new Button
        {
            Text = "Salin",
            Location = new Point(x, y),
            Size = new Size(55, 25),
            Anchor = anchor,
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
            _diagTickCount++;
            if ((!Visible || WindowState == FormWindowState.Minimized) && _diagTickCount % 5 == 0)
            {
                MemoryHelper.TrimWorkingSet();
            }

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
