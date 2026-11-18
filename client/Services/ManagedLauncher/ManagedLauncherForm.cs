#if FACE_WINFORMS_LOCKER
using System.Drawing;
using System.Windows.Forms;

namespace client.Services.ManagedLauncher;

internal static class ManagedLauncherForm
{
    public static ManagedLauncherHost.ManagedEntry? ShowPicker(
        IReadOnlyList<ManagedLauncherHost.ManagedEntry> entries)
    {
        using var form = new Form
        {
            Text = "Alpha AI Tracker — Managed Apps",
            Width = 480,
            Height = 420,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            TopMost = true,
        };

        var label = new Label
        {
            Text = "Choose an approved application to open:",
            Dock = DockStyle.Top,
            Height = 36,
            Padding = new Padding(12, 10, 12, 0),
        };
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10f),
        };
        foreach (var e in entries)
            list.Items.Add(e);

        list.DisplayMember = nameof(ManagedLauncherHost.ManagedEntry.Name);
        ManagedLauncherHost.ManagedEntry? selected = null;
        var ok = new Button
        {
            Text = "Open",
            Dock = DockStyle.Bottom,
            Height = 40,
            DialogResult = DialogResult.OK,
        };
        ok.Click += (_, _) =>
        {
            selected = list.SelectedItem as ManagedLauncherHost.ManagedEntry;
            form.DialogResult = DialogResult.OK;
            form.Close();
        };
        list.DoubleClick += (_, _) => ok.PerformClick();

        form.Controls.Add(list);
        form.Controls.Add(ok);
        form.Controls.Add(label);
        form.AcceptButton = ok;

        return form.ShowDialog() == DialogResult.OK ? selected : null;
    }
}
#endif
