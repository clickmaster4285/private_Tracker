using System.Text.Json;
using client.Core;

namespace client.Services;

/// <summary>
/// Shows one dialog when the service kills a process because AppLocker is not enforcing.
/// While AppLocker verify is good, the service records nothing and this stays quiet
/// so Windows' own "blocked by your organization" dialog is the only prompt.
/// </summary>
public static class AppBlockNotice
{
    private static int _started;

    public static void Start()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        var thread = new Thread(Loop);
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Name = "app-block-notice";
        thread.Start();
    }

    private static void Loop()
    {
#if FACE_WINFORMS_LOCKER
        string? seen = null;
        var marker = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppInfo.DataFolderName,
            "last-app-block.txt");
        while (true)
        {
            Thread.Sleep(1000);
            try
            {
                var notice = Poll();
                if (notice is null || notice.Value.Id == seen)
                    continue;
                if (AlreadyShown(marker, notice.Value.Id))
                {
                    seen = notice.Value.Id;
                    continue;
                }
                using var mutex = new Mutex(false, "Local\\AlphaAITracker.AppBlockNotice");
                var owned = false;
                try
                {
                    owned = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }
                if (!owned)
                    continue;
                try
                {
                    if (AlreadyShown(marker, notice.Value.Id))
                    {
                        seen = notice.Value.Id;
                        continue;
                    }
                    MarkShown(marker, notice.Value.Id);
                    seen = notice.Value.Id;
                    using var dialog = new BlockDialog(notice.Value.Name);
                    dialog.ShowDialog();
                }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch { /* ignore */ }
                }
            }
            catch
            {
                /* keep polling */
            }
        }
#else
        return;
#endif
    }

#if FACE_WINFORMS_LOCKER
    private readonly record struct Notice(string Id, string Name);

    private static Notice? Poll()
    {
        var (connected, body) = DlpPipeClient.SendAsync(
            new { op = "GetEnforceStatus" },
            connectTimeoutMs: 1500,
            responseTimeoutMs: 3000).GetAwaiter().GetResult();
        if (!connected || string.IsNullOrWhiteSpace(body))
            return null;
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("lastAppBlock", out var block) ||
            block.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return new Notice(id, string.IsNullOrWhiteSpace(name) ? "This app" : name);
    }

    private static bool AlreadyShown(string path, string id)
    {
        try
        {
            return File.Exists(path) &&
                   string.Equals(File.ReadAllText(path).Trim(), id, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void MarkShown(string path, string id)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, id);
        }
        catch
        {
            /* the in-memory id still suppresses repeats in this process */
        }
    }

    private sealed class BlockDialog : System.Windows.Forms.Form
    {
        public BlockDialog(string appName)
        {
            Text = "Blocked by your organization";
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new System.Drawing.Size(460, 210);
            BackColor = System.Drawing.Color.White;
            Font = new System.Drawing.Font("Segoe UI", 10f);

            var title = new System.Windows.Forms.Label
            {
                Text = appName + " is blocked",
                Font = new System.Drawing.Font("Segoe UI", 14f, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.FromArgb(15, 23, 42),
                AutoSize = false,
                Location = new System.Drawing.Point(20, 18),
                Size = new System.Drawing.Size(420, 32),
            };
            var body = new System.Windows.Forms.Label
            {
                Text = appName + " is not on the application allowlist.\r\n\r\n" +
                       "An administrator can allow it under Allowlists → Apps.",
                ForeColor = System.Drawing.Color.FromArgb(51, 65, 85),
                AutoSize = false,
                Location = new System.Drawing.Point(20, 58),
                Size = new System.Drawing.Size(420, 80),
            };
            var ok = new System.Windows.Forms.Button
            {
                Text = "OK",
                DialogResult = System.Windows.Forms.DialogResult.OK,
                Location = new System.Drawing.Point(340, 156),
                Size = new System.Drawing.Size(96, 32),
            };
            AcceptButton = ok;
            Controls.Add(title);
            Controls.Add(body);
            Controls.Add(ok);
        }
    }
#endif
}
