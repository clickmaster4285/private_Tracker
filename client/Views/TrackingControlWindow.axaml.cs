using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using client.ViewModels;

namespace client.Views;

public partial class TrackingControlWindow : Window
{
    // Target / clamp fractions of primary working area (DIP).
    private const double WidthTargetFrac = 0.11;
    private const double HeightTargetFrac = 0.10;
    private const double WidthMinFrac = 0.08;
    private const double WidthMaxFrac = 0.14;
    private const double HeightMinFrac = 0.040;
    private const double HeightMaxFrac = 0.070;

    // Reference band at 1680×1050 → 168×52.5; all type/padding scales from current size.
    private const double RefWidth = 168.0;
    private const double RefHeight = 52.5;

    public TrackingControlWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        // Do NOT stop timers on Closed/Hide — process stays in tray and must keep counting.
        PropertyChanged += OnWindowPropertyChanged;
    }

    /// <summary>Hide to tray without stopping the working timer.</summary>
    public void HideToTray()
    {
        try
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Hide();
        }
        catch { /* ignore */ }
    }

    /// <summary>Recompute size + typography from the current primary screen.</summary>
    public void ApplyResponsiveLayout()
    {
        try
        {
            var screen = Screens?.Primary ?? Screens?.All.FirstOrDefault();
            double dipW = 1680;
            double dipH = 1050;
            if (screen is not null)
            {
                var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
                var w = screen.WorkingArea.Width / scale;
                var h = screen.WorkingArea.Height / scale;
                if (w > 100 && h > 100)
                {
                    dipW = w;
                    dipH = h;
                }
            }

            // Screen-relative min / max / target (10% × 5%).
            var minW = dipW * WidthMinFrac;
            var maxW = dipW * WidthMaxFrac;
            var minH = dipH * HeightMinFrac;
            var maxH = dipH * HeightMaxFrac;
            if (minW > maxW) (minW, maxW) = (maxW, minW);
            if (minH > maxH) (minH, maxH) = (maxH, minH);

            var targetW = Clamp(dipW * WidthTargetFrac, minW, maxW);
            var targetH = Clamp(dipH * HeightTargetFrac, minH, maxH);

            // Keep content usable on very small / very large displays without breaking the mock.
            targetW = Clamp(targetW, 200, Math.Max(200, maxW));
            targetH = Clamp(targetH, 100, Math.Max(100, maxH));
            minW = Math.Min(minW, targetW);
            maxW = Math.Max(maxW, targetW);
            minH = Math.Min(minH, targetH);
            maxH = Math.Max(maxH, targetH);

            Width = targetW;
            Height = targetH;
            MinWidth = minW;
            MaxWidth = maxW;
            MinHeight = minH;
            MaxHeight = maxH;

            var sx = targetW / RefWidth;
            var sy = targetH / RefHeight;
            var s = Clamp(Math.Sqrt(sx * sy), 0.85, 1.6);

            var padX = Clamp(targetW * 0.024, 2, 10);
            var padTop = Clamp(targetH * 0.04, 1, 6);
            var padBottom = Clamp(targetH * 0.075, 2, 8);
            var gap = Clamp(targetW * 0.036, 4, 12);

            var timerFont = Clamp(18 * s, 12, 26);
            var btnFont = Clamp(11 * s, 9, 14);
            var captionFont = Clamp(13 * s, 11, 15);
            var closeFont = Clamp(14 * s, 12, 16);
            // Compact hit boxes (larger than 14px, still tighter than the old 28px gap).
            var captionW = Clamp(18 * s, 16, 22);
            var captionH = Clamp(18 * s, 16, 22);
            var btnH = Clamp(22 * s, 18, 28);
            var btnMinW = Clamp(56 * s, 48, 80);
            var btnPadX = Clamp(8 * s, 6, 12);
            const double cardRadius = 4;
            const double actionBtnRadius = 2;
            var letter = Clamp(1 * s, 0, 2);

            if (RootBorder is not null)
                RootBorder.CornerRadius = new CornerRadius(cardRadius);

            // Tuck ─ / × into the top-right corner (small top + right inset).
            if (CaptionButtons is not null)
            {
                CaptionButtons.Margin = new Thickness(0, 1, 2, 0);
                CaptionButtons.Spacing = 2;
            }

            if (TimerRow is not null)
                TimerRow.Margin = new Thickness(0);

            if (TimerText is not null)
            {
                TimerText.FontSize = timerFont;
                TimerText.LetterSpacing = letter;
            }

            if (TimerSecondsText is not null)
            {
                // Seconds (~2/3 of HH:MM) so the trailing :SS reads quieter.
                TimerSecondsText.FontSize = Clamp(timerFont * 0.67, 9, 18);
                TimerSecondsText.LetterSpacing = 0;
                TimerSecondsText.Margin = new Thickness(0, Math.Max(1, timerFont * 0.12), 0, 0);
            }

            if (MinimizeButton is not null)
            {
                MinimizeButton.Width = captionW;
                MinimizeButton.Height = captionH;
                MinimizeButton.FontSize = captionFont;
            }

            if (CloseButton is not null)
            {
                CloseButton.Width = captionW;
                CloseButton.Height = captionH;
                CloseButton.FontSize = closeFont;
            }

            // Mirror caption column width so the timer stays centered with Start/Stop.
            if (CaptionSpacer is not null && CaptionButtons is not null)
            {
                var capW = captionW * 2 + CaptionButtons.Spacing + CaptionButtons.Margin.Right;
                CaptionSpacer.Width = Math.Max(1, capW);
            }

            if (ActionRow is not null)
            {
                ActionRow.Spacing = gap;
                // Equal L/R margin — asymmetric pad was shifting Start/Stop left.
                ActionRow.Margin = new Thickness(padX, 0, padX, padBottom);
            }

            ApplyActionButton(StartButton, btnMinW, btnH, btnPadX, btnFont, actionBtnRadius);
            ApplyActionButton(StopButton, btnMinW, btnH, btnPadX, btnFont, actionBtnRadius);

            try
            {
                var log = Path.Combine(AppContext.BaseDirectory, "tracking-ui.log");
                File.AppendAllText(log,
                    $"{DateTime.Now:HH:mm:ss} size={targetW:0}x{targetH:0} min={minW:0}x{minH:0} max={maxW:0}x{maxH:0} s={s:0.00} dip={dipW:0}x{dipH:0}{Environment.NewLine}");
            }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            Width = RefWidth;
            Height = RefHeight;
            MinWidth = 140;
            MaxWidth = 280;
            MinHeight = 48;
            MaxHeight = 90;
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "tracking-ui.log"),
                    $"{DateTime.Now:HH:mm:ss} ApplyResponsiveLayout FAIL: {ex}{Environment.NewLine}");
            }
            catch { /* ignore */ }
        }
    }

    private static void ApplyActionButton(Button? btn, double minW, double h, double padX, double font, double radius)
    {
        if (btn is null) return;
        // Fixed equal Width so Start/Stop stay symmetric (MinWidth alone can look off-center).
        btn.Width = minW;
        btn.MinWidth = minW;
        btn.Height = h;
        btn.Padding = new Thickness(0);
        btn.FontSize = font;
        btn.CornerRadius = new CornerRadius(radius);
    }

    private static double Clamp(double v, double min, double max) =>
        v < min ? min : (v > max ? max : v);

    private void OnCaptionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Interactive)
        {
            for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
            {
                if (ReferenceEquals(v, MinimizeButton) || ReferenceEquals(v, CloseButton) || v is Button)
                    return;
            }
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            try { BeginMoveDrag(e); }
            catch { /* ignore */ }
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HideToTray();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HideToTray();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty) return;
        if (WindowState != WindowState.Minimized) return;
        Dispatcher.UIThread.Post(HideToTray, DispatcherPriority.Background);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyResponsiveLayout();
        if (DataContext is TrackingControlViewModel vm)
            vm.Start();
    }
}
