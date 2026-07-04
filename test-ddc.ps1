# Тест надёжности DDC/CI по каждому монитору.
# Запуск:  powershell -ExecutionPolicy Bypass -File .\test-ddc.ps1
# Делает по 15 чтений яркости с каждого монитора и показывает % успеха.
# 15/15 = DDC/CI работает идеально. Низкий результат = битый канал (кабель/порт/OSD).

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$src = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class DDCR {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct MONITORINFOEX {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szDevice; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct PHYSICAL_MONITOR {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string szPhysicalMonitorDescription; }
    public delegate bool MonitorEnumProc(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX info);
    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, ref uint n);
    [DllImport("dxva2.dll", CharSet=CharSet.Unicode)] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] a);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorBrightness(IntPtr h, ref uint a, ref uint b, ref uint c);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorContrast(IntPtr h, ref uint a, ref uint b, ref uint c);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);
    [DllImport("kernel32.dll")] public static extern uint GetLastError();
    public static List<IntPtr> hmons = new List<IntPtr>();
    public static List<string> names = new List<string>();
    public static void Enum() {
        hmons.Clear(); names.Clear();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) => {
            var mi=new MONITORINFOEX(); mi.cbSize=Marshal.SizeOf(typeof(MONITORINFOEX)); GetMonitorInfo(h, ref mi);
            hmons.Add(h); names.Add(mi.szDevice); return true; }, IntPtr.Zero);
    }
    public static string TestOne(int idx, int tries) {
        uint num=0; GetNumberOfPhysicalMonitorsFromHMONITOR(hmons[idx], ref num);
        var arr=new PHYSICAL_MONITOR[num]; GetPhysicalMonitorsFromHMONITOR(hmons[idx], num, arr);
        var pm = arr[0];
        int okB=0, okC=0;
        for (int i=0;i<tries;i++){ uint a=0,b=0,c=0;
            if (GetMonitorBrightness(pm.hPhysicalMonitor, ref a, ref b, ref c)) okB++;
            uint d=0,e=0,f=0;
            if (GetMonitorContrast(pm.hPhysicalMonitor, ref d, ref e, ref f)) okC++;
            System.Threading.Thread.Sleep(150);
        }
        DestroyPhysicalMonitor(pm.hPhysicalMonitor);
        return names[idx]+": яркость "+okB+"/"+tries+"   контраст "+okC+"/"+tries;
    }
}
"@
Add-Type -TypeDefinition $src
[DDCR]::Enum()
Write-Output "Тест DDC/CI (по 15 запросов на монитор):`n"
for ($i=0; $i -lt [DDCR]::hmons.Count; $i++) { Write-Output ("  " + [DDCR]::TestOne($i, 15)) }
Write-Output "`n15/15 = отлично. Меньше = битый DDC-канал (меняй кабель/порт, проверь OSD)."