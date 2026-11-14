#if FACE_WINFORMS_LOCKER
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using OpenCvSharp;
// Fully qualify WinForms Application to avoid clash if Avalonia is referenced.

namespace client.Services.FaceVerify.WinForms;

/// <summary>
/// Soft-lock WinForms UI for --face-verify. Not Windows Lock Screen / Credential Provider.
/// Shows immediately; models and camera initialize on background workers.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FaceVerifyLockerHost
{
    public static void Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new FaceVerifyLockerForm());
    }
}

[SupportedOSPlatform("windows")]
internal sealed class FaceVerifyLockerForm : Form
{
    private readonly Label _title;
    private readonly Label _status;
    private readonly PictureBox _preview;
    private readonly PictureBox _reference;
    private readonly Label _refLabel;
    private FaceVerifyController? _controller;
    private Bitmap? _previewBitmap;

    public FaceVerifyLockerForm()
    {
        Text = FacePaths.LockerWindowTitle;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        BackColor = Color.FromArgb(18, 18, 22);
        ShowInTaskbar = true;
        KeyPreview = true;

        _title = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Text = "alpha-ai-tracker-face-verification",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(28, 28, 36),
        };
        _status = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Text = "Preparing…",
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 12f),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(22, 22, 28),
        };
        _preview = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
        };
        _refLabel = new Label
        {
            Text = "Reference",
            ForeColor = Color.Silver,
            Font = new Font("Segoe UI", 9f),
            AutoSize = true,
            Visible = false,
        };
        _reference = new PictureBox
        {
            Size = new System.Drawing.Size(160, 160),
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.Black,
            Visible = false,
        };

        Controls.Add(_preview);
        Controls.Add(_status);
        Controls.Add(_title);
        Controls.Add(_refLabel);
        Controls.Add(_reference);

        Load += async (_, _) => await OnLoadedAsync();
        FormClosing += OnFormClosing;
        Deactivate += (_, _) =>
        {
            if (_controller is { AllowClose: false })
            {
                BeginInvoke(() =>
                {
                    WindowState = FormWindowState.Maximized;
                    TopMost = true;
                    Activate();
                });
            }
        };
        Resize += (_, _) => LayoutOverlay();
    }

    private void LayoutOverlay()
    {
        _reference.Location = new System.Drawing.Point(ClientSize.Width - _reference.Width - 24, 120);
        _refLabel.Location = new System.Drawing.Point(_reference.Left, _reference.Top - 22);
    }

    private async Task OnLoadedAsync()
    {
        LayoutOverlay();
        _controller = new FaceVerifyController();
        _controller.Changed += () =>
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                _title.Text = _controller?.StatusTitle ?? Text;
                _status.Text = _controller?.StatusDetail ?? "";
                var refPath = _controller?.ReferenceImagePath ?? "";
                if (!string.IsNullOrWhiteSpace(refPath) && File.Exists(refPath))
                {
                    try
                    {
                        _reference.Image?.Dispose();
                        _reference.Image = Image.FromFile(refPath);
                        _reference.Visible = true;
                        _refLabel.Visible = true;
                    }
                    catch { /* ignore */ }
                }
                else
                {
                    _reference.Visible = false;
                    _refLabel.Visible = false;
                }
                LayoutOverlay();
            });
        };
        _controller.FrameProcessed += result =>
        {
            if (IsDisposed) return;
            var mat = result.PreviewBgr;
            if (mat == null || mat.Empty()) return;
            try
            {
                var bmp = MatToBitmap(mat);
                BeginInvoke(() =>
                {
                    _previewBitmap?.Dispose();
                    _previewBitmap = bmp;
                    _preview.Image = _previewBitmap;
                });
            }
            catch
            {
                /* ignore preview errors */
            }
            finally
            {
                mat.Dispose();
            }
        };
        _controller.Completed += () =>
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                TopMost = false;
                Close();
            });
        };

        await _controller.StartAsync();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_controller is { AllowClose: false })
        {
            e.Cancel = true;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            return;
        }
        _controller?.Dispose();
        _controller = null;
        _previewBitmap?.Dispose();
        _reference.Image?.Dispose();
    }

    private static Bitmap MatToBitmap(Mat bgr)
    {
        Cv2.ImEncode(".bmp", bgr, out var bytes);
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }
}
#endif
