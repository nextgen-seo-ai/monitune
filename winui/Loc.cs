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
        var wanted = Normalize(setting);
        ActiveLanguage = wanted;

        try
        {
            var ctx = Manager.CreateResourceContext();
            ctx.QualifierValues["Language"] = wanted;
            _context = ctx;
        }
        catch
        {
            // Не смогли создать контекст — строки возьмутся на языке системы.
            _context = null;
        }

        try
        {
            // Дополнительно к контексту: влияет на форматирование дат и чисел
            // в стандартных элементах, которые ресурсы не читают.
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = wanted;
        }
        catch
        {
            // В неупакованном запуске override недоступен.
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
            var candidate = _context != null ? map.TryGetValue(id, _context) : map.TryGetValue(id);
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
