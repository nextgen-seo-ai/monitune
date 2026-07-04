using System.Runtime.InteropServices;

namespace MonitorTune;

// Антисон + имитация активности.
//
// Два независимых механизма:
//
// 1) PreventSleep — SetThreadExecutionState(ES_CONTINUOUS|ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED).
//    Официальный путь Windows API чтобы попросить систему не засыпать.
//    Работает БЕЗ движения мыши, не отображает курсор активным для приложений.
//
// 2) SimulateActivity — каждые N секунд SendInput с MOUSEEVENTF_MOVE на (0,0).
//    Это no-op для пользователя (курсор не двигается), но Teams/Slack/Discord
//    регистрируют это как user activity и держат статус "В сети".
//    Также сбрасывает системный idle timer для блокировки экрана.
public sealed class KeepAwakeService : IDisposable
{
    System.Threading.Timer? _jiggleTimer;
    bool _sleepBlocked;
    bool _disposed;

    public static Action<string> Log { get; set; } = _ => { };

    /// <summary>Применить текущие настройки из SettingsStore.</summary>
    public void Apply()
    {
        if (_disposed) return;
        var s = SettingsStore.Current.KeepAwake;

        // 1) Sleep prevention
        if (s.PreventSleep && !_sleepBlocked)
        {
            Native.SetThreadExecutionState(
                Native.ExecutionState.EsContinuous |
                Native.ExecutionState.EsSystemRequired |
                Native.ExecutionState.EsDisplayRequired);
            _sleepBlocked = true;
            Log("KeepAwake: PreventSleep ON");
        }
        else if (!s.PreventSleep && _sleepBlocked)
        {
            // ES_CONTINUOUS без других флагов = "сбросить блокировку".
            Native.SetThreadExecutionState(Native.ExecutionState.EsContinuous);
            _sleepBlocked = false;
            Log("KeepAwake: PreventSleep OFF");
        }

        // 2) Activity simulation
        _jiggleTimer?.Dispose();
        _jiggleTimer = null;
        if (s.SimulateActivity)
        {
            int periodMs = Math.Max(5, s.IntervalSec) * 1000;
            _jiggleTimer = new System.Threading.Timer(_ => Jiggle(), null, periodMs, periodMs);
            Log($"KeepAwake: SimulateActivity ON, interval={s.IntervalSec}s");
        }
        else
        {
            Log("KeepAwake: SimulateActivity OFF");
        }
    }

    /// <summary>"Движение" мыши. Если VisibleMove=false — невидимое (0,0),
    /// иначе курсор реально дёргается на 1 пиксель туда-обратно.</summary>
    public static void Jiggle()
    {
        bool visible = SettingsStore.Current.KeepAwake.VisibleMove;
        try
        {
            if (visible)
            {
                SendMove(+1, 0);
                System.Threading.Thread.Sleep(50);
                SendMove(-1, 0);
            }
            else
            {
                SendMove(0, 0);
            }
            Log($"KeepAwake: jiggle ({(visible ? "visible" : "invisible")}) at {DateTime.Now:HH:mm:ss.fff}");
        }
        catch (Exception ex) { Log("Jiggle ex: " + ex.Message); }
    }

    static void SendMove(int dx, int dy)
    {
        var input = new Native.INPUT[1];
        input[0].type = Native.INPUT_MOUSE;
        input[0].mi = new Native.MOUSEINPUT
        {
            dx = dx,
            dy = dy,
            dwFlags = Native.MOUSEEVENTF_MOVE,
            time = 0,
            dwExtraInfo = IntPtr.Zero,
            mouseData = 0,
        };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _jiggleTimer?.Dispose();
        if (_sleepBlocked)
            Native.SetThreadExecutionState(Native.ExecutionState.EsContinuous);
    }
}
