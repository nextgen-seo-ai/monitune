using Microsoft.Win32;
using System;
using System.Linq;
using System.Text;

namespace MonitorTune;

// Чтение и парсинг EDID (Extended Display Identification Data) монитора из реестра Windows.
// EDID — это 128-байтный паспорт монитора, который сам монитор сообщает по DDC при подключении.
// Windows сохраняет его в:
//   HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<HardwareId>\<InstancePath>\Device Parameters\EDID
//
// Структура EDID 1.3+ (мы парсим только то что полезно):
//   bytes 0..7   = магия 00 FF FF FF FF FF FF 00
//   bytes 8..9   = Manufacturer ID (3 буквы, 5 бит каждая, big-endian)
//   bytes 10..11 = Product Code (little-endian)
//   bytes 12..15 = Serial Number (32-bit)
//   bytes 16..17 = Week / Year of manufacture (year + 1990)
//   bytes 21..22 = горизонталь/вертикаль в см
//   bytes 54..71 = Descriptor 1 (18 байт)
//   bytes 72..89 = Descriptor 2
//   bytes 90..107 = Descriptor 3
//   bytes 108..125 = Descriptor 4
//
// Descriptor типы (по первым 5 байтам):
//   00 00 00 FC 00 — Monitor Name (13 ASCII)  ← главная цель
//   00 00 00 FF 00 — Serial Number ASCII
//   00 00 00 FD 00 — Monitor Range Limits
//   00 00 00 FE 00 — Unspecified Text (manufacturer alphanumeric data)
//   иначе — Detailed Timing Descriptor (с 0x00 значит другое)
public static class EdidReader
{
    public class EdidInfo
    {
        public string? ManufacturerId;       // SAM, GSM, DEL...
        public string? ManufacturerName;     // Samsung, LG, Dell...
        public ushort ProductCode;
        public uint SerialNumberRaw;
        public string? MonitorName;          // descriptor 0xFC — реальное название, например "S28R55"
        public string? SerialNumberAscii;    // descriptor 0xFF
        public int? WeekOfManufacture;       // 1..53
        public int? YearOfManufacture;       // 1990..
        public int? HSizeCm;                 // физический размер
        public int? VSizeCm;
        public string? EdidHex;              // raw для отладки
    }

    /// <summary>Прочитать EDID для конкретного устройства отображения по его DeviceID/InstancePath.</summary>
    /// <param name="deviceId">DeviceID из EnumDisplayDevices — например MONITOR\SAM1015\{4d36e96e-...}\0000</param>
    public static EdidInfo? Read(string deviceId)
    {
        try
        {
            // DeviceID имеет вид:  MONITOR\SAM1015\{GUID}\Instance
            // В реестре путь:      HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\SAM1015\<серийник_папка>\Device Parameters\EDID
            // Нужно найти подключ который соответствует данному устройству.
            var parts = deviceId.Split('\\');
            if (parts.Length < 4) return null;
            string hwId = parts[1];   // SAM1015
            // 4-я часть это Instance — UID. Реестр хранит его в виде серийной папки.

            using var displayKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{hwId}");
            if (displayKey == null) return null;

            // У одного hwId может быть несколько вложенных папок (если монитор подключали разные разы).
            // Ищем подпапку с Device Parameters\EDID. Берём первую с валидным EDID.
            foreach (var subName in displayKey.GetSubKeyNames())
            {
                using var sub = displayKey.OpenSubKey($@"{subName}\Device Parameters");
                if (sub == null) continue;
                if (sub.GetValue("EDID") is byte[] bytes && bytes.Length >= 128)
                {
                    return Parse(bytes);
                }
            }
            return null;
        }
        catch { return null; }
    }

