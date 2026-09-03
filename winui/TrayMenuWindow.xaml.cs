using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using WinRT.Interop;
using Microsoft.UI;

namespace MonitorTune;

/// <summary>Контекстное меню трея как собственное окно.
///
/// История вопроса: MenuFlyout в режиме SecondWindow рисуется библиотекой в отдельном
/// окне, чей presenter протухает за часы простоя — первый показ выходил компактным, с
/// обрезанными подписями, и лечению прогревами не поддавался. Нативное меню Windows
/// (ContextMenuMode=PopupMenu) размер считает верно, но теряет иконки и вид WinUI.
/// Здесь взят третий путь: окно наше, поэтому размер меряем и выставляем сами тем же
/// приёмом, что уже работает в панели (показать прозрачным → замерить → позиционировать
/// → проявить).</summary>
public sealed partial class TrayMenuWindow : Window
{
    readonly AppWindow _appWindow;
    readonly IntPtr _hwnd;
    DispatcherTimer? _focusPoll;

    /// <summary>Пункт меню: подпись, значок и действие.</summary>
    public sealed class Item
    {
        public string Text = "";
        public Symbol? Icon;
        public Action? Invoke;
        public bool IsSeparator;
        public bool IsChecked;
    }

    public TrayMenuWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            // IsAlwaysOnTop не ставим — он мешает приходу Deactivated, на котором
            // держится закрытие меню (та же причина, что и в панели).
        }
        _appWindow.IsShownInSwitchers = false;
        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        _appWindow.Hide();

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) Hide();
        };

        // Страховка: WinUI 3 не всегда шлёт Deactivated borderless-окну — точно так же,
        // как это уже обходится в панели.
        _focusPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _focusPoll.Tick += (_, _) =>
        {
            try
            {
                var fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != _hwnd) Hide();
            }
            catch { }
        };

        RootGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) Hide();
        };
    }

    public void Hide()
    {
        try { _focusPoll?.Stop(); } catch { }
        try { _appWindow.Hide(); } catch { }
    }

    /// <summary>Пересобрать пункты. Вызывается перед каждым показом, поэтому подписи и
    /// галочки всегда актуальны, а протухшего состояния попросту не остаётся.</summary>
    public void SetItems(IReadOnlyList<Item> items)
    {
        ItemsHost.Children.Clear();
        foreach (var it in items)
        {
            if (it.IsSeparator)
            {
                ItemsHost.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(8, 4, 8, 4),
                    Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                });
                continue;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            // Колонка значка фиксированной ширины: подписи выравниваются по левому краю
            // одинаково и у пунктов со значком, и у пунктов с галочкой.
            var iconHost = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            if (it.IsChecked)
            {
                iconHost.Children.Add(new FontIcon
                {
                    Glyph = "",   // CheckMark
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            else if (it.Icon.HasValue)
            {
                iconHost.Children.Add(new SymbolIcon(it.Icon.Value)
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    // Без явного размера глиф не влезает в колонку и обрезается.
                    RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                });
            }
            row.Children.Add(iconHost);
            row.Children.Add(new TextBlock
            {
                Text = it.Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
            });

            var btn = new Button
            {
                Content = row,
                Style = (Style)RootGrid.Resources["MenuItemStyle"],
            };
            var action = it.Invoke;
            btn.Click += (_, _) =>
            {
                Hide();
                try { action?.Invoke(); }
                catch (Exception ex) { App.LogStatic("TrayMenu item ex: " + ex.Message); }
            };
            ItemsHost.Children.Add(btn);
        }
    }

    /// <summary>Показать меню у точки (обычно позиция курсора при правом клике).</summary>
    public void ShowAt(int x, int y)
    {
        var anchor = new Windows.Graphics.PointInt32(x, y);

        // Тот же порядок, что и в панели: замер на скрытом окне занижен, поэтому
        // показываем прозрачным, меряем ещё раз и только потом проявляем.
        FitToContent();
        Position(anchor);

        RootGrid.Opacity = 0;
        _appWindow.Show();
        Activate();
        MainWindow.ForceToTopPublic(_hwnd);
        _focusPoll?.Start();

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                FitToContent();
                Position(anchor);
            }
            catch (Exception ex) { App.LogStatic("TrayMenu refit ex: " + ex.Message); }
            finally { RootGrid.Opacity = 1; }
        });
    }

    void FitToContent()
    {
        // Меряем именно список пунктов: корневой Grid растянут по окну с прошлого показа,
        // и замер по нему раздувал окно — снизу оставалась пустая полоса.
        ItemsHost.UpdateLayout();
        ItemsHost.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = ItemsHost.DesiredSize;

        uint dpi = Native.GetDpiForWindow(_hwnd);
        double scale = dpi / 96.0;

        // Отступы корневого Grid (Padding 4 + рамка 1 с каждой стороны).
        const double chrome = (4 + 1) * 2;
        // Небольшой запас по ширине: замер текста и его отрисовка расходятся на доли
        // пикселя, и без запаса подписи упираются в правый край окна.
        const double slack = 8;
        double wDip = Math.Max(desired.Width, 180) + chrome + slack;
        double hDip = Math.Max(desired.Height, 32) + chrome;

        int w = (int)Math.Ceiling(wDip * scale);
        int h = (int)Math.Ceiling(hDip * scale);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
    }

    void Position(Windows.Graphics.PointInt32 anchor)
    {
        var wa = DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Primary).WorkArea;
        int w = _appWindow.Size.Width;
        int h = _appWindow.Size.Height;

        // Меню раскрывается вверх от курсора — иконка трея внизу экрана.
        int left = anchor.X;
        int top = anchor.Y - h;
        if (left + w > wa.X + wa.Width - 4) left = wa.X + wa.Width - w - 4;
        if (left < wa.X + 4) left = wa.X + 4;
        if (top < wa.Y + 4) top = wa.Y + 4;
        if (top + h > wa.Y + wa.Height - 4) top = wa.Y + wa.Height - h - 4;
        _appWindow.Move(new Windows.Graphics.PointInt32(left, top));
    }
}
