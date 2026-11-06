using QuantumHub.Contracts.Ui;
using System.Runtime.InteropServices;

namespace QuantumHub.Gui.Services;

/// <summary>
/// Значок в области уведомлений. В WinUI своего нет, а Shell_NotifyIcon достаточно прост, чтобы
/// лишняя зависимость только добавляла риска.
/// </summary>
/// <remarks>
/// Значку нужно скрытое окно: именно ему оболочка шлёт сообщения о щелчках. Сообщения приходят через
/// обычный цикл потока интерфейса.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmTray = WmApp + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmNull = 0;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x10;
    private const uint TpmReturnCmd = 0x100;
    private const uint TpmRightButton = 2;
    private const uint MfString = 0;
    private const uint MfSeparator = 0x800;

    /// <summary>Команда меню «Открыть».</summary>
    private const int CommandOpen = 1;
    /// <summary>Команда меню «Закрыть интерфейс».</summary>
    private const int CommandQuit = 2;
    /// <summary>Команда меню «Завершить хаб и выйти».</summary>
    private const int CommandQuitAll = 3;

    private readonly WndProc _wndProc;
    private readonly Action _open;
    private readonly Action _quit;
    private readonly Action _quitAll;
    private readonly string _tip;
    private readonly nint _window;
    private readonly nint _icon;
    /// <summary>Сообщение, которое приходит после перезапуска проводника.</summary>
    private readonly uint _taskbarCreated;
    private bool _disposed;

    /// <summary>Создаёт значок.</summary>
    /// <param name="iconPath">Путь к .ico.</param>
    /// <param name="tip">Всплывающая подсказка.</param>
    /// <param name="open">Действие «открыть».</param>
    /// <param name="quit">Действие «закрыть интерфейс».</param>
    /// <param name="quitAll">Действие «завершить хаб и выйти».</param>
    public TrayIcon(string iconPath, string tip, Action open, Action quit, Action quitAll)
    {
        _open = open;
        _quit = quit;
        _quitAll = quitAll;
        _tip = tip;
        _wndProc = Process; // Хранится в поле: сборщик мусора не должен собрать то, что вызывает нативный код.
        _icon = LoadImage(0, iconPath, ImageIcon, 32, 32, LrLoadFromFile);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            ClassName = $"QuantumHubTray{Environment.ProcessId}",
        };
        RegisterClassEx(ref windowClass);
        _window = CreateWindowEx(0, windowClass.ClassName, tip, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Add();
    }

    /// <summary>Описание значка для Shell_NotifyIcon.</summary>
    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = WmTray,
        Icon = _icon,
        Tip = _tip,
    };

    /// <summary>Добавляет значок на панель.</summary>
    private void Add()
    {
        var data = Data();
        ShellNotifyIcon(NimAdd, ref data);
    }

    /// <summary>Оконная процедура скрытого окна.</summary>
    private nint Process(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreated)
        {
            Add(); // Проводник перезапустился и забыл все значки.
            return 0;
        }

        if (message == WmTray)
        {
            switch ((int)lParam & 0xFFFF)
            {
                case WmLButtonUp when !GuiSettings.Current.TrayDoubleClick:
                case WmLButtonDblClk when GuiSettings.Current.TrayDoubleClick:
                    _open();
                    break;
                case WmRButtonUp:
                    ShowMenu();
                    break;
            }

            return 0;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    /// <summary>Показывает меню по правому щелчку.</summary>
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MfString, CommandOpen, Loc.T("Открыть QuantumHub", "Open QuantumHub"));
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, CommandQuit, Loc.T("Закрыть интерфейс (хаб продолжит работать)", "Close the interface (the hub keeps running)"));
        AppendMenu(menu, MfString, CommandQuitAll, Loc.T("Завершить хаб и выйти", "Stop the hub and exit"));

        GetCursorPos(out var point);
        // Без этого меню не закрылось бы по щелчку в стороне.
        SetForegroundWindow(_window);
        var command = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, point.X, point.Y, 0, _window, 0);
        PostMessage(_window, WmNull, 0, 0);
        DestroyMenu(menu);

        switch (command)
        {
            case CommandOpen:
                _open();
                break;
            case CommandQuit:
                _quit();
                break;
            case CommandQuitAll:
                _quitAll();
                break;
        }
    }

    /// <summary>Убирает значок. Иначе он висел бы на панели до наведения мыши.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = Data();
        ShellNotifyIcon(NimDelete, ref data);
        DestroyWindow(_window);
    }

    private delegate nint WndProc(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")] private static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
