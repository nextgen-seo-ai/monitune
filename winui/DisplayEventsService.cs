using Microsoft.UI.Dispatching;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MonitorTune;

// Слушает системные события изменения мониторов и питания через message-only HWND:
//   WM_DISPLAYCHANGE       — сменилось разрешение/частота/подключение монитора
//   WM_DEVICECHANGE        — подключение/отключение USB, monitor arrival/removal
//   WM_POWERBROADCAST      — уход в сон, DPMS on/off, разблокировка экрана
//
// При событиях "монитор ожил" — приостанавливает DDC на N миллисекунд (throttle),
// потому что канал ещё живёт своей жизнью 2-10 секунд после hotplug/wake.
// При событиях "монитор пропал" — вызывает Refresh для переоткрытия handles.
public sealed class DisplayEventsService : IDisposable
{
    const int WM_DISPLAYCHANGE = 0x007E;
    const int WM_DEVICECHANGE  = 0x0219;
    const int WM_POWERBROADCAST = 0x0218;

    const int DBT_DEVICEARRIVAL          = 0x8000;
    const int DBT_DEVICEREMOVECOMPLETE   = 0x8004;
    const int DBT_DEVNODES_CHANGED       = 0x0007;

    const int PBT_APMSUSPEND             = 0x0004;
    const int PBT_APMRESUMEAUTOMATIC     = 0x0012;
    const int PBT_POWERSETTINGCHANGE     = 0x8013;

