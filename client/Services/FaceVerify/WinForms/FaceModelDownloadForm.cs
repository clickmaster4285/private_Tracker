#if FACE_WINFORMS_LOCKER
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using client.Core;

namespace client.Services.FaceVerify.WinForms;

/// <summary>
/// Tracker-side progress window while face ONNX models download (headless --background).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FaceModelDownloadForm : Form
{
    private readonly Label _title;
    private readonly Label _status;
    private readonly Label _meta;
    private readonly ProgressBar _bar;
    private Action<FaceDownloadProgress>? _handler;
    private bool _allowClose;

    public FaceModelDownloadForm()
    {
        Text = $"{AppInfo.DisplayName} — Face models";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(440, 180);
        BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Segoe UI", 10f);

        _title = new Label
        {
            Text = "Downloading face models",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            AutoSize = false,
            Location = new Point(20, 16),
            Size = new Size(400, 28),
        };
        _status = new Label
        {
            Text = "Preparing…",
            ForeColor = Color.FromArgb(71, 85, 105),
            AutoSize = false,
            Location = new Point(20, 52),
            Size = new Size(400, 40),
        };
        _bar = new ProgressBar
        {
            Location = new Point(20, 100),
            Size = new Size(400, 14),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Minimum = 0,
            Maximum = 1000,
        };
        _meta = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(51, 65, 85),
            AutoSize = false,
            Location = new Point(20, 126),
            Size = new Size(400, 24),
        };

        Controls.Add(_title);
        Controls.Add(_status);
        Controls.Add(_bar);
        Controls.Add(_meta);
        FormClosing += (_, e) =>
        {
            if (!_allowClose) e.Cancel = true;
        };
    }

    public void Attach()
    {
        _handler = p =>
        {
            if (IsDisposed) return;
            void Apply()
            {
                if (IsDisposed) return;
                _status.Text = string.IsNullOrWhiteSpace(p.Step) ? _status.Text : p.Step;
                var meta = p.MbLine;
                if (!string.IsNullOrEmpty(p.EtaText))
                    meta = string.IsNullOrEmpty(meta) ? p.EtaText : $"{meta}  ·  {p.EtaText}";
                _meta.Text = meta ?? "";

                if (p.Fraction is { } f)
                {
                    _bar.Style = ProgressBarStyle.Continuous;
                    _bar.Value = (int)Math.Clamp(f * 1000, 0, 1000);
                }

                if (p.IsComplete)
                {
                    _title.Text = "Face models ready";
                    _bar.Style = ProgressBarStyle.Continuous;
                    _bar.Value = 1000;
                    _allowClose = true;
                    BeginInvoke(async () =>
                    {
                        await Task.Delay(600);
                        AllowCloseAndClose();
                    });
                }
                else if (p.IsFailed)
                {
                    _title.Text = "Download failed";
                    _status.Text = p.Error ?? p.Step;
                    _allowClose = true;
                }
            }

            if (InvokeRequired) BeginInvoke(Apply);
            else Apply();
        };
        FaceDownloadStatus.Changed += _handler;
        if (FaceDownloadStatus.Current is { } cur)
            _handler(cur);
    }

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        if (_handler != null)
        {
            FaceDownloadStatus.Changed -= _handler;
            _handler = null;
        }
        try { Close(); } catch { /* ignore */ }
        try { Application.ExitThread(); } catch { /* ignore */ }
    }
}
#endif
