namespace QuantumHub.Plugin.MicSwitcher;

/// <summary>Решает, с какими микрофонами плагин вообще имеет дело.</summary>
public static class MicFilter
{
    /// <summary>Bluetooth, а слушать такие не разрешено.</summary>
    public const string Bluetooth = "bluetooth";

    /// <summary>Есть в чёрном списке.</summary>
    public const string Blacklisted = "blacklist";

    /// <summary>Белый список задан, а этого микрофона в нём нет.</summary>
    public const string NotWhitelisted = "whitelist";

    /// <summary>Почему на микрофон нельзя переключать.</summary>
    /// <param name="name">Имя микрофона.</param>
    /// <param name="bluetooth">Подключён ли по Bluetooth.</param>
    /// <param name="options">Настройки.</param>
    /// <returns>Причина или <see langword="null"/>, если можно.</returns>
    public static string? Exclusion(string name, bool bluetooth, MicSwitchOptions options)
    {
        if (bluetooth && !options.WatchBluetooth) return Bluetooth;
        if (Matches(name, options.Blacklist)) return Blacklisted;
        return options.Whitelist.Count > 0 && !Matches(name, options.Whitelist) ? NotWhitelisted : null;
    }

    /// <summary>
    /// Нужно ли открывать поток. Текущий микрофон вне белого списка слушается тоже: на него не переключают, но чтобы
    /// уйти с него, надо знать, что на нём тихо. Bluetooth и чёрный список не открываются никогда.
    /// </summary>
    /// <param name="mic">Микрофон.</param>
    /// <param name="options">Настройки.</param>
    public static bool ShouldListen(MicState mic, MicSwitchOptions options) =>
        Exclusion(mic.Name, mic.Bluetooth, options) switch
        {
            null => true,
            NotWhitelisted => mic.IsDefault,
            _ => false,
        };

    private static bool Matches(string name, IEnumerable<string> parts) =>
        parts.Any(part => part.Length > 0 && name.Contains(part, StringComparison.OrdinalIgnoreCase));
}
