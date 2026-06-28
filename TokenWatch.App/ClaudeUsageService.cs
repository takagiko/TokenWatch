using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TokenWatch.Core.Api;

namespace TokenWatch.App;

/// <summary>
/// Fetches live Claude limit % from claude.ai using a persistent WebView2 session
/// (cookie auth — the user logs in once). The usage endpoint is called via an
/// in-page <c>fetch</c> so it is same-origin and uses the session cookies; the
/// result is returned through <c>postMessage</c>. All members must be called on
/// the UI thread.
/// </summary>
public sealed class ClaudeUsageService
{
    private readonly string _orgId;
    private readonly string _userDataFolder;
    private readonly string _debugPath;

    private ClaudeLoginWindow? _host;
    private CoreWebView2? _core;
    private TaskCompletionSource<string>? _pending;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private bool _initialized;
    private bool _watchLogin;
    private DispatcherTimer? _loginPoll;

    /// <summary>Raised on the UI thread when a login is auto-detected as complete.</summary>
    public event Action? LoginCompleted;

    public bool HasOrg => !string.IsNullOrEmpty(_orgId);
    public bool NeedsLogin { get; private set; }

    public ClaudeUsageService()
    {
        _orgId = ReadOrgId();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _userDataFolder = Path.Combine(local, "TokenWatch", "WebView2");
        _debugPath = Path.Combine(local, "TokenWatch", "claude-usage-debug.json");
        Directory.CreateDirectory(_userDataFolder);
    }

    private static string ReadOrgId()
    {
        try
        {
            var p = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            if (doc.RootElement.TryGetProperty("oauthAccount", out var oa)
                && oa.TryGetProperty("organizationUuid", out var u))
                return u.GetString() ?? "";
        }
        catch { /* fall through */ }
        return "";
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;

        _host = new ClaudeLoginWindow
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
        };
        _host.Show(); // the WebView2 control must be loaded to initialize

        var env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
        await _host.Web.EnsureCoreWebView2Async(env);
        _core = _host.Web.CoreWebView2;
        _core.WebMessageReceived += (_, e) =>
        {
            try { _pending?.TrySetResult(e.TryGetWebMessageAsString()); }
            catch { _pending?.TrySetResult(""); }
        };
        _core.NavigationCompleted += OnLoginNavCompleted;

        await NavigateAsync("https://claude.ai/");
        _host.Hide();
        _initialized = true;
    }

    private Task NavigateAsync(string url)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            _core!.NavigationCompleted -= Handler;
            tcs.TrySetResult();
        }
        _core!.NavigationCompleted += Handler;
        _core.Navigate(url);
        return tcs.Task;
    }

    public async Task<ClaudeLiveUsage?> TryGetAsync(CancellationToken ct = default)
    {
        if (!HasOrg) return null;
        await EnsureInitAsync();

        var res = await FetchUsageAsync();
        if (res is not { } r) return null;
        var (status, body) = r;

        if (status is 401 or 403)
        {
            NeedsLogin = true;
            return null;
        }
        if (status != 200)
        {
            Dump(status, body);
            return null;
        }

        NeedsLogin = false;
        var parsed = ClaudeUsageParser.TryParse(body, DateTimeOffset.UtcNow);
        if (parsed is null) Dump(status, body); // unexpected shape — keep for diagnosis
        return parsed;
    }

    /// <summary>Fetches the usage endpoint (serialized) and returns (status, body).</summary>
    private async Task<(int status, string body)?> FetchUsageAsync()
    {
        await _fetchLock.WaitAsync();
        try
        {
            var src = _core!.Source ?? "";
            if (!src.StartsWith("https://claude.ai", StringComparison.OrdinalIgnoreCase))
                await NavigateAsync("https://claude.ai/");

            var raw = await FetchRawAsync($"https://claude.ai/api/organizations/{_orgId}/usage");
            if (raw is null) return null;

            using var d = JsonDocument.Parse(raw);
            return (d.RootElement.GetProperty("status").GetInt32(),
                    d.RootElement.GetProperty("body").GetString() ?? "");
        }
        catch { return null; }
        finally { _fetchLock.Release(); }
    }

    /// <summary>
    /// While a login is in progress, probe the usage endpoint after each claude.ai
    /// navigation; a 200 means the session is now valid, so close the window and notify.
    /// </summary>
    private async void OnLoginNavCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_watchLogin) return;
        var src = _core?.Source ?? "";
        if (!src.StartsWith("https://claude.ai", StringComparison.OrdinalIgnoreCase)) return;

        var res = await FetchUsageAsync();
        if (res is { status: 200 }) CompleteLogin();
    }

    // Safety net for logins that complete without a top-level navigation (SPA/XHR):
    // poll the usage endpoint while the login window is open.
    private async void OnLoginPollTick(object? sender, EventArgs e)
    {
        if (!_watchLogin) { _loginPoll?.Stop(); return; }
        var res = await FetchUsageAsync();
        if (res is { status: 200 }) CompleteLogin();
    }

    private void CompleteLogin()
    {
        if (!_watchLogin) return;
        _watchLogin = false;
        _loginPoll?.Stop();
        NeedsLogin = false;
        _host?.Hide();
        LoginCompleted?.Invoke();
    }

    private async Task<string?> FetchRawAsync(string url)
    {
        _pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = """
            (function () {
              fetch(URL, { credentials: 'include', headers: { 'accept': 'application/json' } })
                .then(r => r.text().then(t => ({ s: r.status, b: t })))
                .then(o => window.chrome.webview.postMessage(JSON.stringify({ status: o.s, body: o.b })))
                .catch(e => window.chrome.webview.postMessage(JSON.stringify({ status: -1, body: String(e) })));
            })();
            """.Replace("URL", JsonSerializer.Serialize(url));

        await _core!.ExecuteScriptAsync(script);

        var done = await Task.WhenAny(_pending.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        return done == _pending.Task ? _pending.Task.Result : null;
    }

    public async Task ShowLoginAsync()
    {
        await EnsureInitAsync();
        _watchLogin = true;

        _loginPoll ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _loginPoll.Tick -= OnLoginPollTick;
        _loginPoll.Tick += OnLoginPollTick;
        _loginPoll.Start();

        await NavigateAsync("https://claude.ai/login");
        if (!_watchLogin) return; // already logged in — auto-detected during navigation

        var wa = SystemParameters.WorkArea;
        _host!.ShowInTaskbar = true;
        _host.Left = wa.Left + (wa.Width - _host.Width) / 2;
        _host.Top = wa.Top + (wa.Height - _host.Height) / 2;
        _host.Show();
        _host.Activate();
    }

    private void Dump(int status, string body)
    {
        try { File.WriteAllText(_debugPath, $"status={status}\n{body}"); } catch { }
    }

    public void Shutdown() => _host?.ForceClose();
}
