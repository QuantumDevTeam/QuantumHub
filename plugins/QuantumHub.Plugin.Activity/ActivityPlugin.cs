using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.Activity;

/// <summary>Окно в фокусе. Ответ window.current и тело события activity.window.changed.</summary>
/// <param name="Process">Имя процесса.</param>
/// <param name="Title">Заголовок. Пусто, если он отключён настройкой.</param>
/// <param name="ProcessId">Id процесса.</param>
/// <param name="UnixMs">Когда окно стало активным.</param>
public sealed record ForegroundWindow(string Process, string Title, int ProcessId, long UnixMs);

/// <summary>Запрос process.watch и process.list.</summary>
/// <param name="Names">Имена процессов без .exe.</param>
public sealed record ProcessNames(string[] Names);

/// <summary>Ответ idle.get и тело события activity.idle.changed.</summary>
/// <param name="IdleSeconds">Сколько секунд не было ввода.</param>
/// <param name="IsIdle">Отошёл ли пользователь.</param>
public sealed record IdleState(double IdleSeconds, bool IsIdle);

/// <summary>Ответ vr.get и тело события activity.vr.changed.</summary>
/// <param name="Active">Запущен ли VR.</param>
public sealed record VrState(bool Active);

/// <summary>
/// Смотрит за активностью пользователя и предоставляет её как возможность user.activity. Смену окна
/// сообщает сама Windows, а процессы и простой проверяются таймером раз в две секунды.
/// </summary>
[HubPlugin]
public sealed class ActivityPlugin : IHubPlugin, IUiProvider
{
    /// <summary>Топик смены активного окна.</summary>
    public const string WindowTopic = "activity.window.changed";

    /// <summary>Топик запуска и закрытия наблюдаемого процесса.</summary>
    public const string ProcessTopic = "activity.process.changed";

    /// <summary>Топик ухода и возвращения пользователя.</summary>
    public const string IdleTopic = "activity.idle.changed";

    /// <summary>Топик запуска и закрытия VR.</summary>
    public const string VrTopic = "activity.vr.changed";

    private const uint EventSystemForeground = 3;
    private const uint WinEventOutOfContext = 0;
    private const uint WmQuit = 0x0012;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ProcessWatcher _processes = new();
    private readonly IdleTracker _idle = new();
    private IPluginContext _context = null!;
    private WinEventProc? _callback;
    private Thread? _thread;
    private uint _threadId;
    private volatile ForegroundWindow? _current;
    private int _switches;
    private bool _vrActive;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <inheritdoc/>
    public Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        context.Bus.Handle("window.current", (_, _) => ValueTask.FromResult(HubPayload.Json(_current)));
        context.Bus.Handle("vr.get", (_, _) => ValueTask.FromResult(HubPayload.Json(new VrState(IsVrRunning()))));
        context.Bus.Handle("idle.get", (_, _) => ValueTask.FromResult(HubPayload.Json(ReadIdle())));
        context.Bus.Handle("process.list", (request, _) =>
        {
            var names = request.Payload.AsJson<ProcessNames>()?.Names ?? [];
            return ValueTask.FromResult(HubPayload.Json(new ProcessNames(names.Where(IsRunning).ToArray())));
        });
        context.Bus.Handle("process.watch", (request, _) =>
        {
            _processes.Watch(request.Payload.AsJson<ProcessNames>()?.Names ?? []);
            return ValueTask.FromResult(HubPayload.Empty);
        });