    // GUID_MONITOR_POWER_ON — событие включения/выключения экрана (DPMS)
    static readonly Guid GUID_MONITOR_POWER_ON = new("02731015-4510-4526-99E6-E5A17EBD1AEA");
    // GUID_CONSOLE_DISPLAY_STATE — блокировка/разблокировка экрана
    static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    [StructLayout(LayoutKind.Sequential)]
    struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEV_BROADCAST_HDR
    {
        public uint dbch_size;
        public uint dbch_devicetype;
        public uint dbch_reserved;
    }
    const uint DBT_DEVTYP_DEVICEINTERFACE = 0x05;

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string className, string? wndName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool UnregisterClassW(string className, IntPtr hInstance);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandleW(string? m);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, uint Flags);
    [DllImport("user32.dll")]
    static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

    static readonly IntPtr HWND_MESSAGE = new(-3);

    readonly DdcManager _ddc;
    readonly DispatcherQueue _ui;
    readonly Action<string> _log;
    readonly string _className = "MonitorTuneDisplayEvents_" + Guid.NewGuid().ToString("N");
    readonly int _startTick = Environment.TickCount;
    WndProcDelegate? _proc;
    IntPtr _hInstance;
    IntPtr _hwnd;
    IntPtr _monitorPowerNotify;
    IntPtr _consoleDisplayNotify;
    bool _disposed;

    // Дебаунс WM_DISPLAYCHANGE — Nvidia шлёт 2-5 событий за 2 сек при HDR toggle / resolution change.
    long _lastDisplayChangeTick = 0;
    const int DisplayChangeDebounceMs = 500;
    // Дебаунс WM_DEVICECHANGE — USB hotplug шлёт несколько chirp'ов.
    long _lastDeviceChangeTick = 0;

    public event Action? OnConfigChanged;

    public DisplayEventsService(DdcManager ddc, DispatcherQueue ui, Action<string>? log = null)
    {
        _ddc = ddc;
        _ui = ui;
        _log = log ?? (_ => { });
    }

    public void Install()
    {
        _proc = WndProc;
        _hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _proc,
            hInstance = _hInstance,
            lpszClassName = _className,
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            _log($"DisplayEvents: RegisterClassEx FAILED err={Marshal.GetLastWin32Error()}");
            return;
        }
        _hwnd = CreateWindowExW(0, _className, "", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            _log($"DisplayEvents: CreateWindowEx FAILED err={Marshal.GetLastWin32Error()}");
            UnregisterClassW(_className, _hInstance);
            return;
        }

        Guid g1 = GUID_MONITOR_POWER_ON;
        _monitorPowerNotify = RegisterPowerSettingNotification(_hwnd, ref g1, 0);
        Guid g2 = GUID_CONSOLE_DISPLAY_STATE;
        _consoleDisplayNotify = RegisterPowerSettingNotification(_hwnd, ref g2, 0);

        _log($"DisplayEvents: HWND=0x{_hwnd.ToInt64():X}, subscribed to DPMS+display+device events");
    }

    IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_DISPLAYCHANGE:
                {
                    int now = Environment.TickCount;
                    int since = unchecked(now - (int)_lastDisplayChangeTick);
                    _lastDisplayChangeTick = now;
                    if (since < DisplayChangeDebounceMs)
                    {
                        _log($"WM_DISPLAYCHANGE: debounced ({since}ms since last)");
                        break;
                    }
                    _log("WM_DISPLAYCHANGE: config changed");
                    // GPU-драйверы после этого события роняют DDC на 2-10 сек. Замораживаем и перечисляем.
                    _ddc.SuspendDdc(3000, "WM_DISPLAYCHANGE");
                    _ui.TryEnqueue(() =>
                    {
                        try { OnConfigChanged?.Invoke(); }
                        catch (Exception ex) { _log("OnConfigChanged ex: " + ex.Message); }
                    });
                    break;
                }

                case WM_DEVICECHANGE:
                {
                    int evt = wParam.ToInt32();
                    if (evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE || evt == DBT_DEVNODES_CHANGED)
                    {
                        int now = Environment.TickCount;
                        int since = unchecked(now - (int)_lastDeviceChangeTick);
                        _lastDeviceChangeTick = now;
                        if (since < 300) break; // подавляем chirp'ы
                        _log($"WM_DEVICECHANGE: event=0x{evt:X}");
                        _ddc.SuspendDdc(2000, "device change");
                        // USB hotplug без WM_DISPLAYCHANGE — реэнумерируем monitors тоже, но с большей задержкой.
                        _ui.TryEnqueue(() =>
                        {
                            try { OnConfigChanged?.Invoke(); }
                            catch (Exception ex) { _log("OnConfigChanged from DEVICECHANGE ex: " + ex.Message); }
                        });
                    }
                    break;
                }

                case WM_POWERBROADCAST:
                    HandlePower(wParam.ToInt32(), lParam);
                    break;
            }
        }
        catch (Exception ex) { _log("DisplayEvents WndProc ex: " + ex.Message); }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    void HandlePower(int code, IntPtr lParam)
    {
        switch (code)
        {
            case PBT_APMSUSPEND:
                _log("PBT_APMSUSPEND: system going to sleep");
                _ddc.SuspendDdc(30000, "system suspend");
                break;
            case PBT_APMRESUMEAUTOMATIC:
                _log("PBT_APMRESUMEAUTOMATIC: system resumed");
                _ddc.SuspendDdc(5000, "system resume");
                break;
            case PBT_POWERSETTINGCHANGE:
                if (lParam == IntPtr.Zero) return;
                var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                if (setting.PowerSetting == GUID_MONITOR_POWER_ON)
                {
                    if (setting.Data == 1)
                    {
                        _log("Monitor DPMS ON");
                        // При запуске приложения это событие приходит СРАЗУ (мониторы уже давно включены).
                        // Не суспендим впустую в первые 5 секунд после старта.
                        if (Environment.TickCount - _startTick > 5000)
                            _ddc.SuspendDdc(3000, "DPMS wake");
                    }
                    else _log("Monitor DPMS OFF");
                }
                else if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                {
                    _log($"Console display state: {setting.Data}");
                    if (setting.Data == 1 && Environment.TickCount - _startTick > 5000)
                        _ddc.SuspendDdc(2000, "display state on");
                }
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_monitorPowerNotify != IntPtr.Zero) UnregisterPowerSettingNotification(_monitorPowerNotify);
        if (_consoleDisplayNotify != IntPtr.Zero) UnregisterPowerSettingNotification(_consoleDisplayNotify);
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        UnregisterClassW(_className, _hInstance);
        _proc = null;
    }
}
