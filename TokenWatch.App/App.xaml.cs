using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TokenWatch.Core.Models;
using Size = System.Windows.Size;

namespace TokenWatch.App;

public partial class App
{
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Hidden verification mode: render the dashboard with live data to a PNG
        // and exit. Usage: TokenWatch.exe --shot <path>
        var shotIdx = Array.IndexOf(e.Args, "--shot");
        if (shotIdx >= 0 && shotIdx + 1 < e.Args.Length)
        {
            _ = CaptureAsync(e.Args[shotIdx + 1]);
            return;
        }

        var toastIdx = Array.IndexOf(e.Args, "--toast");
        if (toastIdx >= 0 && toastIdx + 1 < e.Args.Length)
        {
            CaptureToast(e.Args[toastIdx + 1]);
            return;
        }

        // Render the dashboard with neutral SAMPLE data (for README screenshots) and exit.
        var sampleIdx = Array.IndexOf(e.Args, "--shot-sample");
        if (sampleIdx >= 0 && sampleIdx + 1 < e.Args.Length)
        {
            CaptureSample(e.Args[sampleIdx + 1]);
            return;
        }

        _tray = new TrayController();
    }

    private async Task CaptureAsync(string path)
    {
        var claude = new ClaudeUsageService();
        try
        {
            var snaps = await new UsageService(claude).RefreshAsync();

            var win = new DetailWindow(() => Task.CompletedTask, () => { })
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
            };
            win.Update(snaps, DateTimeOffset.Now);
            win.Show();
            win.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);

            var content = (FrameworkElement)win.Content;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            content.Arrange(new Rect(content.DesiredSize));
            content.UpdateLayout();

            int w = (int)Math.Ceiling(content.ActualWidth);
            int h = (int)Math.Ceiling(content.ActualHeight);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(content);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path))
                encoder.Save(fs);
        }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".error.txt", ex.ToString());
        }
        finally
        {
            claude.Shutdown();
            Shutdown();
        }
    }

    private void CaptureToast(string path)
    {
        try
        {
            var t = new ToastWindow(Severity.Critical, "TokenWatch — 危険",
                "Codex 5時間枠: 100%（リセット 06-28 13:39）\nCodex 週間枠: 88%（リセット 07-04 11:53）")
            {
                ShowActivated = false,
                Left = -10000,
                Top = -10000,
            };
            t.Show();
            t.UpdateLayout();

            var content = t.Card;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            content.Arrange(new Rect(content.DesiredSize));
            content.UpdateLayout();

            int w = (int)Math.Ceiling(content.ActualWidth);
            int h = (int)Math.Ceiling(content.ActualHeight);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(content);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path))
                encoder.Save(fs);
        }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".error.txt", ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    private void CaptureSample(string path)
    {
        try
        {
            var win = new DetailWindow(() => Task.CompletedTask, () => { })
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
            };
            win.Update(SampleSnapshots(), DateTimeOffset.Now);
            win.Show();
            win.UpdateLayout();

            var content = (FrameworkElement)win.Content;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            content.Arrange(new Rect(content.DesiredSize));
            content.UpdateLayout();
            RenderToPng(content, path);
        }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".error.txt", ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    private static IReadOnlyList<UsageSnapshot> SampleSnapshots()
    {
        var now = DateTimeOffset.Now;
        var claude = new UsageSnapshot(ProviderId.ClaudeCode, now,
            [
                new UsageWindow(WindowKind.FiveHour, 45, now.AddHours(2).AddMinutes(40)),
                new UsageWindow(WindowKind.Weekly, 28, now.AddDays(4)),
            ],
            new TokenTotals(14_000, 95_000, 1_600_000, 6_500_000, 8_209_000),
            new TokenTotals(120_000, 2_000_000, 40_000_000, 370_000_000, 412_120_000),
            new TokenTotals(600_000, 7_200_000, 160_000_000, 1_460_000_000, 1_627_800_000),
            Note: "ライブ (claude.ai)", LimitObservedAt: now);

        var codex = new UsageSnapshot(ProviderId.Codex, now,
            [
                new UsageWindow(WindowKind.FiveHour, 72, now.AddHours(3).AddMinutes(30)),
                new UsageWindow(WindowKind.Weekly, 55, now.AddDays(5)),
            ],
            new TokenTotals(8_400_000, 110_000, 0, 7_900_000, 8_510_000),
            new TokenTotals(170_000_000, 1_900_000, 0, 158_000_000, 171_900_000),
            new TokenTotals(327_000_000, 2_200_000, 0, 300_000_000, 329_200_000),
            Note: "ライブ (chatgpt.com API)", LimitObservedAt: now);

        return [claude, codex];
    }

    private static void RenderToPng(FrameworkElement content, string path)
    {
        int w = (int)Math.Ceiling(content.ActualWidth);
        int h = (int)Math.Ceiling(content.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
