using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using client.ViewModels;
using client.Views;
#if FACE_WINFORMS_LOCKER
using client.Services.FaceVerify.WinForms;
#endif

namespace client.Services.FaceVerify;

/// <summary>
/// Shows a progress UI while face ONNX models download (tracker warm-up).
/// Prefers an Avalonia dialog when the GUI is alive; otherwise a WinForms window
/// on Windows (works from --background).
/// </summary>
public static class FaceModelDownloadUiHost
{
    private static int _showing;

    /// <summary>
    /// Runs <paramref name="download"/> while a progress window is visible when models
    /// actually need downloading. No-ops the UI when files are already present.
    /// </summary>
    public static async Task RunWithUiAsync(Func<IProgress<FaceDownloadProgress>, Task> download, CancellationToken ct)
    {
        var needUi = ModelsNeedDownload();
        IDisposable? ui = null;
        try
        {
            if (needUi)
                ui = TryShow();
            await download(FaceDownloadStatus.CreateProgressSink());
        }
        finally
        {
            ui?.Dispose();
            if (FaceDownloadStatus.Current is { IsFailed: false })
                FaceDownloadStatus.Clear();
        }
    }

    public static bool ModelsNeedDownload()
    {
        try
        {
            var needDet = !File.Exists(FacePaths.DetPath) || new FileInfo(FacePaths.DetPath).Length < 100_000;
            var needRec = !(File.Exists(FacePaths.RecPath) && new FileInfo(FacePaths.RecPath).Length > 1_000_000);
            return needDet || needRec;
        }
        catch
        {
            return true;
        }
    }

    private static IDisposable? TryShow()
    {
        if (Interlocked.CompareExchange(ref _showing, 1, 0) != 0)
            return null;

        try
        {
            if (TryShowAvalonia(out var avalonia))
                return avalonia;

#if FACE_WINFORMS_LOCKER
            if (OperatingSystem.IsWindows())
                return ShowWinForms();
#endif
        }
        catch
        {
            Interlocked.Exchange(ref _showing, 0);
            throw;
        }

        Interlocked.Exchange(ref _showing, 0);
        return null;
    }

    private static bool TryShowAvalonia(out IDisposable? handle)
    {
        handle = null;
        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime)
                return false;
            if (!Dispatcher.UIThread.CheckAccess() && Application.Current == null)
                return false;

            var tcs = new TaskCompletionSource<FaceModelDownloadWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var w = new FaceModelDownloadWindow();
                    w.Attach();
                    w.Show();
                    tcs.TrySetResult(w);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            // Don't block forever if UI thread is parked.
            if (!tcs.Task.Wait(TimeSpan.FromSeconds(3)))
                return false;

            var win = tcs.Task.Result;
            handle = new ActionDisposable(() =>
            {
                try
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (win.DataContext is FaceModelDownloadViewModel vm && !vm.CanClose)
                                vm.Apply(FaceDownloadProgress.Complete());
                            win.Close();
                        }
                        catch { /* ignore */ }
                        finally { Interlocked.Exchange(ref _showing, 0); }
                    });
                }
                catch { Interlocked.Exchange(ref _showing, 0); }
            });
            return true;
        }
        catch
        {
            handle = null;
            return false;
        }
    }

#if FACE_WINFORMS_LOCKER
    [SupportedOSPlatform("windows")]
    private static IDisposable ShowWinForms()
    {
        var formReady = new ManualResetEventSlim(false);
        FaceModelDownloadForm? form = null;
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
                System.Windows.Forms.Application.EnableVisualStyles();
                form = new FaceModelDownloadForm();
                form.Attach();
                formReady.Set();
                System.Windows.Forms.Application.Run(form);
            }
            catch
            {
                formReady.Set();
            }
            finally
            {
                Interlocked.Exchange(ref _showing, 0);
            }
        })
        {
            IsBackground = true,
            Name = "FaceModelDownloadUi",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        formReady.Wait(TimeSpan.FromSeconds(5));

        return new ActionDisposable(() =>
        {
            try
            {
                var f = form;
                if (f == null || f.IsDisposed) return;
                if (f.InvokeRequired)
                    f.BeginInvoke(new Action(() => { try { f.AllowCloseAndClose(); } catch { /* ignore */ } }));
                else
                    f.AllowCloseAndClose();
            }
            catch { /* ignore */ }
        });
    }
#endif

    private sealed class ActionDisposable(Action action) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            try { action(); } catch { /* ignore */ }
        }
    }
}
