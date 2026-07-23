using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;

namespace MonitorTune;

public partial class App : Application
{
    MainWindow? _window;
    internal TrayWindow? _trayWindow;
    DdcManager? _ddc;
    static DispatcherQueue? _ui;
    NightMode? _night;
    HotkeyService? _hotkeys;
    KeepAwakeService? _keepAwake;
    DisplayEventsService? _displayEvents;

    static readonly string LOG = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorTune.log");
    static readonly string LOG_OLD = LOG + ".old";
    const long LOG_MAX_BYTES = 10L * 1024 * 1024;  // 10 MB на файл (итого до 20 MB с .old)

    // Ring buffer последних 500 строк лога — для приложения crash-репорта.
    static readonly System.Collections.Concurrent.ConcurrentQueue<string> _logRing = new();
    const int LOG_RING_SIZE = 500;

    // Async logger — не блокирует UI thread при slider drag.
    static readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue = new();
    static readonly System.Threading.ManualResetEventSlim _logSignal = new(false);
    static volatile bool _logRunning = true;
    static readonly System.Threading.Thread _logWriter = StartLogWriter();

    static System.Threading.Thread StartLogWriter()
    {
        var t = new System.Threading.Thread(LogWriterLoop) { IsBackground = true, Name = "MonitorTune.LogWriter" };
        t.Start();
        return t;
    }

    static int _writesSinceCheck = 0;
    static void CheckRotation()
    {
        // Не проверяем размер на каждой записи — только раз в 200 сбросов буфера.
        if (System.Threading.Interlocked.Increment(ref _writesSinceCheck) < 200) return;
        _writesSinceCheck = 0;
        try
        {
            var fi = new System.IO.FileInfo(LOG);
            if (fi.Exists && fi.Length > LOG_MAX_BYTES)
            {
                try { if (System.IO.File.Exists(LOG_OLD)) System.IO.File.Delete(LOG_OLD); } catch { }
                try { System.IO.File.Move(LOG, LOG_OLD); } catch { }
            }
        }
        catch { }
    }

    static void LogWriterLoop()
    {
        var buf = new System.Text.StringBuilder(4096);
        while (_logRunning || !_logQueue.IsEmpty)
        {
            _logSignal.Wait(500);
            _logSignal.Reset();
            buf.Clear();
            while (_logQueue.TryDequeue(out var line)) buf.Append(line).Append('\n');
            if (buf.Length == 0) continue;
            CheckRotation();
            try { System.IO.File.AppendAllText(LOG, buf.ToString()); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Log write failed: " + ex); }
        }
    }

    static void L(string s)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + s;
        _logQueue.Enqueue(line);
        _logRing.Enqueue(line);
        while (_logRing.Count > LOG_RING_SIZE) _logRing.TryDequeue(out _);
        _logSignal.Set();
    }

    public static void LogStatic(string s) => L(s);
    /// <summary>Snapshot последних N строк для crash-репорта.</summary>
    public static string[] LogTailSnapshot() => _logRing.ToArray();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { System.IO.File.Delete(LOG); } catch { }
        L("OnLaunched");
        AppDomain.CurrentDomain.UnhandledException += (s, e) => L("UNHANDLED: " + e.ExceptionObject);
        UnhandledException += (s, e) => { L("XAML UNHANDLED: " + e.Exception); e.Handled = true; };
        // .NET async unobserved:
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        { L("UnobservedTask: " + e.Exception); e.SetObserved(); };

        _ui = DispatcherQueue.GetForCurrentThread();

        SettingsStore.Load();
        L("settings loaded; sync=" + SettingsStore.Current.SyncAllMonitors);

        // Если мы поднялись после auto-update (через scheduled task или вручную) —
        // почистить остаток scheduled task и marker. Идемпотентно.
        UpdateService.ResumeAfterUpdateIfNeeded();

