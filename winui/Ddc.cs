using System.Management;
using System.Runtime.InteropServices;

namespace MonitorTune;

public class MonInfo
{
    /// <summary>Стабильный Guid, не меняется при Refresh. UI использует его для маппинга карточек.</summary>
    public readonly Guid Id = Guid.NewGuid();
    /// <summary>Handle физического монитора. volatile — читается из воркер-потока без OpLock в SafeWrite null-check pre-flight.</summary>
    private IntPtr _handle;
    public IntPtr Handle { get => System.Threading.Volatile.Read(ref _handle); set => System.Threading.Volatile.Write(ref _handle, value); }
    public string Device = "";
    public string? Token;
    public string Name = "";
    /// <summary>Короткий ID для лога: last4 токена + \.\DISPLAY#. Не меняется после Enumerate.</summary>
    public string ShortId = "";
    public bool HasBrightness;
    public bool HasContrast;
    public int Brightness = -1;
    public int Contrast = -1;
    /// <summary>Фактический max VCP из последнего Get (обычно 100, у Eizo/NEC до 255). Для нормализации.</summary>
    public int BrightnessMax = 100;
    public int ContrastMax = 100;
    public int WriteGapMs = 1000;
    public long LastOpMs = 0;
    public EdidReader.EdidInfo? Edid;
    /// <summary>Тип подключения (DP/HDMI/USB-C/DVI/VGA/Internal) — из DisplayConfig outputTechnology.</summary>
    public OutputTech OutputTechnology = OutputTech.Unknown;
    /// <summary>Время когда DDC-канал должен восстановиться после hotplug/wake (Environment.TickCount).</summary>
    public long DdcSuspendedUntilMs = 0;
    /// <summary>Per-monitor lock — сериализация всех операций к одному физическому монитору.</summary>
    public readonly object OpLock = new();
    /// <summary>Флаг устаревшего MonInfo (после Refresh) — все операции должны быть no-op.</summary>
    public volatile bool Disposed;
    /// <summary>Generation-counter — увеличивается при каждом Refresh глобально. UI сверяет для отбрасывания stale OnValue.</summary>
    public int Generation;

    /// <summary>Диагностическая инфа для отображения пользователю (тултип/info panel).</summary>
    public GpuVendor Gpu = GpuVendor.Unknown;
    public string? AdapterName;
    public bool DdcSupported = true;         // false = монитор не отвечает по DDC/CI вообще
    public bool DisplayLink;                 // подключён через DisplayLink адаптер (нет DDC)
    /// <summary>Встроенный дисплей ноутбука (eDP). Управление яркостью через WMI, не DDC.
    /// Contrast недоступен — только brightness. Detects по OutputTechnology.Internal + WMI probe.</summary>
    public bool IsEdp;
    public bool ProbablyFreeSync;            // подозрительно мало кодов в caps — вероятно FreeSync/HDR блокирует
    public bool ReadOnlyBrightness;          // Set не меняет значение (HDR mode)
    public int VerifyDelayMs = 200;          // задержка перед Get-verify (зависит от GPU)
    public int WriteCounter;                 // счётчик Set для периодического verify

    /// <summary>Sticky: brightness VCP не поддерживается (ERROR_GRAPHICS_DDCCI_VCP_NOT_SUPPORTED). Больше не пытаемся Set.</summary>
    public bool VcpBrightnessUnsupported;
    public bool VcpContrastUnsupported;
    /// <summary>Sticky: DDC/CI подтверждённо недоступен на этом мониторе — не вызывать TryReopenHandle повторно.</summary>
    public bool DdcPermanentlyUnavailable;
    /// <summary>Последний Win32 error из ReadRetry/SafeWrite — показать в статус-баре.</summary>
    public int LastErrorCode;
}

public enum OutputTech
{
    Unknown = 0, Hdmi, DisplayPort, DpOverThunderbolt, UsbC, Dvi, Vga, Internal, Wireless, Other,
}

public enum GpuVendor
{
    Unknown = 0, Intel, Amd, Nvidia, Qualcomm, Microsoft, DisplayLink,
}

public class ValueUpdate { public int MonIndex; public Guid MonId; public int Generation; public byte Vcp; public int Value; }

public class DdcManager
{
    public readonly List<MonInfo> Monitors = new();
    public event Action<ValueUpdate>? OnValue;
    public event Action? OnInitDone;

    readonly object pendingLock = new();
    readonly Dictionary<string, int> pending = new();
    readonly AutoResetEvent signal = new(false);
    volatile bool running = true;
    Thread? worker;

    public const byte VCP_BRIGHTNESS = 0x10;
    public const byte VCP_CONTRAST = 0x12;

    /// <summary>Глобальный счётчик Refresh — Monitor.Generation копирует его при Enumerate. UI сверяет для отбрасывания устаревших OnValue.</summary>
    public int CurrentGeneration;

