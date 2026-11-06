using Microsoft.Win32;

namespace QuantumHub.Gui.Services;

/// <summary>Автозапуск для пользователя через ключ Run: права администратора не нужны, убирается так же легко.</summary>
public static class Autostart
{
    /// <summary>Ключ реестра автозапуска.</summary>
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Имя значения для хаба.</summary>
    public const string HubValue = "QuantumHub";
    /// <summary>Имя значения для GUI.</summary>
    public const string GuiValue = "QuantumHub GUI";

    /// <summary>Включён ли автозапуск.</summary>
    /// <param name="name">Имя значения.</param>
    public static bool IsEnabled(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(name) is not null;
    }

    /// <summary>Включает или выключает автозапуск.</summary>
    /// <param name="name">Имя значения.</param>
    /// <param name="command">Команда запуска. <see langword="null"/> выключает.</param>
    public static void Set(string name, string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (command is null) key.DeleteValue(name, false);
        else key.SetValue(name, command);
    }
}