        // События приходят через очередь сообщений потока, поставившего хук, поэтому ему нужен
        // свой цикл сообщений. Опроса нет, и между переключениями это ничего не стоит.
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => HookLoop(ready)) { IsBackground = true, Name = "Activity hook" };
        _thread.Start();
        ready.Wait(token);

        Update(GetForegroundWindow());

        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => PollLoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Завершает поток хука и опрос. После этого код плагина можно выгружать.</summary>
    /// <param name="token">Отмена.</param>
    public async Task StopAsync(CancellationToken token)
    {
        PostThreadMessage(_threadId, WmQuit, 0, 0);
        _thread?.Join(TimeSpan.FromSeconds(2));
        if (_stop is not null) await _stop.CancelAsync();
        if (_loop is not null) await _loop;
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, token);
                foreach (var change in _processes.Poll(IsRunning))
                {
                    _context.Logger.Info($"{change.Name}: {(change.Running ? Loc.T("запущен", "running") : "закрыт")}");
                    _ = _context.Bus.PublishAsync(ProcessTopic, HubPayload.Json(change), token);
                }

                var vr = IsVrRunning();
                _context.Ui.Set("vr", vr ? Loc.T("запущен", "running") : Loc.T("не запущен", "not running"));
                if (vr != _vrActive)
                {
                    _vrActive = vr;
                    _ = _context.Bus.PublishAsync(VrTopic, HubPayload.Json(new VrState(vr)), token);
                }

                var idle = ReadIdle();
                _context.Ui.Set("idle", idle.IsIdle ? Loc.T("отошёл", "away") : Loc.T("за компьютером", "at the computer"));
                if (_idle.Update(TimeSpan.FromSeconds(idle.IdleSeconds), IdleThreshold()))
                    _ = _context.Bus.PublishAsync(IdleTopic, HubPayload.Json(ReadIdle()), token);
            }
        }
        catch (OperationCanceledException)
        {
            // Плагин останавливается.
        }
    }

    private bool IsVrRunning() => _context.Config.Get("vrProcesses", new List<string>())!.Any(IsRunning);

    private TimeSpan IdleThreshold() => TimeSpan.FromSeconds(_context.Config.Get("idleSeconds", 120d));

    private IdleState ReadIdle()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        var seconds = GetLastInputInfo(ref info) ? (uint)Environment.TickCount - info.Time : 0;
        var idle = TimeSpan.FromMilliseconds(seconds);
        return new IdleState(idle.TotalSeconds, idle >= IdleThreshold());
    }

    private static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        var found = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        return found;
    }

    /// <summary>Ставит хук и крутит цикл сообщений.</summary>
    /// <param name="ready">Сигнал, что хук установлен.</param>
    private void HookLoop(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        _callback = (_, _, window, _, _, _, _) => Update(window);
        var hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _callback, 0, 0, WinEventOutOfContext);

        // Заставляет очередь появиться, чтобы остановка сразу после запуска не потерялась.
        PeekMessage(out _, 0, 0, 0, 0);
        ready.Set();

        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        UnhookWinEvent(hook);
    }

    /// <summary>Обрабатывает смену окна.</summary>
    /// <param name="window">Хэндл окна.</param>
    private void Update(nint window)
    {
        try
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid == 0) return;

            string process;
            try
            {
                using var info = Process.GetProcessById((int)pid);
                process = info.ProcessName;
            }
            catch (ArgumentException)
            {
                return; // Процесс уже завершился.
            }

            var title = _context.Config.Get("includeTitle", true) ? ReadTitle(window) : string.Empty;
            var current = new ForegroundWindow(process, title, (int)pid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _current = current;

            _context.Ui.Set("process", process);
            _context.Ui.Set("title", title);
            _context.Ui.Set("switches", Interlocked.Increment(ref _switches).ToString());
            _ = _context.Bus.PublishAsync(WindowTopic, HubPayload.Json(current));
        }
        catch (Exception ex)
        {
            _context.Logger.Error("Не удалось прочитать активное окно", ex);
        }
    }

    /// <summary>Читает заголовок окна.</summary>
    private static string ReadTitle(nint window)
    {
        var buffer = new StringBuilder(512);
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        yield return Ui.Page("main", Loc.T("Сейчас", "Now"), "E7C4",
            Ui.KeyValue(Loc.T("Процесс", "Process"), "process"),
            Ui.KeyValue(Loc.T("Заголовок", "Title"), "title"),
            Ui.KeyValue(Loc.T("Пользователь", "User"), "idle"),
            Ui.KeyValue("VR", "vr"),
            Ui.KeyValue(Loc.T("Переключений", "Switches"), "switches"));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
    }

    /// <summary>Сигнатура колбэка SetWinEventHook.</summary>
    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);

    /// <summary>Структура MSG из WinAPI.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    /// <summary>Структура LASTINPUTINFO из WinAPI.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
