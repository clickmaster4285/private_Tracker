using System.Text.Json;
using client.Core;

namespace client.Services;

/// <summary>
/// Full-screen gate after login. Stays up until the security service reports every
/// blocker enforcing. When a post-login reboot is armed, it stays up through the restart.
/// </summary>
public static class PolicyGate
{
    public static Task WaitAsync(CancellationToken ct = default)
    {
        AppBlockNotice.Start();
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
#if FACE_WINFORMS_LOCKER
                PolicyGateForm.Run(ct);
#endif
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Name = "policy-gate";
        thread.Start();
        return tcs.Task;
    }
}

#if FACE_WINFORMS_LOCKER
internal static class PolicyGateForm
{
    private const string MutexName = "Local\\AlphaAITracker.PolicyGate";

    public static void Run(CancellationToken ct)
    {
        using var mutex = new Mutex(false, MutexName);
        var acquired = false;
        try
        {
            while (!ct.IsCancellationRequested && !acquired)
            {
                try
                {
                    acquired = mutex.WaitOne(500);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
            }
            if (!acquired)
                return;

            System.Windows.Forms.Application.EnableVisualStyles();
            using var form = new GateForm(ct);
            System.Windows.Forms.Application.Run(form);
        }
        finally
        {
            if (acquired)
            {
                try { mutex.ReleaseMutex(); } catch { /* already released */ }
            }
        }
    }

    private sealed class GateForm : System.Windows.Forms.Form
    {
        private readonly System.Windows.Forms.Label _title;
        private readonly System.Windows.Forms.Label _body;
        private readonly System.Windows.Forms.Label _solution;
        private readonly System.Windows.Forms.Button _retry;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly CancellationToken _ct;
        private bool _allowClose;
        private int _pollBusy;

        public GateForm(CancellationToken ct)
        {
            _ct = ct;
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            Bounds = screen;
            TopMost = true;
            ShowInTaskbar = true;
            BackColor = System.Drawing.Color.FromArgb(15, 23, 42);
            ForeColor = System.Drawing.Color.White;
            Font = new System.Drawing.Font("Segoe UI", 12f);
            Text = AppInfo.DisplayName + " — Security policy";
            Cursor = System.Windows.Forms.Cursors.WaitCursor;

            _title = new System.Windows.Forms.Label
            {
                Text = "Applying security policy",
                Font = new System.Drawing.Font("Segoe UI", 26f, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.White,
                AutoSize = false,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Bounds = new System.Drawing.Rectangle(80, screen.Height / 2 - 160, Math.Max(400, screen.Width - 160), 56),
            };
            _body = new System.Windows.Forms.Label
            {
                Text = "This PC stays on this screen until blocking is on.",
                Font = new System.Drawing.Font("Segoe UI", 14f),
                ForeColor = System.Drawing.Color.FromArgb(203, 213, 225),
                AutoSize = false,
                TextAlign = System.Drawing.ContentAlignment.TopCenter,
                Bounds = new System.Drawing.Rectangle(120, screen.Height / 2 - 90, Math.Max(400, screen.Width - 240), 80),
            };
            _solution = new System.Windows.Forms.Label
            {
                Text = "",
                Font = new System.Drawing.Font("Segoe UI", 13f),
                ForeColor = System.Drawing.Color.FromArgb(253, 224, 71),
                AutoSize = false,
                TextAlign = System.Drawing.ContentAlignment.TopCenter,
                Bounds = new System.Drawing.Rectangle(120, screen.Height / 2 + 0, Math.Max(400, screen.Width - 240), 120),
                Visible = false,
            };
            _retry = new System.Windows.Forms.Button
            {
                Text = "Retry",
                Font = new System.Drawing.Font("Segoe UI", 12f, System.Drawing.FontStyle.Bold),
                FlatStyle = System.Windows.Forms.FlatStyle.Flat,
                BackColor = System.Drawing.Color.FromArgb(37, 99, 235),
                ForeColor = System.Drawing.Color.White,
                Size = new System.Drawing.Size(160, 42),
                Visible = false,
                Cursor = System.Windows.Forms.Cursors.Hand,
            };
            _retry.Location = new System.Drawing.Point((screen.Width - _retry.Width) / 2, screen.Height / 2 + 140);
            _retry.FlatAppearance.BorderSize = 0;
            _retry.Click += async (_, _) =>
            {
                _retry.Enabled = false;
                _body.Text = "Retrying…";
                try
                {
                    await DlpPipeClient.SendAsync(
                        new { op = "RequestPolicyPull" },
                        connectTimeoutMs: 2000,
                        responseTimeoutMs: 4000);
                }
                catch
                {
                    /* the next poll shows the pipe error */
                }
                _retry.Enabled = true;
            };

            Controls.Add(_title);
            Controls.Add(_body);
            Controls.Add(_solution);
            Controls.Add(_retry);

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (_, _) => _ = PollAsync();
            Shown += (_, _) =>
            {
                _timer.Start();
                _ = PollAsync();
            };
            FormClosing += (_, e) =>
            {
                if (!_allowClose)
                    e.Cancel = true;
            };
        }

        private async Task PollAsync()
        {
            if (Interlocked.Exchange(ref _pollBusy, 1) == 1)
                return;
            try
            {
                if (_ct.IsCancellationRequested)
                {
                    CloseReady();
                    return;
                }

                var (connected, body) = await DlpPipeClient.SendAsync(
                    new { op = "GetEnforceStatus" },
                    connectTimeoutMs: 2000,
                    responseTimeoutMs: 4000);

                if (!connected || string.IsNullOrWhiteSpace(body))
                {
                    ShowFailed(
                        "The security service is not running. Start " + AppInfo.WindowsServiceName +
                        ", then choose Retry.");
                    return;
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err) &&
                    err.GetString() is "dlp_disabled")
                {
                    ShowFailed(
                        "Security policy is switched off (ALPHA_DLP_ENABLED). Turn it on and restart " +
                        AppInfo.WindowsServiceName + ".");
                    return;
                }

                var phase = root.TryGetProperty("phase", out var phaseEl) ? phaseEl.GetString() ?? "" : "";
                var rebootArmed = root.TryGetProperty("rebootArmed", out var armedEl) &&
                                   armedEl.ValueKind == JsonValueKind.True;
                var solution = root.TryGetProperty("solution", out var solEl) ? solEl.GetString() ?? "" : "";
                var rebootError = root.TryGetProperty("rebootError", out var reEl) &&
                                   reEl.ValueKind == JsonValueKind.String
                    ? reEl.GetString() ?? ""
                    : "";

                if (phase == "enforcing" && !rebootArmed && string.IsNullOrEmpty(rebootError))
                {
                    CloseReady();
                    return;
                }

                if (!string.IsNullOrEmpty(rebootError))
                {
                    _title.Text = "Restart did not start";
                    _body.Text = "Blocking is on. Windows did not accept the restart.";
                    ShowSolution(solution);
                    Cursor = System.Windows.Forms.Cursors.Default;
                    return;
                }

                if (phase is "reboot_queued" || (phase == "enforcing" && rebootArmed))
                {
                    _title.Text = "Policy is on";
                    _body.Text = "Restarting to lock it. Stay on this screen.";
                    _solution.Visible = false;
                    _retry.Visible = false;
                    Cursor = System.Windows.Forms.Cursors.WaitCursor;
                    return;
                }

                if (phase == "failed")
                {
                    _title.Text = "Security policy did not apply";
                    _body.Text = "Blocking is not on yet. Fix the problem below, then choose Retry.";
                    ShowSolution(string.IsNullOrWhiteSpace(solution)
                        ? "Choose Retry. If it keeps failing, restart " + AppInfo.WindowsServiceName + "."
                        : solution);
                    Cursor = System.Windows.Forms.Cursors.Default;
                    return;
                }

                _title.Text = "Applying security policy";
                _body.Text = "This PC stays on this screen until blocking is on.";
                _solution.Visible = false;
                _retry.Visible = false;
                Cursor = System.Windows.Forms.Cursors.WaitCursor;
            }
            catch
            {
                ShowFailed(
                    "The security service is not running. Start " + AppInfo.WindowsServiceName +
                    ", then choose Retry.");
            }
            finally
            {
                Interlocked.Exchange(ref _pollBusy, 0);
            }
        }

        private void ShowFailed(string solution)
        {
            _title.Text = "Security policy did not apply";
            _body.Text = "Blocking is not on yet.";
            ShowSolution(solution);
            Cursor = System.Windows.Forms.Cursors.Default;
        }

        private void ShowSolution(string solution)
        {
            _solution.Text = solution;
            _solution.Visible = true;
            _retry.Visible = true;
        }

        private void CloseReady()
        {
            _allowClose = true;
            _timer.Stop();
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
#endif
