using System.Windows;

namespace TokenWatch.App;

/// <summary>Shows in-app toasts stacked at the bottom-right of the work area.</summary>
public sealed class ToastManager
{
    private readonly List<ToastWindow> _open = new();

    public void Show(Severity severity, string title, string body)
    {
        var toast = new ToastWindow(severity, title, body);
        toast.Closed += (_, _) =>
        {
            _open.Remove(toast);
            Reposition();
        };
        _open.Add(toast);
        toast.Show();
        toast.UpdateLayout();
        Reposition();
    }

    private void Reposition()
    {
        var wa = SystemParameters.WorkArea;
        double bottom = wa.Bottom;
        for (int i = _open.Count - 1; i >= 0; i--) // newest at the bottom, older stack upward
        {
            var t = _open[i];
            t.UpdateLayout();
            double h = t.ActualHeight > 0 ? t.ActualHeight : t.DesiredSize.Height;
            t.Left = wa.Right - t.Width;
            t.Top = bottom - h;
            bottom -= h;
        }
    }

    public void CloseAll()
    {
        foreach (var t in _open.ToArray())
            t.Close();
        _open.Clear();
    }
}