    public static EdidInfo? Parse(byte[] b)
    {
        if (b.Length < 128) return null;
        // Проверка магии
        var magic = new byte[] { 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0 };
        for (int i = 0; i < 8; i++) if (b[i] != magic[i]) return null;

        var info = new EdidInfo();

        // Manufacturer ID: 3 буквы, упакованы в 2 байта (15 бит, big-endian, 5 бит каждая, +'A'-1)
        int manId = (b[8] << 8) | b[9];
        char c1 = (char)('A' - 1 + ((manId >> 10) & 0x1F));
        char c2 = (char)('A' - 1 + ((manId >> 5) & 0x1F));
        char c3 = (char)('A' - 1 + (manId & 0x1F));
        info.ManufacturerId = $"{c1}{c2}{c3}";
        info.ManufacturerName = LookupManufacturer(info.ManufacturerId);

        // Product code (little-endian)
        info.ProductCode = (ushort)(b[10] | (b[11] << 8));
        // Serial number
        info.SerialNumberRaw = (uint)(b[12] | (b[13] << 8) | (b[14] << 16) | (b[15] << 24));
        // Week/Year
        info.WeekOfManufacture = b[16] >= 1 && b[16] <= 53 ? b[16] : (int?)null;
        info.YearOfManufacture = b[17] + 1990;
        // Размер в см (0 = unknown)
        if (b[21] > 0) info.HSizeCm = b[21];
        if (b[22] > 0) info.VSizeCm = b[22];

        // Парсим 4 дескриптора
        int[] offsets = { 54, 72, 90, 108 };
        foreach (var off in offsets)
        {
            // Detailed Timing Descriptor если первые 2 байта != 0 (это pixel clock)
            if (b[off] != 0 || b[off + 1] != 0) continue;
            // Иначе это Monitor Descriptor. b[off+3] = тип.
            byte type = b[off + 3];
            string text = AsciiClean(b, off + 5, 13);
            switch (type)
            {
                case 0xFC: info.MonitorName = text; break;
                case 0xFF: info.SerialNumberAscii = text; break;
                // 0xFD = monitor range limits, 0xFE = unspecified text — игнорируем
            }
        }

        info.EdidHex = string.Concat(b.Take(128).Select(x => x.ToString("X2")));
        return info;
    }

    static string AsciiClean(byte[] b, int off, int len)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < len && (off + i) < b.Length; i++)
        {
            byte v = b[off + i];
            if (v == 0x0A) break;     // EDID descriptor terminator
            if (v >= 0x20 && v <= 0x7E) sb.Append((char)v);
        }
        return sb.ToString().Trim();
    }

    // Микро-словарь часто встречающихся PNP ID. Полная база — pnp.ids /
    // https://uefi.org/pnp_id_list. Для редких вендоров возвращаем как есть.
    static string LookupManufacturer(string id) => id switch
    {
        "SAM" => "Samsung",
        "GSM" => "LG",
        "LGD" => "LG",
        "LEN" => "Lenovo",
        "DEL" => "Dell",
        "ACI" or "AUS" or "ASU" => "ASUS",
        "BNQ" => "BenQ",
        "ACR" => "Acer",
        "AOC" => "AOC",
        "HWP" or "HPN" => "HP",
        "PHL" => "Philips",
        "MSI" => "MSI",
        "GIG" => "Gigabyte",
        "VSC" => "ViewSonic",
        "IVM" => "iiyama",
        "MEI" => "Panasonic",
        "SHP" => "Sharp",
        "SNY" => "Sony",
        "TSB" => "Toshiba",
        "EIZ" or "ENC" => "EIZO",
        "NEC" => "NEC",
        "PRX" => "Proxima",
        "MTC" => "Mitsubishi",
        "FUS" => "Fujitsu",
        "APP" => "Apple",
        "HSD" => "Hannspree",
        "GBT" => "Gigabyte",
        "AUO" => "AU Optronics",
        "BOE" => "BOE",
        "CMN" => "Chimei Innolux",
        "CMO" => "Chi Mei",
        "MED" => "Medion",
        "DGC" => "Diamond Multimedia",
        "ELO" => "ELO TouchSystems",
        "HIQ" => "Hyundai ImageQuest",
        "HYO" => "Hyundai",
        "ITE" => "Integrated Tech Express",
        "PKB" => "Packard Bell",
        "PLN" => "Planar",
        "QUS" => "QUS",
        "RTK" => "Realtek",
        "SEC" => "Seiko Epson",
        "SPT" => "Sceptre",
        "STN" => "Samtron",
        "VIZ" => "Vizio",
        "WAC" => "Wacom",
        "YMH" => "Yamaha",
        _ => id,
    };
}
