using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GameTranslate.Models;
using GameTranslate.Native;

namespace GameTranslate.Windows;

public partial class SubtitleOverlayWindow : Window
{
    private IntPtr _hwnd;
    private readonly DispatcherTimer _topmostTimer;
    private bool _isEditMode;
    private bool _isUserHidden;
    private bool _hasActiveText;
    private double _configuredFontSize = 30;

    public bool IsEditMode => _isEditMode;
    public bool IsUserHidden => _isUserHidden;
    public bool IsExcludedFromCaptureActive { get; private set; }

    public event Action<ScreenRect>? PositionLocked;

    public SubtitleOverlayWindow()
    {
        InitializeComponent();

        _topmostTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _topmostTimer.Tick += (_, _) =>
        {
            if (_hwnd != IntPtr.Zero && IsVisible && !_isEditMode)
            {
                Win32.EnsureTopmostNoActivate(_hwnd);
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Win32.SetOverlayWindowStyles(_hwnd, clickThrough: true, noActivate: true);
        _topmostTimer.Start();
    }

    public void ApplySettings(AppSettings settings)
    {
        try
        {
            SubtitleTextBlock.FontFamily = new FontFamily(settings.FontFamily);
        }
        catch
        {
            SubtitleTextBlock.FontFamily = new FontFamily("Arial");
        }

        _configuredFontSize = Math.Clamp(settings.FontSize, 14, 72);
        SubtitleTextBlock.FontSize = _configuredFontSize;
        SubtitleTextBlock.FontWeight = settings.FontBold ? FontWeights.Bold : FontWeights.Normal;
        SubtitleTextBlock.FontStyle = settings.FontItalic ? FontStyles.Italic : FontStyles.Normal;
        SubtitleTextBlock.StrokeThickness = Math.Clamp(settings.OutlineThickness, 0, 12);
        SubtitleTextBlock.DropShadowEnabled = settings.DropShadowEnabled;

        SubtitleTextBlock.Fill = ParseBrush(settings.SubtitleColorHex, Color.FromRgb(0xFF, 0xD8, 0x00));
        SubtitleTextBlock.Stroke = ParseBrush(settings.OutlineColorHex, Colors.Black);

        byte backdropAlpha = (byte)Math.Round(Math.Clamp(settings.BackdropOpacity, 0.0, 1.0) * 255.0);
        BackdropBorder.Background = new SolidColorBrush(Color.FromArgb(backdropAlpha, 0, 0, 0));
        BackdropBorder.CornerRadius = new CornerRadius(Math.Clamp(settings.BackdropCornerRadius, 0, 24));
        BackdropBorder.Padding = backdropAlpha > 5 ? new Thickness(20, 8, 20, 8) : new Thickness(4, 2, 4, 2);

        OriginalTextBlock.Visibility = settings.ShowOriginalTextSubtle && !string.IsNullOrWhiteSpace(OriginalTextBlock.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Apply WDA_EXCLUDEFROMCAPTURE so our own BitBlt OCR sees right through the overlay
        if (_hwnd != IntPtr.Zero)
        {
            IsExcludedFromCaptureActive = Win32.TrySetExcludeFromCapture(_hwnd, settings.ExcludeOverlayFromCapture);
        }

        // Compute overlay position cleanly based on mode
        ScreenRect target = settings.OverlayPosition switch
        {
            OverlayPositionMode.BottomOfScreen => Win32.GetBottomOfMonitorSubtitleRect(settings.CaptureRegion),
            OverlayPositionMode.BottomOfCaptureRegion => Win32.GetBottomOfRegionSubtitleRect(settings.CaptureRegion),
            OverlayPositionMode.OverCaptureRegion => settings.CaptureRegion,
            OverlayPositionMode.CustomFree => settings.OverlayRegion.IsValid
                ? settings.OverlayRegion
                : Win32.GetBottomOfMonitorSubtitleRect(settings.CaptureRegion),
            _ => Win32.GetBottomOfMonitorSubtitleRect(settings.CaptureRegion)
        };

        // Anchor to bottom for cinema-style bottom modes; center only when explicitly placed directly over a small subtitle box
        SubtitleContainer.VerticalAlignment = settings.OverlayPosition == OverlayPositionMode.OverCaptureRegion
            ? VerticalAlignment.Center
            : VerticalAlignment.Bottom;

        if (target.IsValid && !_isEditMode)
        {
            SetPhysicalBounds(target);
        }

        if (settings.AlwaysKeepOnTopReinforce)
        {
            if (!_topmostTimer.IsEnabled) _topmostTimer.Start();
        }
        else
        {
            _topmostTimer.Stop();
        }
    }

    public void SetPhysicalBounds(ScreenRect rect)
    {
        if (!rect.IsValid)
            return;

        if (_hwnd == IntPtr.Zero)
        {
            var helper = new WindowInteropHelper(this);
            _hwnd = helper.EnsureHandle();
        }

        int minHeight = Math.Max(rect.Height, 90);
        Win32.MoveWindow(_hwnd, rect.X, rect.Y, rect.Width, minHeight, true);
    }

    public ScreenRect GetPhysicalBounds()
    {
        if (_hwnd != IntPtr.Zero && Win32.GetWindowRect(_hwnd, out Win32.RECT r))
        {
            return new ScreenRect
            {
                X = r.Left,
                Y = r.Top,
                Width = Math.Max(60, r.Right - r.Left),
                Height = Math.Max(40, r.Bottom - r.Top)
            };
        }
        return new ScreenRect { X = (int)Left, Y = (int)Top, Width = (int)ActualWidth, Height = (int)ActualHeight };
    }

    public void ShowSubtitle(string translatedText, string? originalText, bool showOriginal)
    {
        if (_isEditMode)
            return;

        // Dynamically auto-fit font size against real available DIP width & height so long multi-line
        // or word-wrapped RPG dialogues NEVER clip at the top, bottom, or sides!
        SubtitleTextBlock.FontSize = ComputeAutoFitFontSize(translatedText, showOriginal && !string.IsNullOrWhiteSpace(originalText));

        SubtitleTextBlock.Text = translatedText;
        OriginalTextBlock.Text = originalText ?? string.Empty;
        OriginalTextBlock.Visibility = showOriginal && !string.IsNullOrWhiteSpace(originalText)
            ? Visibility.Visible
            : Visibility.Collapsed;

        _hasActiveText = !string.IsNullOrWhiteSpace(translatedText);

        if (_isUserHidden)
            return;

        if (!IsVisible)
            Show();

        // Zero-flicker instant swap if subtitle is already visible on screen; smooth fade-in only when appearing
        if (_hasActiveText && SubtitleContainer.Opacity >= 0.90)
        {
            SubtitleContainer.BeginAnimation(OpacityProperty, null);
            SubtitleContainer.Opacity = 1.0;
        }
        else
        {
            AnimateOpacity(SubtitleContainer, _hasActiveText ? 1.0 : 0.0, 90);
        }

        if (_hwnd != IntPtr.Zero)
            Win32.EnsureTopmostNoActivate(_hwnd);
    }

    internal double ComputeAutoFitFontSize(string text, bool hasOriginalHeader)
    {
        if (string.IsNullOrWhiteSpace(text))
            return _configuredFontSize;

        double maxContentWidth = (ActualWidth > 120 ? ActualWidth : 1000) - 64;
        double maxContentHeight = (ActualHeight > 60 ? ActualHeight : 320) - (hasOriginalHeader ? 56 : 32);

        double size = _configuredFontSize;
        const double minFontSize = 14.0;

        while (size > minFontSize)
        {
            Size measured = SubtitleTextBlock.MeasureTextForFontSize(text, maxContentWidth, size);
            if (measured.Height <= maxContentHeight && measured.Width <= maxContentWidth)
                break;

            size -= 2.0;
        }

        return Math.Max(minFontSize, size);
    }

    public void ClearSubtitle()
    {
        if (_isEditMode)
            return;

        _hasActiveText = false;
        AnimateOpacity(SubtitleContainer, 0.0, 180);
    }

    public void SetUserHidden(bool hidden)
    {
        _isUserHidden = hidden;
        if (_isEditMode)
            return;

        if (hidden)
        {
            AnimateOpacity(SubtitleContainer, 0.0, 100);
        }
        else if (_hasActiveText)
        {
            if (!IsVisible) Show();
            AnimateOpacity(SubtitleContainer, 1.0, 110);
        }
    }

    public void SetEditMode(bool enabled, string sampleText = "Por favor, dê um presente.\nBondade  •  Mente  •  Ambição  •  Bravura  •  Voz")
    {
        _isEditMode = enabled;
        if (!IsVisible)
            Show();

        if (_hwnd == IntPtr.Zero)
            _hwnd = new WindowInteropHelper(this).EnsureHandle();

        if (enabled)
        {
            Win32.SetOverlayWindowStyles(_hwnd, clickThrough: false, noActivate: false);
            EditFrame.Visibility = Visibility.Visible;
            SubtitleTextBlock.FontSize = _configuredFontSize;
            SubtitleTextBlock.Text = sampleText;
            OriginalTextBlock.Visibility = Visibility.Collapsed;
            SubtitleContainer.BeginAnimation(OpacityProperty, null);
            SubtitleContainer.Opacity = 1.0;
            Activate();
        }
        else
        {
            EditFrame.Visibility = Visibility.Collapsed;
            Win32.SetOverlayWindowStyles(_hwnd, clickThrough: true, noActivate: true);
            ScreenRect finalBounds = GetPhysicalBounds();

            if (!_hasActiveText)
            {
                SubtitleTextBlock.Text = string.Empty;
                SubtitleContainer.Opacity = 0.0;
            }

            PositionLocked?.Invoke(finalBounds);
        }
    }

    public void ShowPreviewSample(string sampleText = "Por favor, dê um presente.\nBondade  •  Mente  •  Ambição  •  Bravura  •  Voz")
    {
        ShowSubtitle(sampleText, "Please give it a gift.", OriginalTextBlock.Visibility == Visibility.Visible);
    }

    private void EditFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isEditMode && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isEditMode)
            return;

        double newWidth = Math.Max(260, ActualWidth + e.HorizontalChange);
        double newHeight = Math.Max(80, ActualHeight + e.VerticalChange);
        Width = newWidth;
        Height = newHeight;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_isEditMode && (e.Key == Key.Escape || e.Key == Key.Enter))
        {
            SetEditMode(false);
        }
    }

    private static void AnimateOpacity(UIElement element, double targetOpacity, int durationMs)
    {
        var anim = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        element.BeginAnimation(OpacityProperty, anim);
    }

    private static Brush ParseBrush(string hex, Color fallback)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            var brush = new SolidColorBrush(fallback);
            brush.Freeze();
            return brush;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        base.OnClosed(e);
    }
}