    // Win32 error code classifiers для DDC/CI операций.
    // ERROR_INVALID_HANDLE — handle умер (WM_DISPLAYCHANGE между enumerate и write).
    public static bool IsInvalidHandleError(int err) => err == 6 /*ERROR_INVALID_HANDLE*/ || err == 1400 /*ERROR_INVALID_WINDOW_HANDLE*/;
    // Расширенный набор — handle возможно stale после disconnect/reconnect монитора.
    // ERROR_GEN_FAILURE и MCA_INTERNAL_ERROR НЕ явные "invalid handle", но по факту
    // случаются когда deployment service забыл про handle (кабель дёрнули).
    // Пропускать эти коды через reopen дешевле чем ждать пока юзер нажмёт "Обновить".
    public static bool IsPossiblyStaleHandle(int err) =>
        IsInvalidHandleError(err) ||
        err == 31 /*ERROR_GEN_FAILURE — часто после hotplug*/ ||
        err == unchecked((int)0xC026258A) /*ERROR_GRAPHICS_MCA_INTERNAL_ERROR*/ ||
        err == unchecked((int)0xC0262580) /*ERROR_GRAPHICS_DDCCI_INVALID_DEVICE*/;
    // Terminal — retry бесполезен, монитор физически не может это.
    public static bool IsTerminalDdcError(int err) =>
        err == 5      /*ERROR_ACCESS_DENIED — OSD locked / HDCP*/ ||
        err == 50     /*ERROR_NOT_SUPPORTED*/ ||
        err == 1450   /*ERROR_NO_SYSTEM_RESOURCES*/ ||
        err == unchecked((int)0xC0261FF9) /*ERROR_MONITOR_NO_DESCRIPTOR*/ ||
        err == unchecked((int)0xC0262584) /*ERROR_GRAPHICS_DDCCI_VCP_NOT_SUPPORTED*/ ||
        err == unchecked((int)0xC0262589) /*ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_COMMAND*/ ||
        err == unchecked((int)0xC0262595) /*ERROR_GRAPHICS_MCA_UNSUPPORTED_MCCS_VERSION*/;
    // Transient I2C corruption (шумный кабель/KVM) — короткий retry 50ms помогает.
    public static bool IsTransientI2cError(int err) =>
        err == unchecked((int)0xC0262582) /*INVALID_MESSAGE_CHECKSUM*/ ||
        err == unchecked((int)0xC0262583) /*INVALID_MESSAGE_LENGTH*/ ||
        err == unchecked((int)0xC0262587) /*INVALID_DATA*/ ||
        err == unchecked((int)0xC0262588) /*I2C_ERROR_TRANSMITTING_DATA*/;
    // Признак VCP_NOT_SUPPORTED — надо навсегда пометить VcpBrightness/ContrastUnsupported.
    public static bool IsVcpNotSupported(int err) =>
        err == unchecked((int)0xC0262584) /*VCP_NOT_SUPPORTED*/ ||
        err == 50 /*NOT_SUPPORTED*/;

    public void Start() { worker = new Thread(Loop) { IsBackground = true }; worker.Start(); }

    readonly object _monitorsLock = new();

    /// <summary>Принудительное переоткрытие списка мониторов и их DDC-каналов.
    /// Закрывает старые physical handles, очищает Monitors, заново enumerate.
    /// Использовать когда DDC залип у одного из мониторов или физически отвалился.</summary>
    public void Refresh()
    {
        lock (_monitorsLock)
        {
            // Устаревшие pending idx — обнуляем очередь (индексы больше не соответствуют новым MonInfo после Clear+Enumerate).
            lock (pendingLock) { pending.Clear(); }
            foreach (var m in Monitors)
            {
                lock (m.OpLock)
                {
                    m.Disposed = true;
                    try
                    {
                        if (m.Handle != IntPtr.Zero) Native.DestroyPhysicalMonitor(m.Handle);
                    }
                    catch (Exception ex) { Log?.Invoke($"Refresh destroy ex [{m.ShortId}]: {ex.Message}"); }
                    m.Handle = IntPtr.Zero;
                }
            }
            Monitors.Clear();
            // Новое поколение — UI сверяет его с Generation каждой MonInfo для отбрасывания stale OnValue.
            CurrentGeneration++;
            Log?.Invoke($"Refresh: generation → {CurrentGeneration}");
            try { Enumerate(); }
            catch (Exception ex) { Log?.Invoke($"Refresh enumerate ex: {ex.Message}"); }
        }
    }
    public void Stop()
    {
        running = false; signal.Set();
        try
        {
            worker?.Join(1500);
        }
        catch { }
        try
        {
            lock (_monitorsLock)
            {
                foreach (var m in Monitors)
                {
                    lock (m.OpLock)
                    {
                        m.Disposed = true;
                        if (m.Handle != IntPtr.Zero) Native.DestroyPhysicalMonitor(m.Handle);
                        m.Handle = IntPtr.Zero;
                    }
                }
            }
        }
        catch { }
    }
    public void Request(int monIndex, byte vcp, int value)
    {
        string key = monIndex + ":" + vcp;
        lock (pendingLock) { pending[key] = value; }
        signal.Set();
    }

