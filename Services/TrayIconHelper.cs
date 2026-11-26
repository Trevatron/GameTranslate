using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameTranslate.Services;

public sealed class TrayIconHelper : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleRunItem;
    private readonly ToolStripMenuItem _toggleOverlayItem;
    private readonly Icon _appIcon;

    public TrayIconHelper(
        Action onOpenWindow,
        Action onToggleRun,
        Action onSelectRegion,
        Action onToggleOverlay,
        Action onExit)
    {
        _appIcon = CreateBrandIcon();

        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Abrir GameTranslate", null, (_, _) => onOpenWindow())
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        };
        _toggleRunItem = new ToolStripMenuItem("Iniciar Tradução (Alt+T)", null, (_, _) => onToggleRun());
        var selectItem = new ToolStripMenuItem("Selecionar Área da Legenda (Alt+R)", null, (_, _) => onSelectRegion());
        _toggleOverlayItem = new ToolStripMenuItem("Ocultar Legenda (Alt+H)", null, (_, _) => onToggleOverlay());
        var exitItem = new ToolStripMenuItem("Sair", null, (_, _) => onExit());

        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleRunItem);
        menu.Items.Add(selectItem);
        menu.Items.Add(_toggleOverlayItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "GameTranslate — Legendas EN → PT-BR",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => onOpenWindow();
    }

    public void UpdateState(bool isRunning, bool isOverlayHidden)
    {
        _toggleRunItem.Text = isRunning ? "Pausar Tradução (Alt+T)" : "Iniciar Tradução (Alt+T)";
        _toggleOverlayItem.Text = isOverlayHidden ? "Mostrar Legenda (Alt+H)" : "Ocultar Legenda (Alt+H)";
        _notifyIcon.Text = isRunning
            ? "GameTranslate — ATIVO (EN → PT-BR)"
            : "GameTranslate — Pausado";
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(2500, title, message, ToolTipIcon.Info);
        }
        catch
        {
            // Ignore balloon failures on restricted systems
        }
    }

    public static Icon CreateBrandIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Dark slate rounded square
            using var bgBrush = new SolidBrush(Color.FromArgb(255, 22, 24, 34));
            using var borderPen = new Pen(Color.FromArgb(255, 255, 216, 0), 4f);
            using var path = CreateRoundedRect(new Rectangle(3, 3, 58, 58), 14);
            g.FillPath(bgBrush, path);
            g.DrawPath(borderPen, path);

            // Classic cinema yellow subtitle bars + "GT" badge
            using var font = new Font("Arial", 22, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.FromArgb(255, 255, 216, 0));
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("GT", font, textBrush, new RectangleF(0, 4, 64, 36), sf);

            // Subtitle lines at bottom
            using var lineBrush = new SolidBrush(Color.FromArgb(255, 255, 216, 0));
            g.FillRectangle(lineBrush, 12, 43, 40, 5);
            g.FillRectangle(lineBrush, 19, 51, 26, 4);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
    }
}
