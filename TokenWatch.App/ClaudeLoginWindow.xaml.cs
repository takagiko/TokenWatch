using System.ComponentModel;
using System.Windows;

namespace TokenWatch.App;

public partial class ClaudeLoginWindow : Window
{
    private bool _forceClose;

    public ClaudeLoginWindow()
    {
        InitializeComponent();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Keep the WebView2 host (and its logged-in session) alive; just hide.
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
