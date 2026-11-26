using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GameTranslate.Models;
using GameTranslate.Native;

namespace GameTranslate.Windows;

public partial class RegionSelectorWindow : Window
{
    private Point _startDip;
    private Point _startPhysical;
    private bool _isDragging;

    public ScreenRect? SelectedRegion { get; private set; }
    public bool AutoExcludeTitleBar { get; set; } = true;

    public RegionSelectorWindow(bool autoExcludeTitleBar = true)
    {
        AutoExcludeTitleBar = autoExcludeTitleBar;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Cover the entire multi-monitor virtual desktop using exact physical pixels
        var (vx, vy, vw, vh) = Win32.GetVirtualScreenBounds();
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Win32.MoveWindow(hwnd, vx, vy, vw, vh, true);

        UpdateDimGeometry(Rect.Empty);

        // Center instruction banner on primary screen top
        double bannerX = Math.Max(20, (ActualWidth - 760) / 2);
        Canvas.SetLeft(InstructionBanner, bannerX);
        Canvas.SetTop(InstructionBanner, 28);

        Activate();
        Focus();
    }

    private void UpdateDimGeometry(Rect selectionDip)
    {
        var fullRect = new RectangleGeometry(new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)));
        if (selectionDip.IsEmpty || selectionDip.Width < 2 || selectionDip.Height < 2)
        {
            DimPath.Data = fullRect;
        }
        else
        {
            var hole = new RectangleGeometry(selectionDip);
            DimPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, fullRect, hole);
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _startDip = e.GetPosition(OverlayCanvas);
        _startPhysical = PointToScreen(_startDip);

        SelectionBox.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        CaptureMouse();
        UpdateSelectionVisual(_startDip, _startPhysical);
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
            return;

        Point currentDip = e.GetPosition(OverlayCanvas);
        Point currentPhys = PointToScreen(currentDip);
        UpdateSelectionVisual(currentDip, currentPhys);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        ReleaseMouseCapture();

        Point endDip = e.GetPosition(OverlayCanvas);
        Point endPhys = PointToScreen(endDip);

        int x = (int)Math.Round(Math.Min(_startPhysical.X, endPhys.X));
        int y = (int)Math.Round(Math.Min(_startPhysical.Y, endPhys.Y));
        int w = (int)Math.Round(Math.Abs(endPhys.X - _startPhysical.X));
        int h = (int)Math.Round(Math.Abs(endPhys.Y - _startPhysical.Y));

        // Hide first so Win32.WindowFromPoint inspects the actual game window underneath (not this overlay).
        // Do NOT set DialogResult after Hide() in WPF; caller inspects SelectedRegion directly.
        Hide();

        if (w < 12 && h < 12)
        {
            // Single click on a game window -> auto-select its entire client area (zero titlebar!)
            ScreenRect? clientRect = Win32.GetWindowClientRectAtPoint((int)Math.Round(endPhys.X), (int)Math.Round(endPhys.Y));
            SelectedRegion = clientRect is { IsValid: true } ? clientRect : null;
        }
        else if (w >= 24 && h >= 18)
        {
            var raw = new ScreenRect { X = x, Y = y, Width = w, Height = h };
            SelectedRegion = AutoExcludeTitleBar
                ? Win32.TrimWindowTitleBarIfOverlapping(raw)
                : raw;
        }
        else
        {
            SelectedRegion = null;
        }

        Close();
    }

    private void UpdateSelectionVisual(Point currentDip, Point currentPhys)
    {
        double left = Math.Min(_startDip.X, currentDip.X);
        double top = Math.Min(_startDip.Y, currentDip.Y);
        double width = Math.Abs(currentDip.X - _startDip.X);
        double height = Math.Abs(currentDip.Y - _startDip.Y);

        Canvas.SetLeft(SelectionBox, left);
        Canvas.SetTop(SelectionBox, top);
        SelectionBox.Width = Math.Max(1, width);
        SelectionBox.Height = Math.Max(1, height);

        UpdateDimGeometry(new Rect(left, top, width, height));

        int physW = (int)Math.Round(Math.Abs(currentPhys.X - _startPhysical.X));
        int physH = (int)Math.Round(Math.Abs(currentPhys.Y - _startPhysical.Y));
        SizeBadgeText.Text = $"{physW} × {physH} px";

        double badgeTop = top >= 36 ? top - 32 : top + height + 8;
        Canvas.SetLeft(SizeBadge, left);
        Canvas.SetTop(SizeBadge, badgeTop);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SelectedRegion = null;
            Close();
        }
    }
}
