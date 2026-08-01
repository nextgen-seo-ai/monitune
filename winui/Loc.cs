using System;
using Microsoft.Windows.ApplicationModel.Resources;

namespace MonitorTune;

/// <summary>
/// Доступ к переводимым строкам интерфейса.
///
/// Строки лежат в Strings/&lt;язык&gt;/Resources.resw и попадают в resources.pri при сборке.
/// Язык выбирается один раз при старте: <see cref="ApplyLanguage"/> вызывается из Program.Main
/// до создания App, потому что XAML с x:Uid резолвит ресурсы в момент загрузки разметки —
/// переключение после старта на уже открытые окна не подействует.
///
/// Логи намеренно не переводятся: они читаются разработчиком, а не пользователем,
/// и одинаковый текст в диагностике со всех машин упрощает поиск проблемы.
/// </summary>
public static class Loc
{
    const string SettingAuto = "auto";

    // ResourceLoader берёт язык из системных настроек и не смотрит на
    // ApplicationLanguages.PrimaryLanguageOverride — это UWP-механизм, а Windows App SDK
    // использует собственный MRT Core. Поэтому язык задаём явно, через квалификатор
    // контекста: иначе переключатель в настройках не действует вообще.
    static ResourceManager? _manager;
    static ResourceContext? _context;

    static ResourceManager Manager => _manager ??= new ResourceManager();

    /// <summary>Язык, реально применённый к интерфейсу: "ru-RU" или "en-US".</summary>
    public static string ActiveLanguage { get; private set; } = "ru-RU";

    /// <summary>Языки, между которыми переключается интерфейс.</summary>
    public static readonly string[] Supported = { "ru-RU", "en-US" };

    /// <summary>
    /// Ставит язык интерфейса. "auto" (или пусто) — берём из настроек Windows:
    /// русский для русскоязычной системы, английский для всех остальных.
    /// Вызывать до создания окон.
    /// </summary>
    public static void ApplyLanguage(string? setting)
    {
        // Здесь только запоминаем выбор. Ресурсный контекст создаётся лениво при
        // первом обращении за строкой: ApplyLanguage вызывается из Program.Main
        // до Application.Start, и ResourceManager на этом этапе создаётся не всегда.
        // Раньше контекст строился прямо здесь, а неудача молча гасилась catch —
        // язык навсегда оставался системным, и переключатель в настройках не работал.
        ActiveLanguage = Normalize(setting);
        _context = null;

        try
        {
            // Дополнительно к контексту: влияет на форматирование дат и чисел
            // в стандартных элементах, которые ресурсы не читают.
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = ActiveLanguage;
        }
        catch
        {
            // В неупакованном запуске override недоступен.
        }
    }

    /// <summary>Ресурсный контекст с выбранным языком. Создаётся при первом обращении
    /// и переживает неудачные ранние попытки: пока контекст не построен, пробуем снова.</summary>
    static ResourceContext? Context
    {
        get
        {
            if (_context != null) return _context;
            try
            {
                var ctx = Manager.CreateResourceContext();
                ctx.QualifierValues["Language"] = ActiveLanguage;
                _context = ctx;
            }
            catch (Exception ex)
            {
                // Молча не гасим: без этой записи потерю языка невозможно объяснить по логу.
                App.LogStatic("Loc: не удалось создать ресурсный контекст — " + ex.Message);
                _manager = null;   // на следующем обращении попробуем заново
            }
            return _context;
        }
    }

    /// <summary>Во что превратится настройка языка на этой машине.</summary>
    public static string Normalize(string? setting)
    {
        if (!string.IsNullOrWhiteSpace(setting) && !setting.Equals(SettingAuto, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var s in Supported)
                if (s.Equals(setting, StringComparison.OrdinalIgnoreCase)) return s;
        }
        return SystemPrefersRussian() ? "ru-RU" : "en-US";
    }

    static bool SystemPrefersRussian()
    {
        // Русский интерфейс уместен и для соседних локалей, где им пользуются как вторым.
        string[] cyrillic = { "ru", "uk", "be", "kk", "ky", "tt", "ba", "ce", "cv", "os", "tg", "uz" };
        try
        {
            foreach (var lang in Windows.System.UserProfile.GlobalizationPreferences.Languages)
            {
                var two = (lang ?? "").Split('-')[0].ToLowerInvariant();
                foreach (var c in cyrillic)
                    if (two == c) return true;
            }
        }
        catch
        {
            var two = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            foreach (var c in cyrillic)
                if (two == c) return true;
        }
        return false;
    }

    /// <summary>Записать в лог, что реально видит ресурсный слой.
    /// Без этого расхождение «в настройках en-US, а интерфейс русский»
    /// невозможно разобрать по диагностике с чужой машины.</summary>
    public static void LogDiagnostics()
    {
        try
        {
            var ctx = Context;
            App.LogStatic($"Loc: выбран {ActiveLanguage}, контекст {(ctx == null ? "НЕ создан" : "создан")}");
            if (ctx != null)
            {
                ctx.QualifierValues.TryGetValue("Language", out var langQualifier);
                App.LogStatic($"Loc: квалификатор Language = '{langQualifier}'");
            }
            var map = Manager.MainResourceMap;
            var probe = map.TryGetValue("Resources/MenuOpen", ctx);
            var probeNoCtx = map.TryGetValue("Resources/MenuOpen");
            App.LogStatic($"Loc: MenuOpen с контекстом='{probe?.ValueAsString}', без контекста='{probeNoCtx?.ValueAsString}'");
        }
        catch (Exception ex) { App.LogStatic("Loc diag ex: " + ex.Message); }
    }

    /// <summary>Строка по ключу. Если ключа нет — возвращает сам ключ, чтобы пропажа была заметна.</summary>
    public static string S(string key)
    {
        try
        {
            // Strings\<язык>\Resources.resw попадает в карту как поддерево "Resources"
            var map = Manager.MainResourceMap;
            var v = Lookup(map, "Resources/" + key) ?? Lookup(map, key);
            return string.IsNullOrEmpty(v) ? key : v!;
        }
        catch
        {
            return key;
        }
    }

    static string? Lookup(ResourceMap map, string id)
    {
        try
        {
            var ctx = Context;
            var candidate = ctx != null ? map.TryGetValue(id, ctx) : map.TryGetValue(id);
            return candidate?.ValueAsString;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Строка с подстановками: Loc.F("UpdateTo", "1.2.4").</summary>
    public static string F(string key, params object?[] args)
    {
        var fmt = S(key);
        try { return string.Format(fmt, args); }
        catch { return fmt; }
    }
}
