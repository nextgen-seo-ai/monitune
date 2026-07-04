using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace MonitorTune;

public sealed partial class TrayWindow : Window
{
    public TrayIconHost Tray => Host;

    public TrayWindow()
    {
        InitializeComponent();
        var hwnd = WindowNative.GetWindowHandle(this);
        var aw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        if (aw.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        aw.IsShownInSwitchers = false;                      // прячем из Alt+Tab и taskbar
        aw.MoveAndResize(new Windows.Graphics.RectInt32(-32000, -32000, 1, 1));
    }

    public void HideHard()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var aw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        aw.Hide();
    }
}
