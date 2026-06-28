using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace TokenWatch.App;

/// <summary>
/// A small in-app toast that slides in at the bottom-right and auto-dismisses.
/// Does not rely on the Windows notification system, so it shows regardless of
/// Focus Assist / app-notification settings.
/// </summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _autoClose;

    public ToastWindow(Severity severity, string title, string body, TimeSpan? duration = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        Accent.Background = AccentBrush(severity);
        CloseBtn.Click += (_, _) => Close();

        _autoClose = new DispatcherTimer { Interval = duration ?? TimeSpan.FromSeconds(8) };
        _autoClose.Tick += (_, _) => Close();
        _autoClose.Start();

        Closed += (_, _) => _autoClose.Stop();
    }

    public FrameworkElement Card => (FrameworkElement)Content;

    private static Brush AccentBrush(Severity s)
    {
        var hex = s switch
        {
            Severity.Critical => "#C62828",
            Severity.Warn => "#ED6C02",
            Severity.Ok => "#2E7D32",
            _ => "#757575",
        };
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