        _ddc = new DdcManager();
        DdcManager.Log = L;
        EdpBrightnessService.Log = L;
        _ddc.OnValue += u => _ui!.TryEnqueue(() => _window?.OnValueUpdate(u));
        _ddc.OnInitDone += () => _ui!.TryEnqueue(InitDone);
        try { _ddc.Enumerate(); } catch (Exception ex) { L("Enumerate ex: " + ex); }
        L("monitors: " + _ddc.Monitors.Count);
        foreach (var m in _ddc.Monitors) L("  " + m.Name + " @ " + m.Device);

        _window = new MainWindow(_ddc);
        L("window created");

        _trayWindow = new TrayWindow();
        _trayWindow.Tray.OnOpen += ShowFlyout;
        _trayWindow.Tray.OnOpenFromMenu += () => ShowFlyoutAt(useTrayPosition: true);
        _trayWindow.Tray.OnExit += Exit;
        _trayWindow.Tray.OnAbout += ShowAbout;
        _trayWindow.Tray.OnRefresh += () => _window?.RefreshMonitors();
        _trayWindow.Activate();
        // После активации visual tree готов — теперь окно можно прятать.
        _trayWindow.HideHard();
        L("tray window created, activated, hidden");

        // (был SystemEvents.DisplaySettingsChanged — заменён на WM_DISPLAYCHANGE через DisplayEventsService)

        _night = new NightMode(_ddc, _ui);
        _window!.NightMode = _night;

        HotkeyService.Log = L;
        _hotkeys = new HotkeyService(_ui);
        _hotkeys.OnHotkey += OnHotkey;
        try { _hotkeys.Install(); }
        catch (Exception ex) { L("HotkeyService.Install ex: " + ex); }
        L("night + hotkeys installed");

        KeepAwakeService.Log = L;
        _keepAwake = new KeepAwakeService();
        _keepAwake.Apply();
        SettingsStore.Changed += () => _keepAwake?.Apply();

        // Слушаем WM_DISPLAYCHANGE / WM_DEVICECHANGE / WM_POWERBROADCAST
        // вместо костыля с рестартом приложения на каждый chirp.
        _displayEvents = new DisplayEventsService(_ddc, _ui, L);
        _displayEvents.OnConfigChanged += () =>
        {
            // Дебаунс + Refresh (не рестарт).
            _configRestartTimer?.Dispose();
            _configRestartTimer = new System.Threading.Timer(_ => _ui!.TryEnqueue(() =>
            {
                try { _window?.RefreshMonitors(); L("RefreshMonitors after WM_DISPLAYCHANGE"); }
                catch (Exception ex) { L("RefreshMonitors ex: " + ex); }
            }), null, 1500, System.Threading.Timeout.Infinite);
        };
        _displayEvents.OnSystemResumed += () =>
        {
            // Пробуждение — проверить обновления. Могло пройти сутки во сне.
            if (SettingsStore.Current.AutoCheckUpdates)
            {
                try { UpdateService.CheckInBackground(); L("update check after resume"); }
                catch (Exception ex) { L("update check after resume ex: " + ex.Message); }
            }
        };
        _displayEvents.Install();

        _ddc.Start();

        // Crash reporter — пишет в LocalCache/crashes/*.json при UnhandledException.
        CrashReporter.Install();

        // Auto-update check — silent, background, tray notification при находке.
        if (SettingsStore.Current.AutoCheckUpdates)
        {
            UpdateService.UpdateAvailable += info => _ui!.TryEnqueue(() =>
            {
                try { _trayWindow?.Tray?.ShowUpdateAvailable(info); }
                catch (Exception ex) { L("ShowUpdateAvailable ex: " + ex); }
            });
            UpdateService.CheckInBackground();
            // Периодическая проверка — приложение может жить в трее сутками,
            // без этого узнало бы про обновления только на следующем рестарте.
            // 4 часа = не спам GitHub API, но новые релизы не пропустим на день.
            _updateCheckTimer = new System.Threading.Timer(_ =>
            {
                try { UpdateService.CheckInBackground(); L("periodic update check triggered"); }
                catch (Exception ex) { L("periodic update check ex: " + ex.Message); }
            }, null, TimeSpan.FromHours(4), TimeSpan.FromHours(4));
        }

