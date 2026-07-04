using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace MonitorTune;

// Embedded база данных вендоров и моделей мониторов.
// Источники:
//   pnp_vendors.tsv.gz    — UEFI PNP ID Registry (общедоступные данные)
//   monitor_models.tsv.gz — производный от linux-hardware.org/EDID, лицензия CC-BY-4.0
//
// Lazy loading: загружается при первом обращении, кешируется в памяти.
public static class MonitorDatabase
{
    static Dictionary<string, string>? _vendors;
    static Dictionary<string, string>? _models;
    static readonly object _lock = new();

    /// <summary>Имя вендора по PNP ID (3 буквы). null если не найден.</summary>
    public static string? VendorByPnp(string? pnp)
    {
        if (string.IsNullOrEmpty(pnp)) return null;
        EnsureLoaded();
        return _vendors!.TryGetValue(pnp.ToUpperInvariant(), out var v) ? v : null;
    }

    /// <summary>Marketing-имя модели по (PNP ID, ProductCode). Возвращает только имя без вендора.</summary>
    public static string? ModelByCode(string? pnp, ushort productCode)
    {
        if (string.IsNullOrEmpty(pnp)) return null;
        EnsureLoaded();
        string key = $"{pnp.ToUpperInvariant()}\t{productCode:X4}";
        return _models!.TryGetValue(key, out var m) ? m : null;
    }

    static void EnsureLoaded()
    {
        if (_vendors != null && _models != null) return;
        lock (_lock)
        {
            _vendors ??= LoadTsv("MonitorTune.BuildResources.pnp_vendors.tsv.gz");
            _models ??= LoadTsv("MonitorTune.BuildResources.monitor_models.tsv.gz");
        }
    }

    static Dictionary<string, string> LoadTsv(string resourceName)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var raw = asm.GetManifestResourceStream(resourceName);
            if (raw == null) return dict;
            using var gz = new GZipStream(raw, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                string key, value;
                int secondTab = line.IndexOf('\t', tab + 1);
                if (secondTab > 0)
                {
                    // monitor_models: pnp\tcode\tname → ключ "pnp\tcode"
                    key = line.Substring(0, secondTab);
                    value = line.Substring(secondTab + 1);
                }
                else
                {
                    // pnp_vendors: pnp\tvendor
                    key = line.Substring(0, tab);
                    value = line.Substring(tab + 1);
                }
                dict[key] = value;
            }
        }
        catch { }
        return dict;
    }
}
