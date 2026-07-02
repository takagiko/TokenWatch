using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TokenWatch.App;

/// <summary>
/// A system tray icon backed by Shell_NotifyIcon with a stable <c>guidItem</c>.
/// Unlike WinForms' NotifyIcon (which has no GUID), a fixed GUID lets Windows 11
/// remember the icon's identity — so once the user drags it onto the taskbar it
/// stays there across restarts. Uses NOTIFYICON_VERSION_4 for modern behavior.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYCALLBACK = WM_APP + 1;

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;
    private const int NOTIFYICON_VERSION_4 = 4;

    private const int NIN_SELECT = 0x0400;      // WM_USER — left click (version 4)
    private const int WM_CONTEXTMENU = 0x007B;  // right click (version 4)

    private readonly Guid _guid;
    private readonly TrayWindow _window;
    private bool _added;

    public event Action? LeftClick;
    public event Action<Point>? RightClick;

    public TrayIcon(Guid guid)
    {
        _guid = guid;
        _window = new TrayWindow(OnMessage);
    }

    /// <summary>Adds (first call) or updates the tray icon and tooltip.</summary>
    public void Update(Icon icon, string tip)
    {
        if (!_added)
        {
            var add = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID | NIF_SHOWTIP, icon.Handle, tip);
            if (!Shell_NotifyIcon(NIM_ADD, ref add))
            {
                // A stale GUID→exe-path binding (e.g. the app was moved) makes ADD
                // fail; clear the old registration by GUID and try once more.
                var del = NewData(NIF_GUID, IntPtr.Zero, "");
                Shell_NotifyIcon(NIM_DELETE, ref del);
                Shell_NotifyIcon(NIM_ADD, ref add);
            }

            var ver = NewData(NIF_GUID, IntPtr.Zero, "");
            ver.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref ver);
            _added = true;
        }
        else
        {
            var mod = NewData(NIF_ICON | NIF_TIP | NIF_GUID | NIF_SHOWTIP, icon.Handle, tip);
            Shell_NotifyIcon(NIM_MODIFY, ref mod);
        }
    }

    /// <summary>Shows a context menu at a screen point with correct dismiss behavior.</summary>
    public void ShowMenu(ContextMenuStrip menu, Point screenPoint)
    {
        SetForegroundWindow(_window.Handle); // so the menu closes on outside click
        menu.Show(screenPoint);
    }

    public void Dispose()
    {
        if (_added)
        {
            var del = NewData(NIF_GUID, IntPtr.Zero, "");
            Shell_NotifyIcon(NIM_DELETE, ref del);
            _added = false;
        }
        _window.Dispose();
    }

    private void OnMessage(Message m)
    {
        if (m.Msg != WM_TRAYCALLBACK) return;

        int evt = (int)(m.LParam.ToInt64() & 0xFFFF);
        int wp = (int)m.WParam.ToInt64();
        var pt = new Point((short)(wp & 0xFFFF), (short)((wp >> 16) & 0xFFFF)); // screen coords (v4)

        switch (evt)
        {
            case NIN_SELECT:
                LeftClick?.Invoke();
                break;
            case WM_CONTEXTMENU:
                RightClick?.Invoke(pt);
                break;
        }
    }

    private NOTIFYICONDATA NewData(int flags, IntPtr hIcon, string tip) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 0,
        uFlags = flags,
        uCallbackMessage = WM_TRAYCALLBACK,
        hIcon = hIcon,
        szTip = tip,
        guidItem = _guid,
    };

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    /// <summary>Hidden top-level window that receives the tray callback messages.</summary>
    private sealed class TrayWindow : NativeWindow, IDisposable
    {
        private readonly Action<Message> _handler;

        public TrayWindow(Action<Message> handler)
        {
            _handler = handler;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            _handler(m);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
