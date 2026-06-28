using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Windows.Threading;
using TokenWatch.Core.Models;
using Application = System.Windows.Application;

namespace TokenWatch.App;

/// <summary>
/// Owns the tray icon, the polling timer and the detail window. The tray icon
/// shows the Codex 5h usage % (gray when the local snapshot is stale); the
/// tooltip summarizes both providers; left click toggles the detail flyout.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly NotifyIcon _icon = new();
    private readonly ToastManager _toasts = new();
    private readonly ClaudeUsageService _claude = new();
    private readonly UsageService _service;
    private readonly DispatcherTimer _timer;
    private DetailWindow? _window;
    private Icon? _ownedIcon;
    private bool _refreshing;
    private IReadOnlyList<UsageSnapshot> _last = [];

    // Last severity we notified per (provider, window) — used to alert only when
    // usage crosses a threshold upward, not on every poll.
    private readonly Dictionary<(ProviderId, WindowKind), Severity> _notifiedSeverity = new();

    public TrayController()
    {
        _service = new UsageService(_claude);
        _claude.LoginCompleted += OnClaudeLoginCompleted;
        Formatting.WarnPercent = _settings.WarnPercent;
        Formatting.CriticalPercent = _settings.CriticalPercent;

        SetIcon("··", Severity.Unknown);
        _icon.Text = "TokenWatch";
        _icon.Visible = true;

        var menu = new ContextMenuStrip();
        menu.Items.Add("詳細を表示", null, (_, _) => ShowWindow());
        menu.Items.Add("今すぐ更新", null, async (_, _) => await RefreshAsync());
        menu.Items.Add("Claude にログイン", null, async (_, _) => await LoginClaudeAsync());
        menu.Items.Add(new ToolStripSeparator());

        var autoStart = new ToolStripMenuItem("Windows 起動時に自動起動")
        {
            CheckOnClick = true,
            Checked = AutoStart.IsEnabled(),
        };
        autoStart.CheckedChanged += (_, _) =>
        {
            if (autoStart.Checked) AutoStart.Enable(); else AutoStart.Disable();
            _settings.StartWithWindows = autoStart.Checked;
            _settings.Save();
        };
        menu.Items.Add(autoStart);

        var notify = new ToolStripMenuItem("しきい値超えを通知")
        {
            CheckOnClick = true,
            Checked = _settings.NotificationsEnabled,
        };
        notify.CheckedChanged += (_, _) =>
        {
            _settings.NotificationsEnabled = notify.Checked;
            _settings.Save();
        };
        menu.Items.Add(notify);

        menu.Items.Add("設定フォルダを開く", null, (_, _) => OpenSettingsFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApp());
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Math.Max(0.5, _settings.PollIntervalMinutes)),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    private static void OpenSettingsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppSettings.Folder,
                UseShellExecute = true,
            });
        }
        catch { /* non-fatal */ }
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var snaps = await _service.RefreshAsync();
            _last = snaps;
            UpdateIcon(snaps);
            UpdateTooltip(snaps);
            if (_window is { IsVisible: true })
                _window.Update(snaps, DateTimeOffset.Now);
            EvaluateNotifications(snaps);
        }
        catch
        {
            // Keep the previous reading on error.
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task LoginClaudeAsync()
    {
        // Login completion is auto-detected (see OnClaudeLoginCompleted); just open the window.
        await _claude.ShowLoginAsync();
    }

    private void OnClaudeLoginCompleted()
    {
        _toasts.Show(Severity.Ok, "TokenWatch", "Claude にログインしました");
        _ = RefreshAsync();
    }

    private void EnsureWindow()
    {
        _window ??= new DetailWindow(RefreshAsync, ExitApp);
    }

    private void ShowWindow()
    {
        EnsureWindow();
        _window!.Update(_last, DateTimeOffset.Now);
        _window.ShowNearTray();
    }

    private void ToggleWindow()
    {
        EnsureWindow();
        if (_window!.IsVisible)
        {
            _window.HideFlyout();
        }
        else if ((DateTime.UtcNow - _window.LastHiddenUtc).TotalMilliseconds > 300)
        {
            ShowWindow();
        }
    }

    private void UpdateIcon(IReadOnlyList<UsageSnapshot> snaps)
    {
        var now = DateTimeOffset.UtcNow;

        // Headline = the highest fresh 5h usage across providers.
        double? best = null;
        foreach (var s in snaps)
        {
            var w = s.Window(WindowKind.FiveHour);
            if (w?.UsedPercent is { } p && !Formatting.IsStale(s, w, now) && (best is null || p > best))
                best = p;
        }
        if (best is { } pct)
        {
            SetIcon(((int)Math.Round(pct)).ToString(), Formatting.ForPercent(pct, false));
            return;
        }

        // No fresh data — show a stale number if we have one, else a placeholder.
        var codex = snaps.FirstOrDefault(s => s.Provider == ProviderId.Codex);
        var cw = codex?.Window(WindowKind.FiveHour);
        if (codex is not null && cw?.UsedPercent is { } sp)
            SetIcon(((int)Math.Round(sp)).ToString(), Severity.Stale);
        else
            SetIcon("··", Severity.Unknown);
    }

    private void UpdateTooltip(IReadOnlyList<UsageSnapshot> snaps)
    {
        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();

        var codex = snaps.FirstOrDefault(s => s.Provider == ProviderId.Codex);
        if (codex is not null)
            sb.Append($"Codex 5h {Pct(codex, WindowKind.FiveHour, now)} / 週 {Pct(codex, WindowKind.Weekly, now)}");

        var claude = snaps.FirstOrDefault(s => s.Provider == ProviderId.ClaudeCode);
        if (claude is not null)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(claude.Window(WindowKind.FiveHour)?.UsedPercent is not null
                ? $"Claude 5h {Pct(claude, WindowKind.FiveHour, now)} / 週 {Pct(claude, WindowKind.Weekly, now)}"
                : $"Claude 5h {Formatting.Compact(claude.TokensLast5h.Total)} tok");
        }

        var text = sb.ToString();
        if (text.Length > 127) text = text[..127];
        _icon.Text = text.Length == 0 ? "TokenWatch" : text;
    }

    private static string Pct(UsageSnapshot s, WindowKind kind, DateTimeOffset now)
    {
        var w = s.Window(kind);
        if (w?.UsedPercent is not { } p) return "—";
        return $"{p:0}%" + (Formatting.IsStale(s, w, now) ? "*" : "");
    }

    private void EvaluateNotifications(IReadOnlyList<UsageSnapshot> snaps)
    {
        if (!_settings.NotificationsEnabled) return;
        var now = DateTimeOffset.UtcNow;

        var lines = new List<string>();
        var maxSev = Severity.Ok;

        foreach (var s in snaps)
        {
            foreach (var kind in new[] { WindowKind.FiveHour, WindowKind.Weekly })
            {
                var w = s.Window(kind);
                if (w?.UsedPercent is not { } p) continue;
                if (Formatting.IsStale(s, w, now)) continue;

                var sev = Formatting.ForPercent(p, false);
                var key = (s.Provider, kind);
                var prev = _notifiedSeverity.TryGetValue(key, out var v) ? v : Severity.Ok;

                if (Rank(sev) > Rank(prev))
                {
                    var window = kind == WindowKind.FiveHour ? "5時間枠" : "週間枠";
                    var resetStr = w.ResetsAt is { } r ? $"（リセット {r.ToLocalTime():MM-dd HH:mm}）" : "";
                    lines.Add($"{Formatting.ProviderName(s.Provider)} {window}: {p:0}%{resetStr}");
                    if (Rank(sev) > Rank(maxSev)) maxSev = sev;
                }

                _notifiedSeverity[key] = sev; // track both up and down so re-crossings re-alert
            }
        }

        if (lines.Count > 0)
        {
            var level = maxSev == Severity.Critical ? "危険" : "警告";
            _toasts.Show(maxSev, $"TokenWatch — {level}", string.Join("\n", lines));
        }
    }

    private static int Rank(Severity s) => s switch
    {
        Severity.Warn => 1,
        Severity.Critical => 2,
        _ => 0,
    };

    private void SetIcon(string text, Severity sev)
    {
        var newIcon = IconRenderer.Render(text, sev);
        _icon.Icon = newIcon;
        _ownedIcon?.Dispose();
        _ownedIcon = newIcon;
    }

    private void ExitApp()
    {
        _timer.Stop();
        _icon.Visible = false;
        _toasts.CloseAll();
        _window?.ForceClose();
        _claude.Shutdown();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _ownedIcon?.Dispose();
    }
}
