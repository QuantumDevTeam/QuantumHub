using System.Runtime.InteropServices;

namespace QuantumHub.Service;

/// <summary>
/// Узнаёт о выходе из Windows. Без окна процесс просто убивается, и плагины не успевают вернуть всё как было: например, VR Switcher
/// оставил бы звук на гарнитуре. Поэтому у хаба есть одно скрытое окно, которое ждёт WM_ENDSESSION.
/// </summary>
internal sealed class SessionEndListener : IDisposable
{
    /// <summary>Система спрашивает, можно ли завершить сеанс.</summary>
    private const uint WmQueryEndSession = 0x0011;
    /// <summary>Сеанс завершается.</summary>
    private const uint WmEndSession = 0x0016;

    /// <summary>Сколько ждать хаб. Windows ждёт приложение около пяти секунд.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(4.5);

    private readonly string _className;
    private readonly Action _onEnding;
    private readonly ManualResetEventSlim _finished = new();
    /// <summary>Держит делегат живым: окно вызывает его из неуправляемого кода.</summary>
    private readonly WndProc _proc;

    /// <summary>Создаёт окно на своём потоке.</summary>
    /// <param name="instance">Экземпляр хаба.</param>
    /// <param name="onEnding">Вызывается, когда сеанс завершается.</param>
    public SessionEndListener(string instance, Action onEnding)
    {
        _className = ClassName(instance);
        _onEnding = onEnding;
        _proc = OnMessage;
        new Thread(Loop) { IsBackground = true, Name = "QuantumHub session end" }.Start();
    }

    /// <summary>Имя класса окна.</summary>
    /// <param name="instance">Экземпляр хаба.</param>
    public static string ClassName(string instance) => $"QuantumHub.Service.{instance}";

    /// <summary>Хаб завершился, и Windows можно отпустить.</summary>
    public void Dispose() => _finished.Set();

    /// <summary>Создаёт окно и разбирает сообщения.</summary>
    private void Loop()
    {
        var windowClass = new WindowClass { Proc = Marshal.GetFunctionPointerForDelegate(_proc), Instance = GetModuleHandleW(null), ClassName = _className };
        if (RegisterClassW(ref windowClass) == 0) return;
        if (CreateWindowExW(0, _className, _className, 0, 0, 0, 0, 0, 0, 0, windowClass.Instance, 0) == 0) return;

        while (GetMessageW(out var message, 0, 0, 0) > 0) DispatchMessageW(ref message);
    }

    /// <summary>Обработчик сообщений окна.</summary>
    private nint OnMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WmQueryEndSession) return 1;
        if (message == WmEndSession)
        {
            if (wParam != 0)
            {
                _onEnding();
                _finished.Wait(Grace);
            }

            return 0;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public nint Proc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}
