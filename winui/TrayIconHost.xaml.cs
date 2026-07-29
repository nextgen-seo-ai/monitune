using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System;
using System.Windows.Input;

namespace MonitorTune;

public sealed partial class TrayIconHost : UserControl
{
    public ICommand LeftClick { get; }

    /// <summary>Открыть панель у tray icon (позиция курсора релевантна — левый клик по иконке).</summary>
    public event Action? OnOpen;
    /// <summary>Открыть панель через контекстное меню — курсор в пункте меню, нужна tray-позиция вместо cursor.</summary>
    public event Action? OnOpenFromMenu;
    public event Action? OnExit;
    public event Action? OnAbout;
    public event Action? OnRefresh;

    public TrayIconHost()
    {
        InitializeComponent();
        LeftClick = new Relay(() => OnOpen?.Invoke());
        AutoStartItem.IsChecked = IsAutostart();
        // Фиксированный GUID для tray icon — Windows tracks позицию иконки в трее
        // через NIF_GUID (по Guid), а не по пути exe. Без этого при каждом MSIX-update
        // путь exe меняется (WindowsApps\MonitorTune_X.X.X.X_...) → Windows считает
        // это новой иконкой и роняет её в overflow "стрелка вверх". Юзер должен
        // вручную перетаскивать после каждого обновления. С GUID — position preserved.
        Tray.Id = new Guid("CE7F9D62-89B4-4A4E-9D3A-4B7C5A2F1E6E");
        Tray.ForceCreate();
        // Pre-warm меню: MenuFlyoutPresenter создаётся только при первом ShowAt.
        // ContextMenuMode=SecondWindow: H.NotifyIcon сам вызывает ShowAt через свой popup host,
        // но первый вызов не имеет полного layout pass → меню обрезано в компактный размер.
        // Fix: сами делаем один ShowAt на off-screen позицию + сразу Hide. Presenter создастся,
        // MeasureOverride отработает, размер закэшируется. Реальный правый клик уже правильный.
        // ContextMenuMode=SecondWindow: H.NotifyIcon рендерит меню в отдельном popup window
        // со своим resource tree. Style на MenuFlyoutPresenter НЕ применяется первый раз —
        // поэтому явно ставим MinWidth на каждый item через code (это гарантирует что
        // presenter не может быть уже суммы items и текст не обрезается).
        const double ItemMinWidth = 320;
        OpenMenuItem.MinWidth = ItemMinWidth;
        RefreshMenuItem.MinWidth = ItemMinWidth;
        UpdateMenuItem.MinWidth = ItemMinWidth;
        AutoStartItem.MinWidth = ItemMinWidth;
        DiagnosticMenuItem.MinWidth = ItemMinWidth;
        AboutMenuItem.MinWidth = ItemMinWidth;
        ExitMenuItem.MinWidth = ItemMinWidth;

        // WinUI 3 MenuFlyoutItem обрабатывает только левый клик. Юзеры трея часто
        // держат курсор на правой кнопке (правый клик открыл меню — пальцу удобно
        // тем же кликом выбрать пункт). Дублируем правый клик через RightTapped.
        HookRightClickAsLeft(OpenMenuItem, OpenClick);
        HookRightClickAsLeft(RefreshMenuItem, RefreshClick);
        HookRightClickAsLeft(UpdateMenuItem, UpdateClick);
        HookRightClickAsLeftToggle(AutoStartItem, AutoStartClick);
        HookRightClickAsLeft(DiagnosticMenuItem, DiagnosticClick);
        HookRightClickAsLeft(AboutMenuItem, AboutClick);
        HookRightClickAsLeft(ExitMenuItem, ExitClick);

        // Первый warmup — при инициализации control (Loaded event).
        Loaded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, WarmupContextMenu);
        };

        // При каждом open меню синхронизируем галочку с реальным StartupTaskState —
        // юзер мог включить/выключить автозапуск через Параметры Windows между показами.
        if (Tray.ContextFlyout is MenuFlyout mfOpening)
        {
            mfOpening.Opening += (_, _) =>
            {
                try { AutoStartItem.IsChecked = IsAutostart(); } catch { }
            };
        }
    }

    void HookRightClickAsLeft(MenuFlyoutItem item, RoutedEventHandler click)
    {
        item.RightTapped += (s, e) =>
        {
            e.Handled = true;
            try { click(item, new RoutedEventArgs()); }
            catch (Exception ex) { App.LogStatic("RightTapped click ex: " + ex.Message); }
            try { (Tray.ContextFlyout as MenuFlyout)?.Hide(); } catch { }
        };
    }

    void HookRightClickAsLeftToggle(ToggleMenuFlyoutItem item, RoutedEventHandler click)
    {
        item.RightTapped += (s, e) =>
        {
            e.Handled = true;
            item.IsChecked = !item.IsChecked;   // ToggleMenuFlyoutItem обычно сам toggle'ится по Click, вручную повторяем
            try { click(item, new RoutedEventArgs()); }
            catch (Exception ex) { App.LogStatic("RightTapped toggle ex: " + ex.Message); }
            try { (Tray.ContextFlyout as MenuFlyout)?.Hide(); } catch { }
        };
    }

    /// <summary>Прогреть context-menu presenter: SecondWindow popup инвалидируется
    /// после DPMS off/on или display topology change. Первый последующий right-click
    /// заново создаёт presenter → снова компактный размер. Вызываем этот метод из
    /// display events чтобы юзер не видел обрезанное меню при возврате мониторов.</summary>
    public void WarmupContextMenu()
    {
        try
        {
            Tray.ShowContextMenu(new System.Drawing.Point(-100000, -100000));
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(50);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                try { (Tray.ContextFlyout as MenuFlyout)?.Hide(); } catch { }
            };
            timer.Start();
        }
        catch (Exception ex) { App.LogStatic("WarmupContextMenu ex: " + ex.Message); }
    }

    static void CopyFileToZip(System.IO.Compression.ZipArchive zip, string sourcePath, string entryName)
    {
        var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
        using var src = System.IO.File.OpenRead(sourcePath);
        using var dst = entry.Open();
        src.CopyTo(dst);
    }

    void DiagnosticClick(object sender, RoutedEventArgs e)
    {
        // Собрать диагностический zip на Desktop: log + системная инфа + crash-репорты.
        // Ничего никуда не отправляется — юзер сам решает кому и как передать файл.
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
            var zipPath = System.IO.Path.Combine(desktop, $"MoniTune-diagnostic-{stamp}.zip");

            using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                // 1) Лог приложения (текущий + rotated .old если есть)
                try
                {
                    var logDir = Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;
                    var logPath = System.IO.Path.Combine(logDir, "MonitorTune.log");
                    if (System.IO.File.Exists(logPath)) CopyFileToZip(zip,logPath, "MonitorTune.log");
                    var oldLog = logPath + ".old";
                    if (System.IO.File.Exists(oldLog)) CopyFileToZip(zip,oldLog, "MonitorTune.log.old");
                }
                catch (Exception ex) { App.LogStatic("diagnostic: log copy ex: " + ex.Message); }

                // 2) Crash dumps (последние 10)
                try
                {
                    var crashDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "crashes");
                    if (System.IO.Directory.Exists(crashDir))
                    {
                        foreach (var f in System.IO.Directory.GetFiles(crashDir, "*.json")
                            .OrderByDescending(p => System.IO.File.GetLastWriteTime(p))
                            .Take(10))
                        {
                            CopyFileToZip(zip,f, "crashes/" + System.IO.Path.GetFileName(f));
                        }
                    }
                }
                catch (Exception ex) { App.LogStatic("diagnostic: crashes copy ex: " + ex.Message); }

                // 3) Системная инфа
                try
                {
                    var entry = zip.CreateEntry("system-info.txt");
                    using var sw = new System.IO.StreamWriter(entry.Open());
                    var v = Windows.ApplicationModel.Package.Current.Id.Version;
                    sw.WriteLine($"MoniTune version: {v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
                    sw.WriteLine($"Timestamp: {DateTime.Now:o}");
                    sw.WriteLine($"OS: {Environment.OSVersion}");
                    sw.WriteLine($"Machine .NET: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
                    sw.WriteLine($"Process arch: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
                    sw.WriteLine($"OS arch: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
                    sw.WriteLine($"UI culture: {System.Globalization.CultureInfo.CurrentUICulture.Name}");
                    sw.WriteLine($"Package family: {Windows.ApplicationModel.Package.Current.Id.FamilyName}");
                    sw.WriteLine();
                    sw.WriteLine("=== Мониторы (EnumDisplayMonitors) ===");
                    try
                    {
                        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Native.RECT r, IntPtr d) =>
                        {
                            var mi = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
                            Native.GetMonitorInfo(h, ref mi);
                            sw.WriteLine($"  {mi.szDevice}  ({r.right - r.left}x{r.bottom - r.top})");
                            return true;
                        }, IntPtr.Zero);
                    }
                    catch (Exception ex) { sw.WriteLine("  EnumDisplayMonitors ex: " + ex.Message); }
                    sw.WriteLine();
                    sw.WriteLine("=== eDP WMI ===");
                    sw.WriteLine($"  Available: {EdpBrightnessService.IsAvailable()}");
                    try
                    {
                        var levels = EdpBrightnessService.GetSupportedLevels();
                        if (levels != null) sw.WriteLine($"  Supported levels ({levels.Length}): {string.Join(",", levels)}");
                    }
                    catch { }
                }
                catch (Exception ex) { App.LogStatic("diagnostic: system-info ex: " + ex.Message); }
            }

            App.LogStatic($"diagnostic: сохранён {zipPath}");
            ShowError($"Диагностика собрана: {System.IO.Path.GetFileName(zipPath)} на Рабочем столе. Отправьте файл разработчику.");
            // Открыть Проводник и выделить файл — юзер сразу видит куда.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{zipPath}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { App.LogStatic("diagnostic: open explorer ex: " + ex.Message); }
        }
        catch (Exception ex)
        {
            App.LogStatic("DiagnosticClick ex: " + ex);
            ShowError("Не удалось собрать диагностику: " + ex.Message);
        }
    }

    void OpenClick(object sender, RoutedEventArgs e) => OnOpenFromMenu?.Invoke();
    void ExitClick(object sender, RoutedEventArgs e) => OnExit?.Invoke();
    void AboutClick(object sender, RoutedEventArgs e) => OnAbout?.Invoke();
    void RefreshClick(object sender, RoutedEventArgs e) => OnRefresh?.Invoke();

    UpdateService.UpdateInfo? _pendingUpdate;
    /// <summary>Последняя известная UpdateInfo — используется toast handler'ом чтобы не
    /// re-CheckAsync (иначе получим второй UpdateAvailable event → дубликат toast).</summary>
    public UpdateService.UpdateInfo? PendingUpdate => _pendingUpdate;
    /// <summary>Показать в трее что доступно обновление (через баллун и активацию пункта меню).</summary>
    public void ShowUpdateAvailable(UpdateService.UpdateInfo info)
    {
        _pendingUpdate = info;
        try
        {
            if (UpdateMenuItem != null)
            {
                UpdateMenuItem.Text = $"Обновить до {info.Version}";
                UpdateMenuItem.Visibility = Visibility.Visible;
            }
            // Microsoft.Windows.AppNotifications — единственный путь для WinUI 3 desktop MSIX
            // где toast click правильно доставляется приложению через NotificationInvoked event.
            // Classic Windows.UI.Notifications требует ComServer + [ComVisible] + CLSID активатор;
            // AppNotificationManager делает это автоматически.
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText($"Доступно обновление MoniTune {info.Version}")
                .AddText(info.Notes ?? "Нажмите чтобы установить обновление")
                .AddArgument("action", "update")
                .AddArgument("version", info.Version)
                .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton("Обновить")
                    .AddArgument("action", "update")
                    .AddArgument("version", info.Version))
                .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton("Позже")
                    .AddArgument("action", "dismiss"));
            var notification = builder.BuildNotification();
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex) { App.LogStatic("ShowUpdateAvailable ex: " + ex.Message); }
    }

    public void ShowError(string message)
    {
        try
        {
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText("MoniTune")
                .AddText(message);
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex) { App.LogStatic("ShowError ex: " + ex.Message); }
    }

    /// <summary>Показать progress toast для download и вернуть IProgress который его обновляет.
    /// Toast имеет Tag = "monitune-update-progress" — последующие Show с тем же Tag заменяют предыдущий,
    /// так что update идёт in-place, без спама уведомлений.</summary>
    public IProgress<double> ShowDownloadProgress(string version)
    {
        const string tag = "monitune-update-progress";
        var progressData = new Microsoft.Windows.AppNotifications.AppNotificationProgressData(1)
        {
            Title = $"Загрузка MoniTune {version}",
            Value = 0,
            ValueStringOverride = "0%",
            Status = "Скачиваю обновление…",
        };
        try
        {
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText($"Обновление MoniTune {version}")
                .AddProgressBar(new Microsoft.Windows.AppNotifications.Builder.AppNotificationProgressBar()
                    .BindTitle().BindValueStringOverride().BindStatus());
            var notification = builder.BuildNotification();
            notification.Tag = tag;
            notification.Progress = progressData;
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex) { App.LogStatic("ShowDownloadProgress ex: " + ex.Message); }

        int lastPercent = -1;
        return new Progress<double>(frac =>
        {
            int p = (int)(frac * 100);
            if (p == lastPercent) return;
            lastPercent = p;
            var data = new Microsoft.Windows.AppNotifications.AppNotificationProgressData(2)
            {
                Title = $"Загрузка MoniTune {version}",
                Value = frac,
                ValueStringOverride = p + "%",
                Status = p < 100 ? "Скачиваю обновление…" : "Устанавливаю…",
            };
            try
            {
                _ = Microsoft.Windows.AppNotifications.AppNotificationManager.Default.UpdateAsync(data, tag);
            }
            catch (Exception ex) { App.LogStatic("progress update ex: " + ex.Message); }
        });
    }

    static volatile bool _updateInProgress;
    async void UpdateClick(object sender, RoutedEventArgs e)
    {
        var info = _pendingUpdate;
        if (info == null) return;
        // Двойной клик "Обновить до X" в трее или toast → защита от параллельного download.
        if (_updateInProgress)
        {
            App.LogStatic("UpdateClick: уже идёт download — пропуск повторного клика");
            return;
        }
        _updateInProgress = true;
        try
        {
            App.LogStatic($"User clicked update → {info.Version}");
            var progress = ShowDownloadProgress(info.Version);
            bool ok = await UpdateService.DownloadAndInstallAsync(info, progress);
            if (ok) App.LogStatic("Update installed — приложение должно перезапуститься");
            else
            {
                // Убрать зависший progress toast ПЕРЕД показом error — иначе два уведомления параллельно
                // (юзер видит "50% Скачиваю" и "Не удалось" одновременно, путается).
                RemoveProgressToast();
                ShowError($"Не удалось установить обновление {info.Version}. Проверьте соединение и попробуйте позже.");
            }
        }
        catch (Exception ex)
        {
            App.LogStatic("UpdateClick ex: " + ex);
            RemoveProgressToast();
            ShowError("Ошибка обновления: " + ex.Message);
        }
        finally { _updateInProgress = false; }
    }

    static void RemoveProgressToast() => RemoveProgressToastStatic();

    /// <summary>Убрать зависший progress-toast (например при download fail) — иначе юзер видит
    /// и progress "50% Скачиваю", и error "Не удалось" одновременно.</summary>
    public static void RemoveProgressToastStatic()
    {
        try
        {
            _ = Microsoft.Windows.AppNotifications.AppNotificationManager.Default.RemoveByTagAsync("monitune-update-progress");
        }
        catch (Exception ex) { App.LogStatic("RemoveProgressToast ex: " + ex.Message); }
    }
    async void AutoStartClick(object sender, RoutedEventArgs e)
    {
        bool wanted = AutoStartItem.IsChecked;
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            Windows.ApplicationModel.StartupTaskState newState;
            if (wanted)
            {
                newState = await task.RequestEnableAsync();
                App.LogStatic($"StartupTask RequestEnable → {newState}");
            }
            else
            {
                task.Disable();
                newState = Windows.ApplicationModel.StartupTaskState.Disabled;
                App.LogStatic("StartupTask disabled");
            }

            // Сверяем UI с реальным state — Windows может отказать в enable
            // (DisabledByUser: юзер выключил в Settings→Автозапуск,
            //  DisabledByPolicy: групповая политика запрещает).
            bool actuallyOn = newState == Windows.ApplicationModel.StartupTaskState.Enabled
                           || newState == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            if (AutoStartItem.IsChecked != actuallyOn) AutoStartItem.IsChecked = actuallyOn;

            // Если хотели включить но Windows заблокировала — объяснить юзеру.
            if (wanted && !actuallyOn)
            {
                string reason = newState switch
                {
                    Windows.ApplicationModel.StartupTaskState.DisabledByUser =>
                        "Автозапуск отключён вами в Настройках Windows. Включите его в:\nПараметры → Приложения → Автозагрузка → MoniTune.",
                    Windows.ApplicationModel.StartupTaskState.DisabledByPolicy =>
                        "Автозапуск запрещён групповой политикой (обычно на рабочих ПК). Обратитесь к администратору.",
                    _ => $"Windows не разрешила автозапуск (state={newState}). Попробуйте включить вручную в Параметры → Приложения → Автозагрузка.",
                };
                ShowError(reason);
            }
        }
        catch (Exception ex)
        {
            App.LogStatic("AutoStartClick ex: " + ex.Message);
            // Синхронизируем UI с реальным state (откатим галочку если операция сфейлилась).
            try { AutoStartItem.IsChecked = IsAutostart(); } catch { }
            ShowError("Не удалось изменить автозапуск: " + ex.Message);
        }
    }

    // Автозапуск через StartupTask API (правильный путь для MSIX-приложений).
    // Объявлен в Package.appxmanifest как windows.startupTask с TaskId="MonitorTuneStartup".
    const string StartupTaskId = "MonitorTuneStartup";

    static bool IsAutostart()
    {
        try
        {
            var task = Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId).AsTask().GetAwaiter().GetResult();
            return task.State == Windows.ApplicationModel.StartupTaskState.Enabled
                || task.State == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }
        catch { return false; }
    }

    sealed class Relay : ICommand
    {
        readonly Action _act;
        public Relay(Action a) { _act = a; }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => _act();
        public event EventHandler? CanExecuteChanged;
    }
}
