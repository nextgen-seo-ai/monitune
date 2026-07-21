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
    public NightMode? NightMode;   // ставится извне (App), кнопка дёргает

    public MainWindow(DdcManager ddc)
    {
        InitializeComponent();
        this.ddc = ddc;

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
                appWindow.Hide();
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
        ToolTipService.SetToolTip(linkBtn, "Связать яркость и контраст");
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
            sp.Children.Add(BuildRow(idx, DdcManager.VCP_BRIGHTNESS, "Яркость"));
            // Contrast не показываем — WMI не поддерживает.
        }
        else
        {
            bool ddcAvailable = m.DdcSupported && !m.DisplayLink && m.OutputTechnology != OutputTech.Internal;
            if (!ddcAvailable)
            {
                string reason = m.DisplayLink
                    ? "DisplayLink адаптер — управление яркостью через DDC/CI не передаётся"
                    : m.OutputTechnology == OutputTech.Internal
                        ? "Встроенный дисплей — WMI отдал не удалось прочитать. Обновите драйвер видеокарты."
                        : "Монитор не отвечает по DDC/CI (проверьте, включено ли DDC/CI в OSD)";
                var unavailBanner = new Border
                {
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemControlBackgroundBaseLowBrush"],
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 4, 0, 4),
                    Child = new TextBlock
                    {
                        Text = "Управление недоступно. " + reason,
                        FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.85,
                    },
                };
                sp.Children.Add(unavailBanner);
            }
            else
            {
                sp.Children.Add(BuildRow(idx, DdcManager.VCP_BRIGHTNESS, "Яркость"));
                sp.Children.Add(BuildRow(idx, DdcManager.VCP_CONTRAST, "Контраст"));
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
        ToolTipService.SetToolTip(info, $"{chain}\nthrottle: {m.WriteGapMs} мс\nverify delay: {m.VerifyDelayMs} мс");
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
        OutputTech.Internal => "Встроенный",
        OutputTech.Wireless => "Беспроводной (Miracast)",
        _ => "?",
    };

    static Border? BuildStatusBanner(MonInfo m)
    {
        string? msg = null;
        if (m.DisplayLink)
            msg = "Монитор подключён через USB-адаптер (DisplayLink). Управление яркостью по DDC/CI недоступно.";
        else if (!m.DdcSupported)
            msg = "Монитор не отвечает по DDC/CI. Проверьте что опция DDC/CI включена в экранном меню (OSD).";
        else if (m.ReadOnlyBrightness)
            msg = "Яркость управляется системой (возможно HDR или Adaptive Brightness). Программное управление недоступно.";
        else if (m.ProbablyFreeSync)
            msg = "Похоже что включён FreeSync/G-Sync — DDC/CI ограничен. Отключите FreeSync в OSD для полного управления.";
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

    void SetUiValue(int idx, byte vcp, int value)
    {
        string key = idx + ":" + vcp;
        if (!bars.TryGetValue(key, out var sl)) return;
        suppress = true;
        sl.Value = Math.Clamp(value, 0, 100);
        suppress = false;
        vals[key].Text = value + "%";
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
        SetUiValue(idx, vcp, value);
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

    public async System.Threading.Tasks.Task RefreshMonitorsAsync()
    {
        // Дебаунс: не чаще чем раз в 2 секунды.
        int now = Environment.TickCount;
        if (_refreshInFlight) { App.LogStatic("RefreshMonitors: уже выполняется, пропуск"); return; }
        if (unchecked(now - (int)_lastRefreshTick) < 2000) { App.LogStatic("RefreshMonitors: debounced"); return; }
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
            CardsHost.Children.Clear();
            BuildCards();
            ddc.Rescan();
            App.LogStatic("RefreshMonitors: done");
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

    void FitToContent()
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

        // Грубая страховка от нулевой высоты: считаем минимум по числу карточек.
        // Заголовок ~50 + (130 на карточку + 10 spacing) + padding 28 = base
        double minHeightDip = 50 + ddc.Monitors.Count * 145 + 28;
        double useHeight = Math.Max(desired.Height, minHeightDip);

        int w = (int)Math.Ceiling(TARGET_WIDTH_DIP * scale);
        int h = (int)Math.Ceiling(useHeight * scale) + (int)Math.Ceiling(4 * scale);

        var aw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        aw.Resize(new Windows.Graphics.SizeInt32(w, h));
    }

    public void ShowNearIcon(int iconCenterX, int iconTop)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var aw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));

        // BUG1 fix: первая FitToContent на скрытом окне возвращает крошечный
        // DesiredSize (WinUI не даёт полноценный layout pass на невидимом Content).
        // Из-за этого первый Show показывает сжатое окно. Стратегия:
        // (1) грубая подгонка сейчас — для стартовой позиции;
        // (2) Show + Activate;
        // (3) через DispatcherQueue после первого real layout — точная подгонка + coord fix.
        FitToContent();

        var display = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(iconCenterX, iconTop), DisplayAreaFallback.Primary);
        var wa = display.WorkArea;

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
        aw.Show();
        Activate();
        ForceToTop(hwnd);
        _focusPoll?.Start();

        // BUG1 fix: после того как окно фактически показано, layout пройдёт
        // на реальном визуальном дереве. Пересчитываем размер и позицию.
        // TryEnqueue Low priority — выполнится после первого рендер-фрейма.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                FitToContent();
                Position();
            }
            catch (Exception ex) { App.LogStatic("ShowNearIcon post-show refit ex: " + ex.Message); }
        });

        // Плавное появление: opacity 0→1 + slide-up
        ShowAnim.Begin();
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
