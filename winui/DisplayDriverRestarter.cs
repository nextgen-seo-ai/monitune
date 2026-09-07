using System;
using System.Diagnostics;
using System.Management;
using System.Threading.Tasks;

namespace MonitorTune;

/// <summary>Перезапуск драйвера видеоадаптера.
///
/// Зачем: после отключения питания монитора кнопкой его DDC/CI-канал остаётся в
/// сбойном состоянии на стороне драйвера. Монитор при этом жив — кнопки и экранное
/// меню работают, — но на команды по кабелю не отвечает. Проверено, что канал не
/// поднимают ни переоткрытие handle, ни повторные чтения, ни запись вслепую, ни
/// многочасовое ожидание: единственное работающее средство, кроме перезагрузки, —
/// перезапуск драйвера видеокарты. Он требует прав администратора, поэтому запуск
/// идёт через UAC.</summary>
public static class DisplayDriverRestarter
{
    public enum Result { Ok, Cancelled, NoAdapter, Failed }

    /// <summary>PNP-идентификатор адаптера, к которому подключён монитор. Сопоставляем
    /// по имени из EnumDisplayDevices: на машине может быть несколько адаптеров
    /// (у ноутбуков — встроенный и дискретный), и перезапускать нужно именно нужный.</summary>
    public static string? FindAdapterId(string? adapterName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_VideoController");
            string? firstPci = null;

            foreach (ManagementObject mo in searcher.Get())
            {
                string? id = mo["PNPDeviceID"] as string;
                string? name = mo["Name"] as string;
                // Виртуальные и удалённые адаптеры не сидят на шине PCI, перезапускать
                // их бессмысленно — DDC/CI через них всё равно не идёт.
                if (string.IsNullOrEmpty(id) || !id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
                firstPci ??= id;

                if (!string.IsNullOrEmpty(adapterName) && !string.IsNullOrEmpty(name)
                    && (name.Contains(adapterName, StringComparison.OrdinalIgnoreCase)
                        || adapterName.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    App.LogStatic($"DriverRestart: адаптер '{name}' → {id}");
                    return id;
                }
            }

            if (firstPci != null) App.LogStatic($"DriverRestart: адаптер по имени не найден, беру первый PCI → {firstPci}");
            return firstPci;
        }
        catch (Exception ex)
        {
            App.LogStatic("DriverRestart: поиск адаптера не удался — " + ex.Message);
            return null;
        }
    }

    public static async Task<Result> RestartAsync(string pnpId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/restart-device \"{pnpId}\"",
                // UseShellExecute обязателен для Verb=runas: без него UAC не поднимается.
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            App.LogStatic($"DriverRestart: запускаю pnputil для {pnpId}");
            using var proc = Process.Start(psi);
            if (proc == null) return Result.Failed;

            await proc.WaitForExitAsync();
            App.LogStatic($"DriverRestart: pnputil завершился с кодом {proc.ExitCode}");
            return proc.ExitCode == 0 ? Result.Ok : Result.Failed;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — пользователь закрыл запрос UAC. Это не ошибка.
            App.LogStatic("DriverRestart: пользователь отклонил запрос прав");
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            App.LogStatic("DriverRestart ex: " + ex);
            return Result.Failed;
        }
    }
}
