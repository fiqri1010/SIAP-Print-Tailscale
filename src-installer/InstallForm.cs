using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SiapPrintInstaller;

public class InstallForm : Form
{
    private Label _lblStatus = null!;
    private ProgressBar _progressBar = null!;
    private Button _btnAction = null!;

    public InstallForm()
    {
        Text = "SIAP Print Tailscale Setup — v2.2.0";
        Size = new Size(540, 360);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(248, 250, 252); // Slate-50

        InitializeComponents();
    }

    private void InitializeComponents()
    {
        // 1. Header Banner
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 90,
            BackColor = Color.FromArgb(15, 23, 42) // Slate-900
        };

        var lblTitle = new Label
        {
            Text = "SIAP Print Tailscale Installer",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(24, 18),
            AutoSize = true
        };

        var lblSubtitle = new Label
        {
            Text = "Versi 2.2.0 (Ultra-Low Memory Edition) — KPU BC Tanjung Priok",
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(25, 48),
            AutoSize = true
        };

        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(lblSubtitle);
        Controls.Add(headerPanel);

        // 2. Info Description
        var lblDesc = new Label
        {
            Text = "Installer ini akan memasang atau memperbarui SIAP Print Tailscale ke komputer Anda secara otomatis, menghentikan versi sebelumnya, membuat pintasan Desktop, dan meluncurkan server.",
            Location = new Point(24, 110),
            Size = new Size(480, 50),
            ForeColor = Color.FromArgb(51, 65, 85)
        };
        Controls.Add(lblDesc);

        // 3. Status Text
        _lblStatus = new Label
        {
            Text = "Siap untuk memulai instalasi...",
            Location = new Point(24, 175),
            Size = new Size(480, 24),
            ForeColor = Color.FromArgb(30, 41, 59),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        Controls.Add(_lblStatus);

        // 4. Progress Bar
        _progressBar = new ProgressBar
        {
            Location = new Point(24, 205),
            Size = new Size(480, 22),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 0
        };
        Controls.Add(_progressBar);

        // 5. Action Button
        _btnAction = new Button
        {
            Text = "Pasang Sekarang",
            Location = new Point(340, 255),
            Size = new Size(164, 40),
            BackColor = Color.FromArgb(16, 185, 129), // Emerald-500
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat
        };
        _btnAction.FlatAppearance.BorderSize = 0;
        _btnAction.Click += (s, e) => StartInstall();
        Controls.Add(_btnAction);

        var btnCancel = new Button
        {
            Text = "Batal",
            Location = new Point(240, 255),
            Size = new Size(90, 40),
            BackColor = Color.FromArgb(226, 232, 240),
            ForeColor = Color.FromArgb(71, 85, 105),
            FlatStyle = FlatStyle.Flat
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.Click += (s, e) => Close();
        Controls.Add(btnCancel);
    }

    private async void StartInstall()
    {
        _btnAction.Enabled = false;
        _progressBar.MarqueeAnimationSpeed = 30;

        bool success = await Task.Run(() =>
        {
            return InstallerLogic.ExecuteInstall(status =>
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => _lblStatus.Text = status));
                }
                else
                {
                    _lblStatus.Text = status;
                }
            });
        });

        _progressBar.Style = ProgressBarStyle.Blocks;
        _progressBar.Value = 100;

        if (success)
        {
            _lblStatus.Text = "Instalasi berhasil! Aplikasi telah diluncurkan.";
            _lblStatus.ForeColor = Color.ForestGreen;
            _btnAction.Text = "Selesai";
            _btnAction.BackColor = Color.FromArgb(15, 23, 42);
            _btnAction.Enabled = true;
            _btnAction.Click += (s, e) => Close();
        }
        else
        {
            _lblStatus.ForeColor = Color.Crimson;
            _btnAction.Text = "Coba Lagi";
            _btnAction.Enabled = true;
        }
    }
}

