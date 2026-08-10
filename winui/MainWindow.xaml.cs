using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.UI;
using WinRT.Interop;

namespace MonitorTune;

public sealed partial class MainWindow : Window
{
    readonly DdcManager ddc;
    readonly Dictionary<string, Slider> bars = new();
    readonly Dictionary<string, TextBlock> vals = new();
    readonly Dictionary<int, Microsoft.UI.Xaml.Controls.Primitives.ToggleButton> linkBtns = new();
    Microsoft.UI.Xaml.DispatcherTimer? _focusPoll;
    bool suppress;
    // Ключи слайдеров, которые user сейчас драгает pointer'ом. Пока drag активен,
    // не сдвигаем ЭТОТ slider из background Raise'ов (иначе Samsung с throttle 200ms
    // "прыгает" когда возвращается old value пока user уже перетащил дальше).
    // Важно: гейт пер-ключевой. Раньше проверялось _draggingKeys.Count == 0 — тогда
    // удержание одного слайдера замораживало ВСЕ, и в sync-режиме зеркала не двигались.
    readonly HashSet<string> _draggingKeys = new();
    // Последнее значение, которое МЫ попросили выставить (свой слайдер / зеркало sync /
    // пара link), с временем запроса. Нужно чтобы отличить устаревший Raise из середины
    // драга от актуального ответа железа.
    readonly Dictionary<string, (long Tick, int Value)> _lastRequested = new();
    const int StaleRaiseWindowMs = 1500;
    public NightMode? NightMode;   // ставится извне (App), кнопка дёргает

