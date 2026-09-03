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
        ApplyStrings();
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

    /// <summary>Подписи иконки и пунктов меню — из ресурсов, разметка их не содержит.</summary>
    void ApplyStrings()
    {
        Tray.ToolTipText       = Loc.S("TrayTooltip");
        TrayToolTipText.Text   = Loc.S("MenuHeader");
        OpenMenuItem.Text      = Loc.S("MenuOpen");
        RefreshMenuItem.Text   = Loc.S("MenuRefresh");
        UpdateMenuItem.Text    = Loc.S("MenuCheckUpdates");
        AutoStartItem.Text     = Loc.S("MenuAutoStart");
        DiagnosticMenuItem.Text = Loc.S("MenuDiagnostic");
        AboutMenuItem.Text     = Loc.S("MenuAbout");
        ExitMenuItem.Text      = Loc.S("MenuExit");
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

    /// <summary>Убить tray icon явно: предотвращает crash от post-Exit click.</summary>
    public void DisposeAll()
    {
        try { Tray?.Dispose(); } catch { }
    }

    static void CopyFileToZip(System.IO.Compression.ZipArchive zip, string sourcePath, string entryName)
    {
        var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
        using var src = System.IO.File.OpenRead(sourcePath);
        using var dst = entry.Open();
        src.CopyTo(dst);
    }

    /// <summary>Снапшот текущих мониторов для диагностики. Ставится из App, чтобы
    /// TrayIconHost не знал про DdcManager напрямую.</summary>
    public static Func<System.Collections.Generic.List<MonInfo>>? DiagnosticMonitorsSnapshot;

    static volatile bool _diagInProgress;
    async void DiagnosticClick(object sender, RoutedEventArgs e)
    {
        // Собрать диагностический zip на Desktop: log + системная инфа + crash-репорты.
        // Ничего никуда не отправляется — юзер сам решает кому и как передать файл.
        if (_diagInProgress) { App.LogStatic("DiagnosticClick: сбор уже идёт"); return; }
        _diagInProgress = true;

        // Меню закрываем сразу, иначе оно висит замороженным пока идёт сборка.
        try { (Tray.ContextFlyout as MenuFlyout)?.Hide(); } catch { }
        string prevText = DiagnosticMenuItem.Text;
        DiagnosticMenuItem.IsEnabled = false;
        DiagnosticMenuItem.Text = Loc.S("MenuDiagnosticWorking");
        try
        {
            // Вся работа (до 20 МБ логов + WMI-запросы + zip) уходит с UI-потока:
            // раньше это выполнялось синхронно в обработчике и весь интерфейс замирал
            // на несколько секунд без единого индикатора.
            string zipPath = await System.Threading.Tasks.Task.Run(BuildDiagnosticZip);
            App.LogStatic($"diagnostic: сохранён {zipPath}");
            ShowError(Loc.F("DiagnosticDone", System.IO.Path.GetFileName(zipPath)));
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
            ShowError(Loc.F("DiagnosticFailed", ex.Message));
        }
        finally
        {
            DiagnosticMenuItem.Text = prevText;
            DiagnosticMenuItem.IsEnabled = true;
            _diagInProgress = false;
        }
    }

    /// <summary>Собрать zip. Выполняется на пуле потоков — не трогает UI-элементы.
    /// Возвращает путь к готовому архиву.</summary>
    static string BuildDiagnosticZip()
    {
        {
            // Сбрасываем очередь логгера на диск — иначе свежие строки останутся в памяти.
            App.FlushLog();
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
            var zipPath = System.IO.Path.Combine(desktop, $"MoniTune-diagnostic-{stamp}.zip");

            using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                // 1) Лог приложения — используем ТОТ ЖЕ path что App.LOG (запись),
                // иначе на MSIX packaged app path через LocalCacheFolder может не совпасть
                // с виртуализированным путём Environment.SpecialFolder.LocalApplicationData
                // → File.Exists=false → лог не попадает в zip (regression в v1.1.9/1.1.10).
                try
                {
                    var logPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MonitorTune.log");
                    if (System.IO.File.Exists(logPath)) CopyFileToZip(zip, logPath, "MonitorTune.log");
                    var oldLog = logPath + ".old";
                    if (System.IO.File.Exists(oldLog)) CopyFileToZip(zip, oldLog, "MonitorTune.log.old");

                    // Fallback: если MSIX virtualization редиректит write в другой каталог,
                    // попробуем ещё LocalCache\Local — распространённый virtualized path.
                    var altLog = System.IO.Path.Combine(
                        Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path,
                        "Local", "MonitorTune.log");
                    if (System.IO.File.Exists(altLog) && !System.IO.File.Exists(logPath))
                        CopyFileToZip(zip, altLog, "MonitorTune.log");
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

                // 2b) Настройки — какой throttle, какие overrides, состояние тумблеров.
                // Без этого невозможно понять, почему у юзера поведение отличается.
                try
                {
                    var settingsPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MonitorTune", "settings.json");
                    if (System.IO.File.Exists(settingsPath))
                        CopyFileToZip(zip, settingsPath, "settings.json");
                }
                catch (Exception ex) { App.LogStatic("diagnostic: settings copy ex: " + ex.Message); }

                // 2c) Состояние мониторов из самого DdcManager — то, что реально знает
                // приложение: транспорт, шкала VCP, флаги поддержки, последняя ошибка.
                // EnumDisplayMonitors ниже такого не показывает.
                try
                {
                    var entry = zip.CreateEntry("monitors.txt");
                    using var sw = new System.IO.StreamWriter(entry.Open());
                    var mons = DiagnosticMonitorsSnapshot?.Invoke();
                    if (mons == null || mons.Count == 0) sw.WriteLine("(нет данных)");
                    else
                        foreach (var m in mons)
                        {
                            sw.WriteLine($"[{m.ShortId}] {m.Name}");
                            sw.WriteLine($"    device={m.Device} transport={m.OutputTechnology} gpu={m.Gpu} '{m.AdapterName}'");
                            sw.WriteLine($"    isEdp={m.IsEdp} ddcSupported={m.DdcSupported} displayLink={m.DisplayLink} permUnavailable={m.DdcPermanentlyUnavailable}");
                            sw.WriteLine($"    hasBrightness={m.HasBrightness} hasContrast={m.HasContrast} readOnly={m.ReadOnlyBrightness} probablyFreeSync={m.ProbablyFreeSync}");
                            sw.WriteLine($"    brightness={m.Brightness}% (max={m.BrightnessMax}) contrast={m.Contrast}% (max={m.ContrastMax})");
                            sw.WriteLine($"    throttle={m.WriteGapMs}ms verifyDelay={m.VerifyDelayMs}ms lastError=0x{m.LastErrorCode:X}");
                            sw.WriteLine($"    vcpBrightnessUnsupported={m.VcpBrightnessUnsupported} vcpContrastUnsupported={m.VcpContrastUnsupported}");
                        }
                }
                catch (Exception ex) { App.LogStatic("diagnostic: monitors dump ex: " + ex.Message); }

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

            return zipPath;
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
                UpdateMenuItem.Text = Loc.F("MenuUpdateTo", info.Version);
                UpdateMenuItem.Visibility = Visibility.Visible;
            }

            // Юзер уже нажал «Позже» для этой версии — пункт меню обновляем (установка
            // остаётся в один клик), но тост не показываем. Иначе одно и то же
            // напоминание всплывало каждые 4 часа и после каждого выхода из сна.
            if (!info.Mandatory &&
                string.Equals(SettingsStore.Current.DismissedUpdateVersion, info.Version, StringComparison.Ordinal))
            {
                App.LogStatic($"ShowUpdateAvailable: {info.Version} отклонена юзером — тост не показываем");
                return;
            }

            // Microsoft.Windows.AppNotifications — единственный путь для WinUI 3 desktop MSIX
            // где toast click правильно доставляется приложению через NotificationInvoked event.
            // Classic Windows.UI.Notifications требует ComServer + [ComVisible] + CLSID активатор;
            // AppNotificationManager делает это автоматически.
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText(Loc.F("ToastUpdateTitle", info.Version))
                .AddText(info.Notes ?? Loc.S("ToastUpdateBody"))
                .AddArgument("action", "update")
                .AddArgument("version", info.Version)
                .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton(Loc.S("ToastButtonUpdate"))
                    .AddArgument("action", "update")
                    .AddArgument("version", info.Version))
                .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton(Loc.S("ToastButtonLater"))
                    .AddArgument("action", "dismiss")
                    .AddArgument("version", info.Version));
            var notification = builder.BuildNotification();
            // Стабильный Tag — повторное уведомление заменяет предыдущее, а не копится.
            notification.Tag = "monitune-update-available";
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
            Title = Loc.F("ToastDownloadTitle", version),
            Value = 0,
            ValueStringOverride = "0%",
            Status = Loc.S("ToastDownloading"),
        };
        try
        {
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText(Loc.F("ToastDownloadBody", version))
                .AddProgressBar(new Microsoft.Windows.AppNotifications.Builder.AppNotificationProgressBar()
                    .BindTitle().BindValueStringOverride().BindStatus());
            var notification = builder.BuildNotification();
            notification.Tag = tag;
            notification.Progress = progressData;
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex) { App.LogStatic("ShowDownloadProgress ex: " + ex.Message); }

        int lastPercent = -1;
        // SequenceNumber должен строго возрастать: Windows отбрасывает данные с номером
        // не больше уже показанного. Раньше здесь была константа 2 — то есть после первого
        // апдейта прогресс замирал, юзер видел "0%" всю загрузку 86 МБ.
        uint seq = 1;
        return new Progress<double>(frac =>
        {
            int p = (int)(frac * 100);
            if (p == lastPercent) return;
            lastPercent = p;
            var data = new Microsoft.Windows.AppNotifications.AppNotificationProgressData(++seq)
            {
                Title = Loc.F("ToastDownloadTitle", version),
                Value = frac,
                ValueStringOverride = p + "%",
                Status = p < 100 ? Loc.S("ToastDownloading") : Loc.S("ToastInstalling"),
            };
            try
            {
                _ = Microsoft.Windows.AppNotifications.AppNotificationManager.Default.UpdateAsync(data, tag);
            }
            catch (Exception ex) { App.LogStatic("progress update ex: " + ex.Message); }
        });
    }

    // Флаг только для ручной ПРОВЕРКИ обновлений (сетевой запрос без установки).
    // Защита от параллельной УСТАНОВКИ живёт в UpdateService — она общая для этого
    // пути и для клика по тосту (App.InstallPendingUpdate).
    static volatile bool _manualCheckInProgress;
    async void UpdateClick(object sender, RoutedEventArgs e)
    {
        var info = _pendingUpdate;

        // Если update ещё не найден (default "Проверить обновления") — делаем force check.
        if (info == null)
        {
            if (_manualCheckInProgress)
            {
                App.LogStatic("UpdateClick: проверка уже идёт — пропуск повторного клика");
                return;
            }
            _manualCheckInProgress = true;
            try
            {
                App.LogStatic("User clicked 'Проверить обновления' — force check");
                var found = await UpdateService.CheckAsync();
                if (found == null)
                {
                    ShowError(Loc.F("UpdateAlreadyLatest", UpdateService.CurrentVersion()));
                }
                // если found != null — CheckAsync триггернул UpdateAvailable event →
                // ShowUpdateAvailable → UpdateMenuItem.Text заменится на "Обновить до X.Y.Z"
                // → следующий клик уже пойдёт в install path (info != null).
            }
            catch (Exception ex)
            {
                App.LogStatic("Manual check ex: " + ex);
                ShowError(Loc.F("UpdateCheckFailed", ex.Message));
            }
            finally { _manualCheckInProgress = false; }
            return;
        }

        if (UpdateService.InstallInProgress)
        {
            App.LogStatic("UpdateClick: установка уже идёт — пропуск повторного клика");
            return;
        }
        try
        {
            App.LogStatic($"User clicked update → {info.Version}");
            var progress = ShowDownloadProgress(info.Version);
            bool ok = await UpdateService.DownloadAndInstallAsync(info, progress);
            if (ok) App.LogStatic("Update installed — приложение должно перезапуститься");
            else
            {
                // Прогресс-тост уже снят внутри DownloadAndInstallAsync (finally),
                // здесь только сообщение об ошибке.
                ShowError(Loc.F("UpdateInstallFailed", info.Version));
            }
        }
        catch (Exception ex)
        {
            App.LogStatic("UpdateClick ex: " + ex);
            ShowError(Loc.F("UpdateError", ex.Message));
        }
    }

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
                        Loc.S("AutoStartDisabledByUser"),
                    Windows.ApplicationModel.StartupTaskState.DisabledByPolicy =>
                        Loc.S("AutoStartBlockedByPolicy"),
                    _ => Loc.F("AutoStartRefused", newState),
                };
                ShowError(reason);
            }
        }
        catch (Exception ex)
        {
            App.LogStatic("AutoStartClick ex: " + ex.Message);
            // Синхронизируем UI с реальным state (откатим галочку если операция сфейлилась).
            try { AutoStartItem.IsChecked = IsAutostart(); } catch { }
            ShowError(Loc.F("AutoStartChangeFailed", ex.Message));
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

    // Нативное меню (ContextMenuMode=PopupMenu) выполняет привязанную Command и
    // полностью игнорирует Click — проверено: с Click пункты открывались, но ничего
    // не делали. Обработчики те же, просто вызываются через команду.
    public ICommand OpenCmd       => _openCmd       ??= new Relay(() => OpenClick(this, new RoutedEventArgs()));
    public ICommand RefreshCmd    => _refreshCmd    ??= new Relay(() => RefreshClick(this, new RoutedEventArgs()));
    public ICommand UpdateCmd     => _updateCmd     ??= new Relay(() => UpdateClick(this, new RoutedEventArgs()));
    public ICommand AutoStartCmd  => _autoStartCmd  ??= new Relay(() => AutoStartClick(this, new RoutedEventArgs()));
    public ICommand DiagnosticCmd => _diagnosticCmd ??= new Relay(() => DiagnosticClick(this, new RoutedEventArgs()));
    public ICommand AboutCmd      => _aboutCmd      ??= new Relay(() => AboutClick(this, new RoutedEventArgs()));
    public ICommand ExitCmd       => _exitCmd       ??= new Relay(() => ExitClick(this, new RoutedEventArgs()));

    /// <summary>Выполняется на правый клик по иконке, перед показом меню. В режиме
    /// PopupMenu событие MenuFlyout.Opening не поднимается, а галочку автозапуска надо
    /// сверить с реальным StartupTaskState: пользователь мог переключить его в
    /// Параметрах Windows между показами.</summary>
    public ICommand SyncMenuStateCmd => _syncCmd ??= new Relay(() =>
    {
        try { AutoStartItem.IsChecked = IsAutostart(); }
        catch (Exception ex) { App.LogStatic("SyncMenuState ex: " + ex.Message); }
    });

    ICommand? _syncCmd;
    ICommand? _openCmd, _refreshCmd, _updateCmd, _autoStartCmd, _diagnosticCmd, _aboutCmd, _exitCmd;

    sealed class Relay : ICommand
    {
        readonly Action _act;
        public Relay(Action a) { _act = a; }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => _act();
        public event EventHandler? CanExecuteChanged;
    }
}