        // AppNotificationManager: регистрация ОБЯЗАТЕЛЬНА до первого toast, иначе
        // клик по нему не доставляется. Единственный поддерживаемый путь для WinUI 3 desktop
        // MSIX (classic Windows.UI.Notifications требует COM activator + CLSID).
        try
        {
            var mgr = Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
            mgr.NotificationInvoked += OnToastInvoked;
            mgr.Register();
            L("AppNotifications registered");
        }
        catch (Exception ex) { L("AppNotifications register ex: " + ex.Message); }

        L("OnLaunched done");

        // Первый запуск — приложение могло быть поднято кликом по toast.
        // Здесь обрабатываем ТОЛЬКО AppNotification kind, обычный Launch — не reason
        // показывать flyout (первый запуск не должен вести себя как клик на трее).
        try
        {
            var firstActivation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (firstActivation?.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification)
                HandleRedirectedActivation(firstActivation);
            else
                L($"First activation kind={firstActivation?.Kind} — no action");
        }
        catch (Exception ex) { L("first activation check ex: " + ex.Message); }
    }

    static void OnToastInvoked(Microsoft.Windows.AppNotifications.AppNotificationManager sender,
                                Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs args)
    {
        try
        {
            var action = args.Arguments.TryGetValue("action", out var a) ? a : "";
            LogStatic($"Toast invoked: action='{action}'");
            if (action == "update") _ui?.TryEnqueue(InstallPendingUpdate);
        }
        catch (Exception ex) { LogStatic("OnToastInvoked ex: " + ex); }
    }

    /// <summary>Обработка первичной активации (запуск процесса кликом по toast). Не вызывать для
    /// последующих активаций уже-живого instance — те приходят через NotificationInvoked.</summary>
    public static void HandleRedirectedActivation(Microsoft.Windows.AppLifecycle.AppActivationArguments args)
    {
        if (args == null) return;
        try
        {
            LogStatic($"Activation kind={args.Kind}");
            if (args.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification)
            {
                var data = args.Data as Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs;
                var action = data?.Arguments != null && data.Arguments.TryGetValue("action", out var a) ? a : "";
                LogStatic($"AppNotification activation action='{action}'");
                if (action == "update") _ui?.TryEnqueue(InstallPendingUpdate);
            }
            else if (args.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch)
            {
                // Второй запуск через shortcut/plate: main instance уже жив,
                // единственная разумная реакция — показать flyout под курсором.
                _ui?.TryEnqueue(() => (Current as App)?.ShowFlyout());
            }
        }
        catch (Exception ex) { LogStatic("HandleRedirectedActivation ex: " + ex); }
    }

    static async void InstallPendingUpdate()
    {
        try
        {
            // Используем уже проверенный info из момента показа toast — не re-checkAsync,
            // иначе получим второй toast (CheckAsync триггерит UpdateAvailable event).
            var app = Current as App;
            // fireEvent:false — иначе UpdateAvailable → ShowUpdateAvailable → дубликат toast
            // прямо во время установки (сценарий: пользователь кликает persistent toast из Action
            // Center после рестарта, _pendingUpdate ещё null, идём в fallback CheckAsync).
            var info = app?._trayWindow?.Tray?.PendingUpdate ?? await UpdateService.CheckAsync(fireEvent: false);
            if (info == null)
            {
                LogStatic("Toast install: пусто — обновление уже установлено или проверка не удалась");
                app?._trayWindow?.Tray?.ShowError("Обновление недоступно. Возможно вы уже на последней версии или нет соединения с GitHub.");
                return;
            }
            LogStatic($"Toast install: качаю {info.Version}");
            var progress = app?._trayWindow?.Tray?.ShowDownloadProgress(info.Version);
            bool ok = await UpdateService.DownloadAndInstallAsync(info, progress);
            if (!ok) app?._trayWindow?.Tray?.ShowError($"Не удалось установить обновление {info.Version}. Смотрите лог в LocalCache.");
        }
        catch (Exception ex)
        {
            LogStatic("InstallPendingUpdate ex: " + ex);
            (Current as App)?._trayWindow?.Tray?.ShowError("Ошибка установки: " + ex.Message);
        }
    }

    void InitDone()
    {
        if (_window == null || _ddc == null) return;
        for (int i = 0; i < _ddc.Monitors.Count; i++)
        {
            var m = _ddc.Monitors[i];
            _window.SetSupported(i, DdcManager.VCP_BRIGHTNESS, m.HasBrightness, m.Brightness);
            _window.SetSupported(i, DdcManager.VCP_CONTRAST, m.HasContrast, m.Contrast);
        }
    }

    internal void ShowFlyout() => ShowFlyoutAt(useTrayPosition: false);

    internal void ShowFlyoutAt(bool useTrayPosition)
    {
        L($"ShowFlyout useTrayPosition={useTrayPosition}");
        if (_window == null) return;
        Native.POINT pt;
        Native.GetCursorPos(out pt);
        if (useTrayPosition)
        {
            // Вызов через "Открыть панель" из контекстного меню — курсор в позиции пункта меню,
            // но menu показан на том же дисплее где tray icon. Значит cursor screen = tray screen.
            // DisplayArea.GetFromPoint возвращает правильный display (не hardcoded Primary),
            // работает и с multi-monitor + secondary-only taskbar.
            var cursorPoint = new Windows.Graphics.PointInt32(pt.X, pt.Y);
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
                cursorPoint, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            pt = new Native.POINT { X = area.X + area.Width - 24, Y = area.Y + area.Height };
        }
        _window.ShowNearIcon(pt.X, pt.Y);
    }

    AboutWindow? _aboutWindow;
    void ShowAbout()
    {
        L("ShowAbout");
        try
        {
            // Окно одно: если уже открыто — активируем
            if (_aboutWindow != null) { _aboutWindow.Activate(); L("about activated existing"); return; }
            _aboutWindow = new AboutWindow();
            _aboutWindow.Closed += (_, _) => { _aboutWindow = null; L("about closed"); };
            _aboutWindow.Activate();
            L("about activated new");
        }
        catch (Exception ex) { L("ShowAbout ex: " + ex); }
    }

    System.Threading.Timer? _restartTimer;
    System.Threading.Timer? _configRestartTimer;
    System.Threading.Timer? _updateCheckTimer;
    void OnDisplaysChanged(object? s, EventArgs e)
    {
        L("DisplaySettingsChanged — debounce restart");
        _restartTimer?.Dispose();
        // Дебаунс 1.5с: смена конфигурации обычно идёт каскадом событий.
        _restartTimer = new System.Threading.Timer(_ => _ui?.TryEnqueue(() =>
        {
            try
            {
                var pfn = Windows.ApplicationModel.Package.Current.Id.FamilyName;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{pfn}!App",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { L("restart ex: " + ex); }
            Exit();
        }), null, 1500, System.Threading.Timeout.Infinite);
    }

    void OnHotkey(string action)
    {
        if (_ddc == null || _window == null) return;
        int step = SettingsStore.Current.StepSize;
        if (action == "night_mode") { _night?.Toggle(); return; }
        byte vcp = action.StartsWith("brightness") ? DdcManager.VCP_BRIGHTNESS : DdcManager.VCP_CONTRAST;
        int delta = action.EndsWith("_up") ? step : -step;
        if (_ddc.Monitors.Count == 0) return;
        var m = _ddc.Monitors[0];
        int cur = vcp == DdcManager.VCP_BRIGHTNESS ? m.Brightness : m.Contrast;
        if (cur < 0) cur = 50;
        int next = Math.Clamp(cur + delta, 0, 100);
        _window.ApplyValue(0, vcp, next, fromUser: true);
    }

    void Exit()
    {
        L("Exit");
        _displayEvents?.Dispose();
        _hotkeys?.Uninstall();
        _keepAwake?.Dispose();
        _night?.Stop();
        _ddc?.Stop();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

}