    public MainWindow(DdcManager ddc)
    {
        InitializeComponent();
        this.ddc = ddc;

        // Подписи верхней панели — из ресурсов, разметка их не содержит
        SyncLabel.Text = Loc.S("PanelSync");
        ToolTipService.SetToolTip(SyncSwitch, Loc.S("PanelSyncTip"));
        ToolTipService.SetToolTip(RefreshBtn, Loc.S("PanelRefreshTip"));
        ToolTipService.SetToolTip(NightBtn, Loc.S("PanelNightTip"));
        ToolTipService.SetToolTip(SettingsBtn, Loc.S("PanelSettingsTip"));

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));

        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            // НЕ ставим IsAlwaysOnTop — он мешает приходу WindowActivationState.Deactivated.
            // Окно flyout-стиль: показывается по клику, прячется при потере фокуса.
        }
        appWindow.IsShownInSwitchers = false;
        appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        appWindow.Hide();

        Activated += (_, e) =>
        {
            App.LogStatic($"MainWindow.Activated: {e.WindowActivationState}");
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                // Страховка от залипания: если окно ушло из фокуса во время drag'а,
                // PointerReleased/CaptureLost может не прийти — ключ остался бы в наборе
                // и слайдер навсегда перестал реагировать на значения от железа.
                _draggingKeys.Clear();
                appWindow.Hide();
            }
        };

        // Дополнительный механизм: WinUI 3 не всегда шлёт Deactivated на borderless окно.
        // Поллим foreground window каждые 250мс пока flyout показан — если фокус ушёл, прячем.
        _focusPoll = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _focusPoll.Tick += (_, _) =>
        {
            try
            {
                var fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != hwnd)
                {
                    _focusPoll!.Stop();
                    appWindow.Hide();
                }
            }
            catch { }
        };

        SyncSwitch.IsOn = SettingsStore.Current.SyncAllMonitors;
        BuildCards();
    }

    void BuildCards()
    {
        for (int i = 0; i < ddc.Monitors.Count; i++)
            CardsHost.Children.Add(BuildCard(i, ddc.Monitors[i]));
    }

    Border BuildCard(int idx, MonInfo m)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SurfaceStrokeColorFlyoutBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14, 16, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var sp = new StackPanel { Spacing = 6 };

        // Заголовок + кнопка-связка
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GridLength.Auto.Value, GridUnitType.Auto) });

        var title = new TextBlock
        {
            Text = m.Name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 0); header.Children.Add(title);

        var ms = SettingsStore.GetOrCreate(m.Token ?? "");
        var linkBtn = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
        {
            IsChecked = ms.LinkBrightnessContrast,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        linkBtn.Content = new FontIcon { Glyph = "", FontSize = 14 };   // Link symbol
        ToolTipService.SetToolTip(linkBtn, Loc.S("PanelLinkTip"));
        linkBtn.Click += (_, _) =>
        {
            ms.LinkBrightnessContrast = linkBtn.IsChecked == true;
            SettingsStore.Save();
        };
        linkBtns[idx] = linkBtn;
        Grid.SetColumn(linkBtn, 1); header.Children.Add(linkBtn);
        sp.Children.Add(header);
        sp.Children.Add(new Border { Height = 4 });

        // Info-баннер если что-то не так с DDC-каналом этого монитора
        var banner = BuildStatusBanner(m);
        if (banner != null) sp.Children.Add(banner);

        // Три состояния:
        // 1) eDP (встроенный дисплей ноутбука, WMI) — только Brightness, без Contrast
        // 2) обычный DDC/CI — Brightness + Contrast
        // 3) недоступно (DisplayLink / permanentlyUnavailable) — баннер "не работает"
        if (m.IsEdp)
        {
            sp.Children.Add(BuildRow(idx, DdcManager.VCP_BRIGHTNESS, Loc.S("Brightness")));
            // Contrast не показываем — WMI не поддерживает.
        }
        else
        {
            bool ddcAvailable = m.DdcSupported && !m.DisplayLink && m.OutputTechnology != OutputTech.Internal;
            if (!ddcAvailable)
            {
                string reason = m.DisplayLink
                    ? Loc.S("ReasonDisplayLink")
                    : m.OutputTechnology == OutputTech.Internal
                        ? Loc.S("ReasonInternalWmi")
                        : Loc.S("ReasonNoDdc");
                var unavailBanner = new Border
                {
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemControlBackgroundBaseLowBrush"],
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 4, 0, 4),
                    Child = new TextBlock
                    {
                        Text = Loc.F("ControlUnavailable", reason),
                        FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.85,
                    },
                };
                sp.Children.Add(unavailBanner);
            }
            else
            {
                sp.Children.Add(BuildRow(idx, DdcManager.VCP_BRIGHTNESS, Loc.S("Brightness")));
                sp.Children.Add(BuildRow(idx, DdcManager.VCP_CONTRAST, Loc.S("Contrast")));
            }
        }

        // Полная цепочка соединения: GPU → транспорт → монитор
        var chain = BuildConnectionChain(m);
        var info = new TextBlock
        {
            Text = chain,
            FontSize = 10, Opacity = 0.55, Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        ToolTipService.SetToolTip(info, Loc.F("MonitorDetailsTip", chain, m.WriteGapMs, m.VerifyDelayMs));
        sp.Children.Add(info);

        card.Child = sp;
        return card;
    }

    static string BuildConnectionChain(MonInfo m)
    {
        // Компактное имя GPU без "NVIDIA GeForce" и т.п.
        string gpu = ShortGpu(m.AdapterName, m.Gpu);
        string tech = TechLabel(m.OutputTechnology);
        return $"{gpu}  →  {tech}  →  {m.Name}";
    }

    static string ShortGpu(string? full, GpuVendor v)
    {
        if (!string.IsNullOrEmpty(full))
        {
            // "NVIDIA GeForce RTX 4070 Ti SUPER" → "GeForce RTX 4070 Ti SUPER"
            var s = full.Trim();
            foreach (var prefix in new[] { "NVIDIA ", "AMD ", "Intel(R) ", "Intel " })
                if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    s = s.Substring(prefix.Length);
            return s;
        }
        return v.ToString();
    }

    static string TechLabel(OutputTech t) => t switch
    {
        OutputTech.Hdmi => "HDMI",
        OutputTech.DisplayPort => "DisplayPort",
        OutputTech.DpOverThunderbolt => "DisplayPort (Thunderbolt/USB4)",
        OutputTech.UsbC => "USB-C DP Alt",
        OutputTech.Dvi => "DVI",
        OutputTech.Vga => "VGA",
        OutputTech.Internal => Loc.S("TechInternal"),
        OutputTech.Wireless => Loc.S("TechWireless"),
        _ => "?",
    };

    /// <summary>Со скольких неудач подряд считаем связь потерянной, а не разовым сбоем.
    /// Три — потому что столько же нужно, чтобы Ddc перестал долбить монитор серией повторов.</summary>
    const int LostConnectionThreshold = 3;

    static Border? BuildStatusBanner(MonInfo m)
    {
        // Встроенные панели ноутбуков (eDP) физически не имеют ни канала DDC/CI, ни OSD —
        // Enumerate специально ставит им DdcSupported=false. Показывать им caution-баннер
        // "не отвечает по DDC/CI, включите DDC/CI в экранном меню" бессмысленно и пугает:
        // юзер видит предупреждение над РАБОЧИМ WMI-слайдером яркости.
        // Гейтим по OutputTechnology (а не по IsEdp) — иначе при недоступном WMI
        // (IsEdp=false, tech=Internal) юзер получал два противоречащих баннера подряд.
        if (m.OutputTechnology == OutputTech.Internal) return null;

        string? msg = null;
        if (m.DisplayLink)
            msg = Loc.S("BannerDisplayLink");
        else if (!m.DdcSupported)
            msg = Loc.S("BannerNoDdc");
        // Канал был живым при запуске, а потом отвалился: DdcSupported остался true,
        // и раньше ни одно условие не срабатывало — пользователь видел «?» без единого
        // слова объяснения. Отдельный случай, потому что и причина, и лечение другие:
        // так ведут себя мониторы, теряющие DDC/CI после того, как экран погас.
        else if (m.ConsecutiveFailures >= LostConnectionThreshold)
            msg = Loc.S("BannerLostConnection");
        else if (m.ReadOnlyBrightness)
            msg = Loc.S("BannerSystemControlled");
        else if (m.ProbablyFreeSync)
            msg = Loc.S("BannerAdaptiveSync");
        if (msg == null) return null;
        return new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBackgroundBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 4, 0, 4),
            Child = new TextBlock { Text = msg, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.9 },
        };
    }

    Grid BuildRow(int idx, byte vcp, string caption)
    {
        string key = idx + ":" + vcp;
        var g = new Grid { MinHeight = 40 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

        var lbl = new TextBlock
        {
            Text = caption,
            FontSize = 13,
            Opacity = 0.85,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0); g.Children.Add(lbl);

        var sl = new Slider
        {
            Minimum = 0, Maximum = 100,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = key,
            IsThumbToolTipEnabled = false
        };
        sl.ValueChanged += SliderChanged;
        // Отслеживаем drag через pointer capture. Slider внутренне поглощает
        // PointerPressed/Released (thumb template помечает Handled=true), поэтому
        // подписываемся через AddHandler(handledEventsToo:true), иначе события
        // не долетают до нас и _draggingKeys навсегда пуст.
        sl.AddHandler(UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _draggingKeys.Add(key)),
            handledEventsToo: true);
        sl.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _draggingKeys.Remove(key)),
            handledEventsToo: true);
        sl.AddHandler(UIElement.PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _draggingKeys.Remove(key)),
            handledEventsToo: true);
        sl.AddHandler(UIElement.PointerCanceledEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _draggingKeys.Remove(key)),
            handledEventsToo: true);
        // Клавиатура: Arrow/PageUp/PageDown/Home/End тоже двигают slider через RangeBase,
        // ValueChanged fires — но без PointerPressed → _draggingKeys пуст → тот же прыжок.
        // KeyDown → Add, KeyUp → Remove. Плюс LostFocus как safety-net.
        sl.KeyDown += (_, _) => _draggingKeys.Add(key);
        sl.KeyUp += (_, _) => _draggingKeys.Remove(key);
        sl.LostFocus += (_, _) => _draggingKeys.Remove(key);
        // PointerWheel на slider тоже меняет value — блокируем на 500ms после каждого scroll.
        sl.PointerWheelChanged += (_, _) =>
        {
            _draggingKeys.Add(key);
            var wheelTimer = DispatcherQueue.CreateTimer();
            wheelTimer.Interval = TimeSpan.FromMilliseconds(500);
            wheelTimer.IsRepeating = false;
            wheelTimer.Tick += (_, _) => _draggingKeys.Remove(key);
            wheelTimer.Start();
        };
        Grid.SetColumn(sl, 1); g.Children.Add(sl);
        bars[key] = sl;

        var v = new TextBlock
        {
            Text = "…",
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(v, 2); g.Children.Add(v);
        vals[key] = v;
        return g;
    }

    void SliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (suppress) return;
        var sl = (Slider)sender;
        string key = (string)sl.Tag;
        var p = key.Split(':');
        int idx = int.Parse(p[0]); byte vcp = byte.Parse(p[1]);
        int v = (int)Math.Round(sl.Value);

        ApplyValue(idx, vcp, v, fromUser: true);
    }

    /// <summary>Применяет значение с учётом sync-all и link-bc, обновляя UI и DDC.</summary>
    public void ApplyValue(int idx, byte vcp, int v, bool fromUser)
    {
        SetUiValue(idx, vcp, v);
        ddc.Request(idx, vcp, v);

        if (!fromUser) return;

        // Связка яркость↔контраст у данного монитора
        var m = ddc.Monitors[idx];
        var ms = SettingsStore.GetOrCreate(m.Token ?? "");
        if (ms.LinkBrightnessContrast)
        {
            byte other = vcp == DdcManager.VCP_BRIGHTNESS ? DdcManager.VCP_CONTRAST : DdcManager.VCP_BRIGHTNESS;
            SetUiValue(idx, other, v);
            ddc.Request(idx, other, v);
        }

        // Синхронизация со всеми мониторами
        if (SettingsStore.Current.SyncAllMonitors)
        {
            for (int j = 0; j < ddc.Monitors.Count; j++)
            {
                if (j == idx) continue;
                SetUiValue(j, vcp, v);
                ddc.Request(j, vcp, v);
                if (ms.LinkBrightnessContrast)
                {
                    byte other = vcp == DdcManager.VCP_BRIGHTNESS ? DdcManager.VCP_CONTRAST : DdcManager.VCP_BRIGHTNESS;
                    SetUiValue(j, other, v);
                    ddc.Request(j, other, v);
                }
            }
        }
    }

    /// <summary>Записать значение в UI.
    /// fromHardware=false — источник действие юзера (свой слайдер, зеркало sync, пара link):
    ///   двигаем всегда и запоминаем как "мы это просили".
    /// fromHardware=true — пришёл Raise из воркера после реальной записи/чтения VCP:
    ///   двигаем только если слайдер не под пальцем и значение не устарело.</summary>
    void SetUiValue(int idx, byte vcp, int value, bool fromHardware = false)
    {
        string key = idx + ":" + vcp;
        if (!bars.TryGetValue(key, out var sl)) return;

        if (!fromHardware)
        {
            // Действие юзера: слайдер обязан встать на запрошенное значение — и тот, что
            // тянут, и зеркала при sync/link. Запоминаем, чтобы отсеять устаревшие Raise.
            _lastRequested[key] = (Environment.TickCount64, value);
            suppress = true;
            sl.Value = Math.Clamp(value, 0, 100);
            suppress = false;
            vals[key].Text = value + "%";
            return;
        }

        // Значение от железа. Слайдер под пальцем не трогаем вообще — иначе он дёргается
        // против движения мыши (throttle у Samsung по DP доходит до 1 сек).
        bool underPointer = _draggingKeys.Contains(key);

        // Отсекаем устаревший Raise: если мы недавно просили другое значение, то этот
        // ответ относится к более раннему шагу драга. Через StaleRaiseWindowMs доверяем
        // железу снова — так реальное расхождение (монитор не принял значение) всё равно
        // доедет до UI. Именно здесь раньше стоял глобальный гейт _draggingKeys.Count == 0,
        // из-за которого в sync-режиме зеркальные слайдеры не двигались совсем.
        bool stale = _lastRequested.TryGetValue(key, out var last)
                     && last.Value != value
                     && Environment.TickCount64 - last.Tick < StaleRaiseWindowMs;

        if (!underPointer && !stale)
        {
            suppress = true;
            sl.Value = Math.Clamp(value, 0, 100);
            suppress = false;
            vals[key].Text = value + "%";
        }
        else if (!underPointer)
        {
            // Слайдер оставляем на пользовательской позиции, но подтверждённое железом
            // значение показываем в тексте — видно если монитор реально не принял.
            vals[key].Text = value + "%";
        }
    }

    public void SetValue(int idx, byte vcp, int value)
    {
        string key = idx + ":" + vcp;
        if (!bars.TryGetValue(key, out var sl)) return;
        if (value < 0)
        {
            vals[key].Text = "?";
            sl.IsEnabled = false;
            sl.Opacity = 0.5;
            return;
        }
        SetUiValue(idx, vcp, value, fromHardware: true);
        sl.IsEnabled = true;
        sl.Opacity = 1.0;
    }

    /// <summary>OnValue handler — маппит MonId в текущий idx, отбрасывает stale события.</summary>
    public void OnValueUpdate(ValueUpdate u)
    {
        // Устаревшее поколение — карточек уже нет.
        if (u.Generation != ddc.CurrentGeneration) return;
        int mapped = -1;
        var mons = ddc.Monitors;
        for (int i = 0; i < mons.Count; i++) { if (mons[i].Id == u.MonId) { mapped = i; break; } }
        if (mapped < 0) return;
        SetValue(mapped, u.Vcp, u.Value);
    }

    public void SetSupported(int idx, byte vcp, bool supported, int value)
    {
        string key = idx + ":" + vcp;
        if (!bars.TryGetValue(key, out var sl)) return;
        if (!supported) { sl.IsEnabled = false; vals[key].Text = "n/a"; vals[key].Opacity = 0.5; return; }
        SetValue(idx, vcp, value);
    }

    void OnSyncToggled(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.SyncAllMonitors = SyncSwitch.IsOn;
        SettingsStore.Save();
    }

    void OnNightClick(object sender, RoutedEventArgs e)
    {
        NightMode?.Toggle();
    }

    volatile bool _refreshInFlight;
    long _lastRefreshTick;

    public void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshMonitorsAsync();

    // Обёртка для внешних вызовов (App.xaml.cs OnConfigChanged).
    public void RefreshMonitors() => _ = RefreshMonitorsAsync();

    /// <summary>Сообщение юзеру о результате обновления списка. Ставится из App,
    /// чтобы показывать уведомление при вызове из меню трея — там кнопки нет,
    /// и раньше дебаунс приводил к полному отсутствию реакции на клик.</summary>
    public Action<string>? OnRefreshFeedback;

    public async System.Threading.Tasks.Task RefreshMonitorsAsync(bool notify = false)
    {
        // Дебаунс: не чаще чем раз в 2 секунды.
        int now = Environment.TickCount;
        if (_refreshInFlight)
        {
            App.LogStatic("RefreshMonitors: уже выполняется, пропуск");
            if (notify) OnRefreshFeedback?.Invoke(Loc.S("RefreshInProgress"));
            return;
        }
        if (unchecked(now - (int)_lastRefreshTick) < 2000)
        {
            App.LogStatic("RefreshMonitors: debounced");
            if (notify) OnRefreshFeedback?.Invoke(Loc.S("RefreshTooSoon"));
            return;
        }
        _refreshInFlight = true;
        _lastRefreshTick = now;
        try
        {
            if (RefreshBtn != null) RefreshBtn.IsEnabled = false;
            // Очистка UI на UI thread ПОСЛЕ того как ddc.Refresh отработал,
            // чтобы карточки не пересобирались с наполовину пересозданным списком.
            App.LogStatic("RefreshMonitors: start");
            await System.Threading.Tasks.Task.Run(() =>
            {
                try { ddc.Refresh(); }
                catch (Exception ex) { App.LogStatic("ddc.Refresh ex: " + ex); }
            });
            bars.Clear();
            vals.Clear();
            linkBtns.Clear();
            _draggingKeys.Clear();   // stale keys — Slider объекты уничтожены
            _lastRequested.Clear();  // индексы мониторов после Refresh другие
            CardsHost.Children.Clear();
            BuildCards();
            ddc.Rescan();
            App.LogStatic("RefreshMonitors: done");
            if (notify)
            {
                var mons = ddc.SnapshotMonitors();
                string names = mons.Count == 0
                    ? Loc.S("RefreshNone")
                    : Loc.F("RefreshFound", mons.Count, string.Join(", ", mons.ConvertAll(x => x.Name)));
                OnRefreshFeedback?.Invoke(names);
            }
        }
        catch (Exception ex) { App.LogStatic("RefreshMonitorsAsync ex: " + ex); }
        finally
        {
            if (RefreshBtn != null) RefreshBtn.IsEnabled = true;
            _refreshInFlight = false;
        }
    }

    void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow();
        w.Activate();
    }

    void OnRootKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd)).Hide();
            e.Handled = true;
        }
    }

    const double TARGET_WIDTH_DIP = 400;

    /// <summary>Подогнать размер окна под контент, но не выше рабочей области дисплея,
    /// на котором окно будет показано. anchor — точка привязки (позиция иконки в трее):
    /// по ней выбирается нужный дисплей, иначе масштаб и границы считались бы по текущей
    /// (случайной) позиции ещё скрытого окна.</summary>
    void FitToContent(Windows.Graphics.PointInt32? anchor = null)
    {
        var root = (FrameworkElement)Content;
        // Принудительный layout pass — без этого DesiredSize может быть 0
        // если карточки только что добавлены или окно ещё не было видимым.
        root.UpdateLayout();
        root.Measure(new Windows.Foundation.Size(TARGET_WIDTH_DIP, double.PositiveInfinity));
        var desired = root.DesiredSize;

        var hwnd = WindowNative.GetWindowHandle(this);
        uint dpi = Native.GetDpiForWindow(hwnd);
        double scale = dpi / 96.0;

        // Страховка от нулевой высоты: минимум по числу карточек. У eDP карточка ниже —
        // нет строки контраста, поэтому считаем по факту наличия контраста.
        double minHeightDip = 50 + 28;
        foreach (var m in ddc.SnapshotMonitors())
            minHeightDip += m.IsEdp ? 100 : 145;
        double useHeight = Math.Max(desired.Height, minHeightDip);

        int w = (int)Math.Ceiling(TARGET_WIDTH_DIP * scale);
        int h = (int)Math.Ceiling(useHeight * scale) + (int)Math.Ceiling(4 * scale);

        // Кламп по рабочей области: при 4+ мониторах контент выше экрана, и без этого
        // нижние карточки оказывались за границей без возможности доступа. Прокрутку
        // обеспечивает ScrollViewer вокруг CardsHost.
        try
        {
            var area = anchor.HasValue
                ? DisplayArea.GetFromPoint(anchor.Value, DisplayAreaFallback.Primary)
                : DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd), DisplayAreaFallback.Primary);
            int maxH = area.WorkArea.Height - 16;
            if (maxH > 200 && h > maxH) h = maxH;
        }
        catch (Exception ex) { App.LogStatic("FitToContent clamp ex: " + ex.Message); }

        var aw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        aw.Resize(new Windows.Graphics.SizeInt32(w, h));
    }

    public void ShowNearIcon(int iconCenterX, int iconTop)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var aw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        var anchor = new Windows.Graphics.PointInt32(iconCenterX, iconTop);

        // Первый замер на скрытом окне возвращает заниженный DesiredSize (WinUI не даёт
        // полноценный layout pass на невидимом Content), поэтому нужен второй проход
        // после Show. Чтобы юзер не видел рывок размера и прыжок позиции, окно
        // показывается прозрачным: Show → точный замер → позиционирование → fade-in.
        FitToContent(anchor);

        var wa = DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Primary).WorkArea;

        void Position()
        {
            var size = aw.Size;
            int w = size.Width;
            int h = size.Height;
            int left = iconCenterX - w / 2;
            int top = iconTop - h - 8;
            if (left < wa.X + 4) left = wa.X + 4;
            if (left + w > wa.X + wa.Width - 4) left = wa.X + wa.Width - w - 4;
            if (top < wa.Y + 4) top = wa.Y + 4;
            if (top + h > wa.Y + wa.Height - 4) top = wa.Y + wa.Height - h - 4;
            aw.Move(new Windows.Graphics.PointInt32(left, top));
        }

        Position();
        // Держим содержимое невидимым: ShowAnim стартует уже на финальной геометрии.
        // Именно RootGrid — тот же элемент, чью Opacity анимирует ShowAnim.
        RootGrid.Opacity = 0;
        aw.Show();
        Activate();
        ForceToTop(hwnd);
        _focusPoll?.Start();

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                FitToContent(anchor);
                Position();
            }
            catch (Exception ex) { App.LogStatic("ShowNearIcon post-show refit ex: " + ex.Message); }
            finally
            {
                // Плавное появление уже на выверенных размере и позиции.
                try { ShowAnim.Begin(); }
                catch (Exception ex)
                {
                    App.LogStatic("ShowAnim ex: " + ex.Message);
                    RootGrid.Opacity = 1;   // не оставить окно невидимым
                }
            }
        });
    }

    /// <summary>Принудительный вывод окна на передний план.
    /// Windows блокирует SetForegroundWindow если у нас нет input focus —
    /// обходим через AttachThreadInput к потоку текущего активного окна.</summary>
    static void ForceToTop(IntPtr hwnd)
    {
        try
        {
            IntPtr foregroundHwnd = Native.GetForegroundWindow();
            uint currentThread = Native.GetCurrentThreadId();
            uint foregroundThread = Native.GetWindowThreadProcessId(foregroundHwnd, out _);
            if (foregroundThread != currentThread)
                Native.AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                Native.BringWindowToTop(hwnd);
                Native.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (foregroundThread != currentThread)
                    Native.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
        catch { }
    }
}
