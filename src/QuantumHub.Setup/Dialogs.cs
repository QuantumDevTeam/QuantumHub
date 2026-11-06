using System.Runtime.InteropServices;

namespace QuantumHub.Setup;

/// <summary>
/// Окна установщика на TaskDialog. Это готовое окно Windows, поэтому установщику не нужны ни WPF, ни WinForms,
/// и он остаётся маленьким.
/// </summary>
internal static class Dialogs
{
    private const string Title = "QuantumHub";

    private const int AllowCancellation = 0x0008;
    private const int VerificationChecked = 0x0100;
    private const int ShowProgressBar = 0x0200;
    private const int CallbackTimer = 0x0800;

    private const int IdOk = 1;
    private const int IdCancel = 2;
    /// <summary>Id нашей кнопки подтверждения.</summary>
    private const int IdAccept = 100;

    private const int NotifyCreated = 0;
    private const int NotifyButtonClicked = 2;
    private const int NotifyTimer = 4;

    private const uint MessageClickButton = 0x0400 + 102;
    private const uint MessageSetProgress = 0x0400 + 106;
    private const uint MessageEnableButton = 0x0400 + 111;

    /// <summary>Значение колбэка: не закрывать окно по нажатию.</summary>
    private const int KeepOpen = 1;

    private const uint MessageBoxError = 0x10;

    /// <summary>Спрашивает подтверждение.</summary>
    /// <param name="instruction">Главная строка.</param>
    /// <param name="content">Пояснение.</param>
    /// <param name="accept">Текст кнопки.</param>
    /// <param name="option">Текст галочки или <see langword="null"/>.</param>
    /// <param name="optionChecked">Стоит ли галочка изначально.</param>
    /// <returns>Согласие и состояние галочки.</returns>
    public static (bool Accepted, bool Option) Confirm(string instruction, string content, string accept, string? option, bool optionChecked)
    {
        var acceptText = Marshal.StringToHGlobalUni(accept);
        // Подпись на стандартной кнопке зависит от языка Windows, а весь остальной текст здесь русский.
        var cancelText = Marshal.StringToHGlobalUni("Отмена");
        var size = Marshal.SizeOf<TaskDialogButton>();
        var buttons = Marshal.AllocHGlobal(size * 2);
        try
        {
            Marshal.StructureToPtr(new TaskDialogButton { Id = IdAccept, Text = acceptText }, buttons, false);
            Marshal.StructureToPtr(new TaskDialogButton { Id = IdCancel, Text = cancelText }, buttons + size, false);
            var config = NewConfig(instruction, content);
            config.Flags = AllowCancellation | (optionChecked ? VerificationChecked : 0);
            config.ButtonCount = 2;
            config.Buttons = buttons;
            config.DefaultButton = IdAccept;
            config.VerificationText = option;

            var result = TaskDialogIndirect(ref config, out var button, out _, out var verified);
            Marshal.ThrowExceptionForHR(result);
            return (button == IdAccept, verified);
        }
        finally
        {
            Marshal.FreeHGlobal(buttons);
            Marshal.FreeHGlobal(cancelText);
            Marshal.FreeHGlobal(acceptText);
        }
    }

    /// <summary>Показывает ход работы, пока она не закончится.</summary>
    /// <param name="instruction">Главная строка.</param>
    /// <param name="work">Работа. Получает функцию для сообщения о доле выполненного.</param>
    public static void Progress(string instruction, Func<Action<double>, Task> work)
    {
        var fraction = 0.0;
        var task = Task.Run(() => work(value => Volatile.Write(ref fraction, value)));
        var finished = false;

        TaskDialogCallback callback = (window, notification, wParam, _, _) =>
        {
            switch (notification)
            {
                case NotifyCreated:
                    SendMessage(window, MessageEnableButton, IdOk, 0);
                    break;
                case NotifyTimer:
                    SendMessage(window, MessageSetProgress, (nint)(Volatile.Read(ref fraction) * 100), 0);
                    if (task.IsCompleted && !finished)
                    {
                        finished = true;
                        SendMessage(window, MessageClickButton, IdOk, 0);
                    }

                    break;
                case NotifyButtonClicked when !finished:
                    return KeepOpen;
            }

            return 0;
        };

        var config = NewConfig(instruction, null);
        config.Flags = ShowProgressBar | CallbackTimer;
        config.Callback = Marshal.GetFunctionPointerForDelegate(callback);
        var result = TaskDialogIndirect(ref config, out _, out _, out _);
        GC.KeepAlive(callback);
        Marshal.ThrowExceptionForHR(result);

        task.GetAwaiter().GetResult();
    }

    /// <summary>Показывает ошибку.</summary>
    /// <param name="message">Текст.</param>
    public static void Error(string message) => MessageBox(0, message, Title, MessageBoxError);

    /// <summary>Заготовка настроек окна.</summary>
    private static TaskDialogConfig NewConfig(string instruction, string? content) => new()
    {
        Size = (uint)Marshal.SizeOf<TaskDialogConfig>(),
        WindowTitle = Title,
        MainInstruction = instruction,
        Content = content,
    };

    private delegate int TaskDialogCallback(nint window, uint notification, nint wParam, nint lParam, nint data);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaskDialogButton
    {
        public int Id;
        public nint Text;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct TaskDialogConfig
    {
        public uint Size;
        public nint Parent;
        public nint Instance;
        public int Flags;
        public int CommonButtons;
        public string? WindowTitle;
        public nint MainIcon;
        public string? MainInstruction;
        public string? Content;
        public uint ButtonCount;
        public nint Buttons;
        public int DefaultButton;
        public uint RadioButtonCount;
        public nint RadioButtons;
        public int DefaultRadioButton;
        public string? VerificationText;
        public string? ExpandedInformation;
        public string? ExpandedControlText;
        public string? CollapsedControlText;
        public nint FooterIcon;
        public string? Footer;
        public nint Callback;
        public nint CallbackData;
        public uint Width;
    }

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
    private static extern int TaskDialogIndirect(ref TaskDialogConfig config, out int button, out int radioButton,
        [MarshalAs(UnmanagedType.Bool)] out bool verified);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
}
