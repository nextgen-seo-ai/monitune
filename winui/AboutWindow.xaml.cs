using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinRT.Interop;

namespace MonitorTune;

public sealed partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var v = Windows.ApplicationModel.Package.Current.Id.Version;
        VersionText.Text = $"версия {v.Major}.{v.Minor}.{v.Build}.{v.Revision}";

        var hwnd = WindowNative.GetWindowHandle(this);
        var aw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        uint dpi = Native.GetDpiForWindow(hwnd);
        double scale = dpi / 96.0;
        aw.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Ceiling(640 * scale),
            (int)Math.Ceiling(760 * scale)));
        aw.IsShownInSwitchers = false;
        try { aw.SetIcon("Assets/AppIcon.ico"); } catch { }

        // центрировать на дисплее где курсор (а не в дефолтном углу)
        Native.GetCursorPos(out var pt);
        var display = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(pt.X, pt.Y), DisplayAreaFallback.Primary);
        var wa = display.WorkArea;
        aw.Move(new Windows.Graphics.PointInt32(
            wa.X + (wa.Width - aw.Size.Width) / 2,
            wa.Y + (wa.Height - aw.Size.Height) / 2));

        BuildContent();

        if (Content is FrameworkElement root)
        {
            root.KeyDown += (s, e) =>
            {
                if (e.Key == VirtualKey.Escape) { Close(); e.Handled = true; }
            };
        }

        // При переключении на другое приложение — закрыть окно
        // (поведение типа Quick Settings: уходит из фокуса = убирается).
        Activated += (s, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
                Close();
        };
    }

    void BuildContent()
    {
        // 1. Короткая суть — что это и для кого.
        ContentHost.Children.Add(Paragraph(AboutContent.ShortPitch));

        // 2. Возможности.
        ContentHost.Children.Add(Section("Возможности"));
        foreach (var f in AboutContent.Features)
            ContentHost.Children.Add(Bullet(f));

        // 4. Дальше — подробности.
        ContentHost.Children.Add(Section("О программе"));
        ContentHost.Children.Add(Paragraph(AboutContent.About));

        ContentHost.Children.Add(Section("Как это работает"));
        ContentHost.Children.Add(Paragraph(AboutContent.HowItWorks));

        ContentHost.Children.Add(Section("Приватность"));
        ContentHost.Children.Add(Paragraph(AboutContent.Privacy));

        ContentHost.Children.Add(Section("Частые вопросы"));
        foreach (var (q, a) in AboutContent.Faq)
        {
            ContentHost.Children.Add(FaqQuestion(q));
            ContentHost.Children.Add(FaqAnswer(a));
        }

        ContentHost.Children.Add(Section(AboutContent.LicenseTitle));
        // Лицензия — в свёрнутом блоке, чтобы не мешать обычному пользователю
        var license = new Expander
        {
            Header = "Полный текст лицензионного соглашения",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        license.Content = new TextBlock
        {
            Text = AboutContent.LicenseText,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.9,
            IsTextSelectionEnabled = true
        };
        ContentHost.Children.Add(license);
    }

    static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 4, 0, 2)
    };

    static TextBlock Paragraph(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
        Opacity = 0.9,
        IsTextSelectionEnabled = true
    };

    static Grid Bullet(string text)
    {
        // Grid вместо StackPanel: вторая колонка получает оставшуюся ширину,
        // и TextWrapping действительно срабатывает.
        var g = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GridLength.Auto.Value, GridUnitType.Auto) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = new TextBlock { Text = "•", FontSize = 14, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(dot, 0); g.Children.Add(dot);
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Opacity = 0.9,
            IsTextSelectionEnabled = true
        };
        Grid.SetColumn(tb, 1); g.Children.Add(tb);
        return g;
    }

    static TextBlock FaqQuestion(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 2)
    };

    static TextBlock FaqAnswer(string text) => new()
    {
        Text = text,
        FontSize = 13,
        Opacity = 0.85,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true
    };

    void CloseClick(object sender, RoutedEventArgs e) => Close();
}
