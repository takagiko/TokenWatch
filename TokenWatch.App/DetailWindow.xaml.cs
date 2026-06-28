using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TokenWatch.Core.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace TokenWatch.App;

public partial class DetailWindow : Window
{
    private static readonly Brush Ink = Hex("#222222");
    private static readonly Brush Sub = Hex("#555555");
    private static readonly Brush Gray = Hex("#9E9E9E");
    private static readonly Brush CardBg = Hex("#FFFFFF");
    private static readonly Brush Track = Hex("#E6E6E6");

    private readonly Func<Task> _onRefresh;
    private readonly Action _onExit;
    private bool _forceClose;

    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;

    public DetailWindow(Func<Task> onRefresh, Action onExit)
    {
        _onRefresh = onRefresh;
        _onExit = onExit;
        InitializeComponent();

        RefreshButton.Click += async (_, _) =>
        {
            RefreshButton.IsEnabled = false;
            StatusText.Text = "更新中…";
            try { await _onRefresh(); }
            finally { RefreshButton.IsEnabled = true; StatusText.Text = ""; }
        };
        ExitButton.Click += (_, _) => _onExit();
    }

    public void Update(IReadOnlyList<UsageSnapshot> snaps, DateTimeOffset now)
    {
        LastUpdatedText.Text = "更新 " + now.ToLocalTime().ToString("HH:mm:ss");
        Cards.Children.Clear();
        if (snaps.Count == 0)
        {
            Cards.Children.Add(new TextBlock { Text = "データなし", Foreground = Gray, FontSize = 12 });
            return;
        }
        var nowUtc = now.ToUniversalTime();
        foreach (var s in snaps)
            Cards.Children.Add(BuildCard(s, nowUtc));
    }

    public void ShowNearTray()
    {
        if (!IsVisible) Show();
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Bottom - ActualHeight - 12;
        Activate();
    }

    public void HideFlyout()
    {
        LastHiddenUtc = DateTime.UtcNow;
        Hide();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (IsVisible) HideFlyout();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            HideFlyout();
        }
        base.OnClosing(e);
    }

    private Border BuildCard(UsageSnapshot s, DateTimeOffset nowUtc)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = Formatting.ProviderName(s.Provider),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = Ink,
        });

        foreach (var kind in new[] { WindowKind.FiveHour, WindowKind.Weekly })
            panel.Children.Add(BuildWindowRow(s, s.Window(kind), kind, nowUtc));

        panel.Children.Add(new TextBlock
        {
            Text = "5h  " + Formatting.TokenLine(s.TokensLast5h),
            FontSize = 11, Foreground = Sub, Margin = new Thickness(0, 7, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "7d  " + Formatting.TokenLine(s.TokensLast7d),
            FontSize = 11, Foreground = Sub,
        });

        if (s.Note is not null)
            panel.Children.Add(new TextBlock
            {
                Text = s.Note, FontSize = 10.5, Foreground = Gray,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });

        return new Border
        {
            Background = CardBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            BorderBrush = Hex("#EAEAEA"),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
    }

    private Grid BuildWindowRow(UsageSnapshot s, UsageWindow? w, WindowKind kind, DateTimeOffset nowUtc)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = kind == WindowKind.FiveHour ? "5時間枠" : "週間枠",
            FontSize = 11.5, Foreground = Sub, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        double? pct = w?.UsedPercent;
        bool stale = w is not null && pct is not null && Formatting.IsStale(s, w, nowUtc);
        var sev = Formatting.ForPercent(pct, stale);

        var bar = BuildBar(pct, sev);
        bar.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);

        var value = new TextBlock
        {
            Text = (pct is { } p ? $"{p:0}%" : "—") + (stale ? " *" : ""),
            FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            Foreground = SevBrush(sev),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 40, TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        return grid;
    }

    private static Grid BuildBar(double? pct, Severity sev)
    {
        const double trackW = 150;
        var grid = new Grid { Width = trackW, HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(new Border
        {
            Height = 7, Width = trackW, CornerRadius = new CornerRadius(4), Background = Track,
        });
        double clamped = pct is { } p ? Math.Clamp(p, 0, 100) : 0;
        grid.Children.Add(new Border
        {
            Height = 7, Width = trackW * clamped / 100.0,
            CornerRadius = new CornerRadius(4),
            Background = SevBrush(sev),
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        return grid;
    }

    private static Brush SevBrush(Severity s) => s switch
    {
        Severity.Ok => Hex("#2E7D32"),
        Severity.Warn => Hex("#ED6C02"),
        Severity.Critical => Hex("#C62828"),
        Severity.Stale => Hex("#9E9E9E"),
        _ => Hex("#9E9E9E"),
    };

    private static Brush Hex(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
