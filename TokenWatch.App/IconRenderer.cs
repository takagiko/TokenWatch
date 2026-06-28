using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace TokenWatch.App;

/// <summary>
/// Renders a small tray icon showing a short text (e.g. "69") on a colored
/// rounded background. Produces a managed <see cref="Icon"/> that the caller owns
/// and must dispose; the temporary GDI handle is destroyed here to avoid leaks.
/// </summary>
public static class IconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Color Background(Severity s) => s switch
    {
        Severity.Ok => Color.FromArgb(46, 125, 50),     // green
        Severity.Warn => Color.FromArgb(237, 108, 2),   // orange
        Severity.Critical => Color.FromArgb(198, 40, 40), // red
        _ => Color.FromArgb(110, 110, 110),             // gray (stale/unknown)
    };

    public static Icon Render(string text, Severity severity)
    {
        var bg = Background(severity);
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using (var path = RoundedRect(new Rectangle(1, 1, size - 2, size - 2), 7))
            using (var brush = new SolidBrush(bg))
                g.FillPath(brush, path);

            float emSize = text.Length >= 3 ? 13f : 17f;
            using var font = new Font("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(text, font, textBrush, new RectangleF(0, 0, size, size), fmt);
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