    /// <summary>Запустить начальное чтение caps+значений для новых мониторов после Refresh.</summary>
    public void Rescan() { signal.Set(); _rescanRequested = true; }
    volatile bool _rescanRequested;
    bool TryTake(out int monIndex, out byte vcp, out int value)
    {
        monIndex = 0; vcp = 0; value = 0;
        lock (pendingLock)
        {
            foreach (var kv in pending)
            {
                string[] p = kv.Key.Split(':');
                monIndex = int.Parse(p[0]); vcp = byte.Parse(p[1]); value = kv.Value;
                pending.Remove(kv.Key); return true;
            }
        }
        return false;
    }
    public void Enumerate()
    {
        var nameMap = LoadFriendlyNames();
        var handles = new List<IntPtr>();
        var devices = new List<string>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Native.RECT r, IntPtr d) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            Native.GetMonitorInfo(h, ref mi);
            handles.Add(h); devices.Add(mi.szDevice); return true;
        }, IntPtr.Zero);
        var list = new List<MonInfo>();
        // eDP-детектор: если у устройства нет physical monitor'а (или он не отвечает по DDC),
        // но outputTechnology=Internal и WMI отдаёт WmiMonitorBrightness — это встроенный
        // экран ноутбука. Управление через WMI, а не DDC/CI.
        bool edpWmiAvailable = EdpBrightnessService.IsAvailable();
        Log?.Invoke($"  Enumerate: eDP WMI available = {edpWmiAvailable}");
        for (int i = 0; i < handles.Count; i++)
        {
            uint num = 0;
            bool hasPhysical = Native.GetNumberOfPhysicalMonitorsFromHMONITOR(handles[i], ref num) && num > 0;
            if (!hasPhysical)
            {
                // Проверим — может это eDP: OutputTechnology=Internal + WMI умеет brightness.
                var techPre = MonitorNameResolver.GetOutputTechnology(MonitorDeviceId(devices[i]));
                if (techPre == OutputTech.Internal && edpWmiAvailable)
                {
                    var (edpGpu, edpGpuName) = GpuDetector.DetectForDisplay(devices[i]);
                    string edpToken = MonitorToken(devices[i]) ?? "";
                    string edpDevTail = devices[i].StartsWith("\\\\.\\DISPLAY") ? devices[i].Substring(9) : devices[i];
                    string edpShortId = "eDP@" + edpDevTail;
                    var edpDeviceId = MonitorDeviceId(devices[i]);
                    var edpEdid = edpDeviceId != null ? EdidReader.Read(edpDeviceId) : null;
                    string edpName = edpEdid?.MonitorName ?? "Встроенный дисплей";
                    list.Add(new MonInfo
                    {
                        Handle = IntPtr.Zero,   // eDP не имеет DDC handle
                        Device = devices[i], Token = edpToken,
                        Name = edpName, ShortId = edpShortId, Edid = edpEdid,
                        OutputTechnology = OutputTech.Internal,
                        Gpu = edpGpu, AdapterName = edpGpuName,
                        DdcSupported = false,        // важно: не идём в DDC path
                        IsEdp = true,
                        HasBrightness = true, HasContrast = false,
                        WriteGapMs = 50,             // WMI-write быстрый, throttle минимальный
                        VerifyDelayMs = 100,
                        Generation = CurrentGeneration,
                    });
                    Log?.Invoke($"  Monitor eDP: {edpName} [{edpShortId}] transport=Internal (WMI) gpu={edpGpu}");
                    continue;
                }
                Log?.Invoke($"  Enumerate: GetNumberOfPhysicalMonitors FAILED для {devices[i]} — пропускаем");
                continue;
            }
            var arr = new Native.PHYSICAL_MONITOR[num];
            // GetPhysicalMonitorsFromHMONITOR может отдать hPhysicalMonitor=0 в первые секунды cold start
            // (особенно Nvidia+Samsung по DP). Retry 5 раз с задержкой 400мс (было 3×300ms — Samsung по DP не успевает).
            bool gotHandle = false;
            for (int r = 0; r < 5; r++)
            {
                bool ok = Native.GetPhysicalMonitorsFromHMONITOR(handles[i], num, arr);
                if (ok && arr[0].hPhysicalMonitor != IntPtr.Zero) { gotHandle = true; break; }
                int err = Marshal.GetLastWin32Error();
                Log?.Invoke($"  Enumerate retry {r + 1}/5 для {devices[i]}: ok={ok} handle=0x{arr[0].hPhysicalMonitor.ToInt64():X} err={err}");
                Thread.Sleep(400);
            }
            Log?.Invoke($"  Enumerate result {devices[i]}: handle=0x{arr[0].hPhysicalMonitor.ToInt64():X} gotHandle={gotHandle}");
            if (!gotHandle)
                Log?.Invoke($"  Enumerate: не удалось получить physical handle для {devices[i]} — SafeCaps попробует переоткрыть позже");
            string? token = MonitorToken(devices[i]);
            string? deviceId = MonitorDeviceId(devices[i]);
            EdidReader.EdidInfo? edid = deviceId != null ? EdidReader.Read(deviceId) : null;
            string? wmiFriendly = token != null && nameMap.ContainsKey(token) ? nameMap[token] : null;
            string? vendor = edid?.ManufacturerName ?? GuessVendor(token) ?? MonitorDatabase.VendorByPnp(
                token != null && token.Length >= 3 ? token.Substring(0, 3) : null);

            var resolved = MonitorNameResolver.Resolve(
                deviceId, token, edid?.MonitorName, wmiFriendly, vendor, edid?.ProductCode ?? 0);

            var tech = MonitorNameResolver.GetOutputTechnology(deviceId);
            var (gpu, gpuName) = GpuDetector.DetectForDisplay(devices[i]);
            // ShortId для лога: last4 токена (например "R55" → "R55") + \.\DISPLAY#
            string devTail = devices[i].StartsWith("\\\\.\\DISPLAY") ? devices[i].Substring(9) : devices[i];
            string shortId = ((token != null && token.Length >= 4) ? token.Substring(token.Length - 4) : (token ?? "?")) + "@" + devTail;
            bool isEdp = tech == OutputTech.Internal && edpWmiAvailable;
            list.Add(new MonInfo
            {
                Handle = arr[0].hPhysicalMonitor,
                Device = devices[i], Token = token,
                Name = resolved.Name,
                ShortId = shortId,
                Edid = edid,
                OutputTechnology = tech,
                Gpu = gpu,
                AdapterName = gpuName,
                DisplayLink = gpu == GpuVendor.DisplayLink,
                DdcSupported = gpu != GpuVendor.DisplayLink && tech != OutputTech.Internal,
                IsEdp = isEdp,
                // Для eDP предзаполняем flags — SafeCaps не нужен, WMI прямой.
                HasBrightness = isEdp,
                HasContrast = isEdp ? false : false,
                VerifyDelayMs = isEdp ? 100 : GpuDetector.VerifyDelayFor(gpu),
                WriteGapMs = isEdp ? 50 : ComputeThrottle(vendor, tech),
                Generation = CurrentGeneration,
            });
            Log?.Invoke($"  Monitor: {resolved.Name} [{shortId}] transport={tech} gpu={gpu} '{gpuName}' throttle={ComputeThrottle(vendor, tech)}ms gen={CurrentGeneration}");
        }
        list.Sort((a, b) => string.Compare(a.Device, b.Device, StringComparison.Ordinal));
        Monitors.AddRange(list);
    }
    static string? MonitorToken(string device)
    {
        var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
        if (Native.EnumDisplayDevices(device, 0, ref dd, 0))
        {
            string[] parts = dd.DeviceID.Split('\\');
            if (parts.Length >= 2) return parts[1];
        }
        return null;
    }
    /// <summary>Полный DeviceID монитора для чтения EDID (MONITOR\SAM1015\{GUID}\InstancePath).</summary>
    static string? MonitorDeviceId(string device)
    {
        var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
        if (Native.EnumDisplayDevices(device, 0, ref dd, 0))
            return dd.DeviceID;
        return null;
    }
    // Базовый throttle зависит от транспорта и вендора.
    // По документации VESA DDC/CI + MCCS + опыт (linuxhw, workflow research):
    //  - HDMI            быстрее всех: 80мс достаточно для большинства мониторов.
    //  - DisplayPort     дольше: 120мс (AUX-канал делит трафик со звуком/HPD).
    //  - DP-over-USB4/TB длиннее: 200мс (тоннельный DP через TB4 док/адаптер).
    //  - USB-C DP Alt    200мс (аналогичный тоннель через PD-контроллер).
    //  - DVI             120мс (стар).
    //  - VGA/Internal    DDC/CI обычно отсутствует, но если есть — 100мс.
    // Samsung + DP выделяем отдельно (workflow подтвердил: DDC/CI капризнее на DP).
    static int ComputeThrottle(string? vendor, OutputTech tech)
    {
        bool samsung = vendor != null && vendor.Contains("Samsung", StringComparison.OrdinalIgnoreCase);
        return tech switch
        {
            OutputTech.Hdmi              => samsung ? 120 : 80,
            OutputTech.DisplayPort       => samsung ? 200 : 120,
            OutputTech.DpOverThunderbolt => samsung ? 300 : 200,   // TB4 док режет AUX-канал
            OutputTech.UsbC              => samsung ? 300 : 200,
            OutputTech.Dvi               => 120,
            OutputTech.Vga               => 100,
            OutputTech.Internal          => 100,
            _                            => samsung ? 200 : 150,
        };
    }

    static string? GuessVendor(string? token)
    {
        if (token == null || token.Length < 3) return null;
        string code = token.Substring(0, 3).ToUpperInvariant();
        var map = new Dictionary<string, string> {
            {"SAM","Samsung"},{"SEC","Samsung"},{"GSM","LG"},{"LGD","LG"},
            {"DEL","Dell"},{"ACI","ASUS"},{"AUS","ASUS"},{"ASU","ASUS"},
            {"BNQ","BenQ"},{"ACR","Acer"},{"AOC","AOC"},{"HWP","HP"},{"HPN","HP"},
            {"PHL","Philips"},{"MSI","MSI"},{"GIG","Gigabyte"},{"GBT","Gigabyte"},
            {"VSC","ViewSonic"},{"IVM","iiyama"},{"EIZ","EIZO"},{"ENC","EIZO"},
            {"NEC","NEC"},{"APP","Apple"},{"SHP","Sharp"},{"SNY","Sony"},
            {"VIZ","Vizio"},{"MED","Medion"}
        };
        return map.TryGetValue(code, out var v) ? v : null;
    }
    static Dictionary<string, string> LoadFriendlyNames()
    {
        var map = new Dictionary<string, string>();
        try
        {
            var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID");
            foreach (ManagementObject mo in searcher.Get())
            {
                string? inst = mo["InstanceName"] as string;
                string? token = null;
                if (inst != null) { var parts = inst.Split('\\'); if (parts.Length >= 2) token = parts[1]; }
                string friendly = DecodeUShorts(mo["UserFriendlyName"]);
                if (token != null && friendly.Length > 0 && !map.ContainsKey(token)) map[token] = friendly;
            }
        }
        catch { }
        return map;
    }
    static string DecodeUShorts(object o)
    {
        var sb = new System.Text.StringBuilder();
        try { if (o is ushort[] a) foreach (var c in a) if (c != 0) sb.Append((char)c); } catch { }
        return sb.ToString().Trim();
    }
    void InitialReadAll()
    {
        MonInfo[] snap;
        lock (_monitorsLock) { snap = Monitors.ToArray(); }
        Log?.Invoke($"InitialReadAll: {snap.Length} monitor(s), параллельно");
        // Параллельная обработка: каждый монитор в своём Task. LG не ждёт Samsung.
        // Все SafeCaps/SafeRead уже сериализованы per-monitor через OpLock,
        // а InitDone мы вызываем ПОСЛЕ завершения всех Tasks.
        var tasks = new List<System.Threading.Tasks.Task>();
        foreach (var m in snap)
        {
            var mon = m;
            tasks.Add(System.Threading.Tasks.Task.Run(() => InitOne(mon)));
        }
        try { System.Threading.Tasks.Task.WaitAll(tasks.ToArray(), 30000); }
        catch (Exception ex) { Log?.Invoke("InitialReadAll WaitAll ex: " + ex.Message); }
        Log?.Invoke("InitialReadAll: done");
        OnInitDone?.Invoke();
    }

    void InitOne(MonInfo m)
    {
        try
        {
            if (m.Disposed) { Log?.Invoke($"  [{m.ShortId}]: skip — Disposed at entry"); return; }
            // eDP fast-path: сразу читаем brightness через WMI, никаких DDC caps.
            if (m.IsEdp)
            {
                int b = SafeRead(m, VCP_BRIGHTNESS);
                Log?.Invoke($"  [{m.ShortId}] eDP: brightness={b}");
                if (b >= 0) Raise(IndexOf(m), VCP_BRIGHTNESS, b);
                return;
            }
            Log?.Invoke($"  [{m.ShortId}]: start (handle=0x{m.Handle.ToInt64():X} DdcSup={m.DdcSupported} DL={m.DisplayLink} PermUnav={m.DdcPermanentlyUnavailable})");
            if (m.DisplayLink || !m.DdcSupported || m.DdcPermanentlyUnavailable)
            {
                m.HasBrightness = false; m.HasContrast = false; m.DdcSupported = false;
                Log?.Invoke($"  [{m.ShortId}]: DDC/CI недоступно — пропускаем caps");
                return;
            }
            string? caps = SafeCaps(m);
            if (m.Disposed) { Log?.Invoke($"  [{m.ShortId}]: skip — Disposed after SafeCaps"); return; }
            if (caps != null)
            {
                var codes = TopLevelVcp(caps.ToUpperInvariant());
                m.HasBrightness = codes.Contains("10");
                m.HasContrast = codes.Contains("12");
                Log?.Invoke($"  [{m.ShortId}]: caps OK — codes={codes.Count} HasB={m.HasBrightness} HasC={m.HasContrast}");
                if (codes.Count < 5)
                {
                    m.ProbablyFreeSync = true;
                    Log?.Invoke($"  [{m.ShortId}]: подозрительно мало VCP-кодов ({codes.Count}) — возможно FreeSync/HDR");
                }
            }
            else
            {
                m.HasBrightness = true; m.HasContrast = true;
                Log?.Invoke($"  [{m.ShortId}]: caps=null — fallback: пробуем читать VCP напрямую");
            }
            if (m.Disposed) { Log?.Invoke($"  [{m.ShortId}]: skip — Disposed after caps parse"); return; }
            if (m.HasBrightness)
            {
                int b = SafeRead(m, VCP_BRIGHTNESS); m.Brightness = b;
                Log?.Invoke($"  [{m.ShortId}]: brightness read = {b}");
                if (b >= 0 && !m.Disposed) Raise(IndexOf(m), VCP_BRIGHTNESS, b);
            }
            if (m.Disposed) { Log?.Invoke($"  [{m.ShortId}]: skip — Disposed before contrast"); return; }
            if (m.HasContrast)
            {
                int c = SafeRead(m, VCP_CONTRAST); m.Contrast = c;
                Log?.Invoke($"  [{m.ShortId}]: contrast read = {c}");
                if (c >= 0 && !m.Disposed) Raise(IndexOf(m), VCP_CONTRAST, c);
            }
        }
        catch (Exception ex) { Log?.Invoke($"InitOne [{m.ShortId}] ex: {ex.Message}"); }
    }

    /// <summary>Прочитать caps под per-monitor lock, с try/catch. Возвращает null при ошибке.</summary>
    string? SafeCaps(MonInfo m)
    {
        try
        {
            lock (m.OpLock)
            {
                if (m.Disposed) return null;
                if (m.DisplayLink || !m.DdcSupported || m.DdcPermanentlyUnavailable) return null;
                if (m.Handle == IntPtr.Zero && !TryReopenHandle(m))
                {
                    Log?.Invoke($"SafeCaps [{m.ShortId}]: handle=0 и reopen fail — caps skip");
                    return null;
                }
                RespectSuspension(m);
                // Caps может отсутствовать у монитора вовсе (Samsung LU28R55 и др. —
                // VCP отвечает, а CapabilitiesRequestAndCapabilitiesReply нет).
                // 4 попытки хватит: если поддерживает — отвечает с 1-2й.
                // Reopen+retry не делаем — при handle stale отработает SafeRead.
                var caps = ReadCapsRetry(m.Handle, 4);
                if (caps == null)
                {
                    int err = Marshal.GetLastWin32Error();
                    Log?.Invoke($"SafeCaps [{m.ShortId}]: caps=null (err=0x{err:X}) — вероятно caps unsupported");
                }
                return caps;
            }
        }
        catch (Exception ex) { Log?.Invoke($"SafeCaps '{m.Name}' ex: {ex.Message}"); return null; }
    }

    int SafeRead(MonInfo m, byte vcp)
    {
        // eDP-фаст-path: WMI read, contrast для eDP всегда -1 (не поддерживается).
        if (m.IsEdp)
        {
            if (vcp != VCP_BRIGHTNESS) return -1;
            int b = EdpBrightnessService.Read();
            m.Brightness = b;
            return b;
        }

        try
        {
            lock (m.OpLock)
            {
                if (m.Disposed) return -1;
                if (m.DisplayLink || !m.DdcSupported || m.DdcPermanentlyUnavailable) return -1;
                if (m.Handle == IntPtr.Zero && !TryReopenHandle(m))
                {
                    Log?.Invoke($"SafeRead [{m.ShortId}] vcp=0x{vcp:X}: handle=0 и reopen fail");
                    return -1;
                }
                RespectSuspension(m);
                // Для Samsung + DP базовые 4 попытки часто мало (капризный DDC/CI-канал).
                int attempts = (m.WriteGapMs >= 200) ? 6 : 4;
                int val = ReadRetry(m.Handle, vcp, attempts);
                if (val < 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    m.LastErrorCode = err;
                    // Всегда пробуем переоткрыть handle если чтение упало — Samsung DP
                    // часто отдаёт разные err codes (0, 87, 1F, ...) на битый DDC.
                    // Один re-open + повторный длинный retry намного лучше молчаливого -1.
                    Log?.Invoke($"SafeRead [{m.ShortId}] vcp=0x{vcp:X}: fail (err=0x{err:X}) — reopen+retry");
                    try { Native.DestroyPhysicalMonitor(m.Handle); } catch { }
                    m.Handle = IntPtr.Zero;
                    if (TryReopenHandle(m) && m.Handle != IntPtr.Zero && !m.Disposed)
                    {
                        val = ReadRetry(m.Handle, vcp, attempts);
                        if (val >= 0) Log?.Invoke($"SafeRead [{m.ShortId}] vcp=0x{vcp:X}: retry after reopen ok val={val}");
                        else Log?.Invoke($"SafeRead [{m.ShortId}] vcp=0x{vcp:X}: reopen ok но чтение всё равно fail");
                    }
                }
                return val;
            }
        }
        catch (Exception ex) { Log?.Invoke($"SafeRead '{m.Name}' vcp=0x{vcp:X} ex: {ex.Message}"); return -1; }
    }

    /// <summary>Попытаться переоткрыть physical handle для монитора.
    /// Используется когда handle=0 (не получился при cold enumerate) или стал невалидным.
    /// Вызывать ТОЛЬКО под m.OpLock.</summary>
    bool TryReopenHandle(MonInfo m)
    {
        if (m.Disposed || m.DdcPermanentlyUnavailable || m.DisplayLink || !m.DdcSupported) return false;
        try
        {
            // Найти актуальный HMONITOR по имени устройства.
            IntPtr foundHmon = IntPtr.Zero;
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Native.RECT r, IntPtr d) =>
            {
                var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
                Native.GetMonitorInfo(h, ref mi);
                if (mi.szDevice == m.Device) { foundHmon = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (foundHmon == IntPtr.Zero)
            {
                Log?.Invoke($"TryReopenHandle [{m.ShortId}]: HMONITOR не найден — монитор физически ушёл");
                m.DdcPermanentlyUnavailable = true;
                return false;
            }
            uint num = 0;
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(foundHmon, ref num) || num == 0)
            {
                m.DdcPermanentlyUnavailable = true;
                return false;
            }
            var arr = new Native.PHYSICAL_MONITOR[num];
            for (int r = 0; r < 3; r++)
            {
                bool ok = Native.GetPhysicalMonitorsFromHMONITOR(foundHmon, num, arr);
                if (ok && arr[0].hPhysicalMonitor != IntPtr.Zero)
                {
                    m.Handle = arr[0].hPhysicalMonitor;
                    Log?.Invoke($"TryReopenHandle [{m.ShortId}]: восстановлен handle=0x{m.Handle.ToInt64():X} (попытка {r + 1})");
                    return true;
                }
                Thread.Sleep(200);
            }
            Log?.Invoke($"TryReopenHandle [{m.ShortId}]: не удалось за 3 попытки");
            return false;
        }
        catch (Exception ex) { Log?.Invoke($"TryReopenHandle [{m.ShortId}] ex: {ex.Message}"); return false; }
    }

    /// <summary>Ждать пока истечёт "заморозка DDC" после hotplug/wake для данного монитора.</summary>
    void RespectSuspension(MonInfo m)
    {
        int wait = unchecked((int)(m.DdcSuspendedUntilMs - Environment.TickCount));
        if (wait > 0 && wait < 30000) Thread.Sleep(wait);
    }

    /// <summary>Атомарная запись VCP. Возвращает true если запись подтверждена (ok=true от драйвера).
    /// false = handle invalid / vcp unsupported / access denied / transient — Loop должен показать real value через SafeRead.</summary>
    bool SafeWrite(MonInfo m, byte vcp, int val)
    {
        // eDP-фаст-path: WMI напрямую, никакого DDC/CI. Contrast для eDP игнорируется
        // (WMI не поддерживает — только brightness).
        if (m.IsEdp)
        {
            if (vcp != VCP_BRIGHTNESS) return false;
            bool okEdp = EdpBrightnessService.Write(val);
            if (okEdp)
            {
                m.Brightness = val;
                Log?.Invoke($"SafeWrite [{m.ShortId}] eDP val={val} ok=True");
            }
            else Log?.Invoke($"SafeWrite [{m.ShortId}] eDP val={val} ok=False");
            return okEdp;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            lock (m.OpLock)
            {
                if (m.Disposed) { Log?.Invoke($"SafeWrite skip [{m.ShortId}]: Disposed"); return false; }
                if (m.DisplayLink) { Log?.Invoke($"SafeWrite skip [{m.ShortId}]: DisplayLink adapter"); return false; }
                if (m.DdcPermanentlyUnavailable) return false;
                if (vcp == VCP_BRIGHTNESS && m.VcpBrightnessUnsupported) return false;
                if (vcp == VCP_CONTRAST && m.VcpContrastUnsupported) return false;
                if (m.Handle == IntPtr.Zero && !TryReopenHandle(m))
                {
                    Log?.Invoke($"SafeWrite skip [{m.ShortId}]: handle=0 (reopen fail)");
                    return false;
                }
                RespectSuspension(m);
                if (m.Disposed) return false;
                Throttle(m);

                // Нормализация val (0..100) в raw диапазон монитора.
                int scaleMax = vcp == VCP_BRIGHTNESS ? m.BrightnessMax : m.ContrastMax;
                if (scaleMax <= 0) scaleMax = 100;
                uint raw = (uint)Math.Round(val * scaleMax / 100.0);

                bool ok = Native.SetVCPFeature(m.Handle, vcp, raw);
                int lastErr = ok ? 0 : Marshal.GetLastWin32Error();

                // Transient I2C — один короткий retry.
                if (!ok && IsTransientI2cError(lastErr))
                {
                    Thread.Sleep(50);
                    ok = Native.SetVCPFeature(m.Handle, vcp, raw);
                    if (!ok) lastErr = Marshal.GetLastWin32Error(); else lastErr = 0;
                    Log?.Invoke($"SafeWrite [{m.ShortId}] vcp=0x{vcp:X} transient retry ok={ok}");
                }

                long dur = sw.ElapsedMilliseconds;
                string durTag = dur > 500 ? $" SLOW dur={dur}ms" : "";
                Log?.Invoke($"SafeWrite [{m.ShortId}] vcp=0x{vcp:X} val={val} raw={raw}/{scaleMax} ok={ok}{(ok ? "" : $" err=0x{lastErr:X}({lastErr})")}{durTag}");

                m.LastErrorCode = lastErr;
                if (!ok)
                {
                    if (IsPossiblyStaleHandle(lastErr))
                    {
                        // Handle stale (INVALID_HANDLE или GEN_FAILURE / MCA_INTERNAL — часто после
                        // hotplug монитора). Обнуляем + СРАЗУ пытаемся переоткрыть и повторить write,
                        // чтобы юзеру не пришлось руками жать "Обновить".
                        try { Native.DestroyPhysicalMonitor(m.Handle); } catch { }
                        m.Handle = IntPtr.Zero;
                        Log?.Invoke($"SafeWrite [{m.ShortId}]: handle stale (err=0x{lastErr:X}) — reopening");
                        if (TryReopenHandle(m) && m.Handle != IntPtr.Zero && !m.Disposed)
                        {
                            bool retryOk = Native.SetVCPFeature(m.Handle, vcp, raw);
                            int retryErr = retryOk ? 0 : Marshal.GetLastWin32Error();
                            Log?.Invoke($"SafeWrite [{m.ShortId}] retry after reopen ok={retryOk}{(retryOk ? "" : $" err=0x{retryErr:X}")}");
                            if (retryOk)
                            {
                                if (vcp == VCP_BRIGHTNESS) m.Brightness = val; else m.Contrast = val;
                                return true;
                            }
                            // Если и после reopen fail — не крутимся в цикле, возвращаем false
                        }
                        return false;
                    }
                    if (lastErr == 5 /*ACCESS_DENIED — OSD Lock / HDCP*/)
                    {
                        if (vcp == VCP_BRIGHTNESS) m.ReadOnlyBrightness = true;
                        Log?.Invoke($"SafeWrite [{m.ShortId}]: ACCESS_DENIED — OSD locked / HDCP");
                        return false;
                    }
                    if (IsVcpNotSupported(lastErr))
                    {
                        if (vcp == VCP_BRIGHTNESS) { m.VcpBrightnessUnsupported = true; m.HasBrightness = false; }
                        else { m.VcpContrastUnsupported = true; m.HasContrast = false; }
                        Log?.Invoke($"SafeWrite [{m.ShortId}] vcp=0x{vcp:X}: unsupported — disabled");
                        return false;
                    }
                    return false;
                }

                // ok=true — сохраняем val как последнее подтверждённое.
                if (vcp == VCP_BRIGHTNESS) m.Brightness = val; else m.Contrast = val;
                m.WriteCounter++;
                // Verify только если очередь pending пуста (юзер отпустил слайдер).
                bool pendingEmpty;
                lock (pendingLock) { pendingEmpty = pending.Count == 0; }
                if (m.WriteCounter >= 10 && pendingEmpty && vcp == VCP_BRIGHTNESS && m.Handle != IntPtr.Zero && !m.Disposed)
                {
                    m.WriteCounter = 0;
                    Thread.Sleep(m.VerifyDelayMs);
                    if (m.Handle == IntPtr.Zero || m.Disposed) return true;
                    if (Native.GetVCPFeatureAndVCPFeatureReply(m.Handle, vcp, IntPtr.Zero, out uint cur, out uint mx))
                    {
                        int cm = mx == 0 ? scaleMax : (int)mx;
                        int gotPercent = (int)Math.Round(cur * 100.0 / cm);
                        if (Math.Abs(gotPercent - val) > 5)
                        {
                            m.ReadOnlyBrightness = true;
                            Log?.Invoke($"  [{m.ShortId}]: brightness read-only detected (set {val}%, got {gotPercent}%) — вероятно HDR");
                        }
                    }
                }
                return true;
            }
        }
        catch (Exception ex) { Log?.Invoke($"SafeWrite [{m.ShortId}] vcp=0x{vcp:X} val={val} ex: {ex.Message}"); return false; }
    }

    /// <summary>Задержка после hotplug/wake — DDC-канал не сразу оживает. Snapshot под _monitorsLock для thread-safety.</summary>
    public void SuspendDdc(int ms, string reason)
    {
        long until = Environment.TickCount + ms;
        MonInfo[] snapshot;
        lock (_monitorsLock) { snapshot = Monitors.ToArray(); }
        foreach (var m in snapshot)
        {
            // Продлеваем через Math.Max — не сокращаем существующий suspend.
            long cur = m.DdcSuspendedUntilMs;
            if (until > cur) m.DdcSuspendedUntilMs = until;
        }
        Log?.Invoke($"DDC suspended {ms}ms: {reason}");
    }

    /// <summary>Per-monitor suspend по индексу — для точечного hotplug вместо global.</summary>
    public void SuspendDdc(int monIdx, int ms, string reason)
    {
        MonInfo? m;
        lock (_monitorsLock) { m = (monIdx >= 0 && monIdx < Monitors.Count) ? Monitors[monIdx] : null; }
        if (m == null) return;
        long until = Environment.TickCount + ms;
        if (until > m.DdcSuspendedUntilMs) m.DdcSuspendedUntilMs = until;
        Log?.Invoke($"DDC suspended {ms}ms [{m.ShortId}]: {reason}");
    }

    public static Action<string>? Log { get; set; }

    void Loop()
    {
        // Startup: caps + начальные значения через безопасные обёртки (с OpLock, Disposed guards).
        try { InitialReadAll(); }
        catch (Exception ex) { Log?.Invoke($"InitialReadAll ex: {ex}"); }
        while (running)
        {
            try
            {
                signal.WaitOne();
                if (_rescanRequested)
                {
                    _rescanRequested = false;
                    try { InitialReadAll(); }
                    catch (Exception ex) { Log?.Invoke($"Rescan InitialReadAll ex: {ex}"); }
                }
                while (running && TryTake(out int idx, out byte vcp, out int val))
                {
                    MonInfo? m;
                    lock (_monitorsLock) { m = (idx >= 0 && idx < Monitors.Count) ? Monitors[idx] : null; }
                    if (m == null || m.Disposed) continue;
                    bool ok = SafeWrite(m, vcp, val);
                    if (ok)
                    {
                        Raise(idx, vcp, val);
                    }
                    else
                    {
                        int real = SafeRead(m, vcp);
                        if (real >= 0) Raise(idx, vcp, real);
                    }
                }
            }
            catch (Exception ex)
            {
                // Не даём воркеру умереть от любого race/InvalidOperationException — логируем и продолжаем.
                Log?.Invoke($"Loop iter ex: {ex.GetType().Name} {ex.Message}");
                try { Thread.Sleep(200); } catch { }
            }
        }
    }
    int IndexOf(MonInfo m) { lock (_monitorsLock) { return Monitors.IndexOf(m); } }
    void Raise(int idx, byte vcp, int val)
    {
        MonInfo? m;
        lock (_monitorsLock) { m = (idx >= 0 && idx < Monitors.Count) ? Monitors[idx] : null; }
        if (m == null || m.Disposed) return;
        OnValue?.Invoke(new ValueUpdate { MonIndex = idx, MonId = m.Id, Generation = m.Generation, Vcp = vcp, Value = val });
    }

    string? ReadCapsRetry(IntPtr h, int attempts)
    {
        // Caps обычно отвечает на 1-2 попытку у поддерживающих мониторов.
        // Если не ответил за 4 попытки (~3.7 сек) — вероятно unsupported, дальше нет смысла ждать.
        int[] waits = { 200, 500, 1000, 2000, 2000, 2000, 2000, 2000 };
        for (int i = 0; i < attempts; i++)
        {
            uint len = 0;
            if (Native.GetCapabilitiesStringLength(h, ref len) && len > 0)
            {
                var sb = new System.Text.StringBuilder((int)len);
                if (Native.CapabilitiesRequestAndCapabilitiesReply(h, sb, len)) return sb.ToString();
            }
            Thread.Sleep(waits[Math.Min(i, waits.Length - 1)]);
        }
        return null;
    }
    static List<string> TopLevelVcp(string caps)
    {
        var res = new List<string>();
        int i = caps.IndexOf("VCP(");
        if (i < 0) return res;
        int depth = 0;
        var hex = new System.Text.StringBuilder();
        for (int p = i + 4; p < caps.Length; p++)
        {
            char ch = caps[p];
            if (ch == '(') depth++;
            else if (ch == ')') { if (depth == 0) break; depth--; }
            else if (depth == 0 && IsHex(ch)) hex.Append(ch);
        }
        string s = hex.ToString();
        for (int k = 0; k + 1 < s.Length; k += 2) res.Add(s.Substring(k, 2));
        return res;
    }
    static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
    int ReadRetry(IntPtr h, byte vcp, int attempts)
    {
        int[] waits = { 120, 250, 450, 700, 1000, 1100, 1100, 1100 };
        for (int i = 0; i < attempts; i++)
        {
            if (Native.GetVCPFeatureAndVCPFeatureReply(h, vcp, IntPtr.Zero, out uint cur, out uint _)) return (int)cur;
            Thread.Sleep(waits[Math.Min(i, waits.Length - 1)]);
        }
        return -1;
    }
    void Throttle(MonInfo m)
    {
        int since = unchecked((int)(Environment.TickCount - m.LastOpMs));
        if (since >= 0 && since < m.WriteGapMs) Thread.Sleep(m.WriteGapMs - since);
        m.LastOpMs = Environment.TickCount;
    }
}
