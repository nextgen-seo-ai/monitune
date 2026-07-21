using System.Management;

namespace MonitorTune;

// Управление яркостью встроенных дисплеев ноутбуков (eDP) через WMI.
// DDC/CI на eDP-линии отсутствует физически — сигнал не выходит за пределы SoC/GPU,
// нет обратной шины для VCP-команд. Windows экспонирует управление через
// root\WMI\WmiMonitorBrightness (read) и WmiMonitorBrightnessMethods.WmiSetBrightness (write).
//
// Contrast через WMI НЕ поддерживается — только brightness. Для встроенных дисплеев
// это нормально: контраст управляется драйвером через LUT (Intel/AMD control panels),
// но не MSMonitorClass API.
public static class EdpBrightnessService
{
    public static Action<string>? Log;

    /// <summary>Есть ли на этой машине встроенный дисплей поддерживающий brightness через WMI.</summary>
    public static bool IsAvailable()
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT InstanceName FROM WmiMonitorBrightness"));
            using var results = searcher.Get();
            foreach (var _ in results) return true;
            return false;
        }
        catch (Exception ex) { Log?.Invoke("EdpBrightness.IsAvailable ex: " + ex.Message); return false; }
    }

    /// <summary>Текущая яркость встроенного дисплея [0..100] или -1 при ошибке/отсутствии.</summary>
    public static int Read()
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));
            using var results = searcher.Get();
            foreach (ManagementObject o in results)
            {
                using (o)
                {
                    var v = o["CurrentBrightness"];
                    if (v is byte b) return b;
                    if (v is int i) return i;
                    if (v is uint u) return (int)u;
                }
            }
            return -1;
        }
        catch (Exception ex) { Log?.Invoke("EdpBrightness.Read ex: " + ex.Message); return -1; }
    }

    /// <summary>Установить яркость встроенного дисплея. value: 0..100.
    /// timeoutSec — сколько ждать применения (WmiSetBrightness блокирует до подтверждения драйвером).</summary>
    public static bool Write(int value, uint timeoutSec = 1)
    {
        if (value < 0) value = 0;
        if (value > 100) value = 100;
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT InstanceName FROM WmiMonitorBrightnessMethods"));
            using var results = searcher.Get();
            foreach (ManagementObject method in results)
            {
                using (method)
                {
                    // Ноутбуки поддерживают только определённые уровни (обычно 0,10,20,...,100
                    // или произвольный набор из BIOS). WmiSetBrightness САМ snap'ит value к
                    // ближайшему поддерживаемому — так что округлять не надо.
                    method.InvokeMethod("WmiSetBrightness", new object[] { timeoutSec, (byte)value });
                    return true;
                }
            }
            Log?.Invoke("EdpBrightness.Write: no WmiMonitorBrightnessMethods instance found");
            return false;
        }
        catch (Exception ex) { Log?.Invoke("EdpBrightness.Write ex: " + ex.Message); return false; }
    }

    /// <summary>Получить список поддерживаемых значений яркости у встроенного дисплея.
    /// Некоторые ноутбуки выдают ступенчатую шкалу (например, только 25/50/75/100).
    /// Используется только для диагностики — Write сам snap'ит к ближайшему.</summary>
    public static byte[]? GetSupportedLevels()
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Level FROM WmiMonitorBrightness"));
            using var results = searcher.Get();
            foreach (ManagementObject o in results)
            {
                using (o)
                {
                    if (o["Level"] is byte[] levels) return levels;
                }
            }
            return null;
        }
        catch (Exception ex) { Log?.Invoke("EdpBrightness.GetSupportedLevels ex: " + ex.Message); return null; }
    }
}
