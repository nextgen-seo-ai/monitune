using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MonitorTune;

// Глобальные горячие клавиши через RegisterHotKey на отдельном message-only HWND.
// Никакого subclassing XAML-окна (которое WinUI перетирает своим WndProc).
public sealed class HotkeyService : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
    const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    static readonly IntPtr HWND_MESSAGE = new(-3);

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
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandleW(string? m);

    readonly DispatcherQueue _ui;
    readonly Dictionary<int, Action> _handlers = new();
    readonly string _className = "MonitorTuneHotkeyHost_" + Guid.NewGuid().ToString("N");
    WndProcDelegate? _proc;
    IntPtr _hInstance;
    IntPtr _hwnd;
    int _nextId = 1000;
    bool _disposed;

    public event Action<string>? OnHotkey;
    public static Action<string> Log { get; set; } = _ => { };

    public HotkeyService(DispatcherQueue ui)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    public void Install()
    {
        if (_hwnd != IntPtr.Zero) return;
        _proc = WndProc;
        _hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _proc,
            hInstance = _hInstance,
            lpszClassName = _className,
        };
        ushort atom = RegisterClassExW(ref wc);
        if (atom == 0)
        {
            int err = Marshal.GetLastWin32Error();
            Log($"HotkeyService: RegisterClassEx FAILED err={err}");
            throw new Win32Exception(err);
        }
        _hwnd = CreateWindowExW(0, _className, "", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Log($"HotkeyService: CreateWindowEx FAILED err={err}");
            UnregisterClassW(_className, _hInstance);
            throw new Win32Exception(err);
        }
        Log($"HotkeyService: message-only HWND=0x{_hwnd.ToInt64():X}");
        RegisterAllFromSettings();
    }

    public void Uninstall()
    {
        if (_hwnd == IntPtr.Zero) return;
        foreach (var id in _handlers.Keys) UnregisterHotKey(_hwnd, id);
        _handlers.Clear();
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        UnregisterClassW(_className, _hInstance);
        _proc = null;
        Log("HotkeyService: uninstalled");
    }

    public void RegisterAllFromSettings()
    {
        if (_hwnd == IntPtr.Zero) return;
        foreach (var id in _handlers.Keys) UnregisterHotKey(_hwnd, id);
        _handlers.Clear();
        if (!SettingsStore.Current.Hotkeys.Enabled)
        {
            Log("HotkeyService: hotkeys disabled in settings");
            return;
        }
        var hk = SettingsStore.Current.Hotkeys;
        if (hk.BrightnessUp.IsValid)    Register("brightness_up",    hk.BrightnessUp);
        if (hk.BrightnessDown.IsValid)  Register("brightness_down",  hk.BrightnessDown);
        if (hk.ContrastUp.IsValid)      Register("contrast_up",      hk.ContrastUp);
        if (hk.ContrastDown.IsValid)    Register("contrast_down",    hk.ContrastDown);
        if (hk.ToggleNightMode.IsValid) Register("night_mode",       hk.ToggleNightMode);
    }

    void Register(string name, Hotkey h)
    {
        int id = _nextId++;
        uint mods = MOD_NOREPEAT;
        if ((h.Mod & HotkeyMod.Alt)   != 0) mods |= MOD_ALT;
        if ((h.Mod & HotkeyMod.Ctrl)  != 0) mods |= MOD_CONTROL;
        if ((h.Mod & HotkeyMod.Shift) != 0) mods |= MOD_SHIFT;
        if ((h.Mod & HotkeyMod.Win)   != 0) mods |= MOD_WIN;
        bool ok = RegisterHotKey(_hwnd, id, mods, (uint)h.Key);
        if (ok)
        {
            _handlers[id] = () => OnHotkey?.Invoke(name);
            Log($"HotkeyService: OK name={name} id={id} mods=0x{mods:X} vk=0x{h.Key:X}");
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            string hint = err == ERROR_HOTKEY_ALREADY_REGISTERED
                ? " (1409=занят другим процессом; типично GPU rotation hotkey на Ctrl+Alt+Стрелки)" : "";
            Log($"HotkeyService: FAIL name={name} mods=0x{mods:X} vk=0x{h.Key:X} err={err}{hint}");
        }
    }

    IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_handlers.TryGetValue(id, out var act))
            {
                _ui.TryEnqueue(() => { try { act(); } catch (Exception ex) { Log("handler ex: " + ex); } });
            }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
        GC.SuppressFinalize(this);
    }
}
