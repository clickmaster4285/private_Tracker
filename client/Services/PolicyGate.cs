using System.Text.Json;
using client.Core;

namespace client.Services;

/// <summary>
/// Full-screen gate after login. Stays up until the security service reports every
/// blocker enforcing. When a post-login reboot is armed, it stays up through the restart.
/// Lab / local development: set <c>ALPHA_DLP_POLICY_GATE=false</c> to skip the UI
/// (DLP service enforcement is unchanged). Ship default is on.
/// </summary>
public static class PolicyGate
{
    /// <summary>
    /// Ship default ON. <c>false</c> / <c>0</c> skips the topmost gate window only.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("ALPHA_DLP_POLICY_GATE");
            if (string.IsNullOrWhiteSpace(raw))
                return true;
            return raw is not ("0" or "false" or "False" or "FALSE");
        }
    }

    public static Task WaitAsync(CancellationToken ct = default)
    {
        // Defer AppBlockNotice until the gate closes — competing GetEnforceStatus
        // calls starve the pipe during AppLocker Apply and caused false pipe_timeout.
        if (!OperatingSystem.IsWindows() || !IsEnabled)
        {
            AppBlockNotice.Start();
            return Task.CompletedTask;
        }

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
            finally
            {
                AppBlockNotice.Start();
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
                    Post(CloseReady);
                    return;
                }

                DlpPipeClient.PipeCallResult call;
                try
                {
                    // Longer response budget while Applying — never treat timeout as Enforcing.
                    call = await DlpPipeClient.SendDetailedAsync(
                        new { op = "GetEnforceStatus" },
                        pipeName: AppInfo.WindowsDlpPipeName,
                        connectTimeoutMs: 4000,
                        responseTimeoutMs: 20_000);
                }
                catch (Exception ex)
                {
                    call = new DlpPipeClient.PipeCallResult(false, false, "", ex.Message);
                }

                var scm = "unknown";
                try
                {
                    scm = WindowsScmNative.QueryServiceState(AppInfo.WindowsServiceName);
                }
                catch
                {
                    scm = "query_failed";
                }

                Post(() => ApplyCall(call, scm));
            }
            finally
            {
                Interlocked.Exchange(ref _pollBusy, 0);
            }
        }

        /// <summary>Runs on the window thread. A thrown UI update must not be relabeled.</summary>
        private void Post(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated)
                    return;
                if (InvokeRequired)
                    BeginInvoke(action);
                else
                    action();
            }
            catch (InvalidOperationException)
            {
                /* closing */
            }
        }

        private void ApplyCall(DlpPipeClient.PipeCallResult call, string scm)
        {
            // pipe_timeout is NEVER Enforcing. While the service is RUNNING, stay on
            // Applying and keep polling — do not auto-close the desktop.
            if (call.TimedOut ||
                (!call.Connected && string.Equals(call.Error, "pipe_timeout", StringComparison.OrdinalIgnoreCase)))
            {
                if (scm is "RUNNING" or "START_PENDING" or "CONTINUE_PENDING")
                {
                    _title.Text = "Applying security policy";
                    _body.Text = "Waiting for " + AppInfo.WindowsServiceName +
                                 " (pipe busy). This screen stays until blocking is verified.";
                    _solution.Visible = false;
                    _retry.Visible = false;
                    Cursor = System.Windows.Forms.Cursors.WaitCursor;
                    return;
                }
                ShowFailed(PipeProblem(call, scm));
                return;
            }

            if (!call.Connected || string.IsNullOrWhiteSpace(call.Body))
            {
                ShowFailed(PipeProblem(call, scm));
                return;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(call.Body);
            }
            catch (Exception ex)
            {
                ShowFailed(ServiceIsRunning(scm)
                    + " The reply was not valid (" + ex.Message + ").");
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var error = root.TryGetProperty("error", out var errEl) &&
                            errEl.ValueKind == JsonValueKind.String
                    ? errEl.GetString() ?? ""
                    : "";
                if (error == "dlp_disabled")
                {
                    ShowFailed(
                        "Security policy is switched off (ALPHA_DLP_ENABLED). Turn it on and restart " +
                        AppInfo.WindowsServiceName + ".");
                    return;
                }
                if (error == "unknown_op")
                {
                    ShowFailed(
                        AppInfo.WindowsServiceName +
                        " is running, but it is an older build and does not report policy status. Reinstall the current setup.");
                    return;
                }
                if (error.Length > 0 && !root.TryGetProperty("phase", out _))
                {
                    ShowFailed(ServiceIsRunning(scm) + " Pipe error: " + error + ".");
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

                if (phase == "recovery_required")
                {
                    _title.Text = "Recovery required";
                    _body.Text = "Blocking stays on. This screen does not close.";
                    ShowSolution(string.IsNullOrWhiteSpace(solution)
                        ? "A valid single-use recovery authorization is required to turn blocking off, or Secure Boot must be on before signed policy tamper resistance can be claimed."
                        : solution);
                    Cursor = System.Windows.Forms.Cursors.Default;
                    return;
                }

                if (phase == "verifying")
                {
                    _title.Text = "Verifying security policy";
                    _body.Text = "Checking the firewall, network, and application control. This screen stays until those checks pass.";
                    _solution.Visible = false;
                    _retry.Visible = false;
                    Cursor = System.Windows.Forms.Cursors.WaitCursor;
                    return;
                }

                if (phase == "failed")
                {
                    _title.Text = "Security policy did not apply";
                    _body.Text = "Blocking is not verified. Fix the problem below, then choose Retry.";
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
        }

        private static string PipeProblem(DlpPipeClient.PipeCallResult call, string scm)
        {
            var err = string.IsNullOrWhiteSpace(call.Error) ? "no reply" : call.Error;
            var pipe = AppInfo.WindowsDlpPipeName;
            var svc = AppInfo.WindowsServiceName;
            if (scm == "RUNNING")
                return svc + " is running, but this window cannot reach pipe " + pipe +
                       " (" + err + "). Restart the service.";
            if (scm.Contains("OpenService:5", StringComparison.Ordinal))
                return "Cannot reach pipe " + pipe + " (" + err + "). " +
                       "Windows denied a status check for this account (access denied).";
            if (scm is "STOPPED" or "STOP_PENDING")
                return svc + " is stopped, so blocking cannot be applied (" + err + ").";
            if (scm == "absent")
                return svc + " is not installed. Run the Windows setup again.";
            if (scm is "START_PENDING" or "CONTINUE_PENDING")
                return svc + " is still starting. This window will keep trying (" + err + ").";
            return "Cannot reach pipe " + pipe + " (" + err + "). Service status: " + scm + ".";
        }

        private static string ServiceIsRunning(string scm) =>
            scm == "RUNNING"
                ? AppInfo.WindowsServiceName + " is running."
                : AppInfo.WindowsServiceName + " status: " + scm + ".";

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
